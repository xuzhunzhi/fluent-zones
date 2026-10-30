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

    /// <summary>
    /// 分区可用区域四边留给窗口边框的边距。一次性布局与工作区必须用同一份区域，
    /// 否则同一个布局“进工作区前 / 进工作区后”的分隔线会差几个像素。
    /// </summary>
    public const int ZoneMargin = 4;

    /// <summary>把显示器工作区收成分区可用区域（四边内缩 <see cref="ZoneMargin"/>，至少 80px）。</summary>
    public static WindowRect InsetArea(WindowRect workArea) => new(
        workArea.Left + ZoneMargin,
        workArea.Top + ZoneMargin,
        Math.Max(workArea.Width - ZoneMargin * 2, 80),
        Math.Max(workArea.Height - ZoneMargin * 2, 80));

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

        // 4 个窗口 = 田：竖缝是同一个节点、贯穿上下两行（拖下段的缝，上段跟着动），
        // 两列各自再横切一刀，横缝同高。
        if (count == 4)
        {
            var leftColumn = ZoneNode.Split(
                false, ZoneNode.Leaf(windows[0]), ZoneNode.Leaf(windows[2]), MainRatio);
            var rightColumn = ZoneNode.Split(
                false, ZoneNode.Leaf(windows[1]), ZoneNode.Leaf(windows[3]), MainRatio);

            return ZoneNode.Split(true, leftColumn, rightColumn, MainRatio);
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

    /// <summary>
    /// 4 窗口的「左右两个日」：左列 62% 宽（上主窗 / 下副主窗），右列 38% 宽（上下平分）。
    /// 参数顺序是 [左上, 右上, 左下, 右下]；leftTopRatio 是左列上格占（高度-间隙）的比例。
    /// </summary>
    public static ZoneWorkspace CreateTwoRows(
        IReadOnlyList<WindowInfo> windows, WindowRect area, int gap, double leftTopRatio)
    {
        if (windows.Count != 4)
        {
            throw new ArgumentException("需要正好 4 个窗口", nameof(windows));
        }

        var left = ZoneNode.Split(
            false, ZoneNode.Leaf(windows[0]), ZoneNode.Leaf(windows[2]), leftTopRatio);
        var right = ZoneNode.Split(
            false, ZoneNode.Leaf(windows[1]), ZoneNode.Leaf(windows[3]), 0.5);

        return new ZoneWorkspace(ZoneNode.Split(true, left, right, 0.62), area, gap);
    }

    /// <summary>
    /// 3 窗口的「左日右口」：左列 62% 宽切成上下两格（日），右列 38% 宽一个窗口（口）。
    /// 参数顺序是 [左上, 左下, 右]；leftTopRatio 是左列上格占（高度-间隙）的比例。
    /// </summary>
    public static ZoneWorkspace CreateLeftDay(
        IReadOnlyList<WindowInfo> windows, WindowRect area, int gap, double leftTopRatio)
    {
        if (windows.Count != 3)
        {
            throw new ArgumentException("需要正好 3 个窗口", nameof(windows));
        }

        var left = ZoneNode.Split(
            false, ZoneNode.Leaf(windows[0]), ZoneNode.Leaf(windows[1]), leftTopRatio);

        return new ZoneWorkspace(ZoneNode.Split(true, left, ZoneNode.Leaf(windows[2]), 0.62), area, gap);
    }

    /// <summary>
    /// 网格平铺的槽位规划：3 窗左日右口、4 窗左右两个日；枚举窗口分配并按最小尺寸选位。
    /// 一次性布局与工作区共用同一份规划（工作区只是把结果留下来）。
    /// </summary>
    public static ZoneWorkspace? PlanGrid(IReadOnlyList<WindowInfo> windows, WindowRect area, int gap)
    {
        if (windows.Count == 3)
        {
            return PlanLeftDay(windows, area, gap);
        }

        if (windows.Count == 4)
        {
            return PlanTwoRows(windows, area, gap);
        }

        return null;
    }

    private static ZoneWorkspace PlanLeftDay(IReadOnlyList<WindowInfo> windows, WindowRect area, int gap)
    {
        var natural = new[] { 0, 1, 2 };
        var orders = Permutations(new[] { 0, 1, 2 })
            .OrderByDescending(p => p.SequenceEqual(natural));

        var usableW = area.Width - gap;
        var leftW = (int)Math.Round(usableW * 0.62);
        var rightW = usableW - leftW;
        var usableH = area.Height - gap;
        var half = (int)Math.Floor(usableH * 0.5);

        foreach (var order in orders)
        {
            var lt = windows[order[0]];
            var lb = windows[order[1]];
            var right = windows[order[2]];

            if (leftW < lt.MinWidth || leftW < lb.MinWidth || rightW < right.MinWidth ||
                half < lt.MinHeight || half < lb.MinHeight || usableH < right.MinHeight)
            {
                continue;
            }

            return CreateLeftDay(new[] { lt, lb, right }, area, gap, 0.5);
        }

        // 没有能全放下的分配时也保持「左日右口」的形状，按自然顺序铺
        return CreateLeftDay(new[] { windows[0], windows[1], windows[2] }, area, gap, 0.5);
    }

    private static ZoneWorkspace PlanTwoRows(IReadOnlyList<WindowInfo> windows, WindowRect area, int gap)
    {
        var natural = new[] { 0, 2, 1, 3 };
        var orders = Permutations(new[] { 0, 1, 2, 3 })
            .OrderByDescending(p => p.SequenceEqual(natural));

        var usableW = area.Width - gap;
        var leftW = (int)Math.Round(usableW * 0.62);
        var rightW = usableW - leftW;
        var usableH = area.Height - gap;
        var maxBottom = (int)Math.Floor(usableH * 0.5);

        foreach (var order in orders)
        {
            var lt = windows[order[0]];
            var rt = windows[order[1]];
            var lb = windows[order[2]];
            var rb = windows[order[3]];

            if (leftW < lt.MinWidth || leftW < lb.MinWidth ||
                rightW < rt.MinWidth || rightW < rb.MinWidth)
            {
                continue;
            }

            // 左下可以长高（≤50%），把副主窗的最小高度塞进去
            var bottom = Math.Max((int)Math.Round(usableH * 0.38), lb.MinHeight);
            bottom = Math.Min(bottom, maxBottom);
            var top = usableH - bottom;
            var half = usableH / 2;

            if (top < lt.MinHeight || bottom < lb.MinHeight ||
                half < rt.MinHeight || half < rb.MinHeight)
            {
                continue;
            }

            return CreateTwoRows(new[] { lt, rt, lb, rb }, area, gap, (double)top / usableH);
        }

        // 没有能全放下的分配时也保持「左右两个日」的形状，按自然顺序铺
        return CreateTwoRows(
            new[] { windows[0], windows[2], windows[1], windows[3] }, area, gap, 0.62);
    }

    private static List<int[]> Permutations(int[] items)
    {
        var result = new List<int[]>();
        void Walk(int index)
        {
            if (index == items.Length)
            {
                result.Add((int[])items.Clone());
                return;
            }

            for (var i = index; i < items.Length; i++)
            {
                (items[index], items[i]) = (items[i], items[index]);
                Walk(index + 1);
                (items[index], items[i]) = (items[i], items[index]);
            }
        }

        Walk(0);
        return result;
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

    /// <summary>两条缝的比例差在这个范围内就算“已经对齐”（合并后的状态）。</summary>
    private const double TwinRatioTolerance = 1e-9;

    /// <summary>
    /// 按每个窗口声明的最小尺寸修正切分比例：某个孩子分到的格子小于它的最小宽/高时，
    /// 把分割线推到刚好满足两边的最小尺寸（推不动就保持原比例）。
    /// 进入工作区时也要跑一遍——不能只在“新窗口加入”时才考虑最小尺寸。
    /// 比例本来就满足最小尺寸时不做任何改写，保留用户拖出来的值（避免取整把 62/38 改成 61.97/38.03）。
    /// </summary>
    public void FitToMinimumSizes()
    {
        Fit(Root, Area);
    }

    private void Fit(ZoneNode node, WindowRect rect)
    {
        if (node.Window != null)
        {
            return;
        }

        FitSplit(node, rect);
        FitBranches(node, rect);
    }

    /// <summary>修正一个节点的比例；当前比例已经满足两边最小尺寸时保持原值。</summary>
    private void FitSplit(ZoneNode node, WindowRect rect)
    {
        var horizontal = node.Horizontal;
        var usable = UsableExtent(rect, horizontal);
        var minFirst = Math.Min(ExtentOf(node.First!, horizontal), usable - 2);
        var maxFirst = Math.Max(minFirst, usable - ExtentOf(node.Second!, horizontal));

        var desired = (int)Math.Round(usable * node.Ratio);
        if (desired < minFirst || desired > maxFirst)
        {
            node.Ratio = RatioOf(Math.Clamp(desired, minFirst, maxFirst), usable);
        }
    }

    /// <summary>
    /// 处理一个节点的两个孩子：两条同向缝已经合并时一起拟合，否则各自递归。
    /// </summary>
    private void FitBranches(ZoneNode node, WindowRect rect)
    {
        var first = node.First!;
        var second = node.Second!;
        var (firstRect, secondRect) = SplitRect(rect, node);

        if (IsMergedTwinPair(first, second, firstRect, secondRect))
        {
            FitMergedTwins(first, second, firstRect, secondRect);
            return;
        }

        Fit(first, firstRect);
        Fit(second, secondRect);
    }

    /// <summary>
    /// 两条缝算不算“已合并”：同一个父节点下的两个非叶子子树、方向相同、比例已经对齐，
    /// 且各自的区域在切分轴上等长（左右两列同高 / 上下两行同宽）。
    /// </summary>
    private bool IsMergedTwinPair(
        ZoneNode first, ZoneNode second, WindowRect firstRect, WindowRect secondRect)
    {
        return !first.IsLeaf && !second.IsLeaf &&
               first.Horizontal == second.Horizontal &&
               Math.Abs(first.Ratio - second.Ratio) <= TwinRatioTolerance &&
               UsableExtent(firstRect, first.Horizontal) == UsableExtent(secondRect, second.Horizontal);
    }

    /// <summary>
    /// 已合并的同向缝：比例必须一起改，用两边子树的最小尺寸取交集算出一个共同比例。
    /// 之后只继续向下处理子树、不再各自 FitSplit——否则共同比例会被单侧的最小尺寸又推回去，
    /// 两条缝就错开了（视觉上“日/田”的横缝被拆成两条）。
    /// </summary>
    private void FitMergedTwins(
        ZoneNode first, ZoneNode second, WindowRect firstRect, WindowRect secondRect)
    {
        var horizontal = first.Horizontal;
        var usable = UsableExtent(firstRect, horizontal);
        var minFirst = Math.Min(
            Math.Max(ExtentOf(first.First!, horizontal), ExtentOf(second.First!, horizontal)),
            usable - 2);
        var maxFirst = Math.Max(
            minFirst,
            usable - Math.Max(ExtentOf(first.Second!, horizontal), ExtentOf(second.Second!, horizontal)));

        var desired = (int)Math.Round(usable * first.Ratio);
        var fitted = Math.Clamp(desired, minFirst, maxFirst);
        if (fitted != desired)
        {
            var ratio = RatioOf(fitted, usable);
            first.Ratio = ratio;
            second.Ratio = ratio;
        }

        FitBranches(first, firstRect);
        FitBranches(second, secondRect);
    }

    private int UsableExtent(WindowRect rect, bool horizontal) =>
        Math.Max((horizontal ? rect.Width : rect.Height) - Gap, 2);

    private int ExtentOf(ZoneNode node, bool horizontal)
    {
        var size = node.MinimumSize(Gap);
        return horizontal ? size.Width : size.Height;
    }

    private static double RatioOf(int extent, int usable) =>
        Math.Clamp(extent / (double)usable, 0.02, 0.98);

    /// <summary>
    /// 拖动某个分隔线到指定屏幕坐标；返回比例是否真的变了。
    /// <paramref name="snap"/> 为 true（没按 Ctrl）时：两条同向、处于同一高度的横缝会合并成一条——
    /// 拖左列的横缝，右列的横缝跟着走；两条错开时拖到接近同一高度会自动吸附。
    /// 按住 Ctrl（snap=false）只动当前这一条，用于拆回两个“日”。
    /// </summary>
    public bool Drag(ZoneNode node, int screenX, int screenY, bool snap = true)
    {
        var dividers = Dividers();
        var divider = dividers.FirstOrDefault(d => ReferenceEquals(d.Node, node));
        if (divider == null)
        {
            return false;
        }

        var region = divider.Region;
        var twin = FindTwin(node);
        var twinDivider = twin == null
            ? null
            : dividers.FirstOrDefault(d => ReferenceEquals(d.Node, twin));
        const double MergeTolerance = 0.02;
        var mergedBefore = twin != null && Math.Abs(node.Ratio - twin.Ratio) <= MergeTolerance;

        var horizontal = node.Horizontal;
        var usable = UsableExtent(region, horizontal);
        var range = MinimumRange(
            usable, ExtentOf(node.First!, horizontal), ExtentOf(node.Second!, horizontal));

        // 两条缝已经合并成一条：钳制必须同时满足两列子树的最小尺寸，
        // 否则拖动会把合并缝上另一列先拖破最小尺寸。
        if (snap && twin != null && twinDivider != null && mergedBefore &&
            UsableExtent(twinDivider.Region, horizontal) == usable)
        {
            var twinRange = MinimumRange(
                usable, ExtentOf(twin.First!, horizontal), ExtentOf(twin.Second!, horizontal));
            var min = Math.Max(range.Min, twinRange.Min);
            var max = Math.Min(range.Max, twinRange.Max);
            if (max >= min)
            {
                range = (min, max);
            }
        }

        var position = Math.Clamp(
            horizontal ? screenX - region.Left : screenY - region.Top, range.Min, range.Max);
        var ratio = RatioOf(position, usable);

        if (Math.Abs(ratio - node.Ratio) < 0.0005)
        {
            return false;
        }

        node.Ratio = ratio;
        MergeTwin(twin, node, ratio, mergedBefore, MergeTolerance, snap);
        return true;
    }

    /// <summary>一条缝把可用长度切成两段时的允许范围（第一段的像素区间）。</summary>
    private static (int Min, int Max) MinimumRange(int usable, int firstMin, int secondMin)
    {
        var min = Math.Min(firstMin, usable - 2);
        var max = Math.Max(min, usable - secondMin);
        return (min, max);
    }

    /// <summary>
    /// 找与 <paramref name="node"/> 成对的另一条缝：它们是同一个父节点的两个孩子、方向相同。
    /// 4 窗「左右两个日」里就是左右两列各自的横缝；其它布局（例如 3 列链式切分）不会误配。
    /// </summary>
    private ZoneNode? FindTwin(ZoneNode node)
    {
        var parent = FindParent(Root, node);
        if (parent == null)
        {
            return null;
        }

        var candidates = new[] { parent.First, parent.Second }
            .Where(c => c != null && c.Window == null && c.Horizontal == node.Horizontal)
            .ToList();

        return candidates.Count == 2
            ? (ReferenceEquals(candidates[0], node) ? candidates[1] : candidates[0])
            : null;
    }

    private static ZoneNode? FindParent(ZoneNode current, ZoneNode target)
    {
        if (current.Window != null)
        {
            return null;
        }

        if (ReferenceEquals(current.First, target) || ReferenceEquals(current.Second, target))
        {
            return current;
        }

        return FindParent(current.First!, target) ?? FindParent(current.Second!, target);
    }

    private static void MergeTwin(
        ZoneNode? twin, ZoneNode node, double ratio, bool mergedBefore, double tolerance, bool snap)
    {
        if (twin == null || !snap)
        {
            return;
        }

        // 已经合并（同一高度）→ 一起走；错开时拖到接近同一高度 → 吸附合并
        if (mergedBefore || Math.Abs(ratio - twin.Ratio) <= tolerance)
        {
            twin.Ratio = ratio;
        }
    }
}
