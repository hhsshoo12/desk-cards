using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskCards;

/// <summary>
/// 마우스 휠을 Windows 설정 앱처럼 부드럽게: 한 칸마다 정해진 거리만큼 목표를 옮기고,
/// 지금 자리에서 목표까지 감속 곡선으로 미끄러진다. 빠르게 굴리면 목표가 쌓여 한 번에 확 넘어간다.
/// 터치패드처럼 작은 값은 그만큼만 옮긴다. 휠을 직접 쓰는 컨트롤(Tag = WheelOwner) 위에서는 손대지 않는다.
/// </summary>
internal sealed class SmoothScroll
{
    /// <summary>이 Tag를 단 요소 위에서 굴린 휠은 그 요소가 쓴다(예: 숫자 칸).</summary>
    public const string WheelOwner = "wheel-owner";

    /// <summary>휠 한 칸(120)에 움직이는 거리(DIP) = Windows "한 번에 스크롤할 줄 수" × 한 줄.</summary>
    private const double LineDip = 36;

    private readonly ScrollViewer _viewer;
    private readonly Stopwatch _clock = new();
    private double _from, _to;
    private bool _running;

    private SmoothScroll(ScrollViewer viewer) => _viewer = viewer;

    public static SmoothScroll Attach(ScrollViewer viewer)
    {
        var s = new SmoothScroll(viewer);
        viewer.PreviewMouseWheel += s.OnWheel;
        viewer.Unloaded += (_, _) => s.Stop();
        return s;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != _viewer; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: WheelOwner }) return;
        if (_viewer.ScrollableHeight <= 0) return;
        e.Handled = true;

        int lines = SystemParameters.WheelScrollLines;
        double notch = lines < 0 ? _viewer.ViewportHeight : lines * LineDip; // -1 = 한 화면씩
        double start = _running ? _to : _viewer.VerticalOffset;
        double to = Math.Clamp(start - e.Delta / 120.0 * notch, 0, _viewer.ScrollableHeight);
        if (!Motion.Enabled)
        {
            _viewer.ScrollToVerticalOffset(to);
            return;
        }
        _from = _viewer.VerticalOffset;
        _to = to;
        _clock.Restart();
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalMilliseconds / Motion.Slow;
        _viewer.ScrollToVerticalOffset(_from + (_to - _from) * Motion.Decel(t));
        if (t >= 1) Stop();
    }

    /// <summary>미끄러지는 중이면 그 자리에서 멈춘다(페이지를 바꾸거나 직접 스크롤 위치를 정할 때).</summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }
}
