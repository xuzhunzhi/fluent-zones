using System.Text;

namespace FluentZones.Core;

/// <summary>
/// 鼠标离开工作区窗口时补发 <c>WM_MOUSELEAVE</c>。
///
/// 不能放在 <see cref="ZoneTitleDragHook"/> 的低层鼠标钩子里：那里每次移动都要
/// 在系统超时前返回，查 DWM 可见边框/遍历切分树可能让 Windows 直接把钩子摘掉，
/// 结果交换窗口也一起失效。这里用独立的轮询线程，慢也不会影响拖拽钩子。
///
/// 用“可见边框”而不是 <c>WindowFromPoint</c> 判断离开：窗口贴边时，光标移进
/// 4px 间隙后往往还压在窗口的 DWM 阴影里，系统不会发 leave，Edge/Chromium
/// 的垂直标签栏就会卡在展开状态。
/// </summary>
public sealed class ZoneHoverWatcher : IDisposable
{
    private const int PollIntervalMs = 40;
    private const int VisibleMargin = 0;

    private readonly Func<ZoneWorkspace?> _getWorkspace;
    private readonly object _gate = new();

    private Thread? _thread;
    private volatile bool _stop;
    private IntPtr _lastRoot;
    private WindowRect? _lastVisible;
    private bool _hoverLeft;

    public ZoneHoverWatcher(Func<ZoneWorkspace?> getWorkspace)
    {
        _getWorkspace = getWorkspace;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true })
            {
                return;
            }

            _stop = false;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "FluentZones.ZoneHoverWatcher",
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stop = true;
            _thread?.Join(TimeSpan.FromSeconds(1));
            _thread = null;
        }

        _lastRoot = IntPtr.Zero;
        _lastVisible = null;
        _hoverLeft = false;
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
        if (_getWorkspace() == null)
        {
            _lastRoot = IntPtr.Zero;
            _lastVisible = null;
            _hoverLeft = false;
            return;
        }

        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0)
        {
            // 正在拖拽（交换/分隔线/原生）：不要发 leave，等松手后再重新跟踪。
            _lastRoot = IntPtr.Zero;
            _lastVisible = null;
            _hoverLeft = false;
            return;
        }

        if (!NativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        var under = NativeMethods.WindowFromPoint(point);
        var root = under == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.GetAncestor(under, NativeMethods.GA_ROOT);

        if (root == _lastRoot)
        {
            if (_lastVisible is { } visible && !Contains(visible, point))
            {
                if (!_hoverLeft)
                {
                    _hoverLeft = true;
                    SendMouseLeave(root);
                }
            }
            else
            {
                _hoverLeft = false;
            }

            return;
        }

        var previous = _lastRoot;
        _lastRoot = root;
        _hoverLeft = false;
        _lastVisible = root == IntPtr.Zero ? null : GetVisibleBounds(root);

        if (previous != IntPtr.Zero && NativeMethods.IsWindow(previous))
        {
            // 即使新 root 还是同一个窗口的影子/边框，也补一次离开。
            SendMouseLeave(previous);
        }
    }

    private static WindowRect? GetVisibleBounds(IntPtr hwnd)
    {
        var visible = WindowOps.GetVisibleBounds(hwnd);
        if (visible != null)
        {
            return visible;
        }

        return NativeMethods.GetWindowRect(hwnd, out var rect)
            ? new WindowRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : null;
    }

    private static bool Contains(WindowRect rect, NativeMethods.POINT point)
    {
        var left = rect.Left - VisibleMargin;
        var top = rect.Top - VisibleMargin;
        var right = rect.Right + VisibleMargin;
        var bottom = rect.Bottom + VisibleMargin;
        return point.X >= left && point.X < right &&
               point.Y >= top && point.Y < bottom;
    }

    /// <summary>
    /// 补发鼠标离开。Chromium/Edge 的垂直标签栏展开有延迟，单次 leave 可能在
    /// 展开动画开始前就被忽略；再延迟补两次，期间如果光标回到窗口里就放弃。
    /// </summary>
    private static void SendMouseLeave(IntPtr hwnd)
    {
        NativeMethods.PostMessage(hwnd, NativeMethods.WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);

        if (!IsChromiumWindow(hwnd))
        {
            return;
        }

        ScheduleDelayedLeave(hwnd, 200);
        ScheduleDelayedLeave(hwnd, 500);
    }

    private static void ScheduleDelayedLeave(IntPtr hwnd, int delayMs)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs);
                if (!NativeMethods.IsWindow(hwnd) ||
                    !NativeMethods.GetCursorPos(out var point))
                {
                    return;
                }

                var under = NativeMethods.WindowFromPoint(point);
                var root = under == IntPtr.Zero
                    ? IntPtr.Zero
                    : NativeMethods.GetAncestor(under, NativeMethods.GA_ROOT);

                // 光标又回到窗口里了，不要再发 leave
                if (root == hwnd)
                {
                    return;
                }

                NativeMethods.PostMessage(hwnd, NativeMethods.WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                // 补发失败不影响功能
            }
        });
    }

    private static bool IsChromiumWindow(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, name, name.Capacity);
        return name.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
    }
}
