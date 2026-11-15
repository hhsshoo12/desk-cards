using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace DeskCards;

/// <summary>여러 창이 똑같이 쓰는 Win32 창 조작(z 순서, 창 스타일, Windows 11 모양).</summary>
internal static class Hwnd
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    public static readonly IntPtr Topmost = new(-1);

    public enum Backdrop { None = 1, Mica = 2, Acrylic = 3, DwmAcrylic = 4 }

    /// <summary>창 핸들. 창이 아직 없으면 IntPtr.Zero.</summary>
    public static IntPtr Of(Window? w)
    {
        if (w == null) return IntPtr.Zero;
        try { return WindowNative.GetWindowHandle(w); }
        catch { return IntPtr.Zero; }
    }

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

    /// <summary>다른 창(보통 Progman)을 소유자로 둔다.</summary>
    public static void SetOwner(IntPtr hwnd, IntPtr owner)
    {
        if (hwnd != IntPtr.Zero && owner != IntPtr.Zero) Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, owner);
    }

    /// <summary>
    /// Windows 11 모양: 테마에 맞는 창 틀, 둥근 모서리, 시스템 배경(Mica·아크릴).
    /// 시스템 배경은 늘 활성으로 그려서, 눌러도 활성화되지 않는 바탕화면 카드가 단색으로 바뀌지 않게 한다.
    /// </summary>
    public static void ApplyFluent(Window w, Backdrop backdrop = Backdrop.None, bool roundCorners = true)
    {
        var hwnd = Of(w);
        Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsLight ? 0 : 1);
        if (roundCorners) Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2);
        if (w.Content is FrameworkElement root) root.RequestedTheme = Theme.Element;
        switch (backdrop)
        {
            case Backdrop.Acrylic:
                if (w.SystemBackdrop is ActiveBackdrop a) a.Refresh();
                else w.SystemBackdrop = new ActiveBackdrop(acrylic: true);
                break;
            case Backdrop.DwmAcrylic:
                // WinUI 컨트롤러 대신 Windows(DWM)가 직접 그리는 아크릴. WinUI 쪽은 투명하게 두고 그 밑에 DWM이 그린다(웹 카드와 같은 방식).
                if (w.SystemBackdrop is not DwmPassthroughBackdrop) w.SystemBackdrop = new DwmPassthroughBackdrop();
                Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 3);
                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
                break;
            case Backdrop.Mica:
                if (w.SystemBackdrop is ActiveBackdrop m) m.Refresh();
                else w.SystemBackdrop = new ActiveBackdrop(acrylic: false);
                break;
        }
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct MARGINS { public int Left, Right, Top, Bottom; }

/// <summary>WinUI가 창 뒤를 그리지 않게 투명 색 하나만 붙이는 시스템 배경. 그 밑에 DWM 백드롭이 보인다.</summary>
internal sealed class DwmPassthroughBackdrop : SystemBackdrop
{
    private static global::Windows.UI.Composition.Compositor? _compositor;
    private static object? _queueController;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }

    [System.Runtime.InteropServices.DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    /// <summary>Windows.UI.Composition(시스템 컴포지터)은 시스템 DispatcherQueue가 현재 스레드에 있어야 만들 수 있다.</summary>
    private static global::Windows.UI.Composition.Compositor Compositor()
    {
        if (_compositor != null) return _compositor;
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() == null)
        {
            var options = new DispatcherQueueOptions { Size = System.Runtime.InteropServices.Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 2 };
            if (CreateDispatcherQueueController(options, out var controller) == 0) _queueController = controller;
        }
        return _compositor = new global::Windows.UI.Composition.Compositor();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        target.SystemBackdrop = Compositor().CreateColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
    }
}

/// <summary>창이 비활성이어도 아크릴·Mica가 단색으로 바뀌지 않게 늘 활성으로 그리는 시스템 배경.</summary>
internal sealed class ActiveBackdrop : SystemBackdrop
{
    private readonly bool _acrylic;
    private ISystemBackdropController? _controller;
    private SystemBackdropConfiguration? _config;
    private ICompositionSupportsSystemBackdrop? _target;

    public ActiveBackdrop(bool acrylic) => _acrylic = acrylic;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        _target = target;
        _config = new SystemBackdropConfiguration { IsInputActive = true, Theme = ConfigTheme() };
        if (_acrylic)
        {
            var c = new DesktopAcrylicController();
            c.AddSystemBackdropTarget(target);
            c.SetSystemBackdropConfiguration(_config);
            Tune(c); // 설정을 붙인 뒤에 넣어야 테마 기본값에 덮어써지지 않는다
            _controller = c;
        }
        else
        {
            var c = new MicaController();
            c.AddSystemBackdropTarget(target);
            c.SetSystemBackdropConfiguration(_config);
            _controller = c;
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        if (_controller is DesktopAcrylicController a) a.RemoveSystemBackdropTarget(target);
        else if (_controller is MicaController m) m.RemoveSystemBackdropTarget(target);
        _controller?.Dispose();
        _controller = null;
        _config = null;
        _target = null;
    }

    /// <summary>테마가 바뀌었을 때 배경 색조를 다시 맞춘다.</summary>
    public void Refresh()
    {
        if (_config == null) return;
        _config.Theme = ConfigTheme();
        if (_controller is DesktopAcrylicController a) Tune(a);
    }

    /// <summary>바탕화면이 비치도록 시스템 기본보다 색을 옅게 한다(웹 카드의 DWM 아크릴과 비슷한 정도).</summary>
    private static void Tune(DesktopAcrylicController c)
    {
        c.TintOpacity = 0.1f;
        c.LuminosityOpacity = 0.35f;
    }

    private static SystemBackdropTheme ConfigTheme() => Theme.IsLight ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
}
