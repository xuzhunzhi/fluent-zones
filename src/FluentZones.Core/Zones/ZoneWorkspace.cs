namespace FluentZones.Core;

/// <summary>
/// 一个显示器上的分区工作区：用一棵 ZoneNode 树描述切分关系，
/// 拖拽分隔线就是改某个节点的 Ratio。所有布局都能映射成这棵树。
/// </summary>
public sealed class ZoneWorkspace
{
    private ZoneWorkspace(ZoneNode root, WindowRect area, int gap)
    {
        Root = root;
        Area = area;
        Gap = gap;
    }

    public ZoneNode Root { get; }

    public WindowRect Area { get; }

    public int Gap { get; private set; }

    /// <summary>拖拽时临时改窗口间隙（0 = 两扇窗贴边，边框合成一条）。</summary>
    public void SetGap(int gap) => Gap = Math.Max(0, gap);

    /// <summary>按指定布局把窗口组织成树。</summary>
    public static ZoneWorkspace Create(LayoutKind kind, IReadOnlyList<WindowInfo> windows, WindowRect area, int gap)
    {
        if (windows.Count == 0)
        {
            throw new ArgumentException("至少需要一个窗口", nameof(windows));
        }

        var root = kind switch
        {
            LayoutKind.Rows => Chain(windows, horizontal: false),
            LayoutKind.MainLeft => MainLeft(windows),
            LayoutKind.Grid => Grid(windows),
            _ => Chain(windows, horizontal: true),
        };

        return new ZoneWorkspace(root, area, gap);
    }

    private static ZoneNode Chain(IReadOnlyList<WindowInfo> windows, bool horizontal)
    {
        if (windows.Count == 1)
        {
            return ZoneNode.Leaf(windows[0]);
        }

        // 均分：每层第一块占 1/n
        return ZoneNode.Split(
            horizontal,
            ZoneNode.Leaf(windows[0]),
            Chain(windows.Skip(1).ToList(), horizontal),
            1.0 / windows.Count);
    }

    private static ZoneNode MainLeft(IReadOnlyList<WindowInfo> windows)
    {
        if (windows.Count == 1)
        {
            return ZoneNode.Leaf(windows[0]);
        }

        var right = Chain(windows.Skip(1).ToList(), horizontal: false);
        return ZoneNode.Split(true, ZoneNode.Leaf(windows[0]), right, 0.55);
    }

    /// <summary>主窗占可用空间的比例，网格平铺的"主窗 + 环绕"构图专用。</summary>
    private const double MainRatio = 0.62;

    /// <summary>
    /// 网格平铺：主窗 + 环绕的马赛克构图。
    /// 2 个窗口竖着一刀（62/38）；3 个及以上把主窗外的窗口按"先对半分给右侧列和底部行、
    /// 多出来的那块给底部"分成两组，各自独立切分——所以右侧列和底部行的分割线不必等高。
    /// 1 个窗口铺满。
    /// </summary>
    private static ZoneNode Grid(IReadOnlyList<WindowInfo> windows)
    {
        var count = windows.Count;

        if (count == 1)
        {
            return ZoneNode.Leaf(windows[0]);
        }

        if (count == 2)
        {
            return ZoneNode.Split(
                true, ZoneNode.Leaf(windows[0]), ZoneNode.Leaf(windows[1]), MainRatio);
        }

        // 除主窗外的 count-1 个格子：一半放右侧列，一半放底部行，余数给底部
        var rightCount = (count - 1) / 2;
        var top = ZoneNode.Split(
            true,
            ZoneNode.Leaf(windows[0]),
            Chain(windows.Skip(1).Take(rightCount).ToList(), horizontal: false),
            MainRatio);

        // 底部行有两个窗口时，分割线也和上面主窗的竖线对齐（同一列），否则两条竖线会错开
        var bottomWindows = windows.Skip(1 + rightCount).ToList();
        var bottom = bottomWindows.Count == 2
            ? ZoneNode.Split(
                true, ZoneNode.Leaf(bottomWindows[0]), ZoneNode.Leaf(bottomWindows[1]), MainRatio)
            : Chain(bottomWindows, horizontal: true);

        return ZoneNode.Split(false, top, bottom, MainRatio);
    }

