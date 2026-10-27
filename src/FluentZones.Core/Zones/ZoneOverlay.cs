using System.Runtime.InteropServices;

namespace FluentZones.Core;

/// <summary>
/// 分区模式的覆盖层：一个置顶、不激活的分层窗口，画分隔线并处理拖拽。
/// 非分隔线区域对鼠标完全透明（WM_NCHITTEST 返回 HTTRANSPARENT）。
/// </summary>
public sealed class ZoneOverlay : IDisposable
{
    private const int WmShow = 0x8000 + 21;
    private const int WmHide = 0x8000 + 22;
    private const int WmRefresh = 0x8000 + 23;

    private const uint ColorKey = 0x00FF00FF;   // 洋红作透明色
    private const uint Accent = 0x00FF8D4C;     // #4C8DFF 的 COLORREF

    private readonly object _gate = new();
    private readonly string _className = "FluentZones.ZoneOverlay." + Guid.NewGuid().ToString("N");
    private readonly ManualResetEventSlim _ready = new(false);

    private NativeMethods.WndProc? _wndProc;
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _keyBrush;
    private IntPtr _accentBrush;
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

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "FluentZones.ZoneOverlay",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || _startupError != null || _hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("分区覆盖层启动失败", _startupError);
        }
    }

    public void Show(ZoneWorkspace workspace)
    {
        lock (_gate)
        {
            _workspace = workspace;
        }

        NativeMethods.PostMessage(_hwnd, WmShow, IntPtr.Zero, IntPtr.Zero);
    }

    public void Hide() => NativeMethods.PostMessage(_hwnd, WmHide, IntPtr.Zero, IntPtr.Zero);

    public void Refresh(ZoneWorkspace workspace)
    {
        lock (_gate)
        {
            _workspace = workspace;
        }

        NativeMethods.PostMessage(_hwnd, WmRefresh, IntPtr.Zero, IntPtr.Zero);
    }

    private void MessageLoop()
    {
        try
        {
            _wndProc = WndProc;
            _keyBrush = NativeMethods.CreateSolidBrush(ColorKey);
            _accentBrush = NativeMethods.CreateSolidBrush(Accent);

            var wc = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = _wndProc,
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = _className,
                hbrBackground = _keyBrush,
            };

            if (NativeMethods.RegisterClass(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException("注册分区覆盖层窗口类失败");
            }

            _hwnd = NativeMethods.CreateWindowEx(
                NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW |
                NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE,
                _className,
                "FluentZones Zones",
                NativeMethods.WS_POPUP,
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("创建分区覆盖层窗口失败");
            }

            NativeMethods.SetLayeredWindowAttributes(
                _hwnd, ColorKey, 0, NativeMethods.LWA_COLORKEY);
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
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmShow:
            {
                ZoneWorkspace? ws;
                lock (_gate)
                {
                    ws = _workspace;
                }

                if (ws != null)
                {
                    _area = ws.Area;
                    NativeMethods.SetWindowPos(
                        hWnd, new IntPtr(-1), _area.Left, _area.Top, _area.Width, _area.Height,
                        NativeMethods.SWP_NOACTIVATE);
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWNOACTIVATE);
                    NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, true);
                }

                return IntPtr.Zero;
            }

            case WmHide:
                NativeMethods.ShowWindow(hWnd, 0 /* SW_HIDE */);
                return IntPtr.Zero;

            case WmRefresh:
                NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, true);
                return IntPtr.Zero;

            case NativeMethods.WM_NCHITTEST:
            {
                var hit = HitTest(ClientX(lParam), ClientY(lParam));
                return new IntPtr(hit ? 1 : -1); // HTCLIENT / HTTRANSPARENT
            }

            case NativeMethods.WM_SETCURSOR:
            {
                if (HitTest(ClientX(lParam), ClientY(lParam)))
                {
                    var divider = FindDivider(ClientX(lParam), ClientY(lParam));
                    var cursor = NativeMethods.LoadCursor(IntPtr.Zero,
                        divider is { Horizontal: true } ? NativeMethods.IDC_SIZEWE : NativeMethods.IDC_SIZENS);
                    NativeMethods.SetCursor(cursor);
                    return new IntPtr(1);
                }

                break;
            }

            case NativeMethods.WM_LBUTTONDOWN:
            {
                var divider = FindDivider(ClientX(lParam), ClientY(lParam));
                if (divider != null)
                {
                    _dragging = divider.Node;
                    NativeMethods.SetCapture(hWnd);
                }

                return IntPtr.Zero;
            }

            case NativeMethods.WM_MOUSEMOVE:
            {
                if (_dragging != null)
                {
                    ZoneWorkspace? ws;
                    lock (_gate)
                    {
                        ws = _workspace;
                    }

                    if (ws != null && ws.Drag(_dragging, _area.Left + ClientX(lParam), _area.Top + ClientY(lParam)))
                    {
                        NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, true);
                        Changed?.Invoke(ws);
                    }
                }

                return IntPtr.Zero;
            }

            case NativeMethods.WM_LBUTTONUP:
                if (_dragging != null)
                {
                    _dragging = null;
                    NativeMethods.ReleaseCapture();
                }

                return IntPtr.Zero;

            case NativeMethods.WM_PAINT:
            {
                var ps = new NativeMethods.PAINTSTRUCT();
                var hdc = NativeMethods.BeginPaint(hWnd, out ps);
                var client = new NativeMethods.RECT { Left = 0, Top = 0, Right = _area.Width, Bottom = _area.Height };
                NativeMethods.FillRect(hdc, ref client, _keyBrush);

                ZoneWorkspace? ws;
                lock (_gate)
                {
                    ws = _workspace;
                }

                if (ws != null)
                {
                    foreach (var (line, _) in LineRects(ws))
                    {
                        var r = line;
                        r.Left -= _area.Left;
                        r.Right -= _area.Left;
                        r.Top -= _area.Top;
                        r.Bottom -= _area.Top;
                        NativeMethods.FillRect(hdc, ref r, _accentBrush);
                    }
                }

                NativeMethods.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            }

            case NativeMethods.WM_DESTROY:
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static int ClientX(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    private static int ClientY(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    private ZoneDivider? FindDivider(int x, int y)
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
            if (HitRect(divider).Contains(x, y))
            {
                return divider;
            }
        }

        return null;
    }

    private bool HitTest(int x, int y) => FindDivider(x, y) != null;

    private static List<(NativeMethods.RECT Rect, int Thickness)> LineRects(ZoneWorkspace ws)
    {
        var list = new List<(NativeMethods.RECT, int)>();
        foreach (var divider in ws.Dividers())
        {
            var region = divider.Region;
            var gap = ws.Gap;

            if (divider.Horizontal)
            {
                var usable = Math.Max(region.Width - gap, 2);
                var firstWidth = (int)Math.Round(usable * divider.Node.Ratio);
                var x = region.Left + firstWidth + gap / 2;
                list.Add((new NativeMethods.RECT
                {
                    Left = x - 1,
                    Right = x + 2,
                    Top = region.Top,
                    Bottom = region.Bottom,
                }, 3));
            }
            else
            {
                var usable = Math.Max(region.Height - gap, 2);
                var firstHeight = (int)Math.Round(usable * divider.Node.Ratio);
                var y = region.Top + firstHeight + gap / 2;
                list.Add((new NativeMethods.RECT
                {
                    Left = region.Left,
                    Right = region.Right,
                    Top = y - 1,
                    Bottom = y + 2,
                }, 3));
            }
        }

        return list;
    }

    private NativeMethods.RECT HitRect(ZoneDivider divider)
    {
        var region = divider.Region;

        if (divider.Horizontal)
        {
            var usable = Math.Max(region.Width - CurrentGap(), 2);
            var firstWidth = (int)Math.Round(usable * divider.Node.Ratio);
            var x = region.Left + firstWidth + CurrentGap() / 2;
            return new NativeMethods.RECT
            {
                Left = x - 5,
                Right = x + 6,
                Top = region.Top,
                Bottom = region.Bottom,
            };
        }

        var usableHeight = Math.Max(region.Height - CurrentGap(), 2);
        var firstHeight = (int)Math.Round(usableHeight * divider.Node.Ratio);
        var y = region.Top + firstHeight + CurrentGap() / 2;
        return new NativeMethods.RECT
        {
            Left = region.Left,
            Right = region.Right,
            Top = y - 5,
            Bottom = y + 6,
        };
    }

    private int CurrentGap()
    {
        lock (_gate)
        {
            return _workspace?.Gap ?? 0;
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(_hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));

        if (_keyBrush != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_keyBrush);
        }

        if (_accentBrush != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_accentBrush);
        }

        _ready.Dispose();
    }
}
