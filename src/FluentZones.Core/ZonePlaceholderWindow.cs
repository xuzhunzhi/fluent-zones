namespace FluentZones.Core;

/// <summary>一个占位窗的几何与文字（文字暂只作为窗口标题保留，外观为纯色）。</summary>
public readonly record struct ZonePlaceholderSpec(WindowRect Rect, string Label);

/// <summary>
/// 工作区会话期间的占位窗口管理：每个空余条带一个无激活、点击穿透的自有窗口。
/// 只在 UI 线程调用；<see cref="Enabled"/> 为 false 时不真正建窗（无头测试）。
/// </summary>
public sealed class ZonePlaceholderManager : IDisposable
{
    private readonly List<IntPtr> _handles = new();
    private readonly List<ZonePlaceholderSpec> _last = new();

    public IReadOnlyList<IntPtr> Handles => _handles;

    public bool Enabled { get; set; } = false;

    public void Update(IReadOnlyList<ZonePlaceholderSpec>? specs)
    {
        var next = specs ?? Array.Empty<ZonePlaceholderSpec>();

        // 占位窗的几何没变就不要销毁重建：app 自己 SetWindowPos 会触发 LOCATIONCHANGE，
        // 反复 Apply 时 Clear+Create 会让屏幕上的黑带闪烁。标题变化不影响外观，也不重建。
        if (_last.Count == next.Count)
        {
            var same = true;
            for (var i = 0; i < next.Count; i++)
            {
                if (_last[i].Rect != next[i].Rect)
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return;
            }
        }

        Clear();
        _last.AddRange(next);

        if (!Enabled)
        {
            return;
        }

        foreach (var spec in next)
        {
            if (spec.Rect.Width < ZoneAspectFit.MinGap ||
                spec.Rect.Height < ZoneAspectFit.MinGap)
            {
                continue;
            }

            var handle = ZonePlaceholderWindow.Create(spec.Rect, spec.Label);
            if (handle != IntPtr.Zero)
            {
                _handles.Add(handle);
            }
        }
    }

    public void Clear()
    {
        foreach (var handle in _handles)
        {
            ZonePlaceholderWindow.Destroy(handle);
        }

        _handles.Clear();
        _last.Clear();
    }

    public void Dispose() => Clear();
}

/// <summary>极简的 Win32 纯色占位窗口：无激活、无边框、点击穿透、置顶。</summary>
internal static class ZonePlaceholderWindow
{
    private const string ClassName = "FluentZones.Placeholder";
    private static readonly object Gate = new();
    private static NativeMethods.WndProc? _wndProc;
    private static bool _classRegistered;

    public static IntPtr Create(WindowRect rect, string label)
    {
        if (!EnsureClass())
        {
            return IntPtr.Zero;
        }

        var handle = NativeMethods.CreateWindowEx(
            (uint)(NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW |
                   NativeMethods.WS_EX_TRANSPARENT),
            ClassName,
            label,
            NativeMethods.WS_POPUP,
            rect.Left, rect.Top, rect.Width, rect.Height,
            IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        NativeMethods.SetWindowPos(
            handle, NativeMethods.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOWNOACTIVATE);
        return handle;
    }

    public static void Destroy(IntPtr handle)
    {
        if (handle != IntPtr.Zero && NativeMethods.IsWindow(handle))
        {
            NativeMethods.DestroyWindow(handle);
        }
    }

    private static bool EnsureClass()
    {
        if (_classRegistered)
        {
            return true;
        }

        lock (Gate)
        {
            if (_classRegistered)
            {
                return true;
            }

            _wndProc = WndProc;
            var wc = new NativeMethods.WNDCLASS
            {
                style = (uint)(NativeMethods.CS_HREDRAW | NativeMethods.CS_VREDRAW),
                lpfnWndProc = _wndProc,
                hInstance = NativeMethods.GetModuleHandle(null),
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW),
                hbrBackground = NativeMethods.CreateSolidBrush(0x00202020),
                lpszClassName = ClassName,
            };

            NativeMethods.RegisterClass(ref wc);
            _classRegistered = true;
            GC.KeepAlive(_wndProc);
            return true;
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            return new IntPtr(NativeMethods.HTTRANSPARENT);
        }

        if (msg == NativeMethods.WM_PAINT)
        {
            var paint = NativeMethods.BeginPaint(hWnd, out var ps);
            NativeMethods.EndPaint(hWnd, ref ps);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }
}
