using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskCards;

/// <summary>
/// 바 두께 미리 보기: 바가 다 나왔을 때의 자리를 강조색 테두리로 보여 준다.
/// 창은 가장 두꺼운 바 크기로 가장자리에 붙여 두고, 안의 테두리 두께만 움직인다(ZoneBand와 같은 방식).
/// 처음엔 가장자리에서 자라 나오고, 값을 바꾸면 새 두께로 미끄러지고, 마지막으로 바꾼 뒤 1.5초 있다가 흐려지며 사라진다.
/// </summary>
internal sealed class OutlineBand : Window
{
    private readonly Forms.Screen _screen;
    private readonly ScreenEdge _edge;
    private readonly Border _frame = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(3) };
    private readonly System.Drawing.Rectangle _area;
    private readonly double _scale;
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private bool Side => _edge is ScreenEdge.Left or ScreenEdge.Right;

    public OutlineBand(Forms.Screen screen, ScreenEdge edge)
    {
        _screen = screen;
        _edge = edge;
        _area = BarWindow.FinalRect(screen, edge, Config.BarSizeMax);
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
        _frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        // 테두리는 가장자리 쪽에 붙어서 안쪽으로 자란다.
        _frame.HorizontalAlignment = edge == ScreenEdge.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _frame.VerticalAlignment = edge == ScreenEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        if (Side) { _frame.Width = 0; _frame.VerticalAlignment = VerticalAlignment.Stretch; }
        else { _frame.Height = 0; _frame.HorizontalAlignment = HorizontalAlignment.Stretch; }
        Content = new Grid { Children = { _frame } };
        SourceInitialized += (_, _) =>
        {
            Hwnd.MakeClickThrough(Hwnd.Of(this));
            Place();
        };
        _hold.Tick += (_, _) => { _hold.Stop(); FadeOut(); };
    }

    public bool Matches(Forms.Screen screen, ScreenEdge edge) => screen.DeviceName == _screen.DeviceName && edge == _edge;

    /// <summary>테두리를 sizePercent 두께로 부드럽게 맞추고, 1.5초 유지 시계를 다시 건다.</summary>
    public void ShowSize(int sizePercent)
    {
        var r = BarWindow.FinalRect(_screen, _edge, sizePercent);
        double target = (Side ? r.Width : r.Height) / _scale;
        var property = Side ? WidthProperty : HeightProperty;
        if (!IsVisible)
        {
            Show();
            Place(); // 다른 배율의 모니터로 옮겨지면서 크기가 다시 잡힐 수 있어 한 번 더 맞춘다
            _frame.BeginAnimation(property, Motion.In(target * 0.6, target, Motion.Slow));
            _frame.BeginAnimation(OpacityProperty, Motion.In(0, 1, Motion.Fast));
        }
        else
        {
            _frame.BeginAnimation(property, Motion.In(null, target, Motion.Normal));
            _frame.BeginAnimation(OpacityProperty, Motion.In(null, 1, Motion.Fast)); // 사라지던 중이면 되살린다
        }
        _hold.Stop();
        _hold.Start();
    }

    private void FadeOut()
    {
        var fade = Motion.In(null, 0, Motion.Normal);
        fade.Completed += (_, _) => { if (!_hold.IsEnabled) Hide(); };
        _frame.BeginAnimation(OpacityProperty, fade);
    }

    private void Place() =>
        Native.SetWindowPos(Hwnd.Of(this), Hwnd.Topmost, _area.Left, _area.Top, _area.Width, _area.Height, Native.SWP_NOACTIVATE);
}
