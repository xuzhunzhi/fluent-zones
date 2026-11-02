namespace FluentZones.Core;

/// <summary>
/// 长宽比锁定窗口的适配几何：在槽位内按窗口自身比例等比缩放到最大并居中，
/// 空出来的上下/左右条带由占位窗口覆盖。
/// </summary>
public static class ZoneAspectFit
{
    public const int MinGap = 8;

    /// <summary>按窗口实际宽高比，把槽位内接矩形算出来。</summary>
    public static WindowRect Fit(WindowRect slot, int windowWidth, int windowHeight)
    {
        if (slot.Width <= 0 || slot.Height <= 0 ||
            windowWidth <= 0 || windowHeight <= 0)
        {
            return slot;
        }

        var scale = Math.Min(
            slot.Width / (double)windowWidth,
            slot.Height / (double)windowHeight);
        var width = Math.Clamp((int)Math.Floor(windowWidth * scale), 1, slot.Width);
        var height = Math.Clamp((int)Math.Floor(windowHeight * scale), 1, slot.Height);
        return new WindowRect(
            slot.Left + (slot.Width - width) / 2,
            slot.Top + (slot.Height - height) / 2,
            width,
            height);
    }

    /// <summary>适配矩形覆盖不到的上下（或左右）空余条带；小于 <see cref="MinGap"/> 的细条忽略。</summary>
    public static List<WindowRect> Gaps(WindowRect slot, WindowRect fitted)
    {
        var gaps = new List<WindowRect>(2);
        var clamped = new WindowRect(
            Math.Max(fitted.Left, slot.Left),
            Math.Max(fitted.Top, slot.Top),
            Math.Min(fitted.Width, slot.Width),
            Math.Min(fitted.Height, slot.Height));

        var top = Math.Max(0, clamped.Top - slot.Top);
        var bottom = Math.Max(0, slot.Bottom - clamped.Bottom);
        if (top >= MinGap)
        {
            gaps.Add(new WindowRect(slot.Left, slot.Top, slot.Width, top));
        }

        if (bottom >= MinGap)
        {
            gaps.Add(new WindowRect(slot.Left, clamped.Bottom, slot.Width, bottom));
        }

        if (gaps.Count > 0)
        {
            return gaps;
        }

        var left = Math.Max(0, clamped.Left - slot.Left);
        var right = Math.Max(0, slot.Right - clamped.Right);
        if (left >= MinGap)
        {
            gaps.Add(new WindowRect(slot.Left, slot.Top, left, slot.Height));
        }

        if (right >= MinGap)
        {
            gaps.Add(new WindowRect(clamped.Right, slot.Top, right, slot.Height));
        }

        return gaps;
    }
}
