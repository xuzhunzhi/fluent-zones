using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowLayout.Core;

public sealed class HotkeyEventArgs : EventArgs
{
    public HotkeyEventArgs(HotkeyAction action) => Action = action;

    public HotkeyAction Action { get; }
}

/// <summary>
/// 全局热键服务：独立后台线程 + 隐藏窗口 + 自己的消息循环，
/// 不依赖 UI 线程，主窗口隐藏/最小化时依然有效。
/// 注意：RegisterHotKey 要求调用线程拥有目标窗口，所以注册/注销都排队到热键线程执行。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmRunCommands = 0x8000 + 7;

    private readonly object _gate = new();
    private readonly Dictionary<int, HotkeyCombo> _combos = new();
    private readonly Dictionary<int, HotkeyAction> _actions = new();
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _className = "WindowLayout.HotkeySink." + Guid.NewGuid().ToString("N");

    private NativeMethods.WndProc? _wndProc;
    private IntPtr _hwnd;
    private Thread? _thread;
    private int _threadId;
    private Exception? _startupError;
    private bool _disposed;

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "WindowLayout.Hotkeys",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("热键线程启动超时");
        }

        if (_startupError != null)
        {
            throw new InvalidOperationException("热键服务启动失败", _startupError);
        }

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("热键窗口创建失败");
        }
    }

    /// <summary>注册一个热键；失败时返回 false 并给出原因（通常是被其它程序占用）。</summary>
    public bool TryRegister(int id, HotkeyCombo combo, HotkeyAction action, out string? error)
    {
        error = null;

        if (_hwnd == IntPtr.Zero)
        {
            error = "热键服务尚未启动";
            return false;
        }

        string? failure = null;
        var registered = RunOnHotkeyThread(() =>
        {
            lock (_gate)
            {
                if (_combos.ContainsKey(id))
                {
                    UnregisterCore(id);
                }

                foreach (var pair in _combos)
                {
                    if (pair.Value.Modifiers == combo.Modifiers &&
                        pair.Value.VirtualKey == combo.VirtualKey)
                    {
                        failure = "和另一个动作的热键重复";
                        return false;
                    }
                }

                if (!NativeMethods.RegisterHotKey(
                        _hwnd, id, combo.Modifiers | NativeMethods.MOD_NOREPEAT, combo.VirtualKey))
                {
                    var code = Marshal.GetLastWin32Error();
                    failure = code == 1409
                        ? "已被其它程序占用"
                        : new Win32Exception(code).Message;
                    return false;
                }

                _combos[id] = combo;
                _actions[id] = action;
                return true;
            }
        });

        error = failure;
        return registered;
    }

    public void Unregister(int id)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        RunOnHotkeyThread(() =>
        {
            lock (_gate)
            {
                UnregisterCore(id);
            }

            return true;
        });
    }

    private void UnregisterCore(int id)
    {
        if (_hwnd != IntPtr.Zero && _combos.ContainsKey(id))
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }

        _combos.Remove(id);
        _actions.Remove(id);
    }

    private bool RunOnHotkeyThread(Func<bool> action)
    {
        if (_threadId == Environment.CurrentManagedThreadId)
        {
            return action();
        }

        var result = false;
        using var done = new ManualResetEventSlim(false);
        _commands.Enqueue(() =>
        {
            try
            {
                result = action();
            }
            catch
            {
                result = false;
            }
            finally
            {
                done.Set();
            }
        });

        if (!NativeMethods.PostMessage(_hwnd, WmRunCommands, IntPtr.Zero, IntPtr.Zero))
        {
            throw new InvalidOperationException("无法把请求转发到热键线程");
        }

        if (!done.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("热键线程无响应");
        }

        return result;
    }

    private void MessageLoop()
    {
        _threadId = Environment.CurrentManagedThreadId;

        try
        {
            _wndProc = WndProc;
            var windowClass = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = _wndProc,
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = _className,
            };

            if (NativeMethods.RegisterClass(ref windowClass) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 1410) // ERROR_CLASS_ALREADY_EXISTS
                {
                    throw new Win32Exception(error, "注册热键窗口类失败");
                }
            }

            _hwnd = NativeMethods.CreateWindowEx(
                0,
                _className,
                "WindowLayout Hotkey Sink",
                0,
                0, 0, 0, 0,
                IntPtr.Zero,
                IntPtr.Zero,
                windowClass.hInstance,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "创建热键窗口失败");
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
            return;
        }

        _ready.Set();

        while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmRunCommands:
                while (_commands.TryDequeue(out var command))
                {
                    command();
                }

                return IntPtr.Zero;

            case NativeMethods.WM_HOTKEY:
            {
                var id = wParam.ToInt32();
                HotkeyAction action;
                bool found;
                lock (_gate)
                {
                    found = _actions.TryGetValue(id, out action);
                }

                if (found)
                {
                    try
                    {
                        HotkeyPressed?.Invoke(this, new HotkeyEventArgs(action));
                    }
                    catch
                    {
                        // 绝不让 UI 的异常弄死消息循环
                    }
                }

                return IntPtr.Zero;
            }

            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case NativeMethods.WM_DESTROY:
                return IntPtr.Zero;

            default:
                return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                RunOnHotkeyThread(() =>
                {
                    lock (_gate)
                    {
                        foreach (var id in _combos.Keys.ToList())
                        {
                            UnregisterCore(id);
                        }
                    }

                    return true;
                });

                NativeMethods.PostMessage(_hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // 退出时尽力而为
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
