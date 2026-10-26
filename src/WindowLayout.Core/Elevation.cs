using System.Runtime.InteropServices;

namespace WindowLayout.Core;

/// <summary>
/// 判断某个进程的窗口我们能不能操作。
/// 高完整性级别（管理员）进程的窗口会被 UIPI 保护：SetWindowPos 直接返回 False 且不报错，
/// WM_GETMINMAXINFO 也读不到，表现就是"怎么排都不动"。
/// </summary>
internal static class Elevation
{
    private const int ErrorAccessDenied = 5;

    private static readonly object Gate = new();
    private static readonly Dictionary<uint, Access> Cache = new();

    public static bool IsCurrentProcessElevated { get; } = QueryOwnElevation();

    /// <summary>我们是否有权限操作这个进程的窗口。</summary>
    public static bool CanControl(uint processId)
    {
        if (processId == 0)
        {
            return false;
        }

        // 自己已经是高权限，可以操作低权限进程
        if (IsCurrentProcessElevated)
        {
            return true;
        }

        return GetAccess(processId) == Access.ReadableNotElevated;
    }

    private static Access GetAccess(uint processId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(processId, out var cached))
            {
                return cached;
            }
        }

        var access = Probe(processId);

        lock (Gate)
        {
            Cache[processId] = access;
        }

        return access;
    }

    private static Access Probe(uint processId)
    {
        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

        if (process == IntPtr.Zero)
        {
            // 连查询句柄都拿不到，肯定动不了
            return Access.Denied;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out var token))
            {
                var error = Marshal.GetLastWin32Error();
                return error == ErrorAccessDenied ? Access.Denied : Access.Unknown;
            }

            try
            {
                var elevation = default(NativeMethods.TOKEN_ELEVATION);
                var size = Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
                if (!NativeMethods.GetTokenInformation(
                        token, NativeMethods.TokenElevation, ref elevation, size, out _))
                {
                    return Access.Unknown;
                }

                return elevation.TokenIsElevated != 0
                    ? Access.Elevated
                    : Access.ReadableNotElevated;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static bool QueryOwnElevation()
    {
        var process = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)Environment.ProcessId);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out var token))
            {
                return false;
            }

            try
            {
                var elevation = default(NativeMethods.TOKEN_ELEVATION);
                var size = Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
                return NativeMethods.GetTokenInformation(
                           token, NativeMethods.TokenElevation, ref elevation, size, out _) &&
                       elevation.TokenIsElevated != 0;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private enum Access
    {
        Unknown,
        ReadableNotElevated,
        Elevated,
        Denied,
    }
}
