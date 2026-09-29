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
            dim.BeginAnimation(OpacityProperty, Motion.In(0, 1, Motion.Fast));
        }
    }

    public static void CloseAll()
    {
        foreach (var dim in _all.ToArray()) dim.Close();
        _all.Clear();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Hwnd.Of(this);
        // 눌러도 활성 창이 되지 않아야 편집 막대의 키보드(Esc, 화살표)가 계속 먹는다.
        Hwnd.MakeNoActivateTool(hwnd);
        Native.SetWindowPos(hwnd, Hwnd.Topmost, _area.Left, _area.Top, _area.Width, _area.Height, Native.SWP_NOACTIVATE);
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
}
