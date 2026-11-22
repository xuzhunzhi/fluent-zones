namespace FluentZones.Core;

/// <summary>状态栏消息级别（Core 不认识 WinUI 的颜色/图标，由界面负责映射）。</summary>
public enum ZoneStatusLevel
{
    Info,
    Success,
    Warn,
    Error,
}

/// <summary>
/// 分区工作区的状态机：拥有当前 <see cref="ZoneWorkspace"/>（切分树）、参与的窗口句柄、
/// 显示器、布局类型，以及拖拽钩子、窗口事件监听和「去掉/恢复窗口缩放边框并持久化」的逻辑。
///
/// 它不引用 WinUI：枚举/摆放窗口、弹对话框、更新状态栏都通过构造时注入的委托与事件回到
/// MainWindow（UI 只做胶水）。进入 / 退出由 UI 调 <see cref="Enter"/> / <see cref="Exit"/>。
/// 行为与原先写在 MainWindow 里的实现保持一致。
/// </summary>
public sealed class ZoneSession : IDisposable
{
    private readonly Func<IReadOnlyList<WindowInfo>> _collectWindows;
    private readonly Func<IntPtr, WindowRect> _getArea;
    private readonly Func<int> _getGap;
    private readonly Func<int> _getColumnsLimit;
    private readonly Func<IReadOnlyList<string>>? _getWhitelist;
    private readonly Action<Action>? _postToUi;

    private readonly HashSet<IntPtr> _windowHandles = new();
    private readonly HashSet<IntPtr> _bordered = new();
    private readonly Dictionary<IntPtr, int> _styles = new();
    private readonly List<IntPtr> _lastOrder = new();
    private readonly List<(IntPtr Handle, bool WasTopmost)> _whitelistTopmost = new();
    private readonly HashSet<IntPtr> _initialWhitelistHandles = new();
    private readonly HashSet<uint> _fixedAskedProcesses = new();
    private readonly HashSet<uint> _fixedSkippedProcesses = new();
    private readonly HashSet<IntPtr> _unresizableHandles = new();
    private readonly Dictionary<IntPtr, int> _mismatchCounts = new();
    private readonly Dictionary<IntPtr, (int Width, int Height)> _aspectLocked = new();
    private readonly Dictionary<IntPtr, (int Width, int Height)> _lastActualSizes = new();
    private readonly Dictionary<IntPtr, int> _aspectCounts = new();
    private readonly ZonePlaceholderManager _placeholders = new();
    private readonly HashSet<IntPtr> _declinedNewcomers = new();
    private readonly HashSet<IntPtr> _pendingShow = new();

    // 最小化/最大化是“暂时离开槽位”，与关闭/移出不同：最小化会重排其它窗口，
    // 最大化保留槽位（还原后回到原位），两者都不应被 LOCATIONCHANGE 拉回。
    private readonly HashSet<IntPtr> _minimized = new();
    private readonly HashSet<IntPtr> _maximized = new();

    // 被用户拖到其它显示器的窗口：拖回来时允许重新入槽。
    private readonly HashSet<IntPtr> _detached = new();
    private bool _minimizeSyncQueued;

    private ZoneDragHook? _dragHook;
    private ZoneTitleDragHook? _titleHook;
    private WinEventWatcher? _watcher;
    private bool _syncQueued;
    private bool _candidateSyncQueued;
    private bool _swapInProgress;
    private bool _whitelistIgnoredNotified;
    private bool _fixedSizeNotified;
    private int _lastCrossMonitorCount;
    private DateTime _lastCrossMonitorNotice;

    /// <summary>会话代次：Enter / Exit 时递增，异步对话框返回后据此放弃过期操作。</summary>
    private int _sessionGeneration;

    /// <param name="collectWindows">UI 用 LayoutService 枚举窗口（每次重排前重新取）。</param>
    /// <param name="getArea">UI 给出工作区可用区域（含画边框的边距）。</param>
    /// <param name="getGap">UI 给出当前窗口间隙（来自设置，与一次性布局共用）。</param>
    /// <param name="getColumnsLimit">
    /// UI 给出「左右并排」每行列数（3 / 4，来自设置）；不传按 0（不设上限）。
    /// </param>
    /// <param name="postToUi">把拖拽钩子线程的回调送回 UI 线程；不传就直接执行。</param>
    public ZoneSession(
        Func<IReadOnlyList<WindowInfo>> collectWindows,
        Func<IntPtr, WindowRect> getArea,
        Func<int> getGap,
        Func<int>? getColumnsLimit = null,
        Action<Action>? postToUi = null,
        Func<IReadOnlyList<string>>? getWhitelist = null)
    {
        _collectWindows = collectWindows;
        _getArea = getArea;
        _getGap = getGap;
        _getColumnsLimit = getColumnsLimit ?? (() => 0);
        _postToUi = postToUi;
        _getWhitelist = getWhitelist;
    }

    /// <summary>当前工作区；null = 不在工作区里。</summary>
    public ZoneWorkspace? Workspace { get; private set; }

    /// <summary>当前布局类型（进入时确定，切换布局时重建）。</summary>
    public LayoutKind Kind { get; private set; }

    /// <summary>工作区所在显示器。</summary>
    public IntPtr Monitor { get; private set; }

    /// <summary>标题栏拖拽交换进行中；期间暂停 LOCATIONCHANGE 拉回与窗口事件重排。</summary>
    public bool IsSwapInProgress => _swapInProgress;

    /// <summary>上一次参与分区的窗口句柄（窗口事件靠它判断谁是新来的）。</summary>
    public IReadOnlyCollection<IntPtr> WindowHandles => _windowHandles;

    public bool IsActive => Workspace != null;

    /// <summary>算好的槽位：UI 调 LayoutService.PlaceRects 摆窗口。</summary>
    public event Action<IReadOnlyList<ZonePlacement>>? PlaceRequested;

    /// <summary>状态栏消息。</summary>
    public event Action<string, ZoneStatusLevel>? StatusRequested;

    /// <summary>是否真正创建占位窗口（UI 开启；无头测试保持 false）。</summary>
    public bool PlaceholdersEnabled
    {
        get => _placeholders.Enabled;
        set => _placeholders.Enabled = value;
    }

    /// <summary>当前占位窗口句柄（退出后应为空）。</summary>
    public IReadOnlyList<IntPtr> PlaceholderHandles => _placeholders.Handles;

    /// <summary>
    /// 工作区放不下新窗口：UI 弹「最小化 N 个 / 先不加入」的对话框。
    /// 返回 true 表示最小化这些窗口后继续加入。
    /// </summary>
    public Func<int, IReadOnlyList<WindowInfo>, Task<bool>>? OverflowRequested { get; set; }

    /// <summary>
    /// 网格只有把该窗口换到主窗才排得下：UI 弹窗请求许可。
    /// 返回 true 才允许换主窗；false / 未设置 = 保持自然主窗，按放不下处理。
    /// </summary>
    public Func<WindowInfo, Task<bool>>? MainSwapRequested { get; set; }

    /// <summary>
    /// 标题栏拖拽交换需要调分界线（或放不下）：UI 弹统一对话框。
    /// 返回 true 才应用候选；Impossible 时也应展示（左按钮置灰），返回值不应用。
    /// </summary>
    public Func<WindowInfo, WindowInfo, ZoneSwapResult, Task<bool>>? SwapConfirmationRequested { get; set; }

    /// <summary>交换已应用（UI 用来弹 toast）。</summary>
    public event Action<WindowInfo, WindowInfo, ZoneSwapOutcome>? SwapCompleted;

    /// <summary>
    /// 识别到固定大小（不可缩放）窗口：UI 询问是否加入白名单。
    /// 返回 AddToWhitelist 时 UI 负责保存规则；Skip 只跳过当前会话。
    /// </summary>
    public Func<WindowInfo, Task<ZoneWhitelistDecision>>? UnresizableWindowRequested { get; set; }