    /// <summary>算出每个窗口的目标矩形（可见边框）。</summary>
    public List<ZonePlacement> Compute()
    {
        var result = new List<ZonePlacement>();
        ComputeNode(Root, Area, result);
        return result;
    }

    private void ComputeNode(ZoneNode node, WindowRect rect, List<ZonePlacement> result)
    {
        if (node.Window != null)
        {
            result.Add(new ZonePlacement(node.Window, rect));
            return;
        }

        var (first, second) = SplitRect(rect, node);
        ComputeNode(node.First!, first, result);
        ComputeNode(node.Second!, second, result);
    }

    private (WindowRect First, WindowRect Second) SplitRect(WindowRect rect, ZoneNode node)
    {
        if (node.Horizontal)
        {
            var usable = Math.Max(rect.Width - Gap, 2);
            var firstWidth = (int)Math.Round(usable * node.Ratio);
            return (
                new WindowRect(rect.Left, rect.Top, firstWidth, rect.Height),
                new WindowRect(rect.Left + firstWidth + Gap, rect.Top, usable - firstWidth, rect.Height));
        }

        var usableHeight = Math.Max(rect.Height - Gap, 2);
        var firstHeight = (int)Math.Round(usableHeight * node.Ratio);
        return (
            new WindowRect(rect.Left, rect.Top, rect.Width, firstHeight),
            new WindowRect(rect.Left, rect.Top + firstHeight + Gap, rect.Width, usableHeight - firstHeight));
    }

    /// <summary>当前所有分隔线（含拖拽热区所在的整段区域）。</summary>
    public List<ZoneDivider> Dividers()
    {
        var result = new List<ZoneDivider>();
        CollectDividers(Root, Area, result);
        return result;
    }

    private void CollectDividers(ZoneNode node, WindowRect rect, List<ZoneDivider> result)
    {
        if (node.Window != null)
        {
            return;
        }

        var (first, second) = SplitRect(rect, node);
        result.Add(new ZoneDivider(node, node.Horizontal, rect));
        CollectDividers(node.First!, first, result);
        CollectDividers(node.Second!, second, result);
    }

    /// <summary>拖动某个分隔线到指定屏幕坐标；返回比例是否真的变了。</summary>
    public bool Drag(ZoneNode node, int screenX, int screenY)
    {
        var divider = Dividers().FirstOrDefault(d => ReferenceEquals(d.Node, node));
        if (divider == null)
        {
            return false;
        }

        var region = divider.Region;
        var a = node.First!.MinimumSize(Gap);
        var b = node.Second!.MinimumSize(Gap);

        if (node.Horizontal)
        {
            var usable = Math.Max(region.Width - Gap, 2);
            var minFirst = Math.Min(a.Width, usable - 2);
            var maxFirst = Math.Max(minFirst, usable - b.Width);
            var first = Math.Clamp(screenX - region.Left, minFirst, maxFirst);
            var ratio = Math.Clamp(first / (double)usable, 0.02, 0.98);

            if (Math.Abs(ratio - node.Ratio) < 0.0005)
            {
                return false;
            }

            node.Ratio = ratio;
            return true;
        }

        var usableHeight = Math.Max(region.Height - Gap, 2);
        var minTop = Math.Min(a.Height, usableHeight - 2);
        var maxTop = Math.Max(minTop, usableHeight - b.Height);
        var top = Math.Clamp(screenY - region.Top, minTop, maxTop);
        var ratioV = Math.Clamp(top / (double)usableHeight, 0.02, 0.98);

        if (Math.Abs(ratioV - node.Ratio) < 0.0005)
        {
            return false;
        }

        node.Ratio = ratioV;
        return true;
    }
}
