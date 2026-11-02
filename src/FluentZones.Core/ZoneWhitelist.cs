using System.Collections.Concurrent;
using System.Diagnostics;

namespace FluentZones.Core;

/// <summary>用户对“固定大小窗口加入白名单”的选择。</summary>
public enum ZoneWhitelistDecision
{
    /// <summary>加入白名单（UI 负责写入规则）。</summary>
    AddToWhitelist,

    /// <summary>本次跳过，不加入白名单。</summary>
    Skip,
}

/// <summary>
/// 工作区白名单：规则是进程名（不含 .exe）或窗口标题子串，大小写不敏感。
/// 匹配的窗口不进入分区、不参与重排；一次性布局不受影响。
/// </summary>
public static class ZoneWhitelist
{
    private static readonly ConcurrentDictionary<uint, string> ProcessNames = new();

    public static string ProcessName(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        return ProcessNames.GetOrAdd(processId, id =>
        {
            try
            {
                using var process = Process.GetProcessById((int)id);
                return process.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        });
    }

    public static bool IsWhitelisted(WindowInfo window, IEnumerable<string>? rules)
    {
        if (rules == null)
        {
            return false;
        }

        var processName = ProcessName(window.ProcessId);
        foreach (var raw in rules)
        {
            var rule = raw?.Trim();
            if (string.IsNullOrEmpty(rule))
            {
                continue;
            }

            var processRule = rule.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? rule[..^4]
                : rule;

            if (processName.Length > 0 &&
                processName.Equals(processRule, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(window.Title) &&
                window.Title.Contains(rule, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string ProcessRule(WindowInfo window)
    {
        var name = ProcessName(window.ProcessId);
        return name.Length > 0 ? name : window.Title;
    }
}
