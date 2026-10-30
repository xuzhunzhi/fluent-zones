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

    // 舒适尺寸（有效像素）：一次性布局和工作区共用的分配下限，避免切出一排细条。
    private const double ComfortWidth = 400;
    private const double ComfortHeight = 320;
    private const int AbsoluteMinWidth = 120;
    private const int AbsoluteMinHeight = 90;

    /// <summary>
    /// 左右并排的多行规划：每行最多 <paramref name="perRowLimit"/> 个（0 = 不设上限），
    /// 列宽先给够“舒适宽度”、行高先给够“舒适高度”（按 DPI 缩放，不超过区域一半），
    /// 再把剩余空间平均分。一次性布局与工作区共用这份几何。
    ///
    /// 这里总会返回一棵树：舒适下限塞不下时退到声明的最小尺寸、再退到均分，
    /// 由调用方在 <see cref="FitToMinimumSizes"/> 之后检查是否真的装得下
    /// （装不下就走最小化/提示，不再保留第二套“自动分行”算法）。
    /// 1 个窗口铺满。
    /// </summary>
    public static ZoneWorkspace PlanColumns(
        IReadOnlyList<WindowInfo> windows, WindowRect area, int gap, int perRowLimit)
    {
        if (windows.Count == 0)
        {
            throw new ArgumentException("至少需要一个窗口", nameof(windows));
        }

        if (windows.Count == 1)
        {
            return Create(LayoutKind.Columns, windows, area, gap);
        }

        var floors = ComfortFloors(area, windows);
        var perRow = PerRow(windows, area.Width, gap, floors.Width, perRowLimit);
        var rows = new List<List<WindowInfo>>();
        for (var i = 0; i < windows.Count; i += perRow)
        {
            rows.Add(windows.Skip(i).Take(Math.Min(perRow, windows.Count - i)).ToList());
        }

        var rowNodes = rows.Select(row => BuildColumnRow(row, area.Width, gap, floors.Width)).ToList();
        var rowMins = rows.Select(row => row.Max(w => w.MinHeight)).ToList();
        var rowSizes = Allocate(area.Height, gap, rowMins, floors.Height)
                       ?? Allocate(area.Height, gap, rowMins, AbsoluteMinHeight);
        var root = rowSizes != null
            ? ChainNodes(rowNodes, rowSizes, gap, horizontal: false)
            : ChainEqual(rowNodes, horizontal: false);

        return new ZoneWorkspace(root, area, gap);
    }

    /// <summary>每行放几个：舒适宽度算出的容量与设置上限取小。</summary>
    private static int PerRow(
        IReadOnlyList<WindowInfo> windows, int areaWidth, int gap, int floorWidth, int perRowLimit)
    {
        var capacity = MaxPerCount(windows.Select(w => w.MinWidth).ToList(), areaWidth, gap, floorWidth);
        var limit = perRowLimit > 0 ? Math.Min(perRowLimit, windows.Count) : windows.Count;
        return Math.Max(1, Math.Min(limit, capacity));
    }

    /// <summary>一行里的横向切分：按“声明最小宽 + 舒适宽度”分配，剩余空间平均分。</summary>
    private static ZoneNode BuildColumnRow(
        List<WindowInfo> row, int areaWidth, int gap, int floorWidth)
    {
        var leaves = row.Select(ZoneNode.Leaf).ToList();
        if (leaves.Count == 1)
        {
            return leaves[0];
        }

        var mins = row.Select(w => w.MinWidth).ToList();
        var sizes = Allocate(areaWidth, gap, mins, floorWidth)
                    ?? Allocate(areaWidth, gap, mins, AbsoluteMinWidth);
        return sizes != null
            ? ChainNodes(leaves, sizes, gap, horizontal: true)
            : ChainEqual(leaves, horizontal: true);
    }

    /// <summary>舒适下限：400×320 有效像素按 DPI 缩放，且不超过区域一半。</summary>
    private static (int Width, int Height) ComfortFloors(WindowRect area, IReadOnlyList<WindowInfo> windows)
    {
        var dpi = windows.Count == 0 ? 96 : windows.Max(w => w.Dpi);
        if (dpi < 96)
        {
            dpi = 96;
        }

        var scale = dpi / 96.0;
        var width = (int)Math.Round(ComfortWidth * scale);
        width = Math.Min(width, Math.Max(AbsoluteMinWidth, area.Width / 2));
        var height = (int)Math.Round(ComfortHeight * scale);
        height = Math.Min(height, Math.Max(AbsoluteMinHeight, area.Height / 2));

        return (Math.Max(width, AbsoluteMinWidth), Math.Max(height, AbsoluteMinHeight));
    }

    /// <summary>按最小尺寸分配一段长度；放不下返回 null。</summary>
    private static int[]? Allocate(int total, int gap, IReadOnlyList<int> minimums, int floor)
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

    /// <summary>按顺序切块，每块都要放得下时能切出的最大块数。</summary>
    private static int MaxPerCount(IReadOnlyList<int> minimums, int total, int gap, int floor)
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

    /// <summary>按像素尺寸构造链式切分：比例反推成 double，Compute 后仍逐像素一致。</summary>
    private static ZoneNode ChainNodes(
        IReadOnlyList<ZoneNode> nodes, IReadOnlyList<int> sizes, int gap, bool horizontal, int index = 0)
    {
        if (index == nodes.Count - 1)
        {
            return nodes[index];
        }

        var usable = 0;
        for (var i = index; i < nodes.Count; i++)
        {
            usable += sizes[i];
        }

        usable += gap * (nodes.Count - index - 2);
        var ratio = usable > 0 ? sizes[index] / (double)usable : 0.5;
        return ZoneNode.Split(
            horizontal,
            nodes[index],
            ChainNodes(nodes, sizes, gap, horizontal, index + 1),
            ratio);
    }

    /// <summary>均分链（舒适下限也塞不下时的兜底，交给 FitToMinimumSizes 尽量修正）。</summary>
    private static ZoneNode ChainEqual(IReadOnlyList<ZoneNode> nodes, bool horizontal, int index = 0)
    {
        if (index == nodes.Count - 1)
        {
            return nodes[index];
        }

        return ZoneNode.Split(
            horizontal,
            nodes[index],
            ChainEqual(nodes, horizontal, index + 1),
            1.0 / (nodes.Count - index));
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
    /// 网格平铺的槽位规划：3 窗左日右口、4 窗左右两个日、5 / 6 窗沿用「主窗 + 右侧列 + 底部行」马赛克。
    /// 先找“自然主窗（第一个窗口）当主窗”的排列；只有都不行时才找换主窗的排列，并通过
    /// <see cref="ZoneGridPlan.MainSwapRequested"/> 告诉调用方“必须先把该窗口换到主窗，且需用户同意”。
    /// 一次性布局与工作区共用同一份规划。
    /// </summary>
    /// <param name="keepMain">
    /// true = 只枚举第一个窗口留在主窗的排列（用户已同意换主窗后的重排，或明确不允许换主窗）。
    /// </param>
    public static ZoneGridPlan? PlanGrid(
        IReadOnlyList<WindowInfo> windows, WindowRect area, int gap, bool keepMain = false)
    {
        if (windows.Count == 3)
        {
            return PlanLeftDay(windows, area, gap, keepMain);
        }

        if (windows.Count == 4)
        {
            return PlanTwoRows(windows, area, gap, keepMain);
        }

        if (windows.Count is 5 or 6)
        {
            return PlanMosaic(windows, area, gap, keepMain);
        }

        return null;
    }

    private static ZoneGridPlan PlanLeftDay(
        IReadOnlyList<WindowInfo> windows, WindowRect area, int gap, bool keepMain)
    {
        var natural = new[] { 0, 1, 2 };
        var mainPlan = SearchLeftDay(windows, area, gap, natural);
        if (FitsAfterMinimumFit(mainPlan))
        {
            return new ZoneGridPlan(mainPlan, null);
        }

        if (keepMain)
        {
            // 没有能全放下的分配时也保持「左日右口」的形状，按自然顺序铺；调用方判断放不下
            return new ZoneGridPlan(mainPlan, null);
        }

        // 换主窗：按“需求面积降序”逐个主窗候选做固定主窗搜索；找到也必须交给用户确认
        var swapped = TrySwappedMain(
            windows, index => SearchLeftDay(PinnedOrder(windows, index), area, gap, natural));
        return swapped ?? new ZoneGridPlan(mainPlan, null);
    }

    private static ZoneWorkspace SearchLeftDay(
        IReadOnlyList<WindowInfo> windows, WindowRect area, int gap, int[] natural)
    {
        foreach (var order in FixedMainPermutations(natural))
        {
            var workspace = CreateLeftDay(OrderWindows(windows, order), area, gap, 0.5);
            if (FitsAfterMinimumFit(workspace))
            {
                return workspace;
            }
        }

        return CreateLeftDay(OrderWindows(windows, natural), area, gap, 0.5);
    }

    private static ZoneGridPlan PlanTwoRows(
        IReadOnlyList<WindowInfo> windows, WindowRect area, int gap, bool keepMain)
    {
        var natural = new[] { 0, 2, 1, 3 };
        var mainPlan = SearchTwoRows(windows, area, gap, natural);
        if (FitsAfterMinimumFit(mainPlan))
        {
            return new ZoneGridPlan(mainPlan, null);
        }

        if (keepMain)
        {
            return new ZoneGridPlan(mainPlan, null);
        }

        var swapped = TrySwappedMain(
            windows, index => SearchTwoRows(PinnedOrder(windows, index), area, gap, natural));
        return swapped ?? new ZoneGridPlan(mainPlan, null);
    }

    private static ZoneWorkspace SearchTwoRows(
        IReadOnlyList<WindowInfo> windows, WindowRect area, int gap, int[] natural)
    {
        foreach (var order in FixedMainPermutations(natural))
        {
            var workspace = CreateTwoRows(OrderWindows(windows, order), area, gap, MainRatio);
            if (FitsAfterMinimumFit(workspace))
            {
                return workspace;
            }
        }

        return CreateTwoRows(OrderWindows(windows, natural), area, gap, MainRatio);
    }

    /// <summary>
    /// 5 / 6 窗网格：沿用「主窗 + 右侧列 + 底部行」拓扑（<see cref="Grid"/>），
    /// 把哪个窗口放进哪个格子交给逐排列拟合搜索。120 / 720 个排列，成本可忽略。
    /// </summary>
    private static ZoneGridPlan PlanMosaic(
        IReadOnlyList<WindowInfo> windows, WindowRect area, int gap, bool keepMain)
    {
        var natural = Enumerable.Range(0, windows.Count).ToArray();
        var mainPlan = SearchMosaic(windows, area, gap, natural);
        if (FitsAfterMinimumFit(mainPlan))
        {
            return new ZoneGridPlan(mainPlan, null);
        }

        if (keepMain)
        {
            return new ZoneGridPlan(mainPlan, null);
        }

        var swapped = TrySwappedMain(
            windows, index => SearchMosaic(PinnedOrder(windows, index), area, gap, natural));
        return swapped ?? new ZoneGridPlan(mainPlan, null);
    }

    private static ZoneWorkspace SearchMosaic(
        IReadOnlyList<WindowInfo> windows, WindowRect area, int gap, int[] natural)
    {
        foreach (var order in FixedMainPermutations(natural))
        {
            var workspace = new ZoneWorkspace(Grid(OrderWindows(windows, order)), area, gap);
            if (FitsAfterMinimumFit(workspace))
            {
                return workspace;
            }
        }

        return Create(LayoutKind.Grid, windows, area, gap);
    }

    /// <summary>
    /// 换主窗搜索：候选按“需求面积降序”逐个尝试，用该窗口当主窗再跑一次固定主窗搜索。
    /// 这样返回的 workspace 与“用户同意后、一次性布局用 pinned 重规划”得到的完全一致。
    /// </summary>
    private static ZoneGridPlan? TrySwappedMain(
        IReadOnlyList<WindowInfo> windows, Func<int, ZoneWorkspace> buildPinned)
    {
        var candidates = Enumerable.Range(1, windows.Count - 1)
            .OrderByDescending(index => RequiredArea(windows[index]));

        foreach (var index in candidates)
        {
            var workspace = buildPinned(index);
            if (FitsAfterMinimumFit(workspace))
            {
                return new ZoneGridPlan(workspace, windows[index]);
            }
        }

        return null;
    }

    /// <summary>把指定窗口放到主窗位（[0]），其余保持原顺序。</summary>
    private static WindowInfo[] PinnedOrder(IReadOnlyList<WindowInfo> windows, int mainIndex)
    {
        var ordered = new WindowInfo[windows.Count];
        ordered[0] = windows[mainIndex];
        var write = 1;
        for (var i = 0; i < windows.Count; i++)
        {
            if (i != mainIndex)
            {
                ordered[write++] = windows[i];
            }
        }

        return ordered;
    }

    /// <summary>换主窗候选的排序依据：窗口声明的需求面积。</summary>
    private static long RequiredArea(WindowInfo window) =>
        (long)Math.Max(window.MinWidth, AbsoluteMinWidth) *
        Math.Max(window.MinHeight, AbsoluteMinHeight);

    private static WindowInfo[] OrderWindows(IReadOnlyList<WindowInfo> windows, IReadOnlyList<int> order)
    {
        var ordered = new WindowInfo[order.Count];
        for (var i = 0; i < order.Count; i++)
        {
            ordered[i] = windows[order[i]];
        }

        return ordered;
    }

    /// <summary>
    /// 固定第一个窗口当主窗：只枚举其余窗口的排列，默认顺序优先。
    /// </summary>
    private static List<int[]> FixedMainPermutations(int[] natural)
    {
        var rest = natural.Skip(1).ToArray();
        return Permutations(rest)
            .OrderByDescending(order => order.SequenceEqual(rest))
            .Select(order => new[] { natural[0] }.Concat(order).ToArray())
            .ToList();
    }

    /// <summary>
    /// 排列的可行性：先把分隔线推到满足最小尺寸（FitToMinimumSizes），再确认每一格都不小于
    /// 窗口声明的最小尺寸。与调用方（LayoutService / ZoneSession）的检查用同一个下限。
    /// </summary>
    private static bool FitsAfterMinimumFit(ZoneWorkspace workspace)
    {
        workspace.FitToMinimumSizes();
        return workspace.Compute().All(p =>
            p.Rect.Width >= Math.Max(p.Window.MinWidth, AbsoluteMinWidth) &&
            p.Rect.Height >= Math.Max(p.Window.MinHeight, AbsoluteMinHeight));
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
