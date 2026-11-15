using System;
using System.Windows.Threading;

namespace DeskCards.Expanded;

/// <summary>카드 바가 앱에 알리는 일. 구현은 앱 UI 스레드로 넘겨서 처리한다.</summary>
public interface IBarHost
{
    /// <summary>바가 다 들어가서 창이 닫혔다.</summary>
    void Closed();
}

/// <param name="Edge">0 = 왼쪽, 1 = 위, 2 = 오른쪽, 3 = 아래.</param>
/// <param name="Final">다 나왔을 때 자리(물리 픽셀).</param>
/// <param name="Bounds">바가 붙는 모니터 전체(물리 픽셀). 모니터 밖으로 나간 부분은 잘라서 보여 준다.</param>
public sealed record BarOptions(Palette Palette, int Edge, PixelRect Final, PixelRect Bounds, double Scale);

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>화면 가장자리에서 미끄러져 나오는 카드 바 창(아크릴). 어느 스레드에서 불러도 된다.</summary>
public sealed class BarView
{
    private BarWindow? _window;

    /// <summary>카드 바 창을 화면 밖에서 한 번 그려 본다(첫 열림에 회색 배경이 먼저 비치지 않게).</summary>
    public static void Warmup() => WpfThread.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        try
        {
            var area = new PixelRect(-32000, -32000, -31000, -31400);
            var w = new BarWindow(new WarmupHost(), new BarOptions(new Palette("#FFFFFFFF", "#FFC5C5C5", "#18FFFFFF", "#B82C2C2C", false), 2, area, area, 1));
            w.ShowActivated = false;
            w.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, _) => { timer.Stop(); w.Close(); };
            timer.Start();
        }
        catch { }
    });

    private sealed class WarmupHost : IBarHost
    {
        public void Closed() { }
    }

    public static BarView Open(IBarHost host, BarOptions options)
    {
        var view = new BarView();
        WpfThread.Dispatcher.BeginInvoke(() =>
        {
            var w = new BarWindow(host, options);
            view._window = w;
            w.Open();
        });
        return view;
    }

    /// <summary>바를 가장자리로 다시 밀어 넣고 닫는다.</summary>
    public void BeginClose() => WpfThread.Dispatcher.BeginInvoke(() => _window?.BeginClose());

    /// <summary>앱이 끝나는 중이라 바로 닫는다.</summary>
    public void CloseNow() => WpfThread.Dispatcher.BeginInvoke(() => _window?.Close());
}

/// <summary>커서 둘레에 그리는 원형 게이지. 클릭이 통과하는 투명 창이다.</summary>
public static class Gauge
{
    private static GaugeWindow? _window;

    /// <param name="x">게이지 창의 왼쪽 위(물리 픽셀). 화면 안으로 당겨서 넘겨 준다.</param>
    /// <param name="accent">호 색(#AARRGGBB).</param>
    public static void Update(int x, int y, double progress, string accent) =>
        WpfThread.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            _window ??= new GaugeWindow(accent);
            _window.Update(x, y, progress, accent);
        });

    public static void Hide() => WpfThread.Dispatcher.BeginInvoke(DispatcherPriority.Send, () => _window?.HideNow());

    public static void Close() => WpfThread.Dispatcher.BeginInvoke(() => { _window?.Close(); _window = null; });
}
