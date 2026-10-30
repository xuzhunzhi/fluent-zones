namespace FluentZones.Core;

/// <summary>
/// 顶层窗口的创建 / 销毁 / 显示 / 隐藏事件（SetWinEventHook，WINEVENT_OUTOFCONTEXT）。
/// 必须在有消息循环的线程上创建（WinUI 的 UI 线程即可），回调也回到该线程。
/// </summary>
public sealed class WinEventWatcher : IDisposable
{
    private readonly NativeMethods.WinEventProc _callback;
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>参数：窗口句柄、事件类型（EVENT_OBJECT_*）。</summary>
    public event Action<IntPtr, uint>? WindowEvent;

    public WinEventWatcher()
    {
        _callback = OnWinEvent;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_CREATE,
            NativeMethods.EVENT_OBJECT_HIDE,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("SetWinEventHook 失败");
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint msEventTime)
    {
        // 只看顶层窗口本身（idObject = OBJID_WINDOW，idChild = 0）
        if (hwnd == IntPtr.Zero || idObject != 0 || idChild != 0)
        {
            return;
        }

        WindowEvent?.Invoke(hwnd, eventType);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