    /// <summary>UI 正在弹对话框：此时不要重排，避免和对话流程打架。</summary>
    public Func<bool>? IsInteractionBusy { get; set; }

    /// <summary>
    /// 校验通过、尚未移动窗口时回调（UI 用来记录进入前的窗口位置快照）。
    /// 进入失败时不会触发，因此失败不会污染快照。
    /// </summary>
    public Action<IReadOnlyList<WindowInfo>>? BeforeApply { get; set; }

    /// <summary>
    /// 进入工作区，或按新布局重建（窗口句柄集合按传入的窗口重置）。
    /// 返回 false = 最小尺寸放不下或换主窗未获允许，没有应用这个布局（状态栏已说明原因）。
    /// </summary>
    public async Task<bool> EnterAsync(LayoutKind kind, IReadOnlyList<WindowInfo> windows, IntPtr monitor)
    {
        _sessionGeneration++;
        var generation = _sessionGeneration;
        _whitelistIgnoredNotified = false;

        // 进入/切换布局是一次新的会话代次：之前“先不加入”的窗口要重新参与试算。
        _declinedNewcomers.Clear();
        _pendingShow.Clear();

        // 最大化窗口在新布局里应该回到槽位（Apply 会取消最大化）；
        // 仍处于最小化的窗口保持挂起，等它还原后再入槽。
        _maximized.Clear();
        _minimized.RemoveWhere(h => !NativeMethods.IsWindow(h) || !NativeMethods.IsIconic(h));

        windows = await FilterUnresizableAsync(windows, monitor);

        // 白名单窗口不进入槽位（UI 已经过滤过一次，这里兜底；一次性布局不受影响）。
        _initialWhitelistHandles.Clear();
        var rules = _getWhitelist?.Invoke();
        if (rules != null)
        {
            foreach (var window in windows)
            {
                if (ZoneWhitelist.IsWhitelisted(window, rules))
                {
                    _initialWhitelistHandles.Add(window.Handle);
                }
            }

            windows = windows
                .Where(w => !ZoneWhitelist.IsWhitelisted(w, rules))
                .ToList();
        }

        if (windows.Count == 0)
        {
            StatusRequested?.Invoke("分区模式：没有可参与分区的窗口", ZoneStatusLevel.Warn);
            return false;
        }

        var (workspace, swap) = CreateWorkspace(
            kind, windows, _getArea(monitor), _getGap(), allowMainSwap: true);
        workspace.FitToMinimumSizes();

        if (swap != null)
        {
            var approved = MainSwapRequested != null && await MainSwapRequested(swap);
            if (generation != _sessionGeneration)
            {
                return false;
            }

            if (!approved)
            {
                StatusRequested?.Invoke(
                    IsActive
                        ? $"分区模式（{LayoutNames.Display(kind)}）：需要把「{swap.Title}」换到主窗才能排下，" +
                          "未获允许，保持原布局"
                        : $"分区模式（{LayoutNames.Display(kind)}）：需要把「{swap.Title}」换到主窗才能排下，" +
                          "未获允许，无法进入",
                    ZoneStatusLevel.Warn);
                return false;
            }

            workspace.FitToMinimumSizes();
        }

        if (!ZoneFits(workspace))
        {
            // 切换布局时被“先不加入”拒绝过的窗口会重新出现；优先按本轮 newcomers
            // 重新找退让方案，放不下就再次询问用户。
            var oldHandles = new HashSet<IntPtr>(_windowHandles);
            var newcomers = windows.Where(w => !oldHandles.Contains(w.Handle)).ToList();
            var zOrder = windows.ToList();
            var candidates = windows.ToList();
            ZoneWorkspace? fitted = null;
            List<WindowInfo>? selected = null;
            List<WindowInfo>? victims = null;

            var removable = PickOverflowVictims(zOrder, candidates, newcomers);
            for (var k = 1; k <= removable.Count; k++)
            {
                var drop = removable.Take(k).ToList();
                var subset = candidates
                    .Where(w => !drop.Any(d => d.Handle == w.Handle))
                    .ToList();
                if (subset.Count == 0)
                {
                    break;
                }

                var attempt = CreateWorkspace(
                    kind, subset, _getArea(monitor), _getGap(), allowMainSwap: false).Workspace;
                attempt.FitToMinimumSizes();
                if (ZoneFits(attempt))
                {
                    fitted = attempt;
                    selected = subset;
                    victims = drop;
                    break;
                }
            }

            if (fitted != null && victims != null && selected != null &&
                OverflowRequested != null)
            {
                var accepted = await OverflowRequested(victims.Count, newcomers);
                if (generation != _sessionGeneration)
                {
                    return false;
                }

                if (!accepted)
                {
                    foreach (var newcomer in newcomers)
                    {
                        _declinedNewcomers.Add(newcomer.Handle);
                    }

                    _pendingShow.ExceptWith(newcomers.Select(w => w.Handle));
                    StatusRequested?.Invoke(
                        "分区模式：新窗口未加入（工作区放不下）", ZoneStatusLevel.Warn);
                    return false;
                }

                foreach (var victim in victims)
                {
                    WindowOps.Minimize(victim.Handle);
                }

                windows = selected;
                workspace = fitted;
            }

            if (!ZoneFits(workspace))
            {
                // 与 SyncAsync 的“放不下”处理一致：不静默应用，也不把窗口压到最小尺寸以下
                StatusRequested?.Invoke(
                    IsActive
                        ? $"分区模式（{LayoutNames.Display(kind)}）：窗口最小尺寸放不下，保持原布局"
                        : $"分区模式（{LayoutNames.Display(kind)}）：窗口最小尺寸放不下，无法进入",
                    ZoneStatusLevel.Warn);
                return false;
            }
        }

        // 到这里才确定会应用：让 UI 记录进入前的窗口位置（失败路径不会污染快照）
        BeforeApply?.Invoke(windows);

        Kind = kind;
        Monitor = monitor;
        Workspace = workspace;
        _windowHandles.Clear();
        foreach (var window in windows)
        {
            _windowHandles.Add(window.Handle);
        }

        EnsureDragHook().Show(workspace);
        EnsureTitleHook();
        Apply(workspace);
        StartWatch();
        return true;
    }

    /// <summary>退出工作区：恢复窗口样式、摘掉拖拽钩子与窗口监听，窗口保持当前尺寸。</summary>
    public void Exit() => Close(notifyStatus: true);

    public void Dispose()
    {
        Close(notifyStatus: false);
        _dragHook?.Dispose();
        _dragHook = null;
        _titleHook?.Dispose();
        _titleHook = null;
    }

    private void Close(bool notifyStatus)
    {
        _sessionGeneration++;

        StopWatch();
        RestoreBorders();
        _dragHook?.Hide();
        _titleHook?.Hide();
        Workspace = null;
        _swapInProgress = false;
        _whitelistIgnoredNotified = false;
        _initialWhitelistHandles.Clear();
        _fixedSizeNotified = false;
        _fixedAskedProcesses.Clear();
        _fixedSkippedProcesses.Clear();
        _unresizableHandles.Clear();
        _mismatchCounts.Clear();
        _aspectLocked.Clear();
        _lastActualSizes.Clear();
        _aspectCounts.Clear();
        _placeholders.Clear();
        _declinedNewcomers.Clear();
        _pendingShow.Clear();
        _minimized.Clear();
        _maximized.Clear();
        _detached.Clear();
        _minimizeSyncQueued = false;
        RestoreWhitelistTopmost();
        _windowHandles.Clear();
        _lastOrder.Clear();

        if (notifyStatus)
        {
            StatusRequested?.Invoke(
                "已退出分区模式，Ctrl+Alt+0 可还原到进入前的位置", ZoneStatusLevel.Info);
        }
    }

    // ---------------- 摆放与边框 ----------------

