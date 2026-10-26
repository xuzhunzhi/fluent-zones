using System.Text;

namespace WindowLayout.Core;

/// <summary>排列过程的简单日志，方便排查"某个窗口为什么没动"。</summary>
internal static class LayoutLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowLayout",
        "layout.log");

    public static void Write(IEnumerable<string> lines)
    {
        try
        {
            var folder = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            Directory.CreateDirectory(folder);

            lock (Gate)
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    info.Delete();
                }

                var text = new StringBuilder();
                foreach (var line in lines)
                {
                    text.AppendLine(line);
                }

                File.AppendAllText(FilePath, text.ToString());
            }
        }
        catch
        {
            // 日志写不进去不影响功能
        }
    }
}
