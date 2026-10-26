using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace WindowLayout.Core;

public sealed class HotkeySetting
{
    public string Action { get; set; } = string.Empty;

    public string Combo { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

public sealed class AppSettings
{
    public int Gap { get; set; }

    public LayoutScope Scope { get; set; } = LayoutScope.CursorMonitor;

    public bool StartWithWindows { get; set; }

    public List<HotkeySetting> Hotkeys { get; set; } = new();

    public static AppSettings CreateDefault() => new()
    {
        Gap = 0,
        Scope = LayoutScope.CursorMonitor,
        StartWithWindows = false,
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
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowLayout");

    public static string FilePath { get; } = Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
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
}

/// <summary>通过 HKCU\...\Run 实现开机启动（未打包应用最省事的方式）。</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WindowLayout";

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
