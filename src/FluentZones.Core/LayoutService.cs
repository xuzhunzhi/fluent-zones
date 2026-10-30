using System.Runtime.InteropServices;

namespace FluentZones.Core;

/// <summary>
/// 一次性布局：窗口枚举/筛选、格数上限、主窗槽位、放不下时的“最小化/换主窗”建议与窗口摆放，
/// 具体几何统一交给 <see cref="ZoneWorkspace"/>（与分区工作区共用同一份切分树 + 最小尺寸拟合）。
/// 实在放不下时不擅自降级，而是返回"建议最小化哪些窗口"，交给界面询问用户。
/// </summary>
public sealed class LayoutService
{
    // 绝对下限，防止 0 尺寸的自绘窗口（与 ZoneNode.MinimumSize 的下限一致）
    private const int MinWidth = 120;
    private const int MinHeight = 90;

    private readonly object _gate = new();
    private List<SnapshotEntry> _snapshot = new();
    private List<(IntPtr Handle, WindowRect Requested)> _lastPlan = new();

    public bool CanRestore
    {
        get
        {
            lock (_gate)
            {
                return _snapshot.Count > 0;
            }
        }
    }

    public IReadOnlyList<WindowInfo> GetWindows(LayoutScope scope) =>
        ScopeWindows(WindowCollector.Collect(), scope);

    /// <summary>
    /// 排列窗口。
    /// </summary>
    /// <param name="minimizeOldest">
    /// 先最小化 Z 序最底层（最久未使用）的这么多个窗口，再排列剩下的。
    /// </param>
    /// <returns>放不下且需要用户决策时，返回 <see cref="LayoutResult.NeedsChoice"/> 和建议最小化的窗口。</returns>
    public LayoutResult Apply(
        LayoutKind kind,
        LayoutOptions options,
        IReadOnlyCollection<IntPtr>? restrictTo = null,
        int minimizeOldest = 0,
        IntPtr mainSlot = default,
        bool allowMainSwap = true)
    {
        var log = new List<string>
        {
            $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} kind={kind} gap={options.Gap} scope={options.Scope} minimize={minimizeOldest} allowMainSwap={allowMainSwap}",
            "foreground: " + DescribeWindow(NativeMethods.GetForegroundWindow()),
        };

