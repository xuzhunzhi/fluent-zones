using System.Runtime.InteropServices;

namespace FluentZones.Core;

/// <summary>
/// 布局算法：先读每个窗口自己声明的最小尺寸（WM_GETMINMAXINFO），
/// 每个窗口先拿够最小值、剩余空间平均分；一行放不下自动分行。
/// 实在放不下时不擅自降级，而是返回"建议最小化哪些窗口"，交给界面询问用户。
/// </summary>
public sealed class LayoutService
{
    // 绝对下限，防止 0 尺寸的自绘窗口
    private const int MinWidth = 120;
    private const int MinHeight = 90;

    // 舒适尺寸（有效像素）：比"能不能放下"更严格，避免切出一排细条
    private const double ComfortWidth = 400;
    private const double ComfortHeight = 320;

    private readonly object _gate = new();
    private List<SnapshotEntry> _snapshot = new();
    private List<(WindowInfo Window, WindowRect Requested)> _lastPlan = new();

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
        int minimizeOldest = 0)
    {
        var log = new List<string>
        {
            $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} kind={kind} gap={options.Gap} scope={options.Scope} minimize={minimizeOldest}",
            "foreground: " + DescribeWindow(NativeMethods.GetForegroundWindow()),
        };

        try
        {
            return ApplyCore(kind, options, restrictTo, minimizeOldest, log);
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
            var plan = BuildPlan(kind, groups, gap, limits, notes);

            if (plan == null)
            {
                var suggestion = kind == LayoutKind.Cascade
                    ? new List<WindowInfo>()
                    : FindMinimalMinimize(movable, kind, gap, limits);

                log.Add($"  plan=null suggestion={suggestion.Count}");

                if (suggestion.Count > 0)
                {
                    return LayoutResult.Choice(kind, suggestion);
                }

                return LayoutResult.Fail(
                    $"{LayoutNames.Display(kind)}：窗口最小尺寸放不下，可以改用层叠（Ctrl+Alt+1）");
            }

            // 先记录旧位置（包含将被最小化的窗口），再动手
            _snapshot = movable
                .Select(w => SnapshotEntry.From(w, victims.Contains(w)))
                .ToList();

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
                var retry = BuildPlan(kind, groups, gap, limits, retryNotes);
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
                .Select(p => (p.Window, requested[p.Window.Handle]))
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
        var plan = BuildPlan(kind, GroupByMonitor(scoped.ToList()), gap, limits, notes);
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
            var suggestion = FindMinimalMinimize(scoped, kind, gap, limits);
            note = (note.Length > 0 ? note + "；" : string.Empty) +
                   $"放不下，建议最小化 {suggestion.Count} 个";
        }

        return (items, note);
    }

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
            _lastPlan = new List<(WindowInfo, WindowRect)>();
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

            foreach (var (window, requested) in _lastPlan)
            {
                if (!NativeMethods.IsWindow(window.Handle) ||
                    !NativeMethods.GetWindowRect(window.Handle, out var rect))
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

                log.Add($"  drift {DescribeWindow(window.Handle)} " +
                        $"want={requested.Left},{requested.Top} {requested.Width}x{requested.Height} " +
                        $"now={rect.Left},{rect.Top} {rect.Width}x{rect.Height} " +
                        $"frame={DescribeFrame(window.Handle)}");

                SetBounds(window.Handle, requested, log);
                count++;
            }

            _lastPlan = new List<(WindowInfo, WindowRect)>();

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

    private static WindowRect GetArea(IntPtr monitor)
    {
        var area = NativeMethods.GetWorkArea(monitor);
        return new WindowRect(area.Left, area.Top, area.Width, area.Height);
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

    // ---------------- 生成布局 ----------------

    /// <returns>放不下时返回 null。</returns>
    private static List<PlannedWindow>? BuildPlan(
        LayoutKind kind,
        List<WindowGroup> groups,
        int gap,
        Dictionary<IntPtr, SizeLimits> limits,
        List<string> notes)
    {
        var plan = new List<PlannedWindow>();

        foreach (var group in groups)
        {
            var ordered = PutForegroundFirst(group.Windows);
            if (ordered.Count == 0)
            {
                continue;
            }

            var area = group.Area;
            var floors = GetFloors(area, ordered);
            List<WindowRect>? rects;
            string? note = null;

            switch (kind)
            {
                case LayoutKind.Cascade:
                    rects = Cascade(ordered.Count, area);
                    break;
                case LayoutKind.Columns:
                    rects = Columns(ordered, area, gap, limits, floors, out note);
                    break;
                case LayoutKind.Rows:
                    rects = Rows(ordered, area, gap, limits, floors, out note);
                    break;
                case LayoutKind.Grid:
                    rects = Grid(ordered, area, gap, limits, floors);
                    break;
                case LayoutKind.MainLeft:
                    rects = MainLeft(ordered, area, gap, limits, floors);
                    break;
                default:
                    rects = null;
                    break;
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

    /// <summary>找出最少要最小化几个（从 Z 序最底层开始）才能排得下。</summary>
    private static List<WindowInfo> FindMinimalMinimize(
        IReadOnlyList<WindowInfo> scoped,
        LayoutKind kind,
        int gap,
        Dictionary<IntPtr, SizeLimits> limits)
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
            if (BuildPlan(kind, GroupByMonitor(rest), gap, limits, notes) != null)
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

    /// <summary>把"舒适尺寸"换算成当前显示器物理像素，并限制不超过工作区一半。</summary>
    private static Floors GetFloors(WindowRect area, List<WindowInfo> windows)
    {
        var dpi = windows.Count == 0 ? 96 : windows.Max(w => w.Dpi);
        if (dpi < 96)
        {
            dpi = 96;
        }

        var scale = dpi / 96.0;
        var width = (int)Math.Round(ComfortWidth * scale);
        width = Math.Min(width, Math.Max(MinWidth, area.Width / 2));

        var height = (int)Math.Round(ComfortHeight * scale);
        height = Math.Min(height, Math.Max(MinHeight, area.Height / 2));

        return new Floors(Math.Max(width, MinWidth), Math.Max(height, MinHeight));
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

    private static List<WindowRect>? Columns(
        List<WindowInfo> windows, WindowRect area, int gap,
        Dictionary<IntPtr, SizeLimits> limits, Floors floors, out string? note)
    {
        note = null;
        if (windows.Count == 1)
        {
            return new List<WindowRect> { area };
        }

        var widths = windows.Select(w => limits[w.Handle].Width).ToList();
        var perRow = LayoutMath.MaxPerCount(widths, area.Width, gap, floors.Width);
        var slices = Slice(windows, perRow);

        var rowMins = slices.Select(slice => slice.Max(w => limits[w.Handle].Height)).ToList();
        var rowHeights = LayoutMath.Allocate(area.Height, gap, rowMins, floors.Height);
        if (rowHeights == null)
        {
            return null;
        }

        var result = new List<WindowRect>(windows.Count);
        var y = area.Top;

        for (var row = 0; row < slices.Count; row++)
        {
            var slice = slices[row];
            var sizes = LayoutMath.Allocate(
                area.Width, gap, slice.Select(w => limits[w.Handle].Width).ToList(), floors.Width);
            if (sizes == null)
            {
                return null;
            }

            var x = area.Left;
            for (var i = 0; i < slice.Count; i++)
            {
                result.Add(new WindowRect(x, y, sizes[i], rowHeights[row]));
                x += sizes[i] + gap;
            }

            y += rowHeights[row] + gap;
        }

        if (slices.Count > 1)
        {
            note = $"分 {slices.Count} 行";
        }

        return result;
    }

    private static List<WindowRect>? Rows(
        List<WindowInfo> windows, WindowRect area, int gap,
        Dictionary<IntPtr, SizeLimits> limits, Floors floors, out string? note)
    {
        note = null;
        if (windows.Count == 1)
        {
            return new List<WindowRect> { area };
        }

        var heights = windows.Select(w => limits[w.Handle].Height).ToList();
        var perColumn = LayoutMath.MaxPerCount(heights, area.Height, gap, floors.Height);
        var slices = Slice(windows, perColumn);

        var columnMins = slices.Select(slice => slice.Max(w => limits[w.Handle].Width)).ToList();
        var columnWidths = LayoutMath.Allocate(area.Width, gap, columnMins, floors.Width);
        if (columnWidths == null)
        {
            return null;
        }

        var result = new List<WindowRect>(windows.Count);
        var x = area.Left;

        for (var column = 0; column < slices.Count; column++)
        {
            var slice = slices[column];
            var sizes = LayoutMath.Allocate(
                area.Height, gap, slice.Select(w => limits[w.Handle].Height).ToList(), floors.Height);
            if (sizes == null)
            {
                return null;
            }

            var y = area.Top;
            for (var i = 0; i < slice.Count; i++)
            {
                result.Add(new WindowRect(x, y, columnWidths[column], sizes[i]));
                y += sizes[i] + gap;
            }

            x += columnWidths[column] + gap;
        }

        if (slices.Count > 1)
        {
            note = $"分 {slices.Count} 列";
        }

        return result;
    }

    private static List<WindowRect>? Grid(
        List<WindowInfo> windows, WindowRect area, int gap,
        Dictionary<IntPtr, SizeLimits> limits, Floors floors)
    {
        if (windows.Count == 1)
        {
            return new List<WindowRect> { area };
        }

        // 列数从多到少试，第一种能装下所有最小尺寸的胜出
        var maxColumns = Math.Min(windows.Count, 4);
        for (var columns = maxColumns; columns >= 1; columns--)
        {
            var slices = Slice(windows, columns);

            var rowMins = slices.Select(slice => slice.Max(w => limits[w.Handle].Height)).ToList();
            var rowHeights = LayoutMath.Allocate(area.Height, gap, rowMins, floors.Height);
            if (rowHeights == null)
            {
                continue;
            }

            var result = new List<WindowRect>(windows.Count);
            var y = area.Top;
            var ok = true;

            for (var row = 0; row < slices.Count; row++)
            {
                var slice = slices[row];
                var sizes = LayoutMath.Allocate(
                    area.Width, gap, slice.Select(w => limits[w.Handle].Width).ToList(), floors.Width);
                if (sizes == null)
                {
                    ok = false;
                    break;
                }

                var x = area.Left;
                for (var i = 0; i < slice.Count; i++)
                {
                    result.Add(new WindowRect(x, y, sizes[i], rowHeights[row]));
                    x += sizes[i] + gap;
                }

                y += rowHeights[row] + gap;
            }

            if (ok)
            {
                return result;
            }
        }

        return null;
    }

    private static List<WindowRect>? MainLeft(
        List<WindowInfo> windows, WindowRect area, int gap,
        Dictionary<IntPtr, SizeLimits> limits, Floors floors)
    {
        if (windows.Count == 1)
        {
            return new List<WindowRect> { area };
        }

        var left = windows[0];
        var rest = windows.Skip(1).ToList();
        var leftMin = Math.Max(limits[left.Handle].Width, floors.Width);
        var restWidest = rest.Max(w => limits[w.Handle].Width);

        var leftWidth = Math.Max(leftMin, (area.Width - gap) * 55 / 100);
        if (area.Width - gap - leftWidth < restWidest)
        {
            leftWidth = area.Width - gap - restWidest;
        }

        if (leftWidth < leftMin || area.Width - gap - leftWidth < floors.Width)
        {
            return null;
        }

        var heights = LayoutMath.Allocate(
            area.Height, gap, rest.Select(w => limits[w.Handle].Height).ToList(), floors.Height);
        if (heights == null)
        {
            return null;
        }

        var result = new List<WindowRect>(windows.Count)
        {
            new(area.Left, area.Top, leftWidth, area.Height),
        };

        var x = area.Left + leftWidth + gap;
        var width = area.Width - leftWidth - gap;
        var y = area.Top;

        for (var i = 0; i < rest.Count; i++)
        {
            result.Add(new WindowRect(x, y, width, heights[i]));
            y += heights[i] + gap;
        }

        return result;
    }

    private static List<List<WindowInfo>> Slice(List<WindowInfo> windows, int perSlice)
    {
        var slices = new List<List<WindowInfo>>();
        for (var i = 0; i < windows.Count; i += perSlice)
        {
            slices.Add(windows.GetRange(i, Math.Min(perSlice, windows.Count - i)));
        }

        return slices;
    }

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

    private readonly record struct SizeLimits(int Width, int Height);

    private readonly record struct Floors(int Width, int Height);

    private readonly record struct WindowMargins(int Left, int Top, int Right, int Bottom);

    private sealed record PlannedWindow(WindowInfo Window, WindowRect Rect);

    private sealed record WindowGroup(IntPtr Monitor, List<WindowInfo> Windows, WindowRect Area);

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
