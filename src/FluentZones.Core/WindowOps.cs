using System.Runtime.InteropServices;

namespace FluentZones.Core;

/// <summary>对任意窗口做的小动作（分区模式里腾位置、画边框用）。</summary>
public static class WindowOps
{
    /// <summary>分区模式下窗口边框的颜色（COLORREF：0x00BBGGRR）。</summary>
    public const int ZoneBorderColor = 0x00FF8D4C;   // #4C8DFF

    /// <summary>最小化窗口（不激活）。</summary>
    public static void Minimize(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMINNOACTIVE);
        }
    }

    /// <summary>给窗口设置 DWM 边框颜色（Win11）；传 <see cref="NativeMethods.DWMWA_COLOR_DEFAULT"/> 恢复默认。</summary>
    public static void SetBorderColor(IntPtr hwnd, int color)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref color, sizeof(int));
    }

    /// <summary>窗口当前可见边缘（DWM 扩展边框，取不到时退回 GetWindowRect）。</summary>
    public static WindowRect? GetVisibleBounds(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var rect = new NativeMethods.RECT();
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect,
                Marshal.SizeOf<NativeMethods.RECT>()) != 0 &&
            !NativeMethods.GetWindowRect(hwnd, out rect))
        {
            return null;
        }

        return new WindowRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>恢复系统默认边框色。</summary>
    public static void ResetBorderColor(IntPtr hwnd) =>
        SetBorderColor(hwnd, NativeMethods.DWMWA_COLOR_DEFAULT);

    /// <summary>临时去掉窗口自己的缩放边框（WS_THICKFRAME），返回原样式供恢复。</summary>
    public static int RemoveResizeBorder(IntPtr hwnd)
    {
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);

        // 如果窗口本来就缺缩放边框（上一轮被强杀/没恢复干净），恢复时必须补回来，
        // 不能把“已去掉边框的样式”当成原样式存下来。
        var restore = (style & NativeMethods.WS_THICKFRAME) != 0
            ? style
            : style | NativeMethods.WS_THICKFRAME;

        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style & ~NativeMethods.WS_THICKFRAME);
        RefreshFrame(hwnd);
        return restore;
    }

    /// <summary>恢复窗口原来的样式；若窗口正处于最大化，补回最大化状态（改样式会把窗口还原）。</summary>
    public static void RestoreStyle(IntPtr hwnd, int style)
    {
        var wasMaximized = !NativeMethods.IsIconic(hwnd) && NativeMethods.IsZoomed(hwnd);

        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style);
        RefreshFrame(hwnd);

        // 恢复 WS_THICKFRAME 会让最大化窗口退回还原状态，这里再最大化一次。
        if (wasMaximized && !NativeMethods.IsZoomed(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MAXIMIZE);
        }
    }

    private static void RefreshFrame(IntPtr hwnd) =>
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_FRAMECHANGED);
}
