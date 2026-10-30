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

    private ZoneDragHook? _dragHook;
    private WinEventWatcher? _watcher;
    private bool _syncQueued;

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

    /// <summary>UI 正在弹对话框：此时不要重排，避免和对话流程打架。</summary>
    public Func<bool>? IsInteractionBusy { get; set; }

    /// <summary>进入工作区，或按新布局重建（窗口句柄集合按传入的窗口重置）。</summary>
    public void Enter(LayoutKind kind, IReadOnlyList<WindowInfo> windows, IntPtr monitor)
    {
        _sessionGeneration++;

        var workspace = CreateWorkspace(kind, windows, _getArea(monitor), _getGap());
        workspace.FitToMinimumSizes();

        Kind = kind;
        Monitor = monitor;
        Workspace = workspace;
        _windowHandles.Clear();
        foreach (var window in windows)
        {
            _windowHandles.Add(window.Handle);
        }

        EnsureDragHook().Show(workspace);
        Apply(workspace);
        StartWatch();
    }

    /// <summary>退出工作区：恢复窗口样式、摘掉拖拽钩子与窗口监听，窗口保持当前尺寸。</summary>
    public void Exit() => Close(notifyStatus: true);

    public void Dispose()
    {
        Close(notifyStatus: false);
        _dragHook?.Dispose();
        _dragHook = null;
    }

    private void Close(bool notifyStatus)
    {
        _sessionGeneration++;

        StopWatch();
        RestoreBorders();
        _dragHook?.Hide();
        Workspace = null;
        _windowHandles.Clear();

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
    }

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
        if (Workspace == null || _syncQueued)
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

    private async Task SyncAsync()
    {
        // 合并：把标志清掉再干活（和原实现「先入队、回调里清标志」一致）
        _syncQueued = false;

        // 对话框可能挂很久；期间用户可能退出/切换布局，返回后要能认出“这是上一代会话”
        var generation = _sessionGeneration;

        try
        {
            if (Workspace == null || (IsInteractionBusy?.Invoke() ?? false))
            {
                return;
            }

            var windows = _collectWindows()
                .Where(w => !w.CannotMove && w.Monitor == Monitor)
                .ToList();

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

            while (true)
            {
                if (generation != _sessionGeneration || Workspace == null)
                {
                    return;
                }

                var workspace = CreateWorkspace(Kind, candidates, area, gap);
                workspace.FitToMinimumSizes();
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
                for (var k = 1; k < removable.Count; k++)
                {
                    var drop = removable.Take(k).ToList();
                    var subset = candidates.Where(w => !drop.Contains(w)).ToList();
                    if (subset.Count == 0)
                    {
                        break;
                    }

                    var attempt = CreateWorkspace(Kind, subset, area, gap);
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
    /// 规划入口：3 / 4 窗的网格走 <see cref="ZoneWorkspace.PlanGrid"/>（左日右口 / 左右两个日），
    /// 左右并排走 <see cref="ZoneWorkspace.PlanColumns"/>（每行列数 + 舒适宽度换行），
    /// 其余（主窗在左 / 上下堆叠）仍按 <see cref="ZoneWorkspace.Create"/>。
    /// </summary>
    private ZoneWorkspace CreateWorkspace(
        LayoutKind kind, IReadOnlyList<WindowInfo> windows, WindowRect area, int gap)
    {
        if (kind == LayoutKind.Grid)
        {
            var planned = ZoneWorkspace.PlanGrid(windows, area, gap);
            if (planned != null)
            {
                return planned;
            }
        }

        if (kind == LayoutKind.Columns)
        {
            return ZoneWorkspace.PlanColumns(windows, area, gap, _getColumnsLimit());
        }

        return ZoneWorkspace.Create(kind, windows, area, gap);
    }
}
