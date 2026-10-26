using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WindowLayout.Core;

namespace WindowLayout.UI;

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
        try
        {
            var window = new MainWindow();
            _window = window;
            window.Activate();

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

    private static void Log(string source, Exception? ex)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowLayout");
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
