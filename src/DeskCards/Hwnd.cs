using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskCards;

/// <summary>여러 창이 똑같이 쓰는 Win32 창 조작(z 순서, 창 스타일, Windows 11 모양).</summary>
internal static class Hwnd
{
    public static readonly IntPtr Topmost = new(-1);

    public enum Backdrop { None = 1, Mica = 2, Acrylic = 3 }

    /// <summary>창 핸들. 아직 만들어지지 않았으면 IntPtr.Zero.</summary>
    public static IntPtr Of(Window w) => new WindowInteropHelper(w).Handle;

    /// <summary>위치·크기는 그대로 두고 z 순서만 바꾼다(활성화하지 않음).</summary>
    public static void SetZOrder(IntPtr hwnd, IntPtr insertAfter)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Alt+Tab·작업 표시줄에 나오지 않는 도구 창으로.</summary>
    public static void MakeTool(IntPtr hwnd, long extraExStyle = 0)
    {
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
            new IntPtr(ex | Native.WS_EX_TOOLWINDOW | extraExStyle));
    }

    /// <summary>눌러도 활성 창이 되지 않는 도구 창으로.</summary>
    public static void MakeNoActivateTool(IntPtr hwnd, long extraExStyle = 0) =>
        MakeTool(hwnd, Native.WS_EX_NOACTIVATE | extraExStyle);

    /// <summary>마우스 입력이 통과하는 비활성 도구 창으로.</summary>
    public static void MakeClickThrough(IntPtr hwnd) =>
        MakeNoActivateTool(hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED);

    /// <summary>시스템 메뉴를 빼서 캡션의 × 버튼이 그려지지 않게 한다.</summary>
    public static void RemoveSysMenu(IntPtr hwnd)
    {
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(style & ~Native.WS_SYSMENU));
    }

    /// <summary>
    /// Windows 11 모양: 테마에 맞는 창 틀, 둥근 모서리, 시스템 배경(Mica·아크릴).
    /// 배경을 쓰면 WPF가 칠하는 바탕을 비워 시스템 배경이 비치게 한다.
    /// </summary>
    /// <summary>
    /// 바탕화면 카드용 Windows 11 모양: 둥근 모서리·그림자는 DWM, 배경은 비활성이어도 유지되는 아크릴 흐림에 카드 색을 덧칠한다.
    /// </summary>
    public static void ApplyCardBackdrop(Window w, Color tint)
    {
        var hwnd = Of(w);
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsLight ? 0 : 1);
        Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2);
        Native.SetAcrylicBlur(hwnd, tint.A << 24 | tint.B << 16 | tint.G << 8 | tint.R);
    }

    public static void ApplyFluent(Window w, Backdrop backdrop = Backdrop.None, bool roundCorners = true)
    {
        var hwnd = Of(w);
        if (backdrop != Backdrop.None && HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsLight ? 0 : 1);
        if (roundCorners) Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2);
        if (backdrop != Backdrop.None) Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, (int)backdrop);
    }
}
