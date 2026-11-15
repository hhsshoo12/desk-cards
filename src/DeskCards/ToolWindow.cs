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

    private (int X, int Y, double MaxWidth, bool CenterX)? _anchor;

    /// <summary>
    /// 내용 크기(DIP, 너비는 maxWidth까지)로 키워 화면 좌표(물리 픽셀) x, y에 놓는다. centerX면 x는 가운데다.
    /// 템플릿이 적용되기 전에 잰 크기는 작게 나오므로, 내용이 화면에 올라온 뒤 한 번 더 맞춘다.
    /// </summary>
    protected void Place(int x, int y, double maxWidth = double.PositiveInfinity, bool centerX = false)
    {
        bool first = _anchor == null;
        _anchor = (x, y, maxWidth, centerX);
        Reflow();
        if (first) ((FrameworkElement)Content).Loaded += (_, _) => Reflow();
    }

    private void Reflow()
    {
        if (_anchor is not var (x, y, maxWidth, centerX)) return;
        var root = (FrameworkElement)Content;
        root.Measure(new Windows.Foundation.Size(maxWidth, double.PositiveInfinity));
        double scale = Native.MonitorScaleAt(x, y);
        int w = (int)Math.Round(root.DesiredSize.Width * scale), h = (int)Math.Round(root.DesiredSize.Height * scale);
        Resize(new Windows.Graphics.RectInt32(centerX ? x - w / 2 : x, y, w, h));
    }

    /// <summary>
    /// 안쪽(내용 영역)이 정확히 이 크기가 되게 창을 놓는다. 창 테두리만큼 안쪽이 작아져 내용이 잘리는 일을 막는다.
    /// </summary>
    protected void Resize(Windows.Graphics.RectInt32 r)
    {
        AppWindow.MoveAndResize(r);
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.GetClientRect(hwnd, out var c)) return;
        int dw = r.Width - (c.Right - c.Left), dh = r.Height - (c.Bottom - c.Top);
        if (dw != 0 || dh != 0)
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(r.X, r.Y, r.Width + dw, r.Height + dh));
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

