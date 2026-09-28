using System;
using System.Runtime.InteropServices;

namespace DeskCards;

internal static class Native
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_HWNDPARENT = -8;
    public const int GWL_STYLE = -16;
    public const long WS_SYSMENU = 0x00080000;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TRANSPARENT = 0x20;
    public const long WS_EX_LAYERED = 0x80000;

    public const int WM_WINDOWPOSCHANGING = 0x0046;
    public const int WM_MOVING = 0x0216;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public static readonly IntPtr HWND_BOTTOM = new(1);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    /// <summary>창이 있는 모니터의 지금 배율. 창이 배율 변경 알림을 못 받았어도 실제 값을 돌려준다.</summary>
    public static double MonitorScaleOf(IntPtr hwnd)
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return ReadMonitorScale(mon) ?? PrimaryScale();
    }

    /// <summary>화면 좌표(물리 픽셀)가 있는 모니터의 지금 배율.</summary>
    public static double MonitorScaleAt(int x, int y)
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;
        var mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        return ReadMonitorScale(mon) ?? PrimaryScale();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

    /// <summary>작업 표시줄이 붙은 가장자리. 알 수 없으면 null.</summary>
    public static ScreenEdge? TaskbarEdge()
    {
        const uint ABM_GETTASKBARPOS = 5;
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == IntPtr.Zero || data.uEdge > 3) return null;
        return (ScreenEdge)data.uEdge;
    }

    /// <summary>
    /// 이 디스플레이에서 작업 표시줄이 붙은 가장자리. 작업 영역이 화면보다 좁은 쪽으로 알아내고,
    /// 자동 숨기기라 좁아지지 않았으면 주 작업 표시줄의 가장자리로 본다.
    /// </summary>
    public static ScreenEdge? TaskbarEdgeOn(System.Windows.Forms.Screen screen)
    {
        var b = screen.Bounds;
        var wa = screen.WorkingArea;
        if (wa.Bottom < b.Bottom) return ScreenEdge.Bottom;
        if (wa.Top > b.Top) return ScreenEdge.Top;
        if (wa.Left > b.Left) return ScreenEdge.Left;
        if (wa.Right < b.Right) return ScreenEdge.Right;
        return TaskbarEdge();
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>전체 화면 게임·영상·발표 중인지. 이때는 카드 바를 열지 않는다.</summary>
    public static bool IsFullScreenBusy()
    {
        // 2 = 전체 화면 앱, 3 = Direct3D 전체 화면, 4 = 프레젠테이션 모드
        return SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>마지막 키보드·마우스 입력 시각(Environment.TickCount 기준). 모르면 null.</summary>
    public static int? LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? (int)info.dwTime : null;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint to, bool on);

    /// <summary>
    /// 다른 앱이 앞에 있을 때도 이 창을 활성 창으로 가져온다. Windows는 배경 앱이 앞으로 나서는 걸 막으므로
    /// 잠깐 지금 앞에 있는 앱의 입력 큐에 붙었다가 뗀다.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// 주 모니터의 지금 배율(125% = 1.25). GetDpiForSystem은 로그인 때 값에 머물러 있어서
    /// 실행 중에 Windows 배율을 바꾸면 이걸로 읽어야 한다.
    /// </summary>
    public static double PrimaryScale()
    {
        const uint MONITOR_DEFAULTTOPRIMARY = 1;
        var mon = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        return ReadMonitorScale(mon) ?? GetDpiForSystem() / 96.0;
    }

    /// <summary>모니터의 유효 DPI를 배율로 읽는다. 실패하면 호출한 쪽에서 대체 값을 정한다.</summary>
    private static double? ReadMonitorScale(IntPtr monitor)
    {
        const int MDT_EFFECTIVE_DPI = 0;
        return monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint x, out _) == 0 && x > 0
            ? x / 96.0 : null;
    }

    public const int VK_MENU = 0x12; // Alt

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern int GetObject(IntPtr h, int size, ref BITMAP bmp);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY { public int AccentState, AccentFlags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA { public int Attrib; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    /// <summary>
    /// 창 뒤를 흐리게 비치는 아크릴(문서화되지 않은 창 합성 속성). 시스템 배경(DWMWA_SYSTEMBACKDROP_TYPE)과 달리
    /// 창이 비활성이어도 꺼지지 않는다. abgr은 덧칠할 색(0xAABBGGRR).
    /// </summary>
    public static void SetAcrylicBlur(IntPtr hwnd, int abgr)
    {
        const int WCA_ACCENT_POLICY = 19, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        var accent = new ACCENT_POLICY { AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND, GradientColor = abgr };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA { Attrib = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static void SetDwm(IntPtr hwnd, int attr, int value) =>
        DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int));
}
