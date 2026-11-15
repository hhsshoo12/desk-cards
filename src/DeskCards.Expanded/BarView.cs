using System;
using System.Collections.Generic;
using System.Windows.Threading;

namespace DeskCards.Expanded;

/// <summary>카드 바가 앱에 알리는 일. 구현은 앱 UI 스레드로 넘겨서 처리한다.</summary>
public interface IBarHost : ICardViewHost
{
    /// <summary>바가 다 들어가서 창이 닫혔다.</summary>
    void Closed();
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
    /// <summary>바 옆 메뉴의 편집을 눌렀다.</summary>
    void EditRequested();
    /// <summary>편집 단추의 "카드 넣기"(카드를 고르는 메뉴를 연다).</summary>
    void AddRequested();
    /// <summary>안내선 단추를 눌렀다.</summary>
    void GuidesChanged(bool on);
    /// <summary>바 안에서 카드 이름을 직접 고쳤다.</summary>
    void RenameCard(string group, string name);
    /// <summary>칸 수 단추(anchor는 그 단추의 화면 자리). 앱이 칸 수 고르는 창을 띄운다.</summary>
    void GridRequested(string group, PixelRect anchor);
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
    /// <summary>바 안 웹 카드 화면이 deskcard.send로 보낸 메시지(JSON). 앱이 카드의 exe로 넘긴다.</summary>
    void WebMessage(string id, string json);
    /// <summary>바 안 웹 카드 위에서 우클릭했다.</summary>
    void WebMenu(string id);
}

/// <summary>편집한 카드의 자리(바 두께를 1로 친 값).</summary>
public sealed record BarItemData(string Group, double X, double Y, double W);

/// <summary>바에 놓은 카드 하나. X·Y·W는 바 두께를 1로 친 값이고, 칸 하나의 기준 크기는 Cell(DIP)이다.</summary>
/// <summary>바 안 웹 카드. Aspect는 세로/가로 비율이다.</summary>
public sealed record WebBarCard(string Id, string Folder, string Html, string BrowserDataDir, double Aspect);

public sealed record BarCardData(string Group, string Name, string Folder, int Cols, int Rows, double Cell,
    double X, double Y, double W, IReadOnlyList<TileData> Items, WebBarCard? Web = null);

public sealed record BarContent(IReadOnlyList<BarCardData> Cards, bool HoverExpand, int HoverDelayMs, bool Guides = false);

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
        public void AddRequested() { }
        public void GuidesChanged(bool on) { }
        public void RenameCard(string group, string name) { }
        public void GridRequested(string group, PixelRect anchor) { }
        public PixelRect SnapMove(string group, PixelRect rect) => rect;
        public double SnapScale(string group, PixelRect start, double scale) => scale;
        public int Placement(string group, PixelRect rect) => 0;
        public PixelRect PushApart(string group, PixelRect rect) => rect;
        public void GuidesOff() { }
        public void WebMessage(string id, string json) { }
        public void WebMenu(string id) { }
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

    /// <summary>그 카드 아래 이름 글자를 바로 고칠 수 있게 입력칸으로 바꾼다.</summary>
    public void BeginRename(string group) => WpfThread.Dispatcher.BeginInvoke(() => _window?.BeginRename(group));

    /// <summary>바 안 웹 카드 화면으로 exe가 보낸 메시지(JSON)를 전한다.</summary>
    public void WebToPage(string id, string json) => WpfThread.Dispatcher.BeginInvoke(() => _window?.WebToPage(id, json));

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
