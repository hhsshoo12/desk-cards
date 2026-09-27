using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskCards;

/// <summary>
/// 카드 바 설정을 바꾸는 동안 화면에 잠깐 보여 주는 안내:
/// 바 두께 → 바가 나올 자리의 테두리, 인식 영역 → 가장자리의 어두운 띠, 식별 → 디스플레이마다 큰 번호.
/// 모두 클릭이 통과하는 창이고, 마지막으로 바꾼 뒤 잠시 지나면 사라진다.
/// </summary>
internal static class BarPreview
{
    private static readonly List<Window> _shown = new();
    private static readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    static BarPreview() => _hide.Tick += (_, _) => Clear();

    /// <summary>바가 다 나왔을 때의 자리를 강조색 테두리로.</summary>
    public static void Outline(Forms.Screen screen, ScreenEdge edge, int sizePercent)
    {
        var r = BarWindow.FinalRect(screen, edge, sizePercent);
        var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(3) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        Show(new[] { (r, (UIElement)frame) });
    }

    private static ZoneBand? _zone;

    /// <summary>
    /// 인식 영역(마우스를 대면 게이지가 도는 띠)을 반투명한 어두운 띠로. 카드 바처럼 가장자리에서 부드럽게 나오고,
    /// 값을 바꾸면 새 두께로 부드럽게 바뀌며, 마지막으로 바꾼 뒤 1.5초 있다가 들어간다.
    /// </summary>
    public static void Zone(Forms.Screen screen, ScreenEdge edge, int zone)
    {
        Clear();
        if (_zone == null || !_zone.Matches(screen, edge))
        {
            _zone?.Close();
            _zone = new ZoneBand(screen, edge);
        }
        _zone.ShowDepth(zone);
    }

    /// <summary>디스플레이마다 왼쪽 아래에 큰 번호(Windows 설정의 [식별]처럼).</summary>
    public static void Identify(IReadOnlyList<Forms.Screen> screens)
    {
        var items = new List<(System.Drawing.Rectangle, UIElement)>();
        for (int i = 0; i < screens.Count; i++)
        {
            var wa = screens[i].WorkingArea;
            double s = Native.MonitorScaleAt(wa.Left + 1, wa.Top + 1);
            int w = (int)(150 * s), h = (int)(170 * s), m = (int)(40 * s);
            var number = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 110,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            };
            var plate = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)), Child = number };
            items.Add((new(wa.Left + m, wa.Bottom - m - h, w, h), plate));
        }
        Show(items, TimeSpan.FromSeconds(2.5));
    }

    public static void Clear()
    {
        // 인식 영역 띠는 따로 들어간다(두께를 바꾸는 동안에는 테두리만 지운다).
        _hide.Stop();
        foreach (var w in _shown) w.Close();
        _shown.Clear();
    }

    private static void Show(IEnumerable<(System.Drawing.Rectangle rect, UIElement content)> items, TimeSpan? duration = null)
    {
        Clear();
        foreach (var (rect, content) in items)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                Left = -32000,
                Top = -32000,
                Width = 1,
                Height = 1,
                Content = content,
                Title = "Desk Cards 안내",
            };
            var r = rect;
            w.SourceInitialized += (_, _) =>
            {
                var hwnd = Hwnd.Of(w);
                Hwnd.MakeNoActivateTool(hwnd, 0x20 | 0x80000); // 클릭 통과 + 레이어드
                Native.SetWindowPos(hwnd, Hwnd.Topmost, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);
            };
            w.Show();
            // 다른 배율의 모니터로 옮겨지면서 크기가 다시 잡힐 수 있어 한 번 더 맞춘다.
            Native.SetWindowPos(Hwnd.Of(w), Hwnd.Topmost, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);
            _shown.Add(w);
        }
        _hide.Interval = duration ?? TimeSpan.FromMilliseconds(1500);
        _hide.Start();
    }
}

