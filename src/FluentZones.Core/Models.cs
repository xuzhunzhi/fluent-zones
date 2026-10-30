namespace FluentZones.Core;

public enum LayoutKind
{
    Cascade,
    Columns,
    Rows,
    Grid,
    MainLeft,
}

public enum LayoutScope
{
    /// <summary>只排列鼠标所在显示器上的窗口。</summary>
    CursorMonitor,

    /// <summary>每个显示器各自排列属于自己的窗口。</summary>
    AllMonitors,
}

public readonly record struct WindowRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

/// <summary>参与布局的目标窗口快照信息。</summary>
public sealed class WindowInfo
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public uint ProcessId { get; init; }
    public WindowRect Bounds { get; init; }
    public bool IsMaximized { get; init; }
    public IntPtr Monitor { get; init; }

    /// <summary>窗口自己声明的最小尺寸（来自 WM_GETMINMAXINFO），0 表示没拿到。</summary>
    public int MinWidth { get; init; }

    public int MinHeight { get; init; }

    /// <summary>窗口所在显示器的 DPI（用于把"舒适宽度"换算成物理像素）。</summary>
    public uint Dpi { get; init; }

    /// <summary>目标进程权限比我们高（UIPI 会拦截 SetWindowPos），这个窗口我们动不了。</summary>
    public bool CannotMove { get; init; }
}

public sealed record LayoutOptions(
    int Gap,
    LayoutScope Scope,
    int GridLimit = AppSettings.GridLimitDefault,
    int ColumnsLimit = AppSettings.ColumnsLimitDefault)
{
    public static LayoutOptions Default { get; } = new(0, LayoutScope.CursorMonitor);
}

public sealed record LayoutResult(
    bool Success,
    string Message,
    int WindowCount,
    LayoutKind? Kind = null,
    bool NeedsChoice = false,
    IReadOnlyList<WindowInfo>? SuggestedMinimize = null,
    IReadOnlyList<WindowInfo>? Unmovable = null,
    WindowInfo? SuggestedMain = null,
    bool NeedsConfirmMain = false)
{
    /// <summary>有可行解，但需要用户同意把 <see cref="SuggestedMain"/> 换到主窗。</summary>
    public bool NeedsMainSwap => NeedsConfirmMain && SuggestedMain != null;

    public static LayoutResult Fail(string message) => new(false, message, 0);

    /// <summary>放不下，建议最小化这些窗口后重试。</summary>
    public static LayoutResult Choice(LayoutKind kind, IReadOnlyList<WindowInfo> victims) =>
        new(false, "窗口最小尺寸放不下", 0, kind, true, victims);

    /// <summary>网格有可行解，但必须先得到用户同意把该窗口换到主窗（不能不问就换）。</summary>
    public static LayoutResult ConfirmMain(LayoutKind kind, WindowInfo window) =>
        new(false, $"需要把「{window.Title}」换到主窗才能排下，是否允许？", 0, kind,
            SuggestedMain: window, NeedsConfirmMain: true);
}
