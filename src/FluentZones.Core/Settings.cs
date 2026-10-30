using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace FluentZones.Core;

public sealed class HotkeySetting
{
    public string Action { get; set; } = string.Empty;

    public string Combo { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

public sealed class AppSettings
{
    /// <summary>网格平铺的格数（"最多窗口数"）：默认、下限、上限。</summary>
    public const int GridLimitDefault = 4;
    public const int GridLimitMin = 2;
    public const int GridLimitMax = 6;

    /// <summary>左右并排每行最多几个窗口（"每行列数"）：默认、下限、上限。</summary>
    public const int ColumnsLimitDefault = 4;
    public const int ColumnsLimitMin = 3;
    public const int ColumnsLimitMax = 4;

    public int Gap { get; set; }

    public LayoutScope Scope { get; set; } = LayoutScope.CursorMonitor;

    public bool StartWithWindows { get; set; }

    /// <summary>启动时请求管理员权限（UAC），用于操作提权窗口。打包（MSIX）应用不支持。</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>各布局的"最多窗口数"，键是 <see cref="LayoutKind"/> 的名字；缺失或 0 表示不限制。</summary>
    public Dictionary<string, int> LayoutLimits { get; set; } = new();

    public List<HotkeySetting> Hotkeys { get; set; } = new();

    /// <summary>取某个布局的上限；没配过就返回 <paramref name="fallback"/>。</summary>
    public int GetLimit(LayoutKind kind, int fallback = 0) =>
        LayoutLimits.TryGetValue(kind.ToString(), out var value) && value > 0 ? value : fallback;

    public void SetLimit(LayoutKind kind, int value) => LayoutLimits[kind.ToString()] = value;

    /// <summary>网格格数，范围见 <see cref="GridLimitMin"/> / <see cref="GridLimitMax"/>。</summary>
    [JsonIgnore]
    public int GridLimit
    {
        get => GetLimit(LayoutKind.Grid, GridLimitDefault);
        set => SetLimit(LayoutKind.Grid, Math.Clamp(value, GridLimitMin, GridLimitMax));
    }

    /// <summary>左右并排的每行列数，范围见 <see cref="ColumnsLimitMin"/> / <see cref="ColumnsLimitMax"/>。</summary>
    [JsonIgnore]
    public int ColumnsLimit
    {
        get => GetLimit(LayoutKind.Columns, ColumnsLimitDefault);
        set => SetLimit(LayoutKind.Columns, Math.Clamp(value, ColumnsLimitMin, ColumnsLimitMax));
    }

    public static AppSettings CreateDefault() => new()
    {
        Gap = 0,
        Scope = LayoutScope.CursorMonitor,
        StartWithWindows = false,
        LayoutLimits = new Dictionary<string, int>
        {
            [nameof(LayoutKind.Grid)] = GridLimitDefault,
            [nameof(LayoutKind.Columns)] = ColumnsLimitDefault,
        },
        Hotkeys = new List<HotkeySetting>
        {
            new() { Action = "Cascade", Combo = "Ctrl+Alt+1" },
            new() { Action = "Columns", Combo = "Ctrl+Alt+2" },
            new() { Action = "Rows", Combo = "Ctrl+Alt+3" },
            new() { Action = "Grid", Combo = "Ctrl+Alt+4" },
            new() { Action = "MainLeft", Combo = "Ctrl+Alt+5" },
            new() { Action = "Restore", Combo = "Ctrl+Alt+0" },
        },
    };
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentZones");

    /// <summary>旧版本（叫 WindowLayout 时）的设置目录，用来做一次性迁移。</summary>
    private static string LegacyFolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowLayout");

    public static string FilePath { get; } = Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
        MigrateLegacyFolder();

        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(FilePath), JsonOptions);

                if (settings != null)
                {
                    if (settings.Hotkeys.Count == 0)
                    {
                        settings.Hotkeys = AppSettings.CreateDefault().Hotkeys;
                    }

                    // 老版本的 settings.json 没有 layoutLimits，补上网格默认格数
                    settings.LayoutLimits ??= new Dictionary<string, int>();
                    if (!settings.LayoutLimits.ContainsKey(nameof(LayoutKind.Grid)))
                    {
                        settings.LayoutLimits[nameof(LayoutKind.Grid)] = AppSettings.GridLimitDefault;
                    }

                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时静默回退到默认值
        }

        return AppSettings.CreateDefault();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // 写盘失败不影响当前会话
        }
    }

    /// <summary>把旧目录里的设置（和日志）搬过来，只搬一次。</summary>
    private static void MigrateLegacyFolder()
    {
        try
        {
            if (Directory.Exists(FolderPath) || !Directory.Exists(LegacyFolderPath))
            {
                return;
            }

            Directory.CreateDirectory(FolderPath);
            foreach (var file in Directory.GetFiles(LegacyFolderPath))
            {
                File.Copy(file, Path.Combine(FolderPath, Path.GetFileName(file)));
            }
        }
        catch
        {
            // 迁移失败就按默认设置走
        }
    }
}

/// <summary>通过 HKCU\...\Run 实现开机启动（未打包应用最省事的方式）。</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FluentZones";
    private const string LegacyValueName = "WindowLayout";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法写入开机启动项");

        // 旧名字留下的启动项顺手清掉
        key.DeleteValue(LegacyValueName, throwOnMissingValue: false);

        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法定位程序路径");
            key.SetValue(ValueName, $"\"{exe}\" --minimized", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
