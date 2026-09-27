using System;
using System.Runtime.InteropServices;

namespace DeskCards;

/// <summary>작업 표시줄의 "바탕화면 보기"와 같은 동작.</summary>
internal static class DesktopShell
{
    /// <summary>
    /// 화면에 떠 있는 일반 창이 있으면 Windows의 바탕화면 보기(Shell.Application.ToggleDesktop)를 부른다.
    /// 켜고 끄는 방식이라, 이미 바탕화면만 보이면 부르지 않는다(부르면 창이 도로 나타난다).
    /// </summary>
    /// <returns>바탕화면 보기를 불렀는지.</returns>
    public static bool ShowDesktop()
    {
        if (!AnyAppWindowShown()) return false;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return false;
            dynamic shell = Activator.CreateInstance(type)!;
            try { shell.ToggleDesktop(); }
            finally { Marshal.FinalReleaseComObject(shell); }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>우리 앱 말고, 최소화되지 않고 보이는 일반 앱 창이 있는지.</summary>
    private static bool AnyAppWindowShown()
    {
        uint self = (uint)Environment.ProcessId;
        bool found = false;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return true;
            // 가상 데스크톱의 다른 창, UWP 대기 창처럼 숨겨진(cloaked) 창은 빼고 센다.
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            switch (cls.ToString())
            {
                case "Progman": case "WorkerW": case "Shell_TrayWnd": case "Shell_SecondaryTrayWnd":
                    return true;
            }
            if (!Native.GetWindowRect(h, out var r) || r.Right - r.Left < 2 || r.Bottom - r.Top < 2) return true;
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private const uint GW_OWNER = 4;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
