namespace FluentZones.Core;

public static class LayoutNames
{
    public static string Display(LayoutKind kind) => kind switch
    {
        LayoutKind.Cascade => "层叠窗口",
        LayoutKind.Columns => "左右并排",
        LayoutKind.Rows => "上下堆叠",
        LayoutKind.Grid => "网格平铺",
        LayoutKind.MainLeft => "主窗在左",
        _ => kind.ToString(),
    };

    public static string Description(LayoutKind kind) => kind switch
    {
        LayoutKind.Cascade => "按 Z 序错开，露出每个窗口的标题栏",
        LayoutKind.Columns => "按各窗口最小宽度分配，放不下自动分行",
        LayoutKind.Rows => "按各窗口最小高度分配，放不下自动分列",
        LayoutKind.Grid => "自动挑一个放得下最小尺寸的宫格",
        LayoutKind.MainLeft => "前台窗口在左，其余按最小高度在右侧堆叠",
        _ => string.Empty,
    };
}

/// <summary>热键对应的动作，例如 "Cascade"、"Grid:All"、"Restore"。</summary>
public readonly record struct HotkeyAction(bool IsRestore, LayoutKind Kind, LayoutScope Scope)
{
    public string DisplayName => IsRestore
        ? "还原上一次布局"
        : LayoutNames.Display(Kind) + (Scope == LayoutScope.AllMonitors ? "（所有显示器）" : string.Empty);

    public static bool TryParse(string? text, out HotkeyAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var scope = LayoutScope.CursorMonitor;
        var colon = value.IndexOf(':');
        if (colon > 0)
        {
            var suffix = value[(colon + 1)..];
            if (suffix.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                scope = LayoutScope.AllMonitors;
            }

            value = value[..colon];
        }

        if (value.Equals("Restore", StringComparison.OrdinalIgnoreCase))
        {
            action = new HotkeyAction(true, default, scope);
            return true;
        }

        if (Enum.TryParse<LayoutKind>(value, ignoreCase: true, out var kind))
        {
            action = new HotkeyAction(false, kind, scope);
            return true;
        }

        return false;
    }

    public override string ToString()
    {
        if (IsRestore)
        {
            return "Restore";
        }

        return Scope == LayoutScope.AllMonitors ? $"{Kind}:All" : Kind.ToString();
    }
}
