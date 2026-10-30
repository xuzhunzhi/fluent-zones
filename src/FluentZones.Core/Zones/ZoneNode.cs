namespace FluentZones.Core;

/// <summary>分区树：叶子是窗口，内部节点是一次切分（横切/竖切 + 比例）。</summary>
public sealed class ZoneNode
{
    public WindowInfo? Window { get; init; }
    public ZoneNode? First { get; init; }
    public ZoneNode? Second { get; init; }

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
