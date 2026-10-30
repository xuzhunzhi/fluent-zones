using System.Globalization;

namespace FluentZones.Core;

/// <summary>
/// 记录工作区里被临时去掉 WS_THICKFRAME 的窗口和原样式。
/// 写盘是为了防止程序被强杀（或崩溃）后窗口永远失去边框拖动能力——
/// 下次启动先把这些窗口的样式恢复回去。
/// </summary>
public static class ZoneStyleStore
{
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluentZones",
        "zone-styles.txt");

    public static void Save(IEnumerable<KeyValuePair<IntPtr, int>> styles)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using var writer = new StreamWriter(FilePath, false);
                foreach (var (handle, style) in styles)
                {
                    writer.WriteLine($"{handle.ToInt64()} {style}");
                }
            }
            catch
            {
                // 写不进去不影响功能
            }
        }
    }

    /// <summary>恢复上次没来得及恢复的窗口样式，然后清空记录。</summary>
    public static void RestoreAll()
    {        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }

                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 ||
                        !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawHandle) ||
                        !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var style))
                    {
                        continue;
                    }

                    var handle = new IntPtr(rawHandle);
                    if (!NativeMethods.IsWindow(handle))
                    {
                        continue;
                    }

                    NativeMethods.SetWindowLong(handle, NativeMethods.GWL_STYLE, style);
                    NativeMethods.SetWindowPos(
                        handle, IntPtr.Zero, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                        NativeMethods.SWP_FRAMECHANGED);
                }

                File.Delete(FilePath);
            }
            catch
            {
                // 恢复失败也不阻塞启动
            }
        }
    }

    /// <summary>
    /// 兜底修复：如果之前程序被强杀（没走退出流程），从 layout.log 最后一次 zones place
    /// 里找出当时参与分区的窗口，把被去掉的 WS_THICKFRAME 加回去。
    /// </summary>
    public static void RepairFromLog()
    {
        lock (Gate)
        {
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FluentZones",
                    "layout.log");

                if (!File.Exists(logPath))
                {
                    return;
                }

                var lines = File.ReadAllLines(logPath);
                var start = -1;
                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    if (lines[i].Contains("zones place", StringComparison.Ordinal))
                    {
                        start = i;
                        break;
                    }
                }

                if (start < 0)
                {
                    return;
                }

                for (var i = start + 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!line.StartsWith("  place 0x", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var end = line.IndexOf(' ');
                    if (end <= 10 || !long.TryParse(
                            line.AsSpan(10, end - 10),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var rawHandle))
                    {
                        continue;
                    }

                    var handle = new IntPtr(rawHandle);
                    if (!NativeMethods.IsWindow(handle))
                    {
                        continue;
                    }

                    var style = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_STYLE);
                    if ((style & NativeMethods.WS_THICKFRAME) != 0)
                    {
                        continue;
                    }

                    NativeMethods.SetWindowLong(
                        handle, NativeMethods.GWL_STYLE, style | NativeMethods.WS_THICKFRAME);
                    NativeMethods.SetWindowPos(
                        handle, IntPtr.Zero, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                        NativeMethods.SWP_FRAMECHANGED);
                }
            }
            catch
            {
                // 修复失败不影响启动
            }
        }
    }
}
