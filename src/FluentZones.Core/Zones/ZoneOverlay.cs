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

    // 32bpp 预乘 BGRA：AARRGGBB
    private const uint AccentArgb = 0xFF4C8DFF;   // #4C8DFF，不透明
    private const uint BandArgb = 0x08020408;     // alpha=8 的极淡蓝：只用来接鼠标，不做可见分隔线

    private readonly object _gate = new();
    private readonly string _className = "FluentZones.ZoneOverlay." + Guid.NewGuid().ToString("N");
    private readonly ManualResetEventSlim _ready = new(false);

    private NativeMethods.WndProc? _wndProc;
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _dibBits;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private ZoneWorkspace? _workspace;
    private ZoneNode? _dragging;
    private WindowRect _area;
    private IntPtr _excludeHwnd;
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

    /// <summary>绘制时把这个窗口（Fluent Zones 自己）的区域挖空，线不会压到它上面。</summary>
    public void SetExclusion(IntPtr hwnd)
    {
        _excludeHwnd = hwnd;
    }

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

            var wc = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = _wndProc,
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = _className,
                hbrBackground = IntPtr.Zero,
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW),
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

            // 逐像素 alpha（UpdateLayeredWindow），只有画出来的部分可点击
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

                    // 置顶才能收到间隙里的鼠标；绘制时再把本程序自己的窗口区域挖掉
                    NativeMethods.SetWindowPos(
                        hWnd, new IntPtr(-1),
                        _area.Left, _area.Top, _area.Width, _area.Height,
                        NativeMethods.SWP_NOACTIVATE);
                    NativeMethods.ShowWindow(hWnd, NativeMethods.SW_SHOWNOACTIVATE);
                    EnsureSurface();
                    Render();
                }

                return IntPtr.Zero;
            }

            case WmHide:
                NativeMethods.ShowWindow(hWnd, 0 /* SW_HIDE */);
                return IntPtr.Zero;

            case WmRefresh:
                EnsureSurface();
                Render();
                return IntPtr.Zero;

            case NativeMethods.WM_NCHITTEST:
            {
                // 边框/间隙这条带子整条都归覆盖层，避免鼠标“漏”到下面的浏览器触发 hover
                var hit = InBand(ClientX(lParam), ClientY(lParam));
                return new IntPtr(hit ? 1 : -1); // HTCLIENT / HTTRANSPARENT
            }

            case NativeMethods.WM_SETCURSOR:
            {
                var divider = HitTest(ClientX(lParam), ClientY(lParam))
                    ? FindDivider(ClientX(lParam), ClientY(lParam))
                    : null;

                var cursor = NativeMethods.LoadCursor(
                    IntPtr.Zero,
                    divider == null
                        ? NativeMethods.IDC_ARROW
                        : divider.Horizontal
                            ? NativeMethods.IDC_SIZEWE
                            : NativeMethods.IDC_SIZENS);

                NativeMethods.SetCursor(cursor);
                return new IntPtr(1);
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
                        Render();
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
                NativeMethods.BeginPaint(hWnd, out ps);
                NativeMethods.EndPaint(hWnd, ref ps);
                Render();
                return IntPtr.Zero;
            }

            case NativeMethods.WM_DESTROY:
                DestroySurface();
                // 窗口已销毁，显式退出消息循环（DefWindowProc 不会替我们 PostQuitMessage）
                NativeMethods.PostQuitMessage(0);
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

        var sx = x + _area.Left;
        var sy = y + _area.Top;

        foreach (var divider in ws.Dividers())
        {
            if (HitRect(divider).Contains(sx, sy))
            {
                return divider;
            }
        }

        return null;
    }

    private bool HitTest(int x, int y) => FindDivider(x, y) != null;

    /// <summary>点是否落在窗口外圈的可点击/可绘制带子里。</summary>
    private bool InBand(int x, int y)
    {
        ZoneWorkspace? ws;
        lock (_gate)
        {
            ws = _workspace;
        }

        if (ws == null)
        {
            return false;
        }

        var sx = x + _area.Left;
        var sy = y + _area.Top;

        foreach (var placement in ws.Compute())
        {
            var r = placement.Rect;
            if (sx >= r.Left - 8 && sx < r.Right + 8 &&
                sy >= r.Top - 8 && sy < r.Bottom + 8)
            {
                return true;
            }
        }

        return false;
    }

    private void EnsureSurface()
    {
        var w = Math.Max(_area.Width, 1);
        var h = Math.Max(_area.Height, 1);
        if (_memDc != IntPtr.Zero && w == _surfaceWidth && h == _surfaceHeight)
        {
            return;
        }

        DestroySurface();

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            _memDc = NativeMethods.CreateCompatibleDC(screen);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }

        var bmi = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,     // 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        _dib = NativeMethods.CreateDIBSection(_memDc, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || _dibBits == IntPtr.Zero)
        {
            throw new InvalidOperationException("创建覆盖层画布失败");
        }

        NativeMethods.SelectObject(_memDc, _dib);
        _surfaceWidth = w;
        _surfaceHeight = h;
    }

    private void DestroySurface()
    {
        if (_memDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        _dibBits = IntPtr.Zero;
        _surfaceWidth = 0;
        _surfaceHeight = 0;
    }

    private void Render()
    {
        if (_memDc == IntPtr.Zero || _dibBits == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.RtlZeroMemory(_dibBits, (UIntPtr)(_surfaceWidth * _surfaceHeight * 4));

        ZoneWorkspace? ws;
        lock (_gate)
        {
            ws = _workspace;
        }

        if (ws != null)
        {
            DrawZones(ws);
        }

        if (_excludeHwnd != IntPtr.Zero &&
            NativeMethods.GetWindowRect(_excludeHwnd, out var exclude))
        {
            FillRect(
                exclude.Left - _area.Left, exclude.Top - _area.Top,
                exclude.Width, exclude.Height, 0);
        }

        UpdateLayered();
    }

    /// <summary>
    /// 每扇窗的边框直接压在窗口边缘上（外侧 1px、内侧 2px）：
    /// 看起来就是窗口自己的一圈边框，窗口之间不会多出一条居中的分隔线；
    /// 边框像素是不透明的，鼠标可以直接抓。
    /// </summary>
    private void DrawZones(ZoneWorkspace ws)
    {
        foreach (var placement in ws.Compute())
        {
            var r = placement.Rect;
            var left = r.Left - _area.Left;
            var top = r.Top - _area.Top;
            var right = r.Right - _area.Left;
            var bottom = r.Bottom - _area.Top;

            FillRect(left - 1, top - 1, r.Width + 2, 3, AccentArgb);      // 上
            FillRect(left - 1, bottom - 2, r.Width + 2, 3, AccentArgb);   // 下
            FillRect(left - 1, top - 1, 3, r.Height + 2, AccentArgb);     // 左
            FillRect(right - 2, top - 1, 3, r.Height + 2, AccentArgb);    // 右
        }
    }

    private void FillRect(int x, int y, int width, int height, uint argb)
    {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(_surfaceWidth, x + width);
        var y1 = Math.Min(_surfaceHeight, y + height);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        for (var yy = y0; yy < y1; yy++)
        {
            var offset = (yy * _surfaceWidth + x0) * 4;
            for (var xx = x0; xx < x1; xx++)
            {
                Marshal.WriteInt32(_dibBits, offset, unchecked((int)argb));
                offset += 4;
            }
        }
    }

    private void UpdateLayered()
    {
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var dst = new NativeMethods.POINT { X = _area.Left, Y = _area.Top };
            var size = new NativeMethods.SIZE { cx = _surfaceWidth, cy = _surfaceHeight };
            var src = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };

            NativeMethods.UpdateLayeredWindow(
                _hwnd, screen, ref dst, ref size, _memDc, ref src, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
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
                Left = x - 9,
                Right = x + 10,
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
            Top = y - 9,
            Bottom = y + 10,
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
        _hwnd = IntPtr.Zero;   // 窗口已随 WM_DESTROY 销毁，置零避免重复 Dispose 再 PostMessage
        DestroySurface();

        _ready.Dispose();
    }
}
