using System.Runtime.InteropServices;
using System.Text;

namespace FluentZones.Core;

/// <summary>当前进程的运行形态：未打包 / MSIX 打包。打包应用不能提权。</summary>
public static class HostInfo
{
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>是否以已打包（MSIX）应用的身份运行。</summary>
    public static bool IsPackaged { get; } = QueryPackaged();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    private static bool QueryPackaged()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch
        {
            return false;
        }
    }
}
