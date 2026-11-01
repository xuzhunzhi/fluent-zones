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
    private readonly Action<Action>? _postToUi;

    private readonly HashSet<IntPtr> _windowHandles = new();
    private readonly HashSet<IntPtr> _bordered = new();
    private readonly Dictionary<IntPtr, int> _styles = new();
    private readonly List<IntPtr> _lastOrder = new();

    private ZoneDragHook? _dragHook;
    private ZoneTitleDragHook? _titleHook;
    private WinEventWatcher? _watcher;
    private bool _syncQueued;
    private bool _swapInProgress;

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
        Action<Action>? postToUi = null)
    {
        _collectWindows = collectWindows;
        _getArea = getArea;
        _getGap = getGap;
        _getColumnsLimit = getColumnsLimit ?? (() => 0);
        _postToUi = postToUi;
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
            // 与 SyncAsync 的“放不下”处理一致：不静默应用，也不把窗口压到最小尺寸以下
            StatusRequested?.Invoke(
                IsActive
                    ? $"分区模式（{LayoutNames.Display(kind)}）：窗口最小尺寸放不下，保持原布局"
                    : $"分区模式（{LayoutNames.Display(kind)}）：窗口最小尺寸放不下，无法进入",
                ZoneStatusLevel.Warn);
            return false;
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

        var placements = workspace.Compute();

        // 先把窗口自己的缩放边框去掉，再按分区摆放：边距只由我们的分隔线调整
        SyncBorders(placements);
        PlaceRequested?.Invoke(placements);

        _lastOrder.Clear();
        _lastOrder.AddRange(workspace.WindowsInOrder().Select(w => w.Handle));
    }

    // ---------------- 槽位交换 ----------------

    /// <summary>
    /// B3 标题栏拖拽开始：期间不因窗口位置变化被 realign 拉回，也不处理窗口事件重排；
    /// 结束（松手/取消）时调用 <see cref="EndSwap"/>。
    /// </summary>
    public void BeginSwap() => _swapInProgress = true;

    /// <summary>结束标题栏拖拽交换；下一轮窗口事件/重排恢复正常。</summary>
    public void EndSwap() => _swapInProgress = false;

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
        if (Workspace == null || _swapInProgress)
        {
            return;
        }

        // 工作区窗口被外部工具（如 DSH 的 computer use）改了位置/大小：把它拉回槽位。
        // 可能是程序自己 SetWindowPos 触发的事件，RealignAsync 比较后再决定要不要重排。
        if (eventType == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)
        {
            if (_dragHook?.IsDragging == true || !_windowHandles.Contains(hwnd))
            {
                return;
            }

            _ = QueueRealignAsync();
            return;
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

    private bool _realignQueued;
    private DateTime _lastRealign;

    /// <summary>合并高频 LOCATIONCHANGE，延迟 150ms 再比对，避免和外部程序互相抖。</summary>
    private async Task QueueRealignAsync()
    {
        if (_realignQueued)
        {
            return;
        }

        _realignQueued = true;
        var generation = _sessionGeneration;
        await Task.Delay(150);

        if (_postToUi == null)
        {
            await RealignAsync(generation);
        }
        else
        {
            _postToUi(() => _ = RealignAsync(generation));
        }
    }

    private async Task RealignAsync(int generation)
    {
        _realignQueued = false;

        if (Workspace == null || generation != _sessionGeneration ||
            _swapInProgress || _dragHook?.IsDragging == true)
        {
            return;
        }

        // 限频：一秒钟最多纠正一次，防止和会自行改尺寸的窗口来回打架
        if ((DateTime.UtcNow - _lastRealign).TotalMilliseconds < 1000)
        {
            return;
        }

        foreach (var placement in Workspace.Compute())
        {
            var actual = WindowOps.GetVisibleBounds(placement.Window.Handle);
            if (actual == null)
            {
                continue;
            }

            if (Math.Abs(actual.Value.Left - placement.Rect.Left) > 6 ||
                Math.Abs(actual.Value.Top - placement.Rect.Top) > 6 ||
                Math.Abs(actual.Value.Width - placement.Rect.Width) > 6 ||
                Math.Abs(actual.Value.Height - placement.Rect.Height) > 6)
            {
                _lastRealign = DateTime.UtcNow;
                Apply(Workspace);
                return;
            }
        }

        await Task.CompletedTask;
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

            var windows = LayoutService.ZoneWindowsOnMonitor(_collectWindows(), Monitor);

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
                return;
            }

            var handles = new HashSet<IntPtr>(windows.Select(w => w.Handle));
            if (handles.SetEquals(_windowHandles))
            {
                return;
            }

            var newcomers = windows.Where(w => !_windowHandles.Contains(w.Handle)).ToList();
            _windowHandles.Clear();
            foreach (var handle in handles)
            {
                _windowHandles.Add(handle);
            }

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
                        swapped.FitToMinimumSizes();
                        if (stillNeedsSwap != null || !ZoneFits(swapped))
                        {
                            StatusRequested?.Invoke(
                                "分区模式：窗口在询问期间发生变化，未能换主窗", ZoneStatusLevel.Warn);
                            return;
                        }

                        _windowHandles.Clear();
                        foreach (var window in latest)
                        {
                            _windowHandles.Add(window.Handle);
                        }

                        Workspace = swapped;
                        _dragHook?.Refresh(swapped);
                        Apply(swapped);

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

                // 放不下：从 Z 序最底层开始找「最小化几个才能排下」（新窗口永远保留）
                var removable = candidates.AsEnumerable().Reverse()
                    .Where(w => !newcomers.Any(n => n.Handle == w.Handle))
                    .ToList();

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

                var accepted = OverflowRequested != null &&
                               await OverflowRequested(victims.Count, newcomers);

                // 等待期间用户退出/重新进入：放弃这次加入，不要复活旧工作区
                if (generation != _sessionGeneration || Workspace == null)
                {
                    return;
                }

                if (!accepted)
                {
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

    // ---------------- 规划 ----------------

    /// <summary>每个窗口分到的格子都不小于它声明的最小尺寸。</summary>
    private static bool ZoneFits(ZoneWorkspace workspace) =>
        workspace.Compute().All(p =>
            p.Rect.Width >= p.Window.MinWidth && p.Rect.Height >= p.Window.MinHeight);

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
