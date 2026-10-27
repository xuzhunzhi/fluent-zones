using System.Runtime.InteropServices;
using System.Text;

namespace FluentZones.Core;

/// <summary>所有用到的 Win32 声明，集中放在这里。</summary>
internal static class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;

    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_NOOWNERZORDER = 0x0200;

    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_SHOWMINNOACTIVE = 7;
    internal const int SW_MAXIMIZE = 3;
    internal const int SW_HIDE = 0;

    internal const uint WS_POPUP = 0x80000000;
    internal const int WS_EX_TOPMOST = 0x00000008;
    internal const int WS_EX_LAYERED = 0x00080000;
    internal const uint LWA_COLORKEY = 0x00000001;

    internal const int WM_PAINT = 0x000F;
    internal const int WM_SETCURSOR = 0x0020;
    internal const int WM_NCHITTEST = 0x0084;
    internal const int WM_MOUSEMOVE = 0x0200;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;

    internal const int IDC_SIZEWE = 32644;
    internal const int IDC_SIZENS = 32645;

    internal const int DWMWA_CLOAKED = 14;
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE（Win11）</summary>
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWCP_DONOTROUND = 1;
    internal const int DWMWCP_ROUND = 2;

    internal const uint WM_GETMINMAXINFO = 0x0024;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenElevation = 20;

    internal const uint MONITOR_DEFAULTTONEAREST = 2;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

    internal const int WM_HOTKEY = 0x0312;
    internal const int WM_CLOSE = 0x0010;
    internal const int WM_DESTROY = 0x0002;

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, ref MINMAXINFO lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct TOKEN_ELEVATION
    {
        public int TokenIsElevated;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(
        IntPtr tokenHandle, int tokenInformationClass, ref TOKEN_ELEVATION tokenInformation,
        int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    internal static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    internal static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    internal static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- 创建隐藏消息窗口，供全局热键使用 ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal static RECT GetWorkArea(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return info.rcWork;
        }

        // 极端兜底：主显示器
        var primary = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (primary != IntPtr.Zero && GetMonitorInfo(primary, ref info))
        {
            return info.rcWork;
        }

        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    internal static IntPtr GetCursorMonitor()
    {
        return GetCursorPos(out var pt)
            ? MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST)
            : MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);
    }
}
