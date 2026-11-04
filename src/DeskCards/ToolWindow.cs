using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace DeskCards;

/// <summary>
/// 제목 줄 없는 작은 맨 위 창(편집 막대·펼친 창·말풍선 같은 것)의 바탕.
/// Windows가 그리는 1px 테두리·둥근 모서리·그림자와 아크릴 배경만 두고, 내용 크기에 맞춰 띄운다.
/// 작업 표시줄·Alt+Tab에는 나타나지 않는다.
/// </summary>
internal abstract class ToolWindow : Window
{
    protected ToolWindow(string title, FrameworkElement content, Hwnd.Backdrop backdrop = Hwnd.Backdrop.Acrylic, bool topmost = true)
    {
        Title = title;
        ExtendsContentIntoTitleBar = true;
        var aw = AppWindow;
        aw.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        if (aw.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(true, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = topmost;
        }
        aw.IsShownInSwitchers = false;
        Content = content;
        Hwnd.MakeTool(Handle);
        Hwnd.ApplyFluent(this, backdrop);
        Theme.Changed += OnTheme;
        Closed += (_, _) => Theme.Changed -= OnTheme;
    }

    protected IntPtr Handle => Hwnd.Of(this);

    private void OnTheme() => Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);

    /// <summary>내용 크기(DIP, 너비는 maxWidth까지)로 키워 화면 좌표(물리 픽셀) x, y에 놓는다.</summary>
    protected void Place(int x, int y, double maxWidth = double.PositiveInfinity)
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Windows.Foundation.Size(maxWidth, double.PositiveInfinity));
        double scale = Native.MonitorScaleAt(x, y);
        int w = (int)Math.Round(root.DesiredSize.Width * scale), h = (int)Math.Round(root.DesiredSize.Height * scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    /// <summary>화면 좌표 물리 픽셀로 본 이 창의 크기.</summary>
    protected (int W, int H) PixelSize()
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double scale = Native.MonitorScaleAt(0, 0);
        return ((int)Math.Round(root.DesiredSize.Width * scale), (int)Math.Round(root.DesiredSize.Height * scale));
    }
}