    private void Apply(ZoneWorkspace workspace)
    {
        if (Workspace == null)
        {
            return;
        }

        var (placements, gaps) = BuildLayout(workspace);

        // 先把窗口自己的缩放边框去掉，再按分区摆放：边距只由我们的分隔线调整
        SyncBorders(placements);
        PlaceRequested?.Invoke(placements);
        _placeholders.Update(gaps);
        if (gaps.Count > 0)
        {
            LayoutLog.Write(new[]
            {
                "=== aspect placeholders",
                $"  gaps={string.Join(";", gaps.Select(g => g.Rect.ToString()))}",
                $"  created={_placeholders.Handles.Count}",
            });
        }

        // 不依赖窗口后续再发 LOCATIONCHANGE：Apply 后主动补一次观测，
        // 否则长宽比锁定窗口只会在第一次 realign 后被观测一次，永远收敛不到适配矩形。
        if (_postToUi != null)
        {
            _ = QueueRealignAsync(250, force: true);
        }

        _lastOrder.Clear();
        _lastOrder.AddRange(workspace.WindowsInOrder().Select(w => w.Handle));
    }

    /// <summary>
    /// 槽位矩形 → 实际摆放矩形：长宽比锁定窗口在槽位内等比缩放并居中，
    /// 空余条带同时算成占位窗口。realign 也只以这个结果为目标。
    /// </summary>
    private (List<ZonePlacement> Placements, List<ZonePlaceholderSpec> Gaps) BuildLayout(
        ZoneWorkspace workspace)
    {
        var source = workspace.Compute();
        var placements = new List<ZonePlacement>(source.Count);
        var gaps = new List<ZonePlaceholderSpec>();

        foreach (var placement in source)
        {
            var handle = placement.Window.Handle;

            // 最小化/最大化/被拖到其它显示器的窗口不参与摆放，也不画长宽比占位：
            // 最大化保留槽位，最小化和跨屏则等 Sync 重建工作区。
            if (_minimized.Contains(handle) ||
                _maximized.Contains(handle) ||
                NativeMethods.IsIconic(handle) ||
                !IsOnWorkspaceMonitor(handle))
            {
                continue;
            }

            var rect = placement.Rect;
            if (_aspectLocked.TryGetValue(handle, out var aspect))
            {
                rect = ZoneAspectFit.Fit(placement.Rect, aspect.Width, aspect.Height);
                foreach (var gap in ZoneAspectFit.Gaps(placement.Rect, rect))
                {
                    gaps.Add(new ZonePlaceholderSpec(gap, placement.Window.Title));
                }
            }

            placements.Add(new ZonePlacement(placement.Window, rect));
        }

        return (placements, gaps);
    }

    /// <summary>
    /// 从原始 Z 序（前台在前）从后往前选最久未用的旧窗口作为最小化候选；
    /// newcomers 永远排除，已经在上一轮 victims 里的窗口也不重复。
    /// </summary>
    internal static List<WindowInfo> PickOverflowVictims(
        IReadOnlyList<WindowInfo> zOrder,
        IReadOnlyList<WindowInfo> candidates,
        IReadOnlyList<WindowInfo> newcomers)
    {
        var candidateHandles = candidates.Select(w => w.Handle).ToHashSet();
        var newcomerHandles = newcomers.Select(w => w.Handle).ToHashSet();
        return zOrder
            .AsEnumerable()
            .Reverse()
            .Where(w => candidateHandles.Contains(w.Handle) &&
                        !newcomerHandles.Contains(w.Handle))
            .ToList();
    }

    private void UpdateWindowHandles(IEnumerable<WindowInfo> windows)
    {
        _windowHandles.Clear();
        foreach (var window in windows)
        {
            _windowHandles.Add(window.Handle);
        }

        _pendingShow.RemoveWhere(h => _windowHandles.Contains(h));
        _detached.RemoveWhere(h => _windowHandles.Contains(h));
        _minimized.RemoveWhere(h => _windowHandles.Contains(h) && !NativeMethods.IsIconic(h));
    }

    // ---------------- 白名单 ----------------

