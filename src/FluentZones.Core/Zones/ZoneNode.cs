namespace FluentZones.Core;

/// <summary>分区树：叶子是窗口，内部节点是一次切分（横切/竖切 + 比例）。</summary>
public sealed class ZoneNode
{
    public WindowInfo? Window { get; internal set; }
    public ZoneNode? First { get; internal set; }
    public ZoneNode? Second { get; internal set; }

    /// <summary>true = 左右切（X 轴），false = 上下切（Y 轴）。</summary>
    public bool Horizontal { get; init; }

    /// <summary>第一块占可用空间的比例（0~1）。</summary>
    public double Ratio { get; set; } = 0.5;

    public bool IsLeaf => Window != null;

    public static ZoneNode Leaf(WindowInfo window) => new() { Window = window };

    public static ZoneNode Split(bool horizontal, ZoneNode first, ZoneNode second, double ratio = 0.5) =>
        new() { Horizontal = horizontal, First = first, Second = second, Ratio = ratio };

    /// <summary>这棵子树至少需要多大（像素），用于拖拽时限制比例。</summary>
    public (int Width, int Height) MinimumSize(int gap)
    {
        if (Window != null)
        {
            return (Math.Max(Window.MinWidth, 120), Math.Max(Window.MinHeight, 90));
        }

        var a = First!.MinimumSize(gap);
        var b = Second!.MinimumSize(gap);

        return Horizontal
            ? (a.Width + gap + b.Width, Math.Max(a.Height, b.Height))
            : (Math.Max(a.Width, b.Width), a.Height + gap + b.Height);
    }
}

/// <summary>一个窗口最终落在哪个格子里。</summary>
public sealed record ZonePlacement(WindowInfo Window, WindowRect Rect);

/// <summary>
/// 网格规划结果。<see cref="MainSwapRequested"/> 非 null 表示“自然主窗”排不下：
/// 给出的 <see cref="Workspace"/> 是把该窗口换到主窗后的可行解，
/// 调用方必须先征得用户同意，否则不能应用（按放不下处理）。
/// </summary>
public readonly record struct ZoneGridPlan(ZoneWorkspace Workspace, WindowInfo? MainSwapRequested);

/// <summary>一条可拖拽的分隔线。</summary>
public sealed record ZoneDivider(ZoneNode Node, bool Horizontal, WindowRect Region);

/// <summary>
/// 工作区镜像/主窗位置：<see cref="None"/> = 左（默认），
/// <see cref="Horizontal"/> = 右，<see cref="Vertical"/> = 下，<see cref="Both"/> = 右下。
/// </summary>
public enum Mirror
{
    None,
    Horizontal,
    Vertical,
    Both,
}

/// <summary>交换窗口的结果。</summary>
public enum ZoneSwapOutcome
{
    /// <summary>交换后直接满足所有最小尺寸。</summary>
    Success,

    /// <summary>交换后需要调整分隔线比例才能满足最小尺寸。</summary>
    NeedsDividerAdjustment,

    /// <summary>交换后只有把目标槽位升为主窗（并镜像）才放得下。</summary>
    NeedsMainSwap,

    /// <summary>交换不可行（窗口比整块区域还大，或任何镜像都放不下）。</summary>
    Impossible,
}

/// <summary>
/// 一次交换的候选结果。候选工作区是独立克隆，确认前不会改动当前会话；
/// 调用方确认后再 <c>ZoneSession.CommitSwap</c>。
/// </summary>
public readonly record struct ZoneSwapResult(
    ZoneSwapOutcome Outcome,
    ZoneWorkspace? Candidate,
    Mirror MirrorState,
    WindowInfo? NewMainWindow,
    int Generation);
