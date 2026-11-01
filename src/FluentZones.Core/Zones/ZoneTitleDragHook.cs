using System.Runtime.InteropServices;

namespace FluentZones.Core;

/// <summary>
/// 工作区窗口标题栏拖拽交换：低层鼠标钩子接管 HTCAPTION 上的按下；
/// 移动超过 4px 才进入拖拽，拖动时显示 PrintWindow 截出的半透明缩略图，
/// 目标槽位铺一层灰色半透明（可点穿），松手回调目标窗口。
/// 单击激活窗口，双击最大化/还原；提权窗口不参与。
/// </summary>
public sealed class ZoneTitleDragHook : IDisposable
{
    private const int WmHide = 0x8000 + 61;
    private const int WmCaptureFailure = 0x8000 + 62;
    private const int DragThreshold = 4;

    private readonly Func<ZoneWorkspace?> _getWorkspace;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _className = "FluentZones.ZoneTitleDrag." + Guid.NewGuid().ToString("N");

    private NativeMethods.HookProc? _proc;
    private NativeMethods.WndProc? _wndProc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private IntPtr _thumbHwnd;
    private IntPtr _grayHwnd;
    private IntPtr _thumbDc;
    private IntPtr _thumbDib;
    private IntPtr _thumbBits;
    private int _thumbWidth;
    private int _thumbHeight;
    private IntPtr _grayDc;
    private IntPtr _grayDib;
    private IntPtr _grayBits;
    private int _grayWidth;
    private int _grayHeight;
    private WindowRect _grayArea;

    private IntPtr _pendingWindow;
    private WindowInfo? _pendingInfo;
    private NativeMethods.POINT _downPoint;
    private bool _dragging;
    private IntPtr _lastClickWindow;
    private long _lastClickTick;
    private Exception? _startupError;

    public event Action? DragStarted;
    public event Action? DragEnded;
    public event Action<WindowInfo, WindowInfo>? WindowDropped;

