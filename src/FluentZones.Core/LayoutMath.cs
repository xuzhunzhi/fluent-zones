namespace FluentZones.Core;

/// <summary>
/// 纯计算：在给定总长度里按"每个窗口的最小尺寸"分配尺寸。
/// 所有窗口先拿自己的最小值，剩余空间平均分；放不下时返回 null，由调用方决定分行或降级。
/// </summary>
internal static class LayoutMath
{
    /// <summary>分配一行的宽度 / 一列的高度。</summary>
    /// <param name="total">可用总长度。</param>
    /// <param name="gap">相邻窗口之间的空隙。</param>
    /// <param name="minimums">每个窗口声明的最小尺寸。</param>
    /// <param name="floor">本工具的绝对下限，防止 0 尺寸的自绘窗口。</param>
    public static int[]? Allocate(int total, int gap, IReadOnlyList<int> minimums, int floor)
    {
        var count = minimums.Count;
        if (count == 0)
        {
            return Array.Empty<int>();
        }

        var sizes = new int[count];
        long required = (long)gap * (count - 1);

        for (var i = 0; i < count; i++)
        {
            sizes[i] = Math.Max(minimums[i], floor);
            required += sizes[i];
        }

        if (required > total)
        {
            return null;
        }

        var extra = total - (int)required;
        var share = extra / count;
        var remainder = extra % count;
        for (var i = 0; i < count; i++)
        {
            sizes[i] += share + (i < remainder ? 1 : 0);
        }

        return sizes;
    }

    /// <summary>一行最多能放几个（按顺序切块，每块都要放得下）。</summary>
    public static int MaxPerCount(IReadOnlyList<int> minimums, int total, int gap, int floor)
    {
        var count = minimums.Count;

        for (var per = count; per >= 1; per--)
        {
            var fits = true;
            for (var start = 0; start < count; start += per)
            {
                var take = Math.Min(per, count - start);
                long required = (long)gap * (take - 1);
                for (var i = 0; i < take; i++)
                {
                    required += Math.Max(minimums[start + i], floor);
                }

                if (required > total)
                {
                    fits = false;
                    break;
                }
            }

            if (fits)
            {
                return per;
            }
        }

        return 1;
    }
}
