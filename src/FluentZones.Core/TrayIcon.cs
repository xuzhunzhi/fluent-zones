using System.Runtime.InteropServices;

namespace FluentZones.Core;

/// <summary>
/// 托盘图标：独立 STA 线程 + 隐藏窗口 + 消息循环，不依赖 WinUI。
/// 左键单击/双击触发 <see cref="OpenRequested"/>；右键菜单包含打开与退出。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmTray = 0x8000 + 41;
    private const int IdOpen = 1;
    private const int IdExit = 2;

    private readonly string _iconPath;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _className = "FluentZones.Tray." + Guid.NewGuid().ToString("N");

    private NativeMethods.WndProc? _wndProc;
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private bool _ownsIcon;
    private NativeMethods.NOTIFYICONDATA _data;
    private Exception? _startupError;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon(string iconPath)
    {
        _iconPath = iconPath;
    }

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "FluentZones.Tray",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("托盘线程启动超时");
        }

        if (_startupError != null)
        {
            throw new InvalidOperationException("托盘图标启动失败", _startupError);
        }

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("托盘窗口创建失败");
        }
    }

    private void MessageLoop()
    {
        try
        {
            _wndProc = WndProc;
            var windowClass = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = _wndProc,
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = _className,
            };

            if (NativeMethods.RegisterClass(ref windowClass) == 0 &&
                Marshal.GetLastWin32Error() != 1410)
            {
                throw new InvalidOperationException("注册托盘窗口类失败");
            }

            _hwnd = NativeMethods.CreateWindowEx(
                0, _className, "FluentZones Tray", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("创建托盘窗口失败");
            }

            _icon = LoadTrayIcon();
            if (_icon == IntPtr.Zero)
            {
                throw new InvalidOperationException("加载托盘图标失败");
            }

            _data = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
                uCallbackMessage = WmTray,
                hIcon = _icon,
                szTip = "Fluent Zones",
                szInfo = string.Empty,
                szInfoTitle = string.Empty,
            };

            if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref _data))
            {
                throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) 失败");
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;

            if (_icon != IntPtr.Zero)
            {
                if (_ownsIcon)
                {
                    NativeMethods.DestroyIcon(_icon);
                }

                _icon = IntPtr.Zero;
            }

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

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

    private IntPtr LoadTrayIcon()
    {
        if (!string.IsNullOrWhiteSpace(_iconPath) && File.Exists(_iconPath))
        {
            var loaded = NativeMethods.LoadImage(
                IntPtr.Zero, _iconPath, NativeMethods.IMAGE_ICON, 16, 16,
                NativeMethods.LR_LOADFROMFILE);
            if (loaded != IntPtr.Zero)
            {
                _ownsIcon = true;
                return loaded;
            }
        }

        _ownsIcon = false;
        return NativeMethods.LoadIcon(
            IntPtr.Zero, new IntPtr(NativeMethods.IDI_APPLICATION));
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(menu, 0, new UIntPtr(IdOpen), "打开主窗口");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, 0, new UIntPtr(IdExit), "退出");

            // 必须先让托盘窗口到前台，点菜单外面才会收起。
            NativeMethods.SetForegroundWindow(_hwnd);

            if (!NativeMethods.GetCursorPos(out var point))
            {
                return;
            }

            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_LEFTALIGN | NativeMethods.TPM_TOPALIGN,
                point.X, point.Y, 0, _hwnd, IntPtr.Zero);

            // 菜单消失后补一条空消息，避免菜单卡住。
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command == IdOpen)
            {
                Raise(OpenRequested);
            }
            else if (command == IdExit)
            {
                Raise(ExitRequested);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private static void Raise(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch
        {
            // 绝不让 UI 异常弄死托盘消息循环
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmTray)
        {
            var mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMessage is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_LBUTTONDBLCLK)
            {
                Raise(OpenRequested);
            }
            else if (mouseMessage == NativeMethods.WM_RBUTTONUP)
            {
                ShowMenu();
            }

            return IntPtr.Zero;
        }

        if (message == NativeMethods.WM_CLOSE)
        {
            RemoveIcon();
            NativeMethods.DestroyWindow(hWnd);
            return IntPtr.Zero;
        }

        if (message == NativeMethods.WM_DESTROY)
        {
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void RemoveIcon()
    {
        if (_icon != IntPtr.Zero)
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref _data);
            if (_ownsIcon)
            {
                NativeMethods.DestroyIcon(_icon);
            }

            _icon = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _hwnd = IntPtr.Zero;
        _ready.Dispose();
    }
}
