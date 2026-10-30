using System.Security.Principal;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using FluentZones.Core;

namespace FluentZones.UI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain", e.ExceptionObject as Exception);
        UnhandledException += (_, e) => Log("Xaml", e.Exception);

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 「管理员模式启动」：先以 runas 拉起自己，本进程退出；
        // UAC 被拒绝时继续按普通权限启动，不影响使用。MSIX 打包版不能提权，直接跳过。
        if (SettingsStore.Load().RunAsAdmin && !HostInfo.IsPackaged && !IsElevated())
        {
            if (TryRelaunchAsAdmin())
            {
                Exit();
                return;
            }
        }

        try
        {
            var window = new MainWindow();
            _window = window;
            window.Activate();

            TrySetWindowIcon(window);

            var commandLine = Environment.GetCommandLineArgs();

            if (commandLine.Any(arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                if (window.AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Minimize();
                }
            }

            // 以管理员身份重启后会带上 --apply <布局>，自动重试一次
            for (var i = 0; i < commandLine.Length - 1; i++)
            {
                if (commandLine[i].Equals("--apply", StringComparison.OrdinalIgnoreCase) &&
                    Enum.TryParse<LayoutKind>(commandLine[i + 1], ignoreCase: true, out var kind))
                {
                    window.ApplyOnStartup(kind);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log("Launch", ex);
            throw;
        }
    }

    private static bool IsElevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>用 runas 重新启动自己；UAC 被取消或系统不允许时返回 false。</summary>
    private static bool TryRelaunchAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                return false;
            }

            var arguments = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            };

            return System.Diagnostics.Process.Start(startInfo) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void TrySetWindowIcon(Window window)
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icon))
            {
                window.AppWindow.SetIcon(icon);
            }
        }
        catch
        {
            // 图标设置失败不影响使用
        }
    }

    private static void Log(string source, Exception? ex)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluentZones");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "startup.log"),
                $"[{DateTime.Now:HH:mm:ss}] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去就算了
        }
    }
}
