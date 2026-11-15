using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace DeskCards.Expanded;

/// <summary>카드 바가 앱에 알리는 일. 구현은 앱 UI 스레드로 넘겨서 처리한다.</summary>
public interface IBarHost
{
    /// <summary>바가 다 들어가서 창이 닫혔다.</summary>
    void Closed();
    void Launch(string path);
    /// <summary>카드 안 항목의 우클릭 메뉴(앱이 연다).</summary>
    void ItemMenu(string path);
    /// <summary>카드의 아이콘이 아닌 곳을 우클릭했을 때의 메뉴(앱이 연다).</summary>
    void CardMenu(string group);
    /// <summary>카드를 펼친다. cardRect는 그 카드가 화면에 있는 자리(물리 픽셀).</summary>
    void Expand(string group, bool hover, PixelRect cardRect);
    void AddToGroup(string group, IReadOnlyList<string> paths);
    /// <summary>비어 있는 바를 눌렀다(카드를 넣는 메뉴를 연다).</summary>
    void EmptyClicked(PixelRect barRect);
    /// <summary>편집 중 카드를 눌러 골랐다(null = 선택 해제).</summary>
    void SelectionChanged(string? group);
    /// <summary>편집 중 카드를 옮기거나 키웠다. 모든 카드의 새 자리.</summary>
    void ItemsEdited(IReadOnlyList<BarItemData> items);
    /// <summary>편집 중 Esc를 눌렀다.</summary>
    void EditEnded();
    /// <summary>편집 중 Delete를 눌렀다(그 카드를 바에서 뺀다).</summary>
    void RemoveRequested(string group);
    /// <summary>바 옆 설정 버튼을 눌렀다.</summary>
    void SettingsRequested();
    /// <summary>바 옆 알약의 연필(편집)을 눌렀다.</summary>
    void EditRequested();
    /// <summary>편집 중 알약의 톱니를 눌렀다(편집 막대 펼치기·접기).</summary>
    void ToggleEditBar();
    /// <summary>카드를 끌 때 안내선에 맞춰 붙은 자리(앱이 안내선도 그린다). 화면 물리 픽셀.</summary>
    PixelRect SnapMove(string group, PixelRect rect);
    /// <summary>비율을 지키며 크기를 바꿀 때 안내선에 맞춘 배율(start는 바꾸기 시작할 때 자리).</summary>
    double SnapScale(string group, PixelRect start, double scale);
    /// <summary>그 자리가 괜찮은지. 0 = 괜찮음, 1 = 다른 카드와 너무 가까움, 2 = 겹침.</summary>
    int Placement(string group, PixelRect rect);
    /// <summary>너무 가까운 카드에서 간격만큼 떨어지게 민 자리.</summary>
    PixelRect PushApart(string group, PixelRect rect);
    /// <summary>안내선을 지운다.</summary>
    void GuidesOff();
}

/// <summary>편집한 카드의 자리(바 두께를 1로 친 값).</summary>
public sealed record BarItemData(string Group, double X, double Y, double W);

/// <summary>바에 놓은 카드 하나. X·Y·W는 바 두께를 1로 친 값이고, 칸 하나의 기준 크기는 Cell(DIP)이다.</summary>
public sealed record BarCardData(string Group, string Name, string Folder, int Cols, int Rows, double Cell,
    double X, double Y, double W, IReadOnlyList<TileData> Items);

public sealed record BarContent(IReadOnlyList<BarCardData> Cards, bool HoverExpand, int HoverDelayMs);

/// <param name="Edge">0 = 왼쪽, 1 = 위, 2 = 오른쪽, 3 = 아래.</param>
/// <param name="Final">다 나왔을 때 자리(물리 픽셀).</param>
/// <param name="Bounds">바가 붙는 모니터 전체(물리 픽셀). 모니터 밖으로 나간 부분은 잘라서 보여 준다.</param>
public sealed record BarOptions(Palette Palette, int Edge, PixelRect Final, PixelRect Bounds, double Scale, PixelRect? FloatBody = null);

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>화면 가장자리에서 미끄러져 나오는 카드 바 창(아크릴). 어느 스레드에서 불러도 된다.</summary>
public sealed class BarView
{
    private BarWindow? _window;

    /// <summary>바 창 핸들(창이 뜨기 전에는 0). 이 바에서 연 펼친 창이 그 위에 보이게 하는 데 쓴다.</summary>
    public IntPtr Handle => _window?.HandleValue ?? IntPtr.Zero;

    /// <summary>바 안 카드 내용을 바꾼다(처음·그룹·아이콘이 바뀔 때).</summary>
    public void SetContent(BarContent content) => WpfThread.Dispatcher.BeginInvoke(() => _window?.SetContent(content));

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
        public void Launch(string path) { }
        public void ItemMenu(string path) { }
        public void CardMenu(string group) { }
        public void Expand(string group, bool hover, PixelRect cardRect) { }
        public void AddToGroup(string group, IReadOnlyList<string> paths) { }
        public void EmptyClicked(PixelRect barRect) { }
        public void SelectionChanged(string? group) { }
        public void ItemsEdited(IReadOnlyList<BarItemData> items) { }
        public void EditEnded() { }
        public void RemoveRequested(string group) { }
        public void SettingsRequested() { }
        public void EditRequested() { }
        public void ToggleEditBar() { }
        public PixelRect SnapMove(string group, PixelRect rect) => rect;
        public double SnapScale(string group, PixelRect start, double scale) => scale;
        public int Placement(string group, PixelRect rect) => 0;
        public PixelRect PushApart(string group, PixelRect rect) => rect;
        public void GuidesOff() { }
    }

    public static BarView Open(IBarHost host, BarOptions options, BarContent content)
    {
        var view = new BarView();
        WpfThread.Dispatcher.BeginInvoke(() =>
        {
            var w = new BarWindow(host, options);
            view._window = w;
            w.SetContent(content);
            w.Open();
        });
        return view;
    }

    public void BeginEdit() => WpfThread.Dispatcher.BeginInvoke(() => _window?.BeginEdit());

    public void EndEdit() => WpfThread.Dispatcher.BeginInvoke(() => _window?.EndEdit());

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