    /// <summary>进入工作区时把白名单窗口置顶，并记录原来的 topmost 状态，退出时恢复。</summary>
    public void PrepareWhitelistTopmost(IReadOnlyList<WindowInfo> windows)
    {
        RestoreWhitelistTopmost();
        foreach (var window in windows)
        {
            var style = NativeMethods.GetWindowLong(window.Handle, NativeMethods.GWL_EXSTYLE);
            var wasTopmost = (style & NativeMethods.WS_EX_TOPMOST) != 0;
            _whitelistTopmost.Add((window.Handle, wasTopmost));
            NativeMethods.SetWindowPos(
                window.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>恢复进入工作区前白名单窗口的 topmost 状态；可重复调用。</summary>
    public void RestoreWhitelistTopmost()
    {
        foreach (var (handle, wasTopmost) in _whitelistTopmost)
        {
            if (!NativeMethods.IsWindow(handle))
            {
                continue;
            }

            NativeMethods.SetWindowPos(
                handle,
                wasTopmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE);
        }

        _whitelistTopmost.Clear();
    }

    /// <summary>UI 在进入时已经处理过白名单窗口：记录它们，避免随后第一次 Sync 再提示一次。</summary>
    public void MarkInitialWhitelist(IEnumerable<WindowInfo> windows)
    {
        _initialWhitelistHandles.Clear();
        foreach (var window in windows)
        {
            _initialWhitelistHandles.Add(window.Handle);
        }
    }

    private bool IsUnresizable(WindowInfo window) =>
        window.IsFixedSize || window.IsLayered || _unresizableHandles.Contains(window.Handle);

    /// <summary>
    /// 过滤固定大小/分层窗口，并对每个进程最多询问一次是否加入白名单。
    /// 无论用户选择加入还是跳过，这些窗口都不会进入当前工作区。
    /// </summary>
    private async Task<List<WindowInfo>> FilterUnresizableAsync(
        IReadOnlyList<WindowInfo> windows, IntPtr monitor)
    {
        var fixedWindows = windows
            .Where(w => !w.CannotMove && w.Monitor == monitor &&
                        IsUnresizable(w) && !IsWhitelisted(w))
            .ToList();
        var addedToWhitelist = false;

        foreach (var group in fixedWindows.GroupBy(w => w.ProcessId))
        {
            var first = group.First();
            if (_fixedAskedProcesses.Contains(first.ProcessId) ||
                _fixedSkippedProcesses.Contains(first.ProcessId))
            {
                continue;
            }

            _fixedAskedProcesses.Add(first.ProcessId);
            var decision = ZoneWhitelistDecision.Skip;
            if (UnresizableWindowRequested != null)
            {
                try
                {
                    decision = await UnresizableWindowRequested(first);
                }
                catch
                {
                    decision = ZoneWhitelistDecision.Skip;
                }
            }

            _fixedSkippedProcesses.Add(first.ProcessId);
            addedToWhitelist |= decision == ZoneWhitelistDecision.AddToWhitelist;
        }

        if (fixedWindows.Count > 0 && !_fixedSizeNotified && !addedToWhitelist)
        {
            _fixedSizeNotified = true;
            var reason = fixedWindows[0].IsLayered ? "分层窗口" : "固定大小窗口";
            StatusRequested?.Invoke(
                $"分区模式：{reason}不参与分区：{fixedWindows[0].Title}",
                ZoneStatusLevel.Warn);
        }

        return windows.Where(w => !IsUnresizable(w)).ToList();
    }

    private bool IsWhitelisted(WindowInfo window) =>
        _getWhitelist?.Invoke() is { } rules && ZoneWhitelist.IsWhitelisted(window, rules);

    private void NotifyWhitelistIgnored()
    {
        if (_whitelistIgnoredNotified)
        {
            return;
        }

        _whitelistIgnoredNotified = true;
        StatusRequested?.Invoke("分区模式：白名单窗口已忽略", ZoneStatusLevel.Info);
    }

    // ---------------- 槽位交换 ----------------

    /// <summary>
    /// B3 标题栏拖拽开始：期间不因窗口位置变化被 realign 拉回，也不处理窗口事件重排；
    /// 结束（松手/取消）时调用 <see cref="EndSwap"/>。
    /// </summary>
    public void BeginSwap()
    {
        _swapInProgress = true;
        // 拖拽期间不再把此前的 SHOW 候选带到拖拽结束后触发。
        _pendingShow.Clear();
    }

    /// <summary>结束标题栏拖拽交换；下一轮窗口事件/重排恢复正常。</summary>
    public void EndSwap()
    {
        _swapInProgress = false;
        _pendingShow.Clear();
    }

    /// <summary>
    /// 试算把 <paramref name="first"/> 和 <paramref name="second"/> 两个窗口交换：
    /// 不修改当前工作区，返回候选工作区与结果；确认后调用 <see cref="CommitSwap"/>。
    /// 主窗永远不动，只在必要时调与目标槽位相关的分界线。
    /// </summary>
    public ZoneSwapResult TrySwap(WindowInfo first, WindowInfo second)
    {
        var workspace = Workspace;
        if (workspace == null || first.Handle == second.Handle)
        {
            return new ZoneSwapResult(ZoneSwapOutcome.Impossible, null, _sessionGeneration);
        }

        var ordered = workspace.WindowsInOrder();
        if (ordered.Count == 0 ||
            !ordered.Any(w => w.Handle == first.Handle) ||
            !ordered.Any(w => w.Handle == second.Handle))
        {
            return new ZoneSwapResult(ZoneSwapOutcome.Impossible, null, _sessionGeneration);
        }

        // 窗口本身比整块可用区域还大：任何槽位都不可能。
        if (ExceedsArea(first, workspace.Area) || ExceedsArea(second, workspace.Area))
        {
            return new ZoneSwapResult(ZoneSwapOutcome.Impossible, null, _sessionGeneration);
        }

        var swapped = workspace.Clone();
        if (!swapped.SwapWindows(first, second))
        {
            return new ZoneSwapResult(ZoneSwapOutcome.Impossible, null, _sessionGeneration);
        }

        // 现状就装得下（不改任何分界线）。
        if (ZoneFits(swapped))
        {
            return new ZoneSwapResult(ZoneSwapOutcome.Success, swapped, _sessionGeneration);
        }

        // 主窗不动：只尝试调与目标槽位相关的分界线。
        if (!swapped.TryAdjustDividerFor(first))
        {
            return new ZoneSwapResult(ZoneSwapOutcome.Impossible, null, _sessionGeneration);
        }

        return new ZoneSwapResult(
            ZoneSwapOutcome.NeedsDividerAdjustment, swapped, _sessionGeneration);
    }

    /// <summary>确认一次交换：把候选工作区设为当前工作区并立即重排窗口。</summary>
    public bool CommitSwap(ZoneSwapResult result)
    {
        if (result.Outcome == ZoneSwapOutcome.Impossible ||
            result.Candidate == null ||
            Workspace == null ||
            result.Generation != _sessionGeneration)
        {
            return false;
        }

        // 对话拖得久了，会话可能已切换布局/退出；只接受窗口集合仍一致的候选。
        var handles = result.Candidate.WindowsInOrder().Select(w => w.Handle).ToHashSet();
        if (!handles.SetEquals(_windowHandles))
        {
            return false;
        }

        Workspace = result.Candidate;
        _dragHook?.Refresh(Workspace);
        Apply(Workspace);
        return true;
    }

    private static bool ExceedsArea(WindowInfo window, WindowRect area) =>
        Math.Max(window.MinWidth, 120) > area.Width ||
        Math.Max(window.MinHeight, 90) > area.Height;

    /// <summary>
    /// 工作区窗口：套上 DWM 强调色边框，并临时去掉 WS_THICKFRAME——
    /// 这样拖窗口自己的边不会缩放，只能拖窗口之间的分隔线。
    /// </summary>
    private void SyncBorders(IReadOnlyList<ZonePlacement> placements)
    {
        var handles = placements.Select(p => p.Window.Handle).ToHashSet();

        foreach (var stale in _bordered.Where(h => !handles.Contains(h)).ToList())
        {
            WindowOps.ResetBorderColor(stale);
            RestoreStyle(stale);
            _bordered.Remove(stale);
        }

        foreach (var handle in handles.Where(h => !_bordered.Contains(h)))
        {
            WindowOps.SetBorderColor(handle, WindowOps.ZoneBorderColor);
            _styles[handle] = WindowOps.RemoveResizeBorder(handle);
            _bordered.Add(handle);
        }

        // 写盘是为了程序被强杀后下次启动还能把边框加回去
        ZoneStyleStore.Save(_styles);
    }

    private void RestoreStyle(IntPtr handle)
    {
        if (!_styles.TryGetValue(handle, out var style))
        {
            return;
        }

        _styles.Remove(handle);
        WindowOps.RestoreStyle(handle, style);
    }

    private void RestoreBorders()
    {
        foreach (var handle in _bordered)
        {
            WindowOps.ResetBorderColor(handle);
            RestoreStyle(handle);
        }

        _bordered.Clear();
        _styles.Clear();
        ZoneStyleStore.Save(_styles);
    }

    // ---------------- 拖拽分隔线 ----------------

    private ZoneDragHook EnsureDragHook()
    {
        if (_dragHook != null)
        {
            return _dragHook;
        }

        var hook = new ZoneDragHook();
        hook.Start();
        hook.Changed += workspace =>
        {
            if (_postToUi == null)
            {
                Apply(workspace);
            }
            else
            {
                _postToUi(() => Apply(workspace));
            }
        };

        _dragHook = hook;
        return hook;
    }

    private void EnsureTitleHook()
    {
        if (_titleHook != null)
        {
            return;
        }

        var hook = new ZoneTitleDragHook(() => Workspace);
        hook.DragStarted += BeginSwap;
        hook.DragEnded += EndSwap;
        hook.WindowDropped += OnTitleWindowDropped;

        try
        {
            hook.Start();
            _titleHook = hook;
        }
        catch (Exception ex)
        {
            hook.Dispose();
            StatusRequested?.Invoke(
                "分区模式：标题栏交换不可用（" + ex.Message + "）", ZoneStatusLevel.Warn);
        }
    }

    private void OnTitleWindowDropped(WindowInfo source, WindowInfo target)
    {
        if (_postToUi == null)
        {
            _ = HandleTitleDropAsync(source, target);
        }
        else
        {
            _postToUi(() => _ = HandleTitleDropAsync(source, target));
        }
    }

    private async Task HandleTitleDropAsync(WindowInfo source, WindowInfo target)
    {
        var generation = _sessionGeneration;
        if (Workspace == null)
        {
            return;
        }

        var result = TrySwap(source, target);
        if (generation != _sessionGeneration || Workspace == null)
        {
            return;
        }

        if (result.Outcome == ZoneSwapOutcome.Impossible)
        {
            if (SwapConfirmationRequested != null)
            {
                await SwapConfirmationRequested(source, target, result);
            }

            return;
        }

        if (result.Outcome == ZoneSwapOutcome.NeedsDividerAdjustment)
        {
            if (SwapConfirmationRequested == null)
            {
                return;
            }

            var approved = await SwapConfirmationRequested(source, target, result);
            if (!approved || generation != _sessionGeneration || Workspace == null)
            {
                return;
            }
        }

        if (CommitSwap(result))
        {
            SwapCompleted?.Invoke(source, target, result.Outcome);
        }
    }

    // ---------------- 窗口事件（新窗口 / 关闭 / 最小化） ----------------

    /// <summary>
    /// 工作区开启期间监听顶层窗口事件（SetWinEventHook）：新窗口 SHOW 时自动入槽，
    /// 窗口销毁/隐藏时重新铺开。事件会连发几次，用 <see cref="_syncQueued"/> 合并成一次重排。
    /// </summary>
    private void StartWatch()
    {
        if (_watcher != null)
        {
            return;
        }

        _watcher = new WinEventWatcher();
        _watcher.WindowEvent += OnWindowEvent;
    }

    private void StopWatch()
    {
        if (_watcher == null)
        {
            return;
        }

        _watcher.WindowEvent -= OnWindowEvent;
        _watcher.Dispose();
        _watcher = null;
        _syncQueued = false;
    }

    private void OnWindowEvent(IntPtr hwnd, uint eventType)
    {
        // 销毁事件总是清掉候选记录，即使当前正在拖拽/重排。
        if (eventType == NativeMethods.EVENT_OBJECT_DESTROY)
        {
            _declinedNewcomers.Remove(hwnd);
            _pendingShow.Remove(hwnd);
            _minimized.Remove(hwnd);
            _maximized.Remove(hwnd);
            _detached.Remove(hwnd);
        }

        if (Workspace == null || _swapInProgress)
        {
            return;
        }

        // 最小化：窗口离开槽位，剩余窗口重新铺开。EVENT_OBJECT 区间收不到这条事件，
        // 只靠 LOCATIONCHANGE 的话会被误当成“窗口被挪走”，反而把空槽位一直留着。
        if (eventType == NativeMethods.EVENT_SYSTEM_MINIMIZESTART)
        {
            if (_windowHandles.Contains(hwnd))
            {
                MarkMinimized(hwnd);
                _ = QueueMinimizeSyncAsync();
            }

            return;
        }

        // 还原最小化：和 SHOW 一样按“新窗口”重新入槽。
        if (eventType == NativeMethods.EVENT_SYSTEM_MINIMIZEEND)
        {
            _minimized.Remove(hwnd);
            if (!_windowHandles.Contains(hwnd))
            {
                _declinedNewcomers.Remove(hwnd);
                _pendingShow.Add(hwnd);
                _ = QueueCandidateSyncAsync(200);
            }
        }

        // 工作区窗口被外部工具（如 DSH 的 computer use）改了位置/大小：把它拉回槽位。
        // 可能是程序自己 SetWindowPos 触发的事件，RealignAsync 比较后再决定要不要重排。
        if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (_dragHook?.IsDragging == true)
            {
                return;
            }

            if (_windowHandles.Contains(hwnd))
            {
                // 最小化动画期间窗口矩形会跑到屏幕外，不能拉回槽位。
                if (NativeMethods.IsIconic(hwnd))
                {
                    MarkMinimized(hwnd);
                    _ = QueueMinimizeSyncAsync();
                    return;
                }

                // 最大化：保留槽位，但不再摆放/加边框；还原后自动回到槽位。
                if (NativeMethods.IsZoomed(hwnd))
                {
                    MarkMaximized(hwnd);
                    return;
                }

                if (_maximized.Remove(hwnd))
                {
                    _ = ResumeMaximizedAsync(hwnd);
                    return;
                }

                // 被拖到别的显示器：移出工作区，让剩余窗口重新铺满。
                if (!IsOnWorkspaceMonitor(hwnd))
                {
                    DetachWindow(hwnd, "窗口移到了其它显示器");
                    return;
                }

                _ = QueueRealignAsync();
                return;
            }

            // 被拖到其它显示器、又拖回工作区所在显示器的窗口：重新按新窗口入槽。
            if (_detached.Contains(hwnd) && !NativeMethods.IsIconic(hwnd) &&
                IsOnWorkspaceMonitor(hwnd))
            {
                _detached.Remove(hwnd);
                _declinedNewcomers.Remove(hwnd);
                _pendingShow.Add(hwnd);
                _ = QueueCandidateSyncAsync(250);
                return;
            }

            // 只有 SHOW 登记过的窗口才允许用 LOCATIONCHANGE 补一次候选；
            // 任意未知窗口（尤其是被拒绝后用户拖动它）的移动不能触发 Sync。
            if (_pendingShow.Contains(hwnd))
            {
                _ = QueueCandidateSyncAsync(250);
            }

            return;
        }

        if (eventType == NativeMethods.EVENT_OBJECT_SHOW)
        {
            // 最小化窗口恢复时可能只发 SHOW（没有 MINIMIZEEND）：
            // 如果它本来就还在工作区里，直接补回边框与槽位；否则按新窗口处理。
            if (!NativeMethods.IsIconic(hwnd))
            {
                _minimized.Remove(hwnd);
                if (_windowHandles.Contains(hwnd) && Workspace != null)
                {
                    Apply(Workspace);
                }
            }

            if (!_windowHandles.Contains(hwnd))
            {
                // 重新显示（包括用户关闭/重新打开被拒绝的窗口）允许重新询问。
                _declinedNewcomers.Remove(hwnd);
                _pendingShow.Add(hwnd);

                // 恢复最小化窗口时 SHOW 可能早于 IsIconic 变 false / 标题就绪；
                // 立即 Sync 之外再延迟补一次，避免窗口永远错过加入工作区。
                _ = QueueCandidateSyncAsync(300);
            }
        }

        if (_syncQueued)
        {
            return;
        }

        _syncQueued = true;

        // 和原实现一样先回 UI 线程再重排：合并事件，也避免在 hook 回调里直接弹对话框
        if (_postToUi == null)
        {
            _ = SyncAsync();
        }
        else
        {
            _postToUi(() => _ = SyncAsync());
        }
    }

    /// <summary>
    /// 恢复的最小化窗口进入工作区的补一次 Sync：SHOW 当下可能仍 IsIconic/无标题，
    /// 用延迟 + 去抖等它真正显示出来；期间窗口可能又关闭/退出，用 generation 放弃过期任务。
    /// </summary>
    private async Task QueueCandidateSyncAsync(int delayMs)
    {
        if (_candidateSyncQueued || _pendingShow.Count == 0)
        {
            return;
        }

        _candidateSyncQueued = true;
        var generation = _sessionGeneration;
        try
        {
            await Task.Delay(delayMs);
        }
        finally
        {
            _candidateSyncQueued = false;
        }

        if (Workspace == null || _swapInProgress || generation != _sessionGeneration ||
            _pendingShow.Count == 0)
        {
            return;
        }

        if (_postToUi == null)
        {
            await SyncAsync();
        }
        else
        {
            _postToUi(() => _ = SyncAsync());
        }
    }

    /// <summary>最小化后延迟一次重排：等最小化动画把 IsIconic/可见性稳定下来。</summary>
    private async Task QueueMinimizeSyncAsync()
    {
        if (_minimizeSyncQueued)
        {
            return;
        }

        _minimizeSyncQueued = true;
        var generation = _sessionGeneration;
        try
        {
            await Task.Delay(80);
        }
        finally
        {
            _minimizeSyncQueued = false;
        }

        if (Workspace == null || _swapInProgress || generation != _sessionGeneration)
        {
            return;
        }

        if (_postToUi == null)
        {
            await SyncAsync();
        }
        else
        {
            _postToUi(() => _ = SyncAsync());
        }
    }

    /// <summary>
    /// 窗口最小化：先暂停摆放并收掉它的边框/长宽比占位，真正的重排在
    /// <see cref="QueueMinimizeSyncAsync"/> 里做。不提前从 _windowHandles 移除，
    /// 这样 Sync 会把剩余窗口当成“离开”而不是“全是新窗口”。
    /// </summary>
    private void MarkMinimized(IntPtr handle)
    {
        if (!_minimized.Add(handle))
        {
            return;
        }

        _maximized.Remove(handle);
        _aspectLocked.Remove(handle);
        _aspectCounts.Remove(handle);
        _lastActualSizes.Remove(handle);
        _mismatchCounts.Remove(handle);

        if (_bordered.Remove(handle))
        {
            WindowOps.ResetBorderColor(handle);
            RestoreStyle(handle);
        }

        // 长宽比缺口是灰色占位窗：窗口一最小化先全部收掉，避免留下灰块/闪烁。
        _placeholders.Clear();
    }

    /// <summary>窗口最大化：保留槽位，但不再摆放、不再套分区边框；还原后回到槽位。</summary>
    private void MarkMaximized(IntPtr handle)
    {
        if (!_maximized.Add(handle))
        {
            return;
        }

        _minimized.Remove(handle);
        _aspectLocked.Remove(handle);
        _aspectCounts.Remove(handle);
        _lastActualSizes.Remove(handle);
        _mismatchCounts.Remove(handle);

        if (_bordered.Remove(handle))
        {
            WindowOps.ResetBorderColor(handle);
            RestoreStyle(handle);
        }

        StatusRequested?.Invoke(
            "分区模式：窗口已最大化（槽位保留），还原后会回到原槽位", ZoneStatusLevel.Info);
    }

    /// <summary>取消最大化后等动画/拖拽结束，再把窗口放回槽位并补回分区边框。</summary>
    private async Task ResumeMaximizedAsync(IntPtr handle)
    {
        var generation = _sessionGeneration;

        // 用户可能是按住标题栏把最大化窗口拖下来：等左键松开再处理，
        // 否则会在拖拽中途把窗口拉回槽位。
        var waited = 0;
        while ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0 &&
               waited < 4000)
        {
            await Task.Delay(100);
            waited += 100;
        }

        await Task.Delay(120);

        if (Workspace == null || _swapInProgress || generation != _sessionGeneration)
        {
            return;
        }

        if (!NativeMethods.IsWindow(handle))
        {
            return;
        }

        // 还原动画期间又被最大化了
        if (NativeMethods.IsZoomed(handle))
        {
            _maximized.Add(handle);
            return;
        }

        if (_minimized.Contains(handle) || NativeMethods.IsIconic(handle))
        {
            return;
        }

        // 拖到了另一台显示器：移出工作区，不再拉回。
        if (!IsOnWorkspaceMonitor(handle))
        {
            DetachWindow(handle, "窗口移到了其它显示器");
            return;
        }

        Apply(Workspace);
    }

    /// <summary>
    /// 窗口被移到别的显示器：移出当前工作区（保留窗口本身与样式），
    /// 剩余窗口重新铺满；之后拖回来会按新窗口重新入槽。
    /// </summary>
    private void DetachWindow(IntPtr handle, string reason)
    {
        if (!_windowHandles.Contains(handle))
        {
            return;
        }

        _maximized.Remove(handle);
        _minimized.Remove(handle);
        _aspectLocked.Remove(handle);
        _aspectCounts.Remove(handle);
        _lastActualSizes.Remove(handle);
        _mismatchCounts.Remove(handle);
        _pendingShow.Remove(handle);

        if (_bordered.Remove(handle))
        {
            WindowOps.ResetBorderColor(handle);
            RestoreStyle(handle);
        }

        _placeholders.Clear();
        _detached.Add(handle);
        StatusRequested?.Invoke(
            $"分区模式：{reason}，已移出工作区（剩余窗口重新铺开）；拖回来可重新入槽",
            ZoneStatusLevel.Info);

        // 不手动改 _windowHandles：让 Sync 看到集合差异后重建，避免把剩余窗口误判成 newcomer。
        _ = SyncAsync();
    }

    /// <summary>窗口中心是否还在当前工作区所在显示器上（最小化/最大化窗口不做跨屏判断）。</summary>
    private bool IsOnWorkspaceMonitor(IntPtr handle)
    {
        if (NativeMethods.IsIconic(handle) || NativeMethods.IsZoomed(handle))
        {
            return true;
        }

        if (!NativeMethods.GetWindowRect(handle, out var rect) ||
            rect.Width <= 0 || rect.Height <= 0)
        {
            return true;
        }

        var center = new NativeMethods.POINT
        {
            X = rect.Left + rect.Width / 2,
            Y = rect.Top + rect.Height / 2,
        };
        var monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return monitor == IntPtr.Zero || monitor == Monitor;
    }

    private bool _realignQueued;
    private DateTime _lastRealign;

    /// <summary>
    /// 合并高频 LOCATIONCHANGE，延迟 150ms 再比对；<paramref name="force"/> 用于
    /// Apply 后主动探一次（不依赖窗口是否再发 LOCATIONCHANGE）。
    /// </summary>
    private async Task QueueRealignAsync(int delayMs = 150, bool force = false)
    {
        if (_realignQueued)
        {
            return;
        }

        _realignQueued = true;
        var generation = _sessionGeneration;
        await Task.Delay(delayMs);

        if (_postToUi == null)
        {
            await RealignAsync(generation, force);
        }
        else
        {
            _postToUi(() => _ = RealignAsync(generation, force));
        }
    }

    private async Task RealignAsync(int generation, bool force = false)
    {
        _realignQueued = false;

        if (Workspace == null || generation != _sessionGeneration ||
            _swapInProgress || _dragHook?.IsDragging == true)
        {
            return;
        }

        // 限频：一秒钟最多纠正一次，防止和会自行改尺寸的窗口来回打架；
        // Apply 后的主动探测可以绕过它，否则只发一次 LOCATIONCHANGE 的窗口
        // 永远等不到第二次观测，长宽比锁定就检测不出来。
        if (!force && (DateTime.UtcNow - _lastRealign).TotalMilliseconds < 1000)
        {
            return;
        }

        // 左键按住时先别抢：可能是用户正把最大化窗口拖下来/拖到另一台显示器，
        // 中途扶正会把系统拖拽拽回去。
        if (!force &&
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0 &&
            _swapInProgress == false && _titleHook?.IsDragging != true)
        {
            _ = QueueRealignAsync(300);
            return;
        }

        // 兜底：事件偶尔会漏（最小化动画、系统直接最大化、被其它工具跨屏移动），
        // 在比对之前先按实时状态把窗口标成挂起/移出，避免把最大化或最小化的窗口拉回槽位。
        foreach (var handle in _windowHandles.ToList())
        {
            if (!NativeMethods.IsWindow(handle) || _minimized.Contains(handle))
            {
                continue;
            }

            if (NativeMethods.IsIconic(handle))
            {
                MarkMinimized(handle);
                _ = QueueMinimizeSyncAsync();
                continue;
            }

            if (NativeMethods.IsZoomed(handle))
            {
                MarkMaximized(handle);
                continue;
            }

            if (_maximized.Contains(handle))
            {
                continue;
            }

            if (!IsOnWorkspaceMonitor(handle))
            {
                DetachWindow(handle, "窗口移到了其它显示器");
                return;
            }
        }

        foreach (var placement in BuildLayout(Workspace).Placements)
        {
            if (IsUnresizable(placement.Window))
            {
                continue;
            }

            var actual = WindowOps.GetVisibleBounds(placement.Window.Handle);
            if (actual == null)
            {
                continue;
            }

            var mismatch =
                Math.Abs(actual.Value.Left - placement.Rect.Left) > 6 ||
                Math.Abs(actual.Value.Top - placement.Rect.Top) > 6 ||
                Math.Abs(actual.Value.Width - placement.Rect.Width) > 6 ||
                Math.Abs(actual.Value.Height - placement.Rect.Height) > 6;

            if (!mismatch)
            {
                _mismatchCounts.Remove(placement.Window.Handle);
                _aspectCounts.Remove(placement.Window.Handle);
                continue;
            }

            var handle = placement.Window.Handle;
            if (_aspectLocked.ContainsKey(handle))
            {
                _lastRealign = DateTime.UtcNow;
                Apply(Workspace);
                return;
            }

            // 实际尺寸和进入工作区前完全一样，且已经贴近最小尺寸：更像固定大小/不可缩放窗口，
            // 走自适应排除；否则可能是长宽比锁定（窗口已经处在自己的比例尺寸）。
            var sameAsOriginal =
                actual.Value.Width == placement.Window.Bounds.Width &&
                actual.Value.Height == placement.Window.Bounds.Height;
            var fixedLike = sameAsOriginal &&
                            actual.Value.Width <= Math.Max(placement.Window.MinWidth, 0) + 4 &&
                            actual.Value.Height <= Math.Max(placement.Window.MinHeight, 0) + 4;
            if (fixedLike)
            {
                _mismatchCounts.TryGetValue(handle, out var count);
                count++;
                _mismatchCounts[handle] = count;
                if (count >= 2)
                {
                    _mismatchCounts.Remove(handle);
                    await HandleAdaptiveUnresizableAsync(placement.Window);
                    await SyncAsync();
                    return;
                }

                _lastRealign = DateTime.UtcNow;
                Apply(Workspace);
                return;
            }

            _mismatchCounts.Remove(handle);

            // 连续两次实际宽高比一致、且和槽位比例明显不同：长宽比锁定。
            _lastActualSizes.TryGetValue(handle, out var previous);
            var actualRatio = actual.Value.Height > 0
                ? actual.Value.Width / (double)actual.Value.Height
                : 0;
            var previousRatio = previous.Height > 0
                ? previous.Width / (double)previous.Height
                : 0;
            var stableRatio = previousRatio > 0 &&
                              Math.Abs(actualRatio - previousRatio) <= 0.02;
            if (stableRatio)
            {
                _aspectCounts.TryGetValue(handle, out var aspectCount);
                aspectCount++;
                _aspectCounts[handle] = aspectCount;
                if (aspectCount >= 2)
                {
                    var planRatio = placement.Rect.Width / (double)placement.Rect.Height;
                    if (Math.Abs(planRatio - actualRatio) > 0.02)
                    {
                        _aspectLocked[handle] = (actual.Value.Width, actual.Value.Height);
                        _aspectCounts.Remove(handle);
                        _lastActualSizes.Remove(handle);
                        _lastRealign = DateTime.UtcNow;
                        Apply(Workspace);
                        return;
                    }
                }
            }
            else
            {
                _aspectCounts[handle] = 1;
            }

            _lastActualSizes[handle] = (actual.Value.Width, actual.Value.Height);
            _lastRealign = DateTime.UtcNow;
            Apply(Workspace);
            return;
        }

        await Task.CompletedTask;
    }

    private async Task HandleAdaptiveUnresizableAsync(WindowInfo window)
    {
        _unresizableHandles.Add(window.Handle);
        if (!_fixedAskedProcesses.Contains(window.ProcessId))
        {
            _fixedAskedProcesses.Add(window.ProcessId);
            var decision = ZoneWhitelistDecision.Skip;
            if (UnresizableWindowRequested != null)
            {
                try
                {
                    decision = await UnresizableWindowRequested(window);
                }
                catch
                {
                    decision = ZoneWhitelistDecision.Skip;
                }
            }

            _fixedSkippedProcesses.Add(window.ProcessId);
            if (decision != ZoneWhitelistDecision.AddToWhitelist && !_fixedSizeNotified)
            {
                _fixedSizeNotified = true;
                StatusRequested?.Invoke(
                    $"分区模式：固定大小窗口不参与分区：{window.Title}",
                    ZoneStatusLevel.Warn);
            }
        }
    }

    private async Task SyncAsync()
    {
        // 合并：把标志清掉再干活（和原实现「先入队、回调里清标志」一致）
        _syncQueued = false;

        // 对话框可能挂很久；期间用户可能退出/切换布局，返回后要能认出“这是上一代会话”
        var generation = _sessionGeneration;

        try
        {
            if (Workspace == null || _swapInProgress ||
                (IsInteractionBusy?.Invoke() ?? false))
            {
                return;
            }

            var all = await FilterUnresizableAsync(_collectWindows(), Monitor);
            var rules = _getWhitelist?.Invoke();
            bool Whitelisted(WindowInfo window) =>
                rules != null && ZoneWhitelist.IsWhitelisted(window, rules);
            var whitelistedOnMonitor = all
                .Where(w => !w.CannotMove && w.Monitor == Monitor && Whitelisted(w))
                .ToList();
            if (whitelistedOnMonitor.Count > 0)
            {
                var newlyAppeared = whitelistedOnMonitor
                    .Any(w => !_initialWhitelistHandles.Contains(w.Handle));
                if (newlyAppeared)
                {
                    NotifyWhitelistIgnored();
                }
            }

            var windows = LayoutService.ZoneWindowsOnMonitor(all, Monitor)
                .Where(w => !Whitelisted(w))
                .Where(w => !_declinedNewcomers.Contains(w.Handle))
                .ToList();
            var zOrder = windows.ToList();

            // 恢复的窗口可能在另一台显示器上：当前工作区不跨屏，不能静默丢掉。
            // 白名单窗口本来就不参与，不算跨屏候选。
            var otherMonitor = all
                .Where(w => !w.CannotMove && w.Monitor != Monitor &&
                            !_windowHandles.Contains(w.Handle) &&
                            !_declinedNewcomers.Contains(w.Handle) && !Whitelisted(w))
                .ToList();
            if (otherMonitor.Count > 0)
            {
                NotifyCrossMonitor(otherMonitor.Count);
            }
            else
            {
                _lastCrossMonitorCount = 0;
            }

            // 粘住上一次的槽位分配：窗口短暂消失（被别的工具隐藏/最小化）再回来时，
            // 不能当成新窗口排到最后（否则原本的主窗会被挤到小格子里）。
            // 仍在列表里的老窗口按上次的顺序排，新窗口保持 Z 序接在后面。
            if (_lastOrder.Count > 0)
            {
                windows = windows
                    .OrderBy(w =>
                    {
                        var index = _lastOrder.IndexOf(w.Handle);
                        return index < 0 ? int.MaxValue : index;
                    })
                    .ToList();
            }

            if (windows.Count == 0)
            {
                // 只有确实全部最小化/关闭时才清空；暂时被 cloaked 或移到其它显示器
                // 的窗口不能当成“都走了”，否则回来时会被一起当成新窗口重排。
                var stillThere = _windowHandles.Where(NativeMethods.IsWindow).ToList();
                if (stillThere.Count > 0 && !stillThere.All(NativeMethods.IsIconic))
                {
                    return;
                }

                // 所有窗口都最小化/移走了：清掉句柄与占位，保留会话本身；
                // 任一窗口还原后会被当成新窗口重新入槽。
                SyncBorders(Array.Empty<ZonePlacement>());
                _placeholders.Clear();
                _windowHandles.Clear();
                _lastOrder.Clear();
                StatusRequested?.Invoke(
                    "分区模式：所有窗口都已离开，等待窗口恢复后重新入槽", ZoneStatusLevel.Info);
                return;
            }

            var handles = new HashSet<IntPtr>(windows.Select(w => w.Handle));
            if (handles.SetEquals(_windowHandles))
            {
                _pendingShow.RemoveWhere(h => _windowHandles.Contains(h));
                if (otherMonitor.Count > 0)
                {
                    NotifyCrossMonitor(otherMonitor.Count);
                }

                return;
            }

            var newcomers = zOrder.Where(w => !_windowHandles.Contains(w.Handle)).ToList();

            var gap = _getGap();
            var area = _getArea(Monitor);
            var candidates = new List<WindowInfo>(windows);
            var allowMainSwap = true;

            while (true)
            {
                if (generation != _sessionGeneration || Workspace == null)
                {
                    return;
                }

                var (workspace, swap) = CreateWorkspace(
                    Kind, candidates, area, gap, allowMainSwap);
                PreserveRatios(workspace, candidates.Count);
                workspace.FitToMinimumSizes();

                if (swap != null)
                {
                    // 网格只有换主窗才排得下：先请用户许可；不同意就按“放不下”继续（不再问）
                    if (MainSwapRequested == null)
                    {
                        allowMainSwap = false;
                        continue;
                    }

                    var approved = await MainSwapRequested(swap);
                    if (generation != _sessionGeneration || Workspace == null)
                    {
                        return;
                    }

                    if (approved)
                    {
                        // 对话框期间窗口可能变化：重新枚举一次，用“批准的主窗”重新规划后应用，
                        // 让工作区与一次性布局批准后的 pinned 结果逐像素一致。
                        var latest = LayoutService.ZoneWindowsOnMonitor(_collectWindows(), Monitor);
                        if (generation != _sessionGeneration || Workspace == null)
                        {
                            return;
                        }

                        var latestMain = latest.FirstOrDefault(w => w.Handle == swap.Handle);
                        if (latest.Count == 0 || latestMain == null)
                        {
                            StatusRequested?.Invoke(
                                "分区模式：窗口在询问期间发生变化，已取消换主窗", ZoneStatusLevel.Warn);
                            return;
                        }

                        var pinned = new List<WindowInfo>(latest.Count) { latestMain };
                        pinned.AddRange(latest.Where(w => w.Handle != latestMain.Handle));
                        var (swapped, stillNeedsSwap) = CreateWorkspace(
                            Kind, pinned, area, gap, allowMainSwap: false);
                        PreserveRatios(swapped, pinned.Count);
                        swapped.FitToMinimumSizes();
                        if (stillNeedsSwap != null || !ZoneFits(swapped))
                        {
                            StatusRequested?.Invoke(
                                "分区模式：窗口在询问期间发生变化，未能换主窗", ZoneStatusLevel.Warn);
                            return;
                        }

                        Workspace = swapped;
                        _dragHook?.Refresh(swapped);
                        Apply(swapped);
                        UpdateWindowHandles(latest);

                        var swapChange = newcomers.Count > 0
                            ? $"新窗口已加入（{latest.Count} 个，已换主窗）"
                            : $"窗口已离开（{latest.Count} 个，已换主窗）";
                        StatusRequested?.Invoke(
                            $"分区模式（{LayoutNames.Display(Kind)}）：{swapChange}", ZoneStatusLevel.Info);
                        return;
                    }

                    allowMainSwap = false;
                    continue;
                }

                if (ZoneFits(workspace))
                {
                    Workspace = workspace;
                    _dragHook?.Refresh(workspace);
                    Apply(workspace);
                    UpdateWindowHandles(candidates);

                    var change = newcomers.Count > 0
                        ? $"新窗口已加入（{candidates.Count} 个）"
                        : $"窗口已离开（{candidates.Count} 个）";
                    StatusRequested?.Invoke(
                        $"分区模式（{LayoutNames.Display(Kind)}）：{change}", ZoneStatusLevel.Info);
                    return;
                }

                // 关窗 / 最小化触发时没有新窗口：不要走“最小化几个窗口腾空间”的询问，
                // 也不要把别的窗口最小化；保持原布局并说明。
                if (newcomers.Count == 0)
                {
                    StatusRequested?.Invoke(
                        $"分区模式（{LayoutNames.Display(Kind)}）：窗口已离开，" +
                        "剩余窗口最小尺寸放不下，保持原布局", ZoneStatusLevel.Warn);
                    return;
                }

                // 放不下：从**原始 Z 序**最底层开始找「最小化几个才能排下」，
                // 最久没用的旧窗口优先；新窗口永远不进入 victims，拒绝后下次仍按 newcomer 处理。
                var removable = PickOverflowVictims(zOrder, candidates, newcomers);

                List<WindowInfo>? victims = null;
                for (var k = 1; k <= removable.Count; k++)
                {
                    var drop = removable.Take(k).ToList();
                    var subset = candidates.Where(w => !drop.Contains(w)).ToList();
                    if (subset.Count == 0)
                    {
                        break;
                    }

                    var attempt = CreateWorkspace(
                        Kind, subset, area, gap, allowMainSwap: false).Workspace;
                    attempt.FitToMinimumSizes();
                    if (ZoneFits(attempt))
                    {
                        victims = drop;
                        break;
                    }
                }

                if (victims == null)
                {
                    StatusRequested?.Invoke(
                        "分区模式：新窗口最小尺寸比工作区还大，无法加入", ZoneStatusLevel.Warn);
                    return;
                }

                if (generation != _sessionGeneration || Workspace == null)
                {
                    return;
                }

                var prompted = OverflowRequested != null;
                var accepted = prompted &&
                               await OverflowRequested!(victims.Count, newcomers);

                // 等待期间用户退出/重新进入：放弃这次加入，不要复活旧工作区
                if (generation != _sessionGeneration || Workspace == null)
                {
                    return;
                }

                if (!accepted)
                {
                    if (prompted)
                    {
                        // 用户明确“先不加入”：本会话内记住这些窗口，不再作为 newcomer 重试/弹窗。
                        // 窗口 DESTROY 或重新 SHOW 时移除，允许以后再次询问。
                        foreach (var newcomer in newcomers)
                        {
                            _declinedNewcomers.Add(newcomer.Handle);
                        }

                        _pendingShow.ExceptWith(newcomers.Select(w => w.Handle));
                    }

                    StatusRequested?.Invoke(
                        "分区模式：新窗口未加入（工作区放不下）", ZoneStatusLevel.Warn);
                    return;
                }

                foreach (var victim in victims)
                {
                    WindowOps.Minimize(victim.Handle);
                }

                candidates = candidates.Where(w => !victims.Contains(w)).ToList();
            }
        }
        catch (Exception ex)
        {
            StatusRequested?.Invoke("更新工作区失败：" + ex.Message, ZoneStatusLevel.Error);
        }
    }

    private void NotifyCrossMonitor(int count)
    {
        var now = DateTime.UtcNow;
        if (count == _lastCrossMonitorCount &&
            (now - _lastCrossMonitorNotice).TotalSeconds < 5)
        {
            return;
        }

        _lastCrossMonitorCount = count;
        _lastCrossMonitorNotice = now;
        StatusRequested?.Invoke(
            $"分区模式：{count} 个窗口在另一台显示器上，未加入当前工作区",
            ZoneStatusLevel.Warn);
    }

    // ---------------- 规划 ----------------

    /// <summary>每个窗口分到的格子都不小于它声明的最小尺寸。</summary>
    private static bool ZoneFits(ZoneWorkspace workspace) =>
        workspace.Compute().All(p =>
            p.Rect.Width >= p.Window.MinWidth && p.Rect.Height >= p.Window.MinHeight);

    private void PreserveRatios(ZoneWorkspace workspace, int windowCount)
    {
        var current = Workspace;
        if (current != null && current.WindowsInOrder().Count == windowCount)
        {
            workspace.CopyRatiosFrom(current);
        }
    }

    /// <summary>
    /// 规划入口：网格走 <see cref="ZoneWorkspace.PlanGrid"/>（3/4 窗左日右口 / 左右两个日，
    /// 5/6 窗逐排列拟合；<paramref name="allowMainSwap"/> 为 false 时只接受自然主窗的解），
    /// 左右并排走 <see cref="ZoneWorkspace.PlanColumns"/>（每行列数 + 舒适宽度换行），
    /// 其余（主窗在左 / 上下堆叠）仍按 <see cref="ZoneWorkspace.Create"/>。
    /// 返回值第二个元素非 null 表示“需要用户同意换主窗”，此时不能应用第一个元素。
    /// </summary>
    private (ZoneWorkspace Workspace, WindowInfo? MainSwapRequested) CreateWorkspace(
        LayoutKind kind, IReadOnlyList<WindowInfo> windows, WindowRect area, int gap,
        bool allowMainSwap = true)
    {
        ZoneWorkspace workspace;
        WindowInfo? swap = null;

        if (kind == LayoutKind.Grid)
        {
            var planned = ZoneWorkspace.PlanGrid(windows, area, gap, keepMain: !allowMainSwap);
            if (planned != null)
            {
                workspace = planned.Value.Workspace;
                swap = planned.Value.MainSwapRequested;
                return (workspace, swap);
            }
        }

        workspace = kind == LayoutKind.Columns
            ? ZoneWorkspace.PlanColumns(windows, area, gap, _getColumnsLimit())
            : ZoneWorkspace.Create(kind, windows, area, gap);
        return (workspace, swap);
    }
}
