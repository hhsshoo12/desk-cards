using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeskCards;

/// <summary>
/// 편집 모드 동안 화면을 어둡게 덮는 막(Windows 캡처 도구처럼). 모니터마다 하나씩, 작업 표시줄을 뺀 작업 영역만 덮는다.
/// 카드와 편집 막대는 이 막 위에 올라와 밝게 보인다. 막을 누르면(= 바탕화면을 누르면) 고른 카드가 풀린다.
/// </summary>
internal sealed class EditDim : Window
{
    private static readonly List<EditDim> _all = new();

    private readonly GroupManager _mgr;
    private readonly System.Drawing.Rectangle _area;
    private bool _locked; // 띄운 뒤로는 눌러도 카드·막대 위로 올라오지 않게 z 순서를 고정한다.

    private EditDim(GroupManager mgr, System.Drawing.Rectangle area)
    {
        _mgr = mgr;
        _area = area;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0));
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Title = "Desk Cards 편집 배경";
        Width = Height = 1;
        Opacity = 0;
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += (_, _) => _mgr.Select(null);
    }

    /// <summary>모든 모니터에 막을 띄운다.</summary>
    public static void ShowAll(GroupManager mgr)
    {
        CloseAll();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var dim = new EditDim(mgr, screen.WorkingArea);
            _all.Add(dim);
            dim.Show();
            dim._locked = true;
            dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        }
    }

    public static void CloseAll()
    {
        foreach (var dim in _all.ToArray()) dim.Close();
        _all.Clear();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        // 눌러도 활성 창이 되지 않아야 편집 막대의 키보드(Esc, 화살표)가 계속 먹는다.
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        Native.SetWindowPos(hwnd, HWND_TOPMOST, _area.Left, _area.Top, _area.Width, _area.Height, Native.SWP_NOACTIVATE);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_WINDOWPOSCHANGING && _locked)
        {
            var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            wp.flags |= Native.SWP_NOZORDER;
            Marshal.StructureToPtr(wp, lParam, false);
        }
        return IntPtr.Zero;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true; // 막 위에서는 아무 메뉴도 띄우지 않는다.
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
}

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
