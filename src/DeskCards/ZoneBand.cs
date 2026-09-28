using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskCards;

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
            Hwnd.MakeClickThrough(Hwnd.Of(this));
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
        var anim = Motion.QuarticOut(from, to, ms);
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