/// <summary>인식 영역 미리 보기 띠. 창은 가장 넓은 인식 영역 크기로 가장자리에 붙여 두고, 안의 띠 두께만 움직인다.</summary>
internal sealed class ZoneBand : Window
{
    private const int MinVisible = 4; // 0%(1픽셀)도 보이게
    private readonly Forms.Screen _screen;
    private readonly ScreenEdge _edge;
    private readonly Border _band = new() { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };
    private readonly System.Drawing.Rectangle _area;
    private readonly double _scale;
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private bool Side => _edge is ScreenEdge.Left or ScreenEdge.Right;

    public ZoneBand(Forms.Screen screen, ScreenEdge edge)
    {
        _screen = screen;
        _edge = edge;
        _area = Grow(EdgeBar.ZoneRect(screen, edge, Config.BarZoneMax));
        _scale = Native.MonitorScaleAt(_area.Left + 1, _area.Top + 1);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Left = Top = -32000;
        Width = Height = 1;
        Title = "Desk Cards 안내";
        // 띠는 가장자리 쪽에 붙어서 안쪽으로 자란다.
        _band.HorizontalAlignment = edge == ScreenEdge.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _band.VerticalAlignment = edge == ScreenEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        if (Side) { _band.Width = 0; _band.Height = double.NaN; _band.VerticalAlignment = VerticalAlignment.Stretch; }
        else { _band.Height = 0; _band.Width = double.NaN; _band.HorizontalAlignment = HorizontalAlignment.Stretch; }
        Content = new Grid { Children = { _band } };
        SourceInitialized += (_, _) =>
        {
            Hwnd.MakeNoActivateTool(Hwnd.Of(this), 0x20 | 0x80000); // 클릭 통과 + 레이어드
            Place();
        };
        _hold.Tick += (_, _) => { _hold.Stop(); Animate(0, 220, hideAfter: true); };
    }

    public bool Matches(Forms.Screen screen, ScreenEdge edge) => screen.DeviceName == _screen.DeviceName && edge == _edge;

    /// <summary>띠를 zone(0.1% 단위) 두께로 부드럽게 맞추고, 1.5초 유지 시계를 다시 건다.</summary>
    public void ShowDepth(int zone)
    {
        var r = Grow(EdgeBar.ZoneRect(_screen, _edge, zone));
        double target = (Side ? r.Width : r.Height) / _scale;
        if (!IsVisible)
        {
            Show();
            Place(); // 다른 배율의 모니터로 옮겨지면서 크기가 다시 잡힐 수 있어 한 번 더 맞춘다
            Animate(target, 280, hideAfter: false);
        }
        else Animate(target, 180, hideAfter: false);
        _hold.Stop();
        _hold.Start();
    }

    private void Place() =>
        Native.SetWindowPos(Hwnd.Of(this), Hwnd.Topmost, _area.Left, _area.Top, _area.Width, _area.Height, Native.SWP_NOACTIVATE);

    // 카드 바와 같은 느낌: 처음엔 빠르고 끝으로 갈수록 느려진다.
    private void Animate(double to, double ms, bool hideAfter)
    {
        var property = Side ? WidthProperty : HeightProperty;
        double from = Side ? _band.ActualWidth : _band.ActualHeight;
        if (double.IsNaN(from)) from = 0;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        if (hideAfter) anim.Completed += (_, _) => { if (!_hold.IsEnabled) Hide(); };
        _band.BeginAnimation(property, anim);
    }

    /// <summary>0%(1픽셀)처럼 너무 얇은 띠도 보이도록 최소 두께를 준다.</summary>
    private System.Drawing.Rectangle Grow(System.Drawing.Rectangle r)
    {
        if (Side && r.Width < MinVisible)
            return new(_edge == ScreenEdge.Left ? r.Left : r.Right - MinVisible, r.Top, MinVisible, r.Height);
        if (!Side && r.Height < MinVisible)
            return new(r.Left, _edge == ScreenEdge.Top ? r.Top : r.Bottom - MinVisible, r.Width, MinVisible);
        return r;
    }
}
