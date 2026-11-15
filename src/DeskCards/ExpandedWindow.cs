using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Dispatching;
using DeskCards.Expanded;
using Windows.Foundation;

namespace DeskCards;

/// <summary>
/// 카드를 눌렀을 때 펼쳐지는 전체 목록 창. 창 자체는 WPF 라이브러리(DeskCards.Expanded)가 따로 만든 스레드에서 그리고
/// (WinUI 창은 Windows의 창 열기 효과를 쓸 수 없다), 이 클래스는 그 창에 항목을 넘기고 부탁받은 일을 UI 스레드에서 해 준다.
/// </summary>
internal sealed class ExpandedWindow : IExpandedHost
{
    private static ExpandedWindow? _current;

    private readonly CardWindow _card;
    private readonly GroupManager _mgr;
    private readonly Func<int, int, Point>? _place;
    private readonly IntPtr _anchor;
    private ExpandedView _view = null!;
    private List<ShellEntry> _watched = new();
    private bool _closing, _openSettings, _pushQueued;

    private ExpandedWindow(CardWindow card, GroupManager mgr, Func<int, int, Point>? place, IntPtr anchor)
    {
        _card = card;
        _mgr = mgr;
        _place = place;
        _anchor = anchor;
    }

    private GroupModel Group => _card.Group;

    /// <summary>앱을 켠 뒤 미리 WPF 쪽을 올려 두어 첫 펼침이 늦지 않게 한다.</summary>
    public static void Warmup()
    {
        ExpandedView.Warmup();
        BarView.Warmup();
    }

    /// <param name="hover">더보기 칸에 올려 두어서 연 경우. 마우스가 창 밖으로 나가면 바로 닫힌다.</param>
    /// <param name="place">창 크기(물리 픽셀)를 받아 띄울 자리(물리 픽셀)를 정한다. 없으면 카드 가운데에 펼친다.</param>
    /// <param name="anchor">바탕화면 카드 대신 다른 창에서 열었을 때의 창. 그 창이 있는 쪽에 뜨고 그 위에 보인다.</param>
    public static void Open(CardWindow card, GroupManager mgr, bool editTitle = false, bool hover = false,
        Func<int, int, Point>? place = null, IntPtr anchor = default)
    {
        _current?.Close();
        var w = new ExpandedWindow(card, mgr, place, anchor);
        _current = w;
        var options = new ExpandedOptions(
            new Palette(Hex("Fg"), Hex("SubFg"), Hex("HoverBg"), Hex("PopupBg"), Theme.IsLight),
            EditTitle: editTitle, Hover: hover, Topmost: mgr.Editing || anchor != IntPtr.Zero,
            Reference: anchor != IntPtr.Zero ? anchor : card.Handle);
        w._view = ExpandedView.Open(w, options, card.Group.Name, card.Group.Folder, w.Snapshot());
        card.Group.Changed += w.Push;
        w.Watch();
    }

    public static bool IsOpen => _current is { _closing: false };

    public static bool IsOpenFor(CardWindow card) => _current is { _closing: false } w && w._card == card;

    public static void CloseCurrent() => _current?.Close();

    public static void CloseFor(CardWindow card)
    {
        if (_current != null && _current._card == card) _current.Close();
    }

    private void Close()
    {
        if (_closing) return;
        _view.Close();
    }

    /// <summary>지금 테마의 색(카드 바 같은 다른 WPF 창에도 쓴다).</summary>
    public static Palette CurrentPalette() => new(Hex("Fg"), Hex("SubFg"), Hex("HoverBg"), Hex("PopupBg"), Theme.IsLight, Hex("CardBg"), Hex("CardBorder"), Hex("Accent"));

    public static string Hex(string key)
    {
        var c = Theme.Color(key);
        return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    // ----- 내용 보내기 -----

    private IReadOnlyList<TileData> Snapshot() => Group.Items
        .Select(e => new TileData(e.Path, e.Name, e.IconPixels?.Width ?? 0, e.IconPixels?.Height ?? 0, e.IconPixels?.Pixels))
        .ToList();

    /// <summary>항목이 바뀌거나 아이콘이 도착하면 한꺼번에 모아 한 번만 보낸다.</summary>
    private void Push()
    {
        if (_closing || _pushQueued) return;
        _pushQueued = true;
        Ui.Post(() =>
        {
            _pushQueued = false;
            if (_closing) return;
            Watch();
            _view.SetContent(Group.Name, Group.Folder, Snapshot());
        });
    }

    private void Watch()
    {
        foreach (var e in _watched) e.PropertyChanged -= OnEntryChanged;
        _watched = Group.Items.ToList();
        foreach (var e in _watched) e.PropertyChanged += OnEntryChanged;
    }

    private void OnEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Push();

    // ----- IExpandedHost: WPF 창 스레드에서 불린다 -----

    internal static T OnUi<T>(Func<T> func, T fallback)
    {
        if (Ui.CheckAccess) return func();
        T result = fallback;
        using var done = new ManualResetEventSlim();
        if (!Ui.Queue.TryEnqueue(() => { try { result = func(); } finally { done.Set(); } })) return fallback;
        return done.Wait(3000) ? result : fallback;
    }

    public (int X, int Y) Place(int widthPx, int heightPx) => OnUi(() =>
    {
        var reference = _anchor != IntPtr.Zero ? _anchor : _card.Handle;
        Point p;
        if (_place != null) p = _place(widthPx, heightPx);
        else
        {
            double scale = Native.MonitorScaleOf(reference);
            Native.GetWindowRect(reference, out var rect);
            var wa = Screens.FromHandle(reference).WorkArea;
            p = PixelPlacement.Beside(rect, new Rect(wa.Left, wa.Top, wa.Width, wa.Height), new Size(widthPx / scale, heightPx / scale), scale, centered: true);
        }
        return ((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }, (0, 0));

    public bool Rename(string name) => OnUi(() => _mgr.Cards.Contains(_card) && _mgr.RenameGroup(_card, name), false);

    public void Launch(string path) => Ui.Post(() => FileOps.Launch(path));

    public void ItemMenu(string path) => Ui.Post(() =>
    {
        var entry = Group.Items.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry == null) { _view.MenuClosed(); return; }
        Menus.ForEntry(entry, launched: Close).OnClosed(() => _view.MenuClosed()).ShowAtCursor();
    });

    public void AddToGroup(IReadOnlyList<string> paths) => Ui.Post(() => FileOps.AddToGroup(paths, Group.Folder, _mgr.Root));

    public void SetOrder(IReadOnlyList<string> paths) => Ui.Post(() =>
    {
        var byPath = Group.Items.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        _mgr.SetOrder(Group, paths.Where(byPath.ContainsKey).Select(p => byPath[p]));
    });

    public void OpenSettings() => Ui.Post(() => _openSettings = true);

    public void Closed() => Ui.Post(() =>
    {
        _closing = true;
        Group.Changed -= Push;
        foreach (var e in _watched) e.PropertyChanged -= OnEntryChanged;
        _watched.Clear();
        if (_current == this) _current = null;
        // 설정 창은 이 창이 완전히 닫힌 다음에 연다.
        if (_openSettings) Ui.Post(() => SettingsWindow.Open(_mgr, _card), DispatcherQueuePriority.Low);
    });
}
