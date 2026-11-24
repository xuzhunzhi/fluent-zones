namespace FluentZones.Core;

/// <summary>
/// 自动隐藏任务栏的动态避让：轮询任务栏当前是否真的滑出来了，滑出时把工作区缩进去，
/// 收起后恢复铺满。只在分区工作区内运行，一次性布局不参与。
///
/// 轮询而不是监听 ABN_*：任务栏的显示/隐藏没有可靠的通知，ABM_GETSTATE 只反映设置，
/// rcWork 在自动隐藏时又始终是整屏；只能按当前命中结果判断。
/// </summary>
public sealed class TaskbarWatcher : IDisposable
{
    private const int PollIntervalMs = 120;
    private const int ApplyTimeoutMs = 800;

    private readonly Func<IntPtr> _getMonitor;
    private readonly Func<bool> _isActive;
    private readonly Func<bool> _isBusy;
    private readonly Func<WindowRect?> _getCurrentArea;
    private readonly Func<WindowRect, bool> _applyArea;
    private readonly Action<Action>? _postToUi;

    private Thread? _thread;
    private volatile bool _stop;
    private WindowRect? _lastLogged;

    public TaskbarWatcher(
        Func<IntPtr> getMonitor,
        Func<bool> isActive,
        Func<bool> isBusy,
        Func<WindowRect?> getCurrentArea,
        Func<WindowRect, bool> applyArea,
        Action<Action>? postToUi)
    {
        _getMonitor = getMonitor;
        _isActive = isActive;
        _isBusy = isBusy;
        _getCurrentArea = getCurrentArea;
        _applyArea = applyArea;
        _postToUi = postToUi;
    }

    public void Start()
    {
        if (_thread is { IsAlive: true })
        {
            return;
        }

        _stop = false;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "FluentZones.TaskbarWatcher",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        _lastLogged = null;
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                Poll();
            }
            catch
            {
                // 轮询失败不影响功能
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    private void Poll()
    {
        if (!_isActive())
        {
            return;
        }

        var monitor = _getMonitor();
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var work = NativeMethods.GetDynamicWorkArea(monitor);
        var area = ZoneWorkspace.InsetArea(
            new WindowRect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top));

        if (_getCurrentArea() == area)
        {
            _lastLogged = area;
            return;
        }

        if (_isBusy())
        {
            // 拖拽/弹窗期间先不动，下一轮再试
            return;
        }

        var applied = Apply(area);
        if (applied)
        {
            if (_lastLogged != area)
            {
                _lastLogged = area;
                LayoutLog.Write(new[]
                {
                    "=== taskbar area " + area,
                });
            }
        }
    }

    private bool Apply(WindowRect area)
    {
        if (_postToUi == null)
        {
            return _applyArea(area);
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _postToUi(() =>
        {
            try
            {
                completion.TrySetResult(_applyArea(area));
            }
            catch
            {
                completion.TrySetResult(false);
            }
        });

        return completion.Task.Wait(ApplyTimeoutMs) && completion.Task.Result;
    }
}
