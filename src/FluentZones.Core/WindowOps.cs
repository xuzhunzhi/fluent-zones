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

    /// <summary>恢复系统默认边框色。</summary>
    public static void ResetBorderColor(IntPtr hwnd) =>
        SetBorderColor(hwnd, NativeMethods.DWMWA_COLOR_DEFAULT);

    /// <summary>临时去掉窗口自己的缩放边框（WS_THICKFRAME），返回原样式供恢复。</summary>
    public static int RemoveResizeBorder(IntPtr hwnd)
    {
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style & ~NativeMethods.WS_THICKFRAME);
        RefreshFrame(hwnd);
        return style;
    }

    /// <summary>恢复窗口原来的样式。</summary>
    public static void RestoreStyle(IntPtr hwnd, int style)
    {
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style);
        RefreshFrame(hwnd);
    }

    private static void RefreshFrame(IntPtr hwnd) =>
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_FRAMECHANGED);
}
