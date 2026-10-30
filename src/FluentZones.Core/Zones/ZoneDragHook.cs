using System.Runtime.InteropServices;

namespace FluentZones.Core;

/// <summary>
/// 工作区拖拽器：不创建任何窗口、不画任何东西，只装一个低层鼠标钩子。
/// 鼠标在分隔线附近按下 → 进入拖拽；移动 → 改切分比例并重排两侧窗口；
/// 离开分隔线 → 恢复箭头。因为屏幕上没有覆盖层，所以不会抢窗口的 hover/点击。
/// </summary>
public sealed class ZoneDragHook : IDisposable
{
    private const int HitRadius = 10;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _thread;
    private IntPtr _hook;
    private NativeMethods.HookProc? _proc;
    private ZoneWorkspace? _workspace;
    private ZoneNode? _dragging;
    private WindowRect _area;
    private Exception? _startupError;

    /// <summary>拖拽改变了比例（参数是当前工作区，外部据此重排窗口）。</summary>
    public event Action<ZoneWorkspace>? Changed;

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(HookLoop)
        {
            IsBackground = true,
            Name = "FluentZones.ZoneDragHook",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || _startupError != null || _hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("分区拖拽钩子启动失败", _startupError);
        }
    }

    public void Show(ZoneWorkspace workspace)
    {
        lock (_gate)
        {
            _workspace = workspace;
            _area = workspace.Area;
        }
    }

    public void Refresh(ZoneWorkspace workspace) => Show(workspace);

    public void Hide()
    {
        lock (_gate)
        {
            _dragging = null;
            _workspace = null;
        }
    }

    private void HookLoop()
    {
        try
        {
            _proc = HookProc;
            _hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);

            if (_hook == IntPtr.Zero)
            {
                throw new InvalidOperationException("SetWindowsHookEx 失败");
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var message = wParam.ToInt32();

            if (message == NativeMethods.WM_LBUTTONDOWN)
            {
                var node = FindDivider(info.pt.X, info.pt.Y);
                if (node != null)
                {
                    lock (_gate)
                    {
                        _dragging = node;
                    }

                    SetCursor(horizontal: node.Horizontal);
                    return new IntPtr(1);   // 吃掉这次点击，别传给下面的窗口
                }
            }
            else if (message == NativeMethods.WM_MOUSEMOVE)
            {
                ZoneNode? dragging;
                ZoneWorkspace? ws;
                lock (_gate)
                {
                    dragging = _dragging;
                    ws = _workspace;
                }

                if (dragging != null && ws != null)
                {
                    // 按住 Ctrl 只动当前这条缝（拆开两个日）；否则同高的两条横缝合并成一条一起走
                    var snap = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) == 0;
                    if (ws.Drag(dragging, info.pt.X, info.pt.Y, snap))
                    {
                        Changed?.Invoke(ws);
                    }

                    return CallNext(nCode, wParam, lParam);
                }

                var hover = FindDivider(info.pt.X, info.pt.Y);
                if (hover != null)
                {
                    SetCursor(horizontal: hover.Horizontal);
                }
            }
            else if (message == NativeMethods.WM_LBUTTONUP)
            {
                bool wasDragging;
                lock (_gate)
                {
                    wasDragging = _dragging != null;
                    _dragging = null;
                }

                if (wasDragging)
                {
                    return new IntPtr(1);
                }
            }
        }

        return CallNext(nCode, wParam, lParam);
    }

    private IntPtr CallNext(int nCode, IntPtr wParam, IntPtr lParam) =>
        NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

    private static void SetCursor(bool horizontal) =>
        NativeMethods.SetCursor(NativeMethods.LoadCursor(
            IntPtr.Zero,
            horizontal ? NativeMethods.IDC_SIZEWE : NativeMethods.IDC_SIZENS));

    private ZoneNode? FindDivider(int x, int y)
    {
        ZoneWorkspace? ws;
        lock (_gate)
        {
            ws = _workspace;
        }

        if (ws == null)
        {
            return null;
        }

        foreach (var divider in ws.Dividers())
        {
            if (HitRect(divider, ws.Gap, _area).Contains(x, y))
            {
                return divider.Node;
            }
        }

        return null;
    }

    private static NativeMethods.RECT HitRect(ZoneDivider divider, int gap, WindowRect area)
    {
        var region = divider.Region;

        if (divider.Horizontal)
        {
            var usable = Math.Max(region.Width - gap, 2);
            var firstWidth = (int)Math.Round(usable * divider.Node.Ratio);
            var x = region.Left + firstWidth + gap / 2;
            return new NativeMethods.RECT
            {
                Left = x - HitRadius,
                Right = x + HitRadius,
                Top = region.Top,
                Bottom = region.Bottom,
            };
        }

        var usableHeight = Math.Max(region.Height - gap, 2);
        var firstHeight = (int)Math.Round(usableHeight * divider.Node.Ratio);
        var y = region.Top + firstHeight + gap / 2;
        return new NativeMethods.RECT
        {
            Left = region.Left,
            Right = region.Right,
            Top = y - HitRadius,
            Bottom = y + HitRadius,
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _dragging = null;
            _workspace = null;
        }

        if (_thread != null && _thread.IsAlive && _hook != IntPtr.Zero)
        {
            NativeMethods.PostThreadMessage(
                (uint)_thread.ManagedThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _hook = IntPtr.Zero;
        _ready.Dispose();
    }
}