    public ZoneTitleDragHook(Func<ZoneWorkspace?> getWorkspace)
    {
        _getWorkspace = getWorkspace;
    }

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(HookLoop)
        {
            IsBackground = true,
            Name = "FluentZones.ZoneTitleDrag",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(10)) || _startupError != null ||
            _hook == IntPtr.Zero || _thumbHwnd == IntPtr.Zero || _grayHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("标题栏交换钩子启动失败", _startupError);
        }
    }

    /// <summary>退出工作区时清理半透明缩略图/灰色覆盖层和未完成的拖拽。</summary>
    public void Hide()
    {
        if (_thread != null)
        {
            NativeMethods.PostThreadMessage(_threadId, WmHide, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void HookLoop()
    {
        _threadId = (uint)Environment.CurrentManagedThreadId;

        try
        {
            _proc = HookProc;
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
                throw new InvalidOperationException("注册标题栏交换窗口类失败");
            }

            const int exStyle = NativeMethods.WS_EX_LAYERED |
                                NativeMethods.WS_EX_TRANSPARENT |
                                NativeMethods.WS_EX_TOOLWINDOW |
                                NativeMethods.WS_EX_TOPMOST |
                                NativeMethods.WS_EX_NOACTIVATE;

            _thumbHwnd = NativeMethods.CreateWindowEx(
                exStyle, _className, "FluentZones Swap", NativeMethods.WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            _grayHwnd = NativeMethods.CreateWindowEx(
                exStyle, _className, "FluentZones Swap Slots", NativeMethods.WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (_thumbHwnd == IntPtr.Zero || _grayHwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("创建标题栏交换覆盖层失败");
            }

            NativeMethods.SetLayeredWindowAttributes(_thumbHwnd, 0, 190, NativeMethods.LWA_ALPHA);

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
            if (msg.message == WmHide)
            {
                HideVisuals();
                continue;
            }

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        CleanupSurfaces();
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var message = wParam.ToInt32();

            if (message == NativeMethods.WM_LBUTTONDOWN)
            {
                if (TryGetCaptionWindow(info.pt, out var hwnd, out var window))
                {
                    _pendingWindow = hwnd;
                    _pendingInfo = window;
                    _downPoint = info.pt;
                    _dragging = false;
                    return new IntPtr(1);
                }
            }
            else if (message == NativeMethods.WM_MOUSEMOVE && _pendingWindow != IntPtr.Zero)
            {
                if (!_dragging)
                {
                    var dx = info.pt.X - _downPoint.X;
                    var dy = info.pt.Y - _downPoint.Y;
                    if (dx * dx + dy * dy > DragThreshold * DragThreshold)
                    {
                        _dragging = true;
                        _lastClickWindow = IntPtr.Zero;
                        _lastClickTick = 0;
                        StartVisuals(info.pt);
                        var started = DragStarted;
                        started?.Invoke();
                    }
                }

                if (_dragging)
                {
                    MoveVisuals(info.pt);
                    return new IntPtr(1);
                }
            }
            else if (message == NativeMethods.WM_LBUTTONUP && _pendingWindow != IntPtr.Zero)
            {
                var wasDragging = _dragging;
                var source = _pendingInfo;
                var sourceHwnd = _pendingWindow;
                _pendingWindow = IntPtr.Zero;
                _pendingInfo = null;
                _dragging = false;

                if (!wasDragging)
                {
                    HandleClick(sourceHwnd);
                    return new IntPtr(1);
                }

                var target = FindTarget(info.pt);
                HideVisuals();
                var ended = DragEnded;
                ended?.Invoke();

                if (source != null && target != null &&
                    target.Window.Handle != source.Handle)
                {
                    WindowDropped?.Invoke(source, target.Window);
                }

                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandleClick(IntPtr hwnd)
    {
        var now = Environment.TickCount64;
        var doubleClick = hwnd == _lastClickWindow &&
                          now - _lastClickTick <= NativeMethods.GetDoubleClickTime();

        if (doubleClick)
        {
            _lastClickWindow = IntPtr.Zero;
            _lastClickTick = 0;
            NativeMethods.ShowWindow(
                hwnd,
                NativeMethods.IsZoomed(hwnd) ? NativeMethods.SW_RESTORE : NativeMethods.SW_MAXIMIZE);
            return;
        }

        NativeMethods.SetForegroundWindow(hwnd);
        _lastClickWindow = hwnd;
        _lastClickTick = now;
    }

    private bool TryGetCaptionWindow(
        NativeMethods.POINT point, out IntPtr hwnd, out WindowInfo? window)
    {
        hwnd = IntPtr.Zero;
        window = null;

        var workspace = _getWorkspace();
        if (workspace == null)
        {
            return false;
        }

        var under = NativeMethods.WindowFromPoint(point);
        if (under == IntPtr.Zero)
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(under, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero || NativeMethods.IsHungAppWindow(root))
        {
            return false;
        }

        window = workspace.WindowsInOrder().FirstOrDefault(w => w.Handle == root);
        if (window == null || window.CannotMove)
        {
            return false;
        }

        var lParam = unchecked((IntPtr)((point.Y << 16) | (point.X & 0xFFFF)));
        var result = NativeMethods.SendMessageTimeout(
            root, NativeMethods.WM_NCHITTEST, IntPtr.Zero, lParam,
            NativeMethods.SMTO_ABORTIFHUNG, 50, out var hit);
        if (result == IntPtr.Zero || hit.ToInt64() != NativeMethods.HTCAPTION)
        {
            return false;
        }

        hwnd = root;
        return true;
    }

    private ZonePlacement? FindTarget(NativeMethods.POINT point)
    {
        var workspace = _getWorkspace();
        if (workspace == null)
        {
            return null;
        }

        var area = workspace.Area;
        if (point.X < area.Left || point.X >= area.Right ||
            point.Y < area.Top || point.Y >= area.Bottom)
        {
            return null;
        }

        return workspace.Compute().FirstOrDefault(p =>
            point.X >= p.Rect.Left && point.X < p.Rect.Right &&
            point.Y >= p.Rect.Top && point.Y < p.Rect.Bottom);
    }

    private void StartVisuals(NativeMethods.POINT point)
    {
        var workspace = _getWorkspace();
        if (workspace == null || _pendingWindow == IntPtr.Zero)
        {
            return;
        }

        CaptureThumbnail(_pendingWindow);
        MoveThumbnail(point);
        ShowThumbnail();
        UpdateGrayTarget(point);
    }

    private void MoveVisuals(NativeMethods.POINT point)
    {
        MoveThumbnail(point);
        UpdateGrayTarget(point);
    }

    private void CaptureThumbnail(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var width = Math.Max(rect.Width, 1);
        var height = Math.Max(rect.Height, 1);
        if (_thumbDc == IntPtr.Zero || width != _thumbWidth || height != _thumbHeight)
        {
            DestroyThumbSurface();
            var screen = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                _thumbDc = NativeMethods.CreateCompatibleDC(screen);
                var bmi = new NativeMethods.BITMAPINFO
                {
                    bmiHeader = new NativeMethods.BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0,
                    },
                };
                _thumbDib = NativeMethods.CreateDIBSection(
                    _thumbDc, ref bmi, 0, out _thumbBits, IntPtr.Zero, 0);
                if (_thumbDib != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(_thumbDc, _thumbDib);
                }
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }

            _thumbWidth = width;
            _thumbHeight = height;
        }

        if (_thumbDc == IntPtr.Zero)
        {
            return;
        }

        if (!NativeMethods.PrintWindow(hwnd, _thumbDc, NativeMethods.PW_RENDERFULLCONTENT))
        {
            // PrintWindow 对部分应用无效：退化为当前屏幕区域复制，至少能跟手。
            var screen = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                NativeMethods.BitBlt(
                    _thumbDc, 0, 0, width, height, screen, rect.Left, rect.Top,
                    NativeMethods.SRCCOPY);
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        NativeMethods.InvalidateRect(_thumbHwnd, IntPtr.Zero, true);
    }

    private void MoveThumbnail(NativeMethods.POINT point)
    {
        if (_thumbHwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _thumbHwnd, new IntPtr(-1), point.X + 12, point.Y + 12,
            Math.Max(_thumbWidth, 1), Math.Max(_thumbHeight, 1),
            NativeMethods.SWP_NOACTIVATE);
    }

    private void ShowThumbnail()
    {
        if (_thumbHwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_thumbHwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }
    }

    private void UpdateGrayTarget(NativeMethods.POINT point)
    {
        var workspace = _getWorkspace();
        var target = FindTarget(point);
        if (workspace == null || target == null)
        {
            HideGray();
            return;
        }

        EnsureGraySurface(workspace.Area);
        if (_grayBits == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.RtlZeroMemory(
            _grayBits, (UIntPtr)(_grayWidth * _grayHeight * 4));

        // 灰色半透明、不描边：预乘 alpha 的 BGRA。
        var alpha = 70;
        var gray = (byte)(40);
        var value = unchecked((int)((uint)alpha << 24 | (uint)gray << 16 |
                                   (uint)gray << 8 | gray));

        var r = target.Rect;
        var x0 = Math.Max(0, r.Left - workspace.Area.Left);
        var y0 = Math.Max(0, r.Top - workspace.Area.Top);
        var x1 = Math.Min(_grayWidth, r.Right - workspace.Area.Left);
        var y1 = Math.Min(_grayHeight, r.Bottom - workspace.Area.Top);

        for (var y = y0; y < y1; y++)
        {
            var offset = (y * _grayWidth + x0) * 4;
            for (var x = x0; x < x1; x++)
            {
                Marshal.WriteInt32(_grayBits, offset, value);
                offset += 4;
            }
        }

        UpdateGrayLayered(workspace.Area);
    }

    private void EnsureGraySurface(WindowRect area)
    {
        if (_grayDc != IntPtr.Zero && _grayArea == area &&
            _grayWidth == Math.Max(area.Width, 1) && _grayHeight == Math.Max(area.Height, 1))
        {
            return;
        }

        DestroyGraySurface();
        _grayArea = area;
        _grayWidth = Math.Max(area.Width, 1);
        _grayHeight = Math.Max(area.Height, 1);

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            _grayDc = NativeMethods.CreateCompatibleDC(screen);
            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = _grayWidth,
                    biHeight = -_grayHeight,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
            };
            _grayDib = NativeMethods.CreateDIBSection(
                _grayDc, ref bmi, 0, out _grayBits, IntPtr.Zero, 0);
            if (_grayDib != IntPtr.Zero)
            {
                NativeMethods.SelectObject(_grayDc, _grayDib);
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void UpdateGrayLayered(WindowRect area)
    {
        if (_grayHwnd == IntPtr.Zero || _grayDc == IntPtr.Zero || _grayBits == IntPtr.Zero)
        {
            return;
        }

        var screen = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var dst = new NativeMethods.POINT { X = area.Left, Y = area.Top };
            var size = new NativeMethods.SIZE { cx = _grayWidth, cy = _grayHeight };
            var src = new NativeMethods.POINT { X = 0, Y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA,
            };

            NativeMethods.UpdateLayeredWindow(
                _grayHwnd, screen, ref dst, ref size, _grayDc, ref src, 0, ref blend,
                NativeMethods.ULW_ALPHA);
            NativeMethods.ShowWindow(_grayHwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void HideGray()
    {
        if (_grayHwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_grayHwnd, NativeMethods.SW_HIDE);
        }
    }

    private void HideVisuals()
    {
        _pendingWindow = IntPtr.Zero;
        _pendingInfo = null;
        _dragging = false;
        if (_thumbHwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_thumbHwnd, NativeMethods.SW_HIDE);
        }

        HideGray();
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            return new IntPtr(-1); // HTTRANSPARENT：缩略图/灰色槽位都不吃鼠标
        }

        if (msg == 0x0021 /* WM_MOUSEACTIVATE */)
        {
            return new IntPtr(3); // MA_NOACTIVATE
        }

        if (msg == NativeMethods.WM_PAINT && hWnd == _thumbHwnd && _thumbDc != IntPtr.Zero)
        {
            NativeMethods.BeginPaint(hWnd, out var ps);
            NativeMethods.BitBlt(
                ps.hdc, 0, 0, _thumbWidth, _thumbHeight, _thumbDc, 0, 0,
                NativeMethods.SRCCOPY);
            NativeMethods.EndPaint(hWnd, ref ps);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void DestroyThumbSurface()
    {
        if (_thumbDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_thumbDc);
            _thumbDc = IntPtr.Zero;
        }

        if (_thumbDib != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_thumbDib);
            _thumbDib = IntPtr.Zero;
        }

        _thumbBits = IntPtr.Zero;
        _thumbWidth = 0;
        _thumbHeight = 0;
    }

    private void DestroyGraySurface()
    {
        if (_grayDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_grayDc);
            _grayDc = IntPtr.Zero;
        }

        if (_grayDib != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_grayDib);
            _grayDib = IntPtr.Zero;
        }

        _grayBits = IntPtr.Zero;
        _grayWidth = 0;
        _grayHeight = 0;
    }

    private void CleanupSurfaces()
    {
        DestroyThumbSurface();
        DestroyGraySurface();
    }

    public void Dispose()
    {
        if (_thread != null && _thread.IsAlive)
        {
            NativeMethods.PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _thread = null;
        _hook = IntPtr.Zero;
        _thumbHwnd = IntPtr.Zero;
        _grayHwnd = IntPtr.Zero;
        _ready.Dispose();
    }
}
