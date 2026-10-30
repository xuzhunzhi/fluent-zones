using System.Diagnostics;
using System.Text;

namespace FluentZones.Core;

/// <summary>挑出"值得参与布局"的顶层窗口：可见、有标题、非工具窗、不在其它虚拟桌面。</summary>
public static class WindowCollector
{
    /// <summary>截图 / 贴图工具不参与窗口分配。</summary>
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Snipaste",
        "SnippingTool",
        "ScreenSketch",
        "ShareX",
        "Greenshot",
    };

    private static readonly Dictionary<uint, string> ProcessNames = new();

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow",
        "ForegroundStaging",
        "TaskListThumbnailWnd",
        "Windows.Internal.Shell.TabProxyWindow",
    };

    public static List<WindowInfo> Collect(IntPtr monitorFilter = default)
    {
        var result = new List<WindowInfo>();
        var shell = NativeMethods.GetShellWindow();
        var ownPid = (uint)Environment.ProcessId;
        var title = new StringBuilder(512);
        var className = new StringBuilder(256);

        NativeMethods.EnumWindowsProc callback = (hWnd, _) =>
        {
            var info = TryDescribe(hWnd, shell, ownPid, title, className);
            if (info is null)
            {
                return true;
            }

            if (monitorFilter != IntPtr.Zero && info.Monitor != monitorFilter)
            {
                return true;
            }

            result.Add(info);
            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    private static WindowInfo? TryDescribe(
        IntPtr hWnd, IntPtr shell, uint ownPid, StringBuilder title, StringBuilder className)
    {
        if (hWnd == IntPtr.Zero || hWnd == shell)
        {
            return null;
        }

        if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == ownPid || IsExcludedProcess(pid))
        {
            return null;
        }

        if (NativeMethods.GetWindowTextLength(hWnd) == 0)
        {
            return null;
        }

        var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 ||
            (exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0)
        {
            return null;
        }

        // Windows 10/11 上，其它虚拟桌面的窗口、被挂起的 UWP 窗口都是 cloaked 状态
        if (IsCloaked(hWnd))
        {
            return null;
        }

        title.Clear();
        NativeMethods.GetWindowText(hWnd, title, title.Capacity);
        className.Clear();
        NativeMethods.GetClassName(hWnd, className, className.Capacity);
        var cls = className.ToString();
        if (IgnoredClasses.Contains(cls))
        {
            return null;
        }

        if (!NativeMethods.GetWindowRect(hWnd, out var rect) || rect.Width < 60 || rect.Height < 40)
        {
            return null;
        }

        var monitor = NativeMethods.MonitorFromWindow(hWnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return null;
        }

        GetSizeLimits(hWnd, out var minWidth, out var minHeight);

        return new WindowInfo
        {
            Handle = hWnd,
            Title = title.ToString(),
            ClassName = cls,
            ProcessId = pid,
            Bounds = new WindowRect(rect.Left, rect.Top, rect.Width, rect.Height),
            IsMaximized = NativeMethods.IsZoomed(hWnd),
            Monitor = monitor,
            MinWidth = minWidth,
            MinHeight = minHeight,
            Dpi = NativeMethods.GetDpiForWindow(hWnd),
            CannotMove = !Elevation.CanControl(pid),
        };
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        var cloaked = 0;
        return NativeMethods.DwmGetWindowAttribute(
            hWnd, NativeMethods.DWMWA_CLOAKED, ref cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static bool IsExcludedProcess(uint pid)
    {
        string name;
        lock (ProcessNames)
        {
            if (!ProcessNames.TryGetValue(pid, out name!))
            {
                try
                {
                    name = Process.GetProcessById((int)pid).ProcessName;
                }
                catch
                {
                    name = string.Empty;
                }

                ProcessNames[pid] = name;
            }
        }

        return name.Length > 0 && ExcludedProcesses.Contains(name);
    }

    /// <summary>诊断用：列出所有顶层窗口以及"参与 / 不参与"的原因。</summary>
    internal static List<WindowDiagnostic> Diagnose()
    {
        var result = new List<WindowDiagnostic>();
        var shell = NativeMethods.GetShellWindow();
        var ownPid = (uint)Environment.ProcessId;
        var title = new StringBuilder(512);
        var className = new StringBuilder(256);

        NativeMethods.EnumWindowsProc callback = (hWnd, _) =>
        {
            title.Clear();
            NativeMethods.GetWindowText(hWnd, title, title.Capacity);
            className.Clear();
            NativeMethods.GetClassName(hWnd, className, className.Capacity);
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
            var monitor = NativeMethods.MonitorFromWindow(hWnd, NativeMethods.MONITOR_DEFAULTTONEAREST);

            var reason = SkipReason.None;

            if (hWnd == shell)
            {
                reason = SkipReason.Desktop;
            }
            else if (!NativeMethods.IsWindowVisible(hWnd))
            {
                reason = SkipReason.Invisible;
            }
            else if (NativeMethods.IsIconic(hWnd))
            {
                reason = SkipReason.Minimized;
            }
            else if (pid == ownPid)
            {
                reason = SkipReason.OwnProcess;
            }
            else if (title.Length == 0)
            {
                reason = SkipReason.NoTitle;
            }
            else if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            {
                reason = SkipReason.ToolWindow;
            }
            else if ((exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0)
            {
                reason = SkipReason.NoActivate;
            }
            else if (IsCloaked(hWnd))
            {
                reason = SkipReason.Cloaked;
            }
            else if (IgnoredClasses.Contains(className.ToString()))
            {
                reason = SkipReason.SystemWindow;
            }
            else if (!NativeMethods.GetWindowRect(hWnd, out var rect) ||
                     rect.Width < 60 || rect.Height < 40)
            {
                reason = SkipReason.TooSmall;
            }

            result.Add(new WindowDiagnostic(
                hWnd,
                title.ToString(),
                className.ToString(),
                pid,
                monitor,
                reason));

            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    /// <summary>问窗口自己要一个最小尺寸（WM_GETMINMAXINFO），顺便把不同 DPI 意识级别换算成物理像素。</summary>
    private static void GetSizeLimits(IntPtr hWnd, out int minWidth, out int minHeight)
    {
        minWidth = 0;
        minHeight = 0;

        var info = default(NativeMethods.MINMAXINFO);
        var handled = NativeMethods.SendMessageTimeout(
            hWnd,
            NativeMethods.WM_GETMINMAXINFO,
            IntPtr.Zero,
            ref info,
            NativeMethods.SMTO_ABORTIFHUNG,
            80,
            out _);

        if (handled == IntPtr.Zero)
        {
            return;
        }

        var scale = 1.0;
        try
        {
            var awareness = NativeMethods.GetAwarenessFromDpiAwarenessContext(
                NativeMethods.GetWindowDpiAwarenessContext(hWnd));
            if (awareness == 0) // DPI_AWARENESS_UNAWARE：它给的是 96 DPI 下的逻辑像素
            {
                var dpi = NativeMethods.GetDpiForWindow(hWnd);
                if (dpi > 0)
                {
                    scale = dpi / 96.0;
                }
            }
        }
        catch
        {
            // 老系统上没有这些 API，按原值使用
        }

        minWidth = (int)Math.Round(info.ptMinTrackSize.X * scale);
        minHeight = (int)Math.Round(info.ptMinTrackSize.Y * scale);
    }
}

/// <summary>诊断信息：顶层窗口是否参与布局，以及不参与的原因。</summary>
internal sealed record WindowDiagnostic(
    IntPtr Handle,
    string Title,
    string ClassName,
    uint ProcessId,
    IntPtr Monitor,
    SkipReason Reason)
{
    public bool Included => Reason == SkipReason.None;
}

internal enum SkipReason
{
    None,
    Desktop,
    Invisible,
    Minimized,
    OwnProcess,
    NoTitle,
    ToolWindow,
    NoActivate,
    Cloaked,
    SystemWindow,
    TooSmall,
}