        try
        {
            return ApplyCore(kind, options, restrictTo, minimizeOldest, mainSlot, allowMainSwap, log);
        }
        finally
        {
            LayoutLog.Write(log);
        }
    }

    private LayoutResult ApplyCore(
        LayoutKind kind,
        LayoutOptions options,
        IReadOnlyCollection<IntPtr>? restrictTo,
        int minimizeOldest,
        IntPtr mainSlot,
        bool allowMainSwap,
        List<string> log)
    {
        lock (_gate)
        {
            var gap = Math.Clamp(options.Gap, 0, 200);
            var all = WindowCollector.Collect();
            if (restrictTo != null)
            {
                all = all.FindAll(w => restrictTo.Contains(w.Handle));
            }

            var scoped = ScopeWindows(all, options.Scope);
            if (scoped.Count == 0)
            {
                return LayoutResult.Fail("没有找到可以排列的窗口");
            }

            var blocked = scoped.Where(w => w.CannotMove).ToList();
            var movable = scoped.Where(w => !w.CannotMove).ToList();

            foreach (var window in blocked)
            {
                log.Add($"  blocked (elevated) \"{window.Title}\"");
            }

            if (movable.Count == 0)
            {
                return new LayoutResult(
                    false,
                    "这些窗口都以管理员权限运行，本工具权限不足，动不了它们",
                    0,
                    kind,
                    Unmovable: blocked);
            }

            foreach (var window in scoped)
            {
                log.Add($"  scope hwnd=0x{window.Handle.ToInt64():X} \"{window.Title}\" " +
                        $"min={window.MinWidth}x{window.MinHeight} at {window.Bounds.Left},{window.Bounds.Top} " +
                        $"{window.Bounds.Width}x{window.Bounds.Height}");
            }

            var cursorMonitor = NativeMethods.GetCursorMonitor();
            foreach (var diagnostic in WindowCollector.Diagnose())
            {
                if (diagnostic.Included || diagnostic.Reason is SkipReason.Invisible or SkipReason.Desktop)
                {
                    continue;
                }

                if (options.Scope == LayoutScope.CursorMonitor && diagnostic.Monitor != cursorMonitor)
                {
                    continue;
                }

                log.Add($"  excluded {diagnostic.Reason} \"{diagnostic.Title}\" class={diagnostic.ClassName}");
            }

            var victims = PickOldest(movable, minimizeOldest);
            var participants = movable.Where(w => !victims.Contains(w)).ToList();
            ApplyGridLimit(kind, participants, victims, options.GridLimit, log);

            if (participants.Count == 0)
            {
                return new LayoutResult(
                    false,
                    "窗口都以管理员权限运行，本工具权限不足，动不了它们",
                    0,
                    kind,
                    Unmovable: blocked);
            }

            foreach (var victim in victims)
            {
                log.Add($"  victim \"{victim.Title}\"");
            }

            var limits = movable.ToDictionary(w => w.Handle, w => new SizeLimits(w.MinWidth, w.MinHeight));
            var groups = GroupByMonitor(participants);
            var notes = new List<string>();
            var unfit = new List<WindowInfo>();
            var initial = ResolveInitialPlan(
                kind, groups, participants, movable, gap, options.ColumnsLimit, mainSlot,
                allowMainSwap, limits, notes, unfit, log, out var plan);
            if (initial != null)
            {
                return initial;
            }

            plan ??= new List<PlannedWindow>(); // ResolveInitialPlan 返回 null 时一定带回了计划

            // 只在还没有快照时记录：Ctrl+Alt+0 要回到"第一次按布局热键之前"的状态，
            // 之后连续切换布局不覆盖它，直到用户真的还原一次。
            if (_snapshot.Count == 0)
            {
                _snapshot = movable
                    .Select(w => SnapshotEntry.From(w, victims.Contains(w)))
                    .ToList();
            }

            foreach (var victim in victims)
            {
                NativeMethods.ShowWindow(victim.Handle, NativeMethods.SW_SHOWMINNOACTIVE);
            }

            var requested = PlaceAll(plan, log);

            // 系统会按窗口自己的最小尺寸强行撑大，实测一遍，被撑大的重排一次
            if (TryDetectClamping(requested, out var clamped))
            {
                foreach (var pair in clamped)
                {
                    if (!limits.TryGetValue(pair.Key, out var limit))
                    {
                        continue;
                    }

                    limits[pair.Key] = new SizeLimits(
                        Math.Max(limit.Width, pair.Value.Width),
                        Math.Max(limit.Height, pair.Value.Height));
                }

                var retryNotes = new List<string>();
                var retry = BuildPlan(
                    kind, groups, gap, limits, retryNotes, options.ColumnsLimit, mainSlot,
                    allowMainSwap: false, out _);
                if (retry != null && retry.Count > 0)
                {
                    log.Add("  retry after clamping");
                    requested = PlaceAll(retry, log);
                    plan = retry;
                    notes.Clear();
                    notes.AddRange(retryNotes);
                }
            }

            var parts = new List<string> { $"已排列 {plan.Count} 个窗口" };
            if (unfit.Count > 0)
            {
                parts.Add($"{unfit.Count} 个窗口放不下，保持原位");
            }

            if (victims.Count > 0)
            {
                parts.Add($"最小化 {victims.Count} 个最久未使用的窗口");
            }

            if (groups.Count > 1)
            {
                parts.Add($"{groups.Count} 台显示器");
            }

            parts.AddRange(notes);

            var skipSummary = BuildSkipSummary(
                options.Scope,
                victims.Select(v => v.Handle).ToHashSet());
            if (skipSummary != null)
            {
                parts.Add(skipSummary);
            }

            if (blocked.Count > 0)
            {
                var names = string.Join("、", blocked.Take(3).Select(w => w.Title));
                if (blocked.Count > 3)
                {
                    names += $" 等 {blocked.Count} 个";
                }

                parts.Add($"无法移动：{names}（管理员权限）");
            }

            var message = $"{LayoutNames.Display(kind)}：" + string.Join("，", parts);
            log.Add("  result: " + message);

            _lastPlan = plan
                .Where(p => requested.ContainsKey(p.Window.Handle))
                .Select(p => (p.Window.Handle, requested[p.Window.Handle]))
                .ToList();

            return new LayoutResult(true, message, plan.Count, kind, Unmovable: blocked);
        }
    }

    /// <summary>只算不动：返回每个窗口将被放到哪里（调试 / 测试用）。</summary>
    internal (List<PreviewItem> Items, string Note) Preview(LayoutKind kind, LayoutOptions options)
    {
        var scoped = GetWindows(options.Scope).Where(w => !w.CannotMove).ToList();
        if (scoped.Count == 0)
        {
            return (new List<PreviewItem>(), "没有窗口");
        }

        var gap = Math.Clamp(options.Gap, 0, 200);
        var limits = scoped.ToDictionary(w => w.Handle, w => new SizeLimits(w.MinWidth, w.MinHeight));
        var notes = new List<string>();
        var plan = BuildPlan(
            kind, GroupByMonitor(scoped.ToList()), gap, limits, notes, options.ColumnsLimit,
            preferredMain: default, allowMainSwap: false, out _);
        var planned = plan?.ToDictionary(p => p.Window.Handle, p => p.Rect);

        var items = new List<PreviewItem>();
        foreach (var window in scoped)
        {
            WindowRect rect = default;
            var placed = planned != null && planned.TryGetValue(window.Handle, out rect);
            items.Add(new PreviewItem(
                window.Handle,
                window.Title,
                window.MinWidth,
                window.MinHeight,
                GetArea(window.Monitor),
                placed ? rect : default,
                placed));
        }

        var note = string.Join("；", notes);
        if (plan == null)
        {
            var suggestion = FindMinimalMinimize(scoped, kind, gap, limits, options.ColumnsLimit);
            note = (note.Length > 0 ? note + "；" : string.Empty) +
                   $"放不下，建议最小化 {suggestion.Count} 个";
        }

        return (items, note);
    }

    /// <summary>
    /// 无头验证用：给定窗口快照 / 区域 / 间隙，只算矩形（不枚举真实窗口、不摆放）。
    /// 走的是和 <see cref="Apply"/> 同一条 <see cref="BuildPlan"/>，用来校验一次性布局
    /// 与工作区 <see cref="ZoneWorkspace"/> 的几何逐像素一致。
    /// </summary>
    internal static (List<WindowRect>? Rects, string Note) PlanRects(
        LayoutKind kind, IReadOnlyList<WindowInfo> windows, WindowRect area, int gap,
        int columnsLimit = 0, IntPtr preferredMain = default, bool allowMainSwap = false)
    {
        if (windows.Count == 0)
        {
            return (new List<WindowRect>(), string.Empty);
        }

        var limits = windows.ToDictionary(w => w.Handle, w => new SizeLimits(w.MinWidth, w.MinHeight));
        var ordered = PutForegroundFirst(windows.ToList());
        var notes = new List<string>();
        var plan = BuildPlan(
            kind,
            new List<WindowGroup> { new(IntPtr.Zero, ordered, area) },
            gap,
            limits,
            notes,
            columnsLimit,
            preferredMain,
            allowMainSwap,
            out _);

        if (plan == null)
        {
            return (null, string.Join("；", notes));
        }

        var byHandle = plan.ToDictionary(p => p.Window.Handle, p => p.Rect);
        var rects = new List<WindowRect>(windows.Count);
        foreach (var window in windows)
        {
            if (!byHandle.TryGetValue(window.Handle, out var rect))
            {
                return (null, string.Join("；", notes));
            }

            rects.Add(rect);
        }

        return (rects, string.Join("；", notes));
    }

    /// <summary>分区模式用：把当前窗口位置记下来，之后 Ctrl+Alt+0 可以整体还原。</summary>
    public void CaptureSnapshot(IReadOnlyCollection<WindowInfo> windows)
    {
        lock (_gate)
        {
            // 同 Apply：只在第一次进入分区模式时记录原始位置
            if (_snapshot.Count == 0)
            {
                _snapshot = windows.Select(w => SnapshotEntry.From(w, false)).ToList();
            }

            _lastPlan = new List<(IntPtr, WindowRect)>();
        }
    }

    /// <summary>按分区树算出来的格子摆放窗口（带阴影边距补偿）。</summary>
    public void PlaceRects(IEnumerable<(IntPtr Handle, WindowRect Cell)> cells)
    {
        lock (_gate)
        {
            var log = new List<string>();
            var requested = new Dictionary<IntPtr, WindowRect>();

            foreach (var (handle, cell) in cells)
            {
                requested[handle] = Place(handle, cell, log);
            }

            _lastPlan = requested.Select(p => (p.Key, p.Value)).ToList();

            // 分区模式也走同一条日志，排查"拖了分隔线但窗口没动"时看这里
            if (log.Count > 0)
            {
                log.Insert(0, $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} zones place");
                LayoutLog.Write(log);
            }
        }
    }

    /// <summary>显示器上的分区可用区域（含 <see cref="ZoneWorkspace.ZoneMargin"/> 的边框内缩）。</summary>
    public WindowRect GetMonitorArea(IntPtr monitor) => GetArea(monitor);

    public LayoutResult Restore()
    {
        lock (_gate)
        {
            if (_snapshot.Count == 0)
            {
                return LayoutResult.Fail("还没有可以还原的布局");
            }

            var restored = 0;
            foreach (var entry in _snapshot)
            {
                if (!NativeMethods.IsWindow(entry.Handle))
                {
                    continue;
                }

                if (entry.Minimized)
                {
                    // 把我们最小化的窗口取消最小化，但不激活
                    NativeMethods.ShowWindow(entry.Handle, NativeMethods.SW_SHOWNOACTIVATE);
                }

                if (entry.Maximized)
                {
                    NativeMethods.ShowWindow(entry.Handle, NativeMethods.SW_MAXIMIZE);
                    restored++;
                    continue;
                }

                if (NativeMethods.IsZoomed(entry.Handle))
                {
                    Unmaximize(entry.Handle);
                }

                NativeMethods.SetWindowPos(
                    entry.Handle,
                    IntPtr.Zero,
                    entry.Rect.Left,
                    entry.Rect.Top,
                    Math.Max(entry.Rect.Width, 40),
                    Math.Max(entry.Rect.Height, 40),
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
                KeepRounded(entry.Handle);
                restored++;
            }

            _snapshot = new List<SnapshotEntry>();
            _lastPlan = new List<(IntPtr, WindowRect)>();
            return new LayoutResult(true, $"已还原 {restored} 个窗口的位置", restored);
        }
    }

    /// <summary>
    /// 排完过一会儿再检查一次：有些应用（Chromium/Electron 系）会在收到尺寸变化后自己微调窗口，
    /// 导致和相邻窗口差几像素。这里把偏移超过 2px 的窗口重新放回格子。
    /// </summary>
    public int RealignDrifted()
    {
        lock (_gate)
        {
            if (_lastPlan.Count == 0)
            {
                return 0;
            }

            var log = new List<string> { $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} realign" };
            var count = 0;

            foreach (var (handle, requested) in _lastPlan)
            {
                if (!NativeMethods.IsWindow(handle) ||
                    !NativeMethods.GetWindowRect(handle, out var rect))
                {
                    continue;
                }

                if (Math.Abs(rect.Left - requested.Left) <= 2 &&
                    Math.Abs(rect.Top - requested.Top) <= 2 &&
                    Math.Abs(rect.Width - requested.Width) <= 2 &&
                    Math.Abs(rect.Height - requested.Height) <= 2)
                {
                    continue;
                }

                log.Add($"  drift {DescribeWindow(handle)} " +
                        $"want={requested.Left},{requested.Top} {requested.Width}x{requested.Height} " +
                        $"now={rect.Left},{rect.Top} {rect.Width}x{rect.Height} " +
                        $"frame={DescribeFrame(handle)}");

                SetBounds(handle, requested, log);
                count++;
            }

            _lastPlan = new List<(IntPtr, WindowRect)>();

            if (count > 0)
            {
                LayoutLog.Write(log);
            }

            return count;
        }
    }

    // ---------------- 窗口分组 ----------------

    private static List<WindowInfo> ScopeWindows(List<WindowInfo> windows, LayoutScope scope)
    {
        if (scope == LayoutScope.AllMonitors)
        {
            return windows;
        }

        var monitor = NativeMethods.GetCursorMonitor();
        var onMonitor = windows.FindAll(w => w.Monitor == monitor);
        return onMonitor.Count > 0 ? onMonitor : windows;
    }

    /// <summary>状态栏用：告诉用户还有哪些窗口没参与，以及为什么。</summary>
    private static string? BuildSkipSummary(LayoutScope scope, HashSet<IntPtr> handled)
    {
        var cursorMonitor = NativeMethods.GetCursorMonitor();
        var counts = new Dictionary<SkipReason, int>();

        foreach (var diagnostic in WindowCollector.Diagnose())
        {
            if (diagnostic.Included || handled.Contains(diagnostic.Handle))
            {
                continue;
            }

            if (scope == LayoutScope.CursorMonitor && diagnostic.Monitor != cursorMonitor)
            {
                continue;
            }

            switch (diagnostic.Reason)
            {
                case SkipReason.Minimized:
                case SkipReason.ToolWindow:
                case SkipReason.Cloaked:
                    counts[diagnostic.Reason] = counts.GetValueOrDefault(diagnostic.Reason) + 1;
                    break;
            }
        }

        if (counts.Count == 0)
        {
            return null;
        }

        var labels = new (SkipReason Reason, string Text)[]
        {
            (SkipReason.Minimized, "最小化"),
            (SkipReason.ToolWindow, "工具窗口"),
            (SkipReason.Cloaked, "其它虚拟桌面"),
        };

        var parts = labels
            .Where(l => counts.ContainsKey(l.Reason))
            .Select(l => $"{l.Text} {counts[l.Reason]}");

        return "未参与：" + string.Join("，", parts);
    }

    private static List<WindowGroup> GroupByMonitor(List<WindowInfo> windows) =>
        windows
            .GroupBy(w => w.Monitor)
            .Select(g => new WindowGroup(g.Key, g.ToList(), GetArea(g.Key)))
            .ToList();

    /// <summary>
    /// 一次性布局用的区域：与工作区共用 <see cref="ZoneWorkspace.InsetArea"/>（四边内缩），
    /// 保证同一个布局在“进工作区前 / 进工作区后”几何逐像素一致。
    /// </summary>
    private static WindowRect GetArea(IntPtr monitor)
    {
        var area = NativeMethods.GetWorkArea(monitor);
        return ZoneWorkspace.InsetArea(new WindowRect(area.Left, area.Top, area.Width, area.Height));
    }

    private static List<WindowInfo> PutForegroundFirst(List<WindowInfo> windows)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var ordered = new List<WindowInfo>(windows.Count);

        foreach (var window in windows)
        {
            if (window.Handle == foreground)
            {
                ordered.Add(window);
            }
        }

        foreach (var window in windows)
        {
            if (window.Handle != foreground)
            {
                ordered.Add(window);
            }
        }

        return ordered;
    }

    /// <summary>取 Z 序最底层（最久未使用）的若干个窗口。</summary>
    private static List<WindowInfo> PickOldest(IReadOnlyList<WindowInfo> scoped, int count)
    {
        if (count <= 0)
        {
            return new List<WindowInfo>();
        }

        count = Math.Min(count, Math.Max(0, scoped.Count - 1));
        return scoped.AsEnumerable().Reverse().Take(count).ToList();
    }

    /// <summary>
    /// 网格的格数上限：窗口比格子多时，把 Z 序末尾（最久没用）的那些挪进 victims，
    /// 由调用方最小化——不静默少排几个把窗口留在原地压着网格。
    /// </summary>
    private static void ApplyGridLimit(
        LayoutKind kind, List<WindowInfo> participants, List<WindowInfo> victims,
        int limit, List<string> log)
    {
        if (kind != LayoutKind.Grid || limit <= 0 || participants.Count <= limit)
        {
            return;
        }

        var extra = participants.Skip(limit).ToList();
        foreach (var window in extra)
        {
            participants.Remove(window);
        }

        victims.AddRange(extra);
        log.Add($"  grid limit {limit}: minimize {extra.Count} extra");
    }

    // ---------------- 生成布局 ----------------

    /// <returns>
    /// 放不下时返回 null；<paramref name="mainSwap"/> 非 null 表示需要用户同意把该窗口换到主窗。
    /// </returns>
    private static List<PlannedWindow>? BuildPlan(
        LayoutKind kind,
        List<WindowGroup> groups,
        int gap,
        Dictionary<IntPtr, SizeLimits> limits,
        List<string> notes,
        int columnsLimit,
        IntPtr preferredMain,
        bool allowMainSwap,
        out WindowInfo? mainSwap)
    {
        var plan = new List<PlannedWindow>();
        mainSwap = null;

        foreach (var group in groups)
        {
            var ordered = PutForegroundFirst(group.Windows);
            if (ordered.Count == 0)
            {
                continue;
            }

            var area = group.Area;
            List<WindowRect>? rects;
            string? note = null;

            if (kind == LayoutKind.Cascade)
            {
                rects = Cascade(ordered.Count, area);
            }
            else
            {
                rects = ZoneLayout(
                    kind, ordered, area, gap, limits, columnsLimit, preferredMain,
                    allowMainSwap, out var swap, out note);
                if (swap != null)
                {
                    mainSwap = swap;
                }
            }

            if (rects == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(note))
            {
                AddNote(notes, note);
            }

            for (var i = 0; i < ordered.Count && i < rects.Count; i++)
            {
                plan.Add(new PlannedWindow(ordered[i], rects[i]));
            }
        }

        return plan;
    }

    /// <summary>
    /// 初始规划 + “装不下”兜底：与 <see cref="ApplyCore"/> 走同一条路径，抽出来便于无头验证。
    /// 返回 null 表示可以继续应用（计划通过 <paramref name="plan"/> 带回）；
    /// 返回 <see cref="LayoutResult"/> 表示需要界面决策（换主窗 / 最小化 / 失败）。
    /// </summary>
    internal static LayoutResult? ResolveInitialPlan(
        LayoutKind kind,
        List<WindowGroup> groups,
        List<WindowInfo> participants,
        List<WindowInfo> movable,
        int gap,
        int columnsLimit,
        IntPtr mainSlot,
        bool allowMainSwap,
        Dictionary<IntPtr, SizeLimits> limits,
        List<string> notes,
        List<WindowInfo> unfit,
        List<string> log,
        out List<PlannedWindow>? plan)
    {
        plan = BuildPlan(
            kind, groups, gap, limits, notes, columnsLimit, mainSlot, allowMainSwap, out var mainSwap);

        if (plan == null && mainSwap != null && mainSlot == IntPtr.Zero && allowMainSwap)
        {
            log.Add($"  needs main swap \"{mainSwap.Title}\" " +
                    $"min={mainSwap.MinWidth}x{mainSwap.MinHeight}");
            return LayoutResult.ConfirmMain(kind, mainSwap);
        }

        if (plan != null)
        {
            return null;
        }

        return ResolvePlanFallback(
            kind, groups, participants, movable, gap, columnsLimit, mainSlot,
            limits, unfit, log, out plan);
    }

    /// <summary>
    /// ApplyCore 的“装不下”兜底，抽出来便于无头验证：
    /// 1) 网格按 docs/GRID.md 先问“最小化最久未使用的 N 个后重排”；
    /// 2) 其它布局沿用“前台优先少排几个、放不下的保持原位”；
    /// 3) 连一个窗口都排不下时提示改用层叠。
    /// 成功少排时通过 <paramref name="plan"/> 返回计划并返回 null；
    /// 需要界面决策时返回对应的 <see cref="LayoutResult"/>。
    /// </summary>
    internal static LayoutResult? ResolvePlanFallback(
        LayoutKind kind,
        List<WindowGroup> groups,
        List<WindowInfo> participants,
        List<WindowInfo> movable,
        int gap,
        int columnsLimit,
        IntPtr mainSlot,
        Dictionary<IntPtr, SizeLimits> limits,
        List<WindowInfo> unfit,
        List<string> log,
        out List<PlannedWindow>? plan)
    {
        plan = null;

        // 网格且多个窗口都放不下：先问“最小化 N 个后重排”（docs/GRID.md），
        // 不静默把放不下的窗口留在原位。其它布局保持“前台优先少排几个”的约定。
        if (kind == LayoutKind.Grid)
        {
            var suggestion = FindMinimalMinimize(movable, kind, gap, limits, columnsLimit);
            if (suggestion.Count > 0)
            {
                log.Add($"  suggestion={suggestion.Count}");
                return LayoutResult.Choice(kind, suggestion);
            }
        }

        // 前台优先：从 Z 序最底层开始少排几个，放不下的窗口留在原位。
        // 只剩前台一个窗口时就是把它铺满——不应该再弹"放不下"。
        var candidates = new List<WindowInfo>(participants);
        while (candidates.Count > 1)
        {
            unfit.Insert(0, candidates[^1]);
            candidates.RemoveAt(candidates.Count - 1);

            var attempt = BuildPlan(
                kind, GroupByMonitor(candidates), gap, limits,
                new List<string>(), columnsLimit, mainSlot, allowMainSwap: false, out _);
            if (attempt != null)
            {
                plan = attempt;
                break;
            }
        }

        log.Add($"  plan=null unfit={unfit.Count}");

        if (plan != null)
        {
            return null;
        }

        var fallbackSuggestion = kind == LayoutKind.Cascade
            ? new List<WindowInfo>()
            : FindMinimalMinimize(movable, kind, gap, limits, columnsLimit);

        log.Add($"  suggestion={fallbackSuggestion.Count}");

        return fallbackSuggestion.Count > 0
            ? LayoutResult.Choice(kind, fallbackSuggestion)
            : LayoutResult.Fail(
                $"{LayoutNames.Display(kind)}：窗口最小尺寸放不下，可以改用层叠（Ctrl+Alt+1）");
    }

    /// <summary>找出最少要最小化几个（从 Z 序最底层开始）才能排得下。</summary>
    private static List<WindowInfo> FindMinimalMinimize(
        IReadOnlyList<WindowInfo> scoped,
        LayoutKind kind,
        int gap,
        Dictionary<IntPtr, SizeLimits> limits,
        int columnsLimit = 0)
    {
        for (var count = 1; count < scoped.Count; count++)
        {
            var victims = PickOldest(scoped, count);
            var rest = scoped.Where(w => !victims.Contains(w)).ToList();
            if (rest.Count == 0)
            {
                break;
            }

            var notes = new List<string>();
            if (BuildPlan(
                    kind, GroupByMonitor(rest), gap, limits, notes, columnsLimit,
                    preferredMain: default, allowMainSwap: false, out _) != null)
            {
                return victims;
            }
        }

        return new List<WindowInfo>();
    }

    private static void AddNote(List<string> notes, string note)
    {
        if (!notes.Contains(note))
        {
            notes.Add(note);
        }
    }

    private static List<WindowRect> Cascade(int count, WindowRect area)
    {
        var result = new List<WindowRect>(count);
        if (count == 1)
        {
            result.Add(area);
            return result;
        }

        var width = Math.Clamp(area.Width * 3 / 4, 240, area.Width);
        var height = Math.Clamp(area.Height * 3 / 4, 180, area.Height);

        var availX = area.Width - width;
        var availY = area.Height - height;
        var step = Math.Min(30, Math.Min(availX, availY) / (count - 1));
        step = Math.Max(step, 0);

        for (var i = 0; i < count; i++)
        {
            result.Add(new WindowRect(area.Left + i * step, area.Top + i * step, width, height));
        }

        return result;
    }

    /// <summary>
    /// 一次性布局的矩形来源：与工作区共用 <see cref="ZoneWorkspace"/>（切分树 + 最小尺寸拟合）。
    /// 左右并排走 <see cref="ZoneWorkspace.PlanColumns"/>（每行列数 + 舒适宽度换行），
    /// 网格走 <see cref="ZoneWorkspace.PlanGrid"/>，其余走 <see cref="ZoneWorkspace.Create"/>。
    /// 任一格（包括只剩一个窗口时）小于窗口声明的最小尺寸就返回 null，交给外层
    /// “最小化 / 换主窗 / 层叠”提示逻辑。
    /// <paramref name="preferredMain"/> 只用于网格的“主窗槽位”，其它布局用前台窗口当主窗。
    /// <paramref name="allowMainSwap"/> 为 false 时只接受“自然主窗”的网格解；
    /// 否则当自然主窗排不下、必须换主窗时，<paramref name="mainSwap"/> 会带回那个窗口。
    /// </summary>
    private static List<WindowRect>? ZoneLayout(
        LayoutKind kind, List<WindowInfo> windows, WindowRect area, int gap,
        Dictionary<IntPtr, SizeLimits> limits, int columnsLimit, IntPtr preferredMain,
        bool allowMainSwap, out WindowInfo? mainSwap, out string? note)
    {
        note = null;
        mainSwap = null;

        // FitToMinimumSizes 读的是 WindowInfo 上的最小尺寸；重试时 limits 可能被系统实际撑大，
        // 这里用快照副本把两者合并，保证一次性布局和工作区按同一份最小尺寸拟合。
        var effective = windows.Select(w => WithLimits(w, limits[w.Handle])).ToList();
        var ordered = effective;
        var pinned = kind == LayoutKind.Grid && preferredMain != IntPtr.Zero
            ? effective.FirstOrDefault(w => w.Handle == preferredMain)
            : null;

        if (pinned != null)
        {
            ordered = new List<WindowInfo>(effective.Count) { pinned };
            ordered.AddRange(effective.Where(w => w.Handle != preferredMain));
            note = $"「{pinned.Title}」已放到主窗";
        }

        ZoneWorkspace workspace;
        if (kind == LayoutKind.Grid)
        {
            var gridPlan = ZoneWorkspace.PlanGrid(
                ordered, area, gap, keepMain: pinned != null || !allowMainSwap);
            if (gridPlan is { MainSwapRequested: { } suggested })
            {
                // 自然主窗排不下；有解但必须先问用户是否允许换主窗，不能直接应用
                mainSwap = suggested;
                return null;
            }

            workspace = gridPlan?.Workspace
                        ?? ZoneWorkspace.Create(LayoutKind.Grid, ordered, area, gap);
        }
        else if (kind == LayoutKind.Columns)
        {
            workspace = ZoneWorkspace.PlanColumns(ordered, area, gap, columnsLimit);
        }
        else
        {
            workspace = ZoneWorkspace.Create(kind, ordered, area, gap);
        }

        workspace.FitToMinimumSizes();

        var byHandle = workspace.Compute().ToDictionary(p => p.Window.Handle, p => p.Rect);
        var rects = new List<WindowRect>(windows.Count);
        foreach (var window in windows)
        {
            if (!byHandle.TryGetValue(window.Handle, out var rect) ||
                rect.Width < Math.Max(limits[window.Handle].Width, MinWidth) ||
                rect.Height < Math.Max(limits[window.Handle].Height, MinHeight))
            {
                return null;
            }

            rects.Add(rect);
        }

        return rects;
    }

    /// <summary>把系统实际的最小尺寸合并进窗口快照，供 <see cref="ZoneWorkspace"/> 拟合。</summary>
    private static WindowInfo WithLimits(WindowInfo window, SizeLimits limits) => new()
    {
        Handle = window.Handle,
        Title = window.Title,
        ClassName = window.ClassName,
        ProcessId = window.ProcessId,
        Bounds = window.Bounds,
        IsMaximized = window.IsMaximized,
        Monitor = window.Monitor,
        MinWidth = Math.Max(window.MinWidth, limits.Width),
        MinHeight = Math.Max(window.MinHeight, limits.Height),
        Dpi = window.Dpi,
        CannotMove = window.CannotMove,
    };

    // ---------------- 执行 ----------------

    private static Dictionary<IntPtr, WindowRect> PlaceAll(List<PlannedWindow> plan, List<string> log)
    {
        var requested = new Dictionary<IntPtr, WindowRect>();

        foreach (var item in plan)
        {
            requested[item.Window.Handle] = Place(item.Window.Handle, item.Rect, log);
        }

        return requested;
    }

    /// <summary>把窗口"可见边框"放到 cell 上：普通窗口外面有约 9px 的透明阴影边距，需要补回来。</summary>
    private static WindowRect Place(IntPtr hWnd, WindowRect cell, List<string> log)
    {
        if (NativeMethods.IsZoomed(hWnd))
        {
            Unmaximize(hWnd);
        }

        var margins = GetFrameMargins(hWnd);
        var target = new WindowRect(
            cell.Left - margins.Left,
            cell.Top - margins.Top,
            Math.Max(cell.Width + margins.Left + margins.Right, 40),
            Math.Max(cell.Height + margins.Top + margins.Bottom, 40));

        SetBounds(hWnd, target, log, cell);
        return target;
    }

    private static void SetBounds(IntPtr hWnd, WindowRect target, List<string> log, WindowRect? cell = null)
    {
        if (NativeMethods.IsZoomed(hWnd))
        {
            Unmaximize(hWnd);
        }

        var ok = NativeMethods.SetWindowPos(
            hWnd,
            IntPtr.Zero,
            target.Left,
            target.Top,
            Math.Max(target.Width, 40),
            Math.Max(target.Height, 40),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);

        var actual = "?";
        if (NativeMethods.GetWindowRect(hWnd, out var after))
        {
            actual = $"{after.Left},{after.Top} {after.Width}x{after.Height}";
        }

        log.Add($"  place {DescribeWindow(hWnd)} " +
                (cell is { } c ? $"cell={c.Left},{c.Top} {c.Width}x{c.Height} " : string.Empty) +
                $"rect={target.Left},{target.Top} {target.Width}x{target.Height} ok={ok} now={actual} " +
                $"frame={DescribeFrame(hWnd)}");

        KeepRounded(hWnd);
    }

    /// <summary>窗口矩形和 DWM 可见边框之间的透明阴影边距。</summary>
    private static WindowMargins GetFrameMargins(IntPtr hWnd)
    {
        if (!NativeMethods.GetWindowRect(hWnd, out var rect) ||
            rect.Width <= 0 || rect.Height <= 0 ||
            NativeMethods.DwmGetWindowAttribute(
                hWnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frame,
                Marshal.SizeOf<NativeMethods.RECT>()) != 0 ||
            frame.Width <= 0 || frame.Height <= 0)
        {
            return default;
        }

        return new WindowMargins(
            Sanitize(frame.Left - rect.Left),
            Sanitize(frame.Top - rect.Top),
            Sanitize(rect.Right - frame.Right),
            Sanitize(rect.Bottom - frame.Bottom));

        static int Sanitize(int value) => Math.Clamp(value, 0, 24);
    }

    /// <summary>DWM 认为的可见边框（不含阴影的透明边距）。</summary>
    private static string DescribeFrame(IntPtr hWnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                hWnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frame,
                Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            return "?";
        }

        return $"{frame.Left},{frame.Top} {frame.Width}x{frame.Height}";
    }

    private static string DescribeWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return "(none)";
        }

        var title = new System.Text.StringBuilder(256);
        NativeMethods.GetWindowText(hWnd, title, title.Capacity);
        return $"0x{hWnd.ToInt64():X} \"{title}\"";
    }

    /// <summary>
    /// 取消最大化。用 SetWindowPlacement 而不是 ShowWindow：不抢焦点，
    /// 而且会让 DWM 正常退出"最大化"状态——否则 Win11 会把方角一直留着。
    /// </summary>
    private static void Unmaximize(IntPtr hWnd)
    {
        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
        };

        if (NativeMethods.GetWindowPlacement(hWnd, ref placement))
        {
            placement.showCmd = NativeMethods.SW_SHOWNOACTIVATE;
            if (NativeMethods.SetWindowPlacement(hWnd, ref placement))
            {
                return;
            }
        }

        NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWNOACTIVATE);
    }

    /// <summary>把窗口的 DWM 圆角重新打开；应用自己明确要求方角的（DONOTROUND）不碰。</summary>
    private static void KeepRounded(IntPtr hWnd)
    {
        var preference = 0;
        var known = NativeMethods.DwmGetWindowAttribute(
            hWnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)) == 0;

        if (known && preference == NativeMethods.DWMWCP_DONOTROUND)
        {
            return;
        }

        var round = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            hWnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    /// <summary>窗口被系统按最小尺寸撑大了吗？撑大的说明我们估小了。</summary>
    private static bool TryDetectClamping(
        Dictionary<IntPtr, WindowRect> requested, out Dictionary<IntPtr, WindowRect> clamped)
    {
        clamped = new Dictionary<IntPtr, WindowRect>();

        foreach (var pair in requested)
        {
            if (!NativeMethods.IsWindow(pair.Key) ||
                !NativeMethods.GetWindowRect(pair.Key, out var rect))
            {
                continue;
            }

            if (rect.Width > pair.Value.Width + 2 || rect.Height > pair.Value.Height + 2)
            {
                clamped[pair.Key] = new WindowRect(rect.Left, rect.Top, rect.Width, rect.Height);
            }
        }

        return clamped.Count > 0;
    }

    // ---------------- 内部类型 ----------------

    // 供无头验证（InternalsVisibleTo wltest）构造规划输入，不暴露给 UI。
    internal readonly record struct SizeLimits(int Width, int Height);

    private readonly record struct WindowMargins(int Left, int Top, int Right, int Bottom);

    internal sealed record PlannedWindow(WindowInfo Window, WindowRect Rect);

    internal sealed record WindowGroup(IntPtr Monitor, List<WindowInfo> Windows, WindowRect Area);

    private sealed class SnapshotEntry
    {
        public IntPtr Handle { get; init; }
        public WindowRect Rect { get; init; }
        public bool Maximized { get; init; }
        public bool Minimized { get; init; }

        public static SnapshotEntry From(WindowInfo window, bool minimized) => new()
        {
            Handle = window.Handle,
            Rect = window.Bounds,
            Maximized = window.IsMaximized,
            Minimized = minimized,
        };
    }
}

/// <summary>预演结果：某个窗口将被放到哪里。</summary>
internal sealed record PreviewItem(
    IntPtr Handle,
    string Title,
    int MinWidth,
    int MinHeight,
    WindowRect Area,
    WindowRect Rect,
    bool Placed);
