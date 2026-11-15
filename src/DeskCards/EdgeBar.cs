using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using DeskCards.Expanded;
using Microsoft.UI.Dispatching;

namespace DeskCards;

/// <summary>
/// 카드 바 여는 조건을 지켜본다: 조합키를 누른 채 정한 가장자리에 마우스를 대고 있으면
/// 커서 둘레의 게이지가 12시부터 시계방향으로 차오르고, 한 바퀴가 되는 순간 바가 나온다.
/// 작업 표시줄이 있는 가장자리와 전체 화면 앱 위에서는 열지 않는다.
/// 조합키는 Windows 단축키로 등록해 둬서, 누르는 동안 앞의 앱에 키가 가지 않는다.
/// 바 창과 게이지는 WPF 라이브러리(DeskCards.Expanded)가 그리고, 여기서는 조건과 시간을 본다.
/// </summary>
internal static class EdgeBar
{
    private const int HotkeyId = 1, WM_HOTKEY = 0x0312;
    private const double Gap = 8;

    private static MessageWindow? _hotkeyWindow;
    private static string _registeredFor = "";
    private static bool _suspended;

    /// <summary>조합키 등록 결과. 카드 바가 꺼져 있으면 null, 다른 곳에서 이미 쓰는 조합이면 false.</summary>
    public static bool? HotkeyRegistered { get; private set; }
    private static GroupManager? _mgr;
    private static DispatcherQueueTimer? _timer;
    private static bool _gaugeShown;
    private static OpenBar? _bar;
    private static readonly Stopwatch _dwell = new();
    private static bool _armed = true;

    /// <summary>떠 있는 카드 바 하나. 바 창(WPF)이 부탁하는 일을 UI 스레드에서 해 준다.</summary>
    private sealed class OpenBar : IBarHost
    {
        public BarView View = null!;
        public GroupManager Mgr = null!;
        public Rectangle Final, Bounds, WorkArea;
        public ScreenEdge Edge;
        public double Scale;
        public bool Gone, Closing, Editing;
        private bool _saving;
        public string? Selected { get; private set; }
        private List<ShellEntry> _watched = new();
        private bool _pushQueued;

        private const double Pad = 12, Gap = 8, BarFloat = 52;
        private bool Side => Edge is ScreenEdge.Left or ScreenEdge.Right;
        private double U => Math.Max(1, (Side ? Final.Width : Final.Height) / Scale - 2 * Pad);
        private (double W, double H) Inner => (Math.Max(1, Final.Width / Scale - 2 * Pad), Math.Max(1, Final.Height / Scale - 2 * Pad));

        public void Attach()
        {
            Mgr.Changed += Push;
            Mgr.BarChanged += Push;
            Watch();
        }

        private void Detach()
        {
            Mgr.Changed -= Push;
            Mgr.BarChanged -= Push;
            foreach (var e in _watched) e.PropertyChanged -= OnEntryChanged;
            _watched.Clear();
        }

        private IEnumerable<(BarItem Item, CardWindow Card)> Placed() =>
            Mgr.BarItems.Select(i => (Item: i, Card: Mgr.GroupCard(i.Group))).Where(x => x.Card != null).Select(x => (x.Item, x.Card!));

        public BarContent Content() => new(
            Placed().Select(x =>
            {
                var layout = Mgr.GetLayout(x.Card.Group.Name);
                var items = ExpandedWindow.ToTiles(x.Card.Group);
                return new BarCardData(x.Card.Group.Name, x.Card.Group.Name, x.Card.Group.Folder, layout.Cols, layout.Rows, Mgr.CellSize,
                    x.Item.X, x.Item.Y, x.Item.W, items);
            }).ToList(),
            Mgr.HoverExpand, Mgr.HoverExpandDelay, Mgr.ShowGuides);

        private void Watch()
        {
            foreach (var e in _watched) e.PropertyChanged -= OnEntryChanged;
            _watched = Placed().SelectMany(x => x.Card.Group.Items).ToList();
            foreach (var e in _watched) e.PropertyChanged += OnEntryChanged;
        }

        private void OnEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Push();

        /// <summary>그룹·배치가 바뀌거나 아이콘이 도착하면 한꺼번에 모아 한 번만 보낸다.</summary>
        private void Push()
        {
            if (Gone || _pushQueued || _saving) return;
            _pushQueued = true;
            Ui.Post(() =>
            {
                _pushQueued = false;
                if (Gone) return;
                Watch();
                View.SetContent(Content());
            });
        }

        // ----- IBarHost: WPF 창 스레드에서 불린다 -----

        public void Closed() => Ui.Post(() => { Gone = true; End(); Detach(); SyncPolling(); });

        public void Launch(string path) => Ui.Post(() => FileOps.Launch(path));

        public void ItemMenu(string path) => Ui.Post(() =>
        {
            var entry = Placed().SelectMany(x => x.Card.Group.Items).FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (entry != null) Menus.ForEntry(entry).ShowAtCursor();
        });

        public void CardMenu(string group) => Ui.Post(() =>
        {
            if (Mgr.GroupCard(group) is not { } card) return;
            var m = Menus.ForCard(card, Mgr, expand: () => ExpandFor(card, false, null), edit: Begin, rename: () => { Begin(); Ui.Post(() => View.BeginRename(group)); }).Separator();
            m.Item("", "카드 바에서 빼기", () => Remove(group)).ShowAtCursor();
        });

        public void AddToGroup(string group, IReadOnlyList<string> paths) => Ui.Post(() =>
        {
            if (Mgr.GroupCard(group) is { } card) FileOps.AddToGroup(paths, card.Group.Folder, Mgr.Root);
        });

        public void Expand(string group, bool hover, PixelRect cardRect) => Ui.Post(() =>
        {
            if (Mgr.GroupCard(group) is not { } card) return;
            ExpandFor(card, hover, cardRect);
        });

        private void ExpandFor(CardWindow card, bool hover, PixelRect? cardRect)
        {
            if (hover && ExpandedWindow.IsOpenFor(card)) return;
            ExpandedWindow.Open(card, Mgr, hover: hover, anchor: View.Handle, place: (w, h) =>
            {
                if (cardRect is not { } r) return CenterIn(w, h);
                // 시작 메뉴 폴더처럼 누른 카드 자리 가운데에 바 안쪽으로 펼친다(바보다 크면 바 가운데, 화면 밖으로는 나가지 않게).
                double cx = (r.Left + r.Right) / 2.0, cy = (r.Top + r.Bottom) / 2.0;
                double x = Math.Clamp(cx - w / 2.0, Final.Left, Math.Max(Final.Left, Final.Right - w));
                double y = Math.Clamp(cy - h / 2.0, Final.Top, Math.Max(Final.Top, Final.Bottom - h));
                if (w > Final.Width) x = (Final.Left + Final.Right - w) / 2.0;
                if (h > Final.Height) y = (Final.Top + Final.Bottom - h) / 2.0;
                x = Math.Clamp(x, WorkArea.Left, Math.Max(WorkArea.Left, WorkArea.Right - w));
                y = Math.Clamp(y, WorkArea.Top, Math.Max(WorkArea.Top, WorkArea.Bottom - h));
                return new Windows.Foundation.Point(x, y);
            });
        }

        public void EmptyClicked(PixelRect barRect) => Ui.Post(() =>
        {
            if (!Editing) Begin();
            ShowAddMenu();
        });

        public void ShowAddMenu()
        {
            var menu = new FluentMenu();
            var cards = Mgr.CardsNotInBar().ToList();
            foreach (var c in cards)
            {
                var card = c;
                menu.Item("", card.Group.Name, () => Add(card), image: card.Group.Items.FirstOrDefault()?.Icon);
            }
            if (cards.Count > 0) menu.Separator();
            menu.Item("", "카드 바에만 둘 새 그룹", () =>
            {
                if (Mgr.NewBarGroup() is not { } card) return;
                Add(card);
            });
            menu.ShowAtCursor();
        }

        // ----- 바 편집 -----

        public void Begin()
        {
            if (Editing || Gone) return;
            Editing = true;
            Selected = null;
            ExpandedWindow.CloseCurrent();
            View.BeginEdit();
        }

        /// <summary>바 편집을 끝낸다(막대를 닫고 바는 평소 모습으로).</summary>
        public void End()
        {
            if (!Editing) return;
            Editing = false;
            Selected = null;
            if (!Gone) View.EndEdit();
        }

        public void SelectionChanged(string? group) => Ui.Post(() => Selected = group);

        public void ItemsEdited(IReadOnlyList<BarItemData> items) => Ui.Post(() =>
        {
            var next = items.Select(i => new BarItem { Group = i.Group, X = i.X, Y = i.Y, W = i.W }).ToList();
            _saving = true;
            try { Mgr.SetBarItems(next); }
            finally { _saving = false; }
        });

        public void EditEnded() => Ui.Post(End);

        // ----- 안내선: 바탕화면 카드와 같은 SmartGuides를 바 안쪽 영역 기준 물리 픽셀로 쓴다. WPF 창 스레드가 기다리며 부른다. -----

        private Native.RECT RectPx(double x, double y, double w, double aspect)
        {
            double u = U, s = Scale;
            int l = (int)Math.Round(Final.Left + (Pad + x * u) * s), t = (int)Math.Round(Final.Top + (Pad + y * u) * s);
            int wp = (int)Math.Round(w * u * s), hp = (int)Math.Round(w * u * aspect * s);
            return new Native.RECT { Left = l, Top = t, Right = l + wp, Bottom = t + hp };
        }

        private List<Native.RECT> OthersPx(string group) => Placed()
            .Where(p => !string.Equals(p.Item.Group, group, StringComparison.OrdinalIgnoreCase))
            .Select(p => RectPx(p.Item.X, p.Item.Y, p.Item.W, AspectOf(p.Card))).ToList();

        private Rectangle Area()
        {
            int pad = (int)Math.Round(Pad * Scale);
            return new Rectangle(Final.Left + pad, Final.Top + pad, Math.Max(1, Final.Width - 2 * pad), Math.Max(1, Final.Height - 2 * pad));
        }

        private static Native.RECT Rc(PixelRect r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };

        private static PixelRect Pr(Native.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

        public PixelRect SnapMove(string group, PixelRect rect) => ExpandedWindow.OnUi(() =>
        {
            var (x, y, lines) = SmartGuides.Snap(rect.Left, rect.Top, rect.Width, rect.Height, OthersPx(group), Area(), Scale);
            if (Mgr.ShowGuides) SmartGuides.Show(Area(), lines); else SmartGuides.Hide();
            return new PixelRect(x, y, x + rect.Width, y + rect.Height);
        }, rect);

        public double SnapScale(string group, PixelRect start, double scale) => ExpandedWindow.OnUi(() =>
        {
            var (g, lines) = SmartGuides.SnapScale(Rc(start), scale, OthersPx(group), Area(), Scale);
            if (Mgr.ShowGuides) SmartGuides.Show(Area(), lines); else SmartGuides.Hide();
            return g;
        }, scale);

        public int Placement(string group, PixelRect rect) => ExpandedWindow.OnUi(
            () => (int)SmartGuides.Check(Rc(rect), OthersPx(group), SmartGuides.GapPx(Scale)), 0);

        public PixelRect PushApart(string group, PixelRect rect) => ExpandedWindow.OnUi(() =>
        {
            var (dx, dy) = SmartGuides.PushApart(Rc(rect), OthersPx(group), SmartGuides.GapPx(Scale));
            return new PixelRect(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);
        }, rect);

        public void GuidesOff() => Ui.Post(SmartGuides.Hide);

        public void SettingsRequested() => Ui.Post(() => SettingsWindow.OpenBar(Mgr));

        public void EditRequested() => Ui.Post(Begin);



        /// <summary>바 옆 메뉴 햄버거 칸의 자리(물리 픽셀): 바의 시작 모서리, 화면 안쪽으로 틈만큼 떨어진 곳.</summary>
        public Rectangle FloatBody()
        {
            int c = (int)Math.Round(BarFloat * Scale), gap = (int)Math.Round(Gap * Scale);
            return Edge switch
            {
                ScreenEdge.Left => new(Final.Right + gap, Final.Top, c, c),
                ScreenEdge.Right => new(Final.Left - gap - c, Final.Top, c, c),
                ScreenEdge.Top => new(Final.Left, Final.Bottom + gap, c, c),
                _ => new(Final.Left, Final.Top - gap - c, c, c),
            };
        }

        public void RemoveRequested(string group) => Ui.Post(() => RemoveFromBar(group));

        public void RemoveFromBar(string group)
        {
            Remove(group);
            if (Selected == group) Selected = null;
        }

        
        public void AddRequested() => Ui.Post(ShowAddMenu);

        public void GuidesChanged(bool on) => Ui.Post(() => Mgr.ShowGuides = on);

        private Rectangle ToRect(PixelRect r) => new(r.Left, r.Top, r.Width, r.Height);

        public void RenameCard(string group, string name) => Ui.Post(() =>
        {
            if (Mgr.GroupCard(group) is { } card) Mgr.RenameGroup(card, name);
        });

        public void GridRequested(string group, PixelRect anchor) => Ui.Post(() =>
        {
            if (Mgr.GroupCard(group) is { } card) BarPopup.ShowGrid(Mgr, card, ToRect(anchor), Edge == ScreenEdge.Right);
        });

        private Windows.Foundation.Point CenterIn(int w, int h)
        {
            double x = (Final.Left + Final.Right - w) / 2.0, y = (Final.Top + Final.Bottom - h) / 2.0;
            x = Math.Clamp(x, WorkArea.Left, Math.Max(WorkArea.Left, WorkArea.Right - w));
            y = Math.Clamp(y, WorkArea.Top, Math.Max(WorkArea.Top, WorkArea.Bottom - h));
            return new Windows.Foundation.Point(x, y);
        }

        // ----- 카드 넣기·빼기 -----

        private double AspectOf(CardWindow card)
        {
            var l = Mgr.GetLayout(card.Group.Name);
            return (l.Rows * Mgr.CellSize + 28) / Math.Max(1, l.Cols * Mgr.CellSize);
        }

        /// <summary>카드를 바 안 빈자리(가장자리 쪽부터 차례로)에 넣는다.</summary>
        private void Add(CardWindow card)
        {
            if (Mgr.BarItems.Any(i => string.Equals(i.Group, card.Group.Name, StringComparison.OrdinalIgnoreCase))) return;
            double aspect = AspectOf(card);
            double w = 0.8 / Math.Max(1, aspect); // 바 두께의 80%에 맞춘 크기(가로·세로 중 긴 쪽 기준)
            var item = new BarItem { Group = card.Group.Name, W = w };
            (item.X, item.Y) = FreeSpot(w, aspect);
            Mgr.SetBarItems(Mgr.BarItems.Append(item));
        }

        private void Remove(string group)
        {
            Mgr.SetBarItems(Mgr.BarItems.Where(i => !string.Equals(i.Group, group, StringComparison.OrdinalIgnoreCase)));
            // 카드 바에만 있던 그룹은 없어지지 않게 바탕화면으로 꺼낸다.
            if (Mgr.IsBarOnly(group) && Mgr.GroupCard(group) is { } card) Mgr.ShowOnDesktop(card);
        }

        /// <summary>바 길이 방향으로 훑으며 다른 카드와 간격을 두고 들어갈 첫 자리(바 두께 단위).</summary>
        private (double X, double Y) FreeSpot(double w, double aspect)
        {
            double u = U, gap = SmartGuides.GapDip / u;
            double cross = Side ? w : w * aspect;     // 두께 방향 크기
            double along = Side ? w * aspect : w;     // 길이 방향 크기
            double c0 = Math.Max(0, (1 - cross) / 2); // 두께 방향 가운데
            double length = (Side ? Inner.H : Inner.W) / u;
            var others = Placed().Select(x => (x.Item, Aspect: AspectOf(x.Card))).ToList();
            for (double a = 0; a + along <= length + 1e-6; a += 0.05)
            {
                var (rx, ry) = Side ? (c0, a) : (a, c0);
                bool free = others.All(o =>
                {
                    double ow = o.Item.W, oh = o.Item.W * o.Aspect;
                    return rx + w + gap <= o.Item.X || o.Item.X + ow + gap <= rx || ry + w * aspect + gap <= o.Item.Y || o.Item.Y + oh + gap <= ry;
                });
                if (free) return (rx, ry);
            }
            return Side ? (c0, 0) : (0, c0);
        }
    }
    private static (string Device, ScreenEdge Edge)? _lastPlace;

    /// <summary>
    /// 이 카드가 카드 바 안 카드면 바 편집을 시작한다(설정의 "편집" 버튼용).
    /// 설정 창으로 마우스가 옮겨 가면 바는 이미 들어가 있으므로, 그때는 마지막으로 열렸던 자리에 다시 열고 편집을 시작한다.
    /// </summary>
    public static bool TryBeginEdit(CardWindow card)
    {
        if (_mgr is not { IsShuttingDown: false } mgr || !mgr.BarEnabled) return false;
        if (mgr.BarItems.All(i => !string.Equals(i.Group, card.Group.Name, StringComparison.OrdinalIgnoreCase))) return false;
        return OpenForEdit();
    }

    /// <summary>카드 바를 열고(이미 열려 있으면 그대로) 바 편집을 시작한다(설정의 "카드 바 편집" 버튼용).</summary>
    public static bool OpenForEdit()
    {
        if (_mgr is not { IsShuttingDown: false } mgr || !mgr.BarEnabled) return false;
        if (_bar is { Gone: false, Closing: false } open) { open.Begin(); return true; }
        if (PlaceForEdit(mgr) is not { } place) return false;
        if (_bar is { Gone: false } closing) { closing.Gone = true; closing.End(); closing.View.CloseNow(); }
        Reset();
        _armed = false;
        OpenBarOn(place.Screen, place.Edge);
        _bar!.Begin();
        SyncPolling();
        return true;
    }

    /// <summary>바를 다시 열 자리: 마지막으로 열렸던 디스플레이·가장자리, 없으면 바를 여는 첫 디스플레이.</summary>
    private static (Screens.Monitor Screen, ScreenEdge Edge)? PlaceForEdit(GroupManager mgr)
    {
        var screens = Screens.All();
        if (_lastPlace is { } last && screens.FirstOrDefault(s => s.DeviceName == last.Device) is { } s0 && mgr.BarEdgeFor(s0.DeviceName) == last.Edge)
            return (s0, last.Edge);
        foreach (var s in screens.OrderByDescending(s => s.DeviceName == Screens.Primary.DeviceName))
            if (mgr.BarEdgeFor(s.DeviceName) is { } edge && edge != Native.TaskbarEdgeOn(s)) return (s, edge);
        return null;
    }

    public static void Start(GroupManager mgr)
    {
        _mgr = mgr;
        _hotkeyWindow = new MessageWindow(OnHotkeyMessage);
        ApplyHotkey();
        mgr.Changed += ApplyHotkey;
        _timer = Ui.Queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick += (_, _) => Tick();
        SyncPolling();
    }

    public static void Stop()
    {
        _timer?.Stop();
        if (_mgr != null) _mgr.Changed -= ApplyHotkey;
        Unregister();
        _hotkeyWindow?.Dispose();
        _hotkeyWindow = null;
        Gauge.Close();
        _bar?.View.CloseNow();
        _bar = null;
        _mgr = null;
        _timer = null;
    }

    // ----- 조합키 등록 -----

    /// <summary>설정(켜기·조합)에 맞게 단축키를 등록하거나 푼다. 바뀐 게 없으면 그대로 둔다.</summary>
    private static void ApplyHotkey()
    {
        SyncPolling();
        if (_mgr == null || _hotkeyWindow == null) return;
        string want = _mgr.BarEnabled && !_suspended ? string.Join(",", _mgr.BarKeys) : "";
        if (want == _registeredFor) return;
        Unregister();
        _registeredFor = want;
        if (want.Length == 0) { HotkeyRegistered = _mgr.BarEnabled ? HotkeyRegistered : null; return; }
        // 보조키만 있는 조합은 등록할 수 없고 그럴 필요도 없다(앱에 아무 일도 일으키지 않는다).
        if (!KeyCombo.NeedsHotkey(_mgr.BarKeys)) { HotkeyRegistered = true; _registeredFor = ""; return; }
        var (mods, key) = KeyCombo.ToHotkey(_mgr.BarKeys);
        const uint MOD_NOREPEAT = 0x4000;
        HotkeyRegistered = RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, mods | MOD_NOREPEAT, key);
    }

    private static void SyncPolling()
    {
        if (_timer == null) return;
        // 이미 열린 바는 닫힘 감지가 끝날 때까지 폴링한다. 비활성화 중 게이지는 즉시 치운다.
        if (_mgr is { IsShuttingDown: false } && (_mgr.BarEnabled || _bar is { Gone: false })) _timer.Start();
        else { Reset(); _armed = true; _timer.Stop(); }
        if (_mgr is { BarEnabled: false }) Reset();
    }

    private static void Unregister()
    {
        if (_hotkeyWindow != null && _registeredFor.Length > 0) UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        _registeredFor = "";
    }

    /// <summary>이 조합을 지금 단축키로 등록할 수 있는지(Windows나 다른 앱이 쓰고 있지 않은지) 잠깐 등록해 보고 푼다.</summary>
    public static bool IsHotkeyFree(IEnumerable<int> keys)
    {
        if (_hotkeyWindow == null) return true;
        const int ProbeId = 2;
        var (mods, key) = KeyCombo.ToHotkey(keys);
        if (!RegisterHotKey(_hotkeyWindow.Handle, ProbeId, mods | 0x4000, key)) return false;
        UnregisterHotKey(_hotkeyWindow.Handle, ProbeId);
        return true;
    }

    /// <summary>설정에서 새 조합을 누르는 동안에는 지금 조합을 풀어 둔다(안 그러면 그 키가 설정 창에 오지 않는다).</summary>
    public static void SuspendHotkey(bool on)
    {
        _suspended = on;
        ApplyHotkey();
    }

    private static void OnHotkeyMessage(int msg, IntPtr wParam)
    {
        // 조합을 누른 순간 빈 키를 한 번 눌러, 나중에 Alt·Win을 뗄 때 앱 메뉴·시작 메뉴가 열리지 않게 한다.
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId && _mgr != null) KeyCombo.SuppressRelease(_mgr.BarKeys);
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // ----- 가장자리 지켜보기 -----

    private static void Tick()
    {
        if (_mgr == null || _mgr.IsShuttingDown) return;
        if (_bar != null)
        {
            if (_bar.Gone) { _bar = null; SyncPolling(); }
            else { CheckLeave(_bar); return; }
        }

        if (!Native.GetCursorPos(out var pt) || !AtEdge(pt, out var screen, out var edge))
        {
            Reset();
            _armed = true; // 한 번 조건에서 벗어나야 다시 열 수 있다(닫히자마자 또 열리지 않게).
            return;
        }
        if (!_armed) return;

        if (!_dwell.IsRunning) _dwell.Restart();
        double progress = _mgr.BarDelay <= 0 ? 1 : _dwell.ElapsedMilliseconds / (double)_mgr.BarDelay;
        if (progress >= 1)
        {
            Reset();
            _armed = false;
            // 보조키만 있는 조합은 WM_HOTKEY가 오지 않으니, 여기서 막아야 Alt·Win을 뗄 때 메뉴가 열리지 않는다.
            KeyCombo.SuppressRelease(_mgr.BarKeys);
            OpenBarOn(screen, edge);
            return;
        }
        ShowGauge(pt, progress);
        _timer!.Interval = TimeSpan.FromMilliseconds(15); // 게이지가 도는 동안은 부드럽게
    }

    private static void Reset()
    {
        _dwell.Reset();
        if (_gaugeShown) { _gaugeShown = false; Gauge.Hide(); }
        if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(40);
    }

    private static void ShowGauge(Native.POINT pt, double progress)
    {
        double s = Native.MonitorScaleAt(pt.X, pt.Y);
        int px = (int)Math.Round(44 * s);
        var b = Screens.FromPoint(pt.X, pt.Y).Bounds;
        int x = Math.Clamp(pt.X - px / 2, b.Left, b.Right - px);
        int y = Math.Clamp(pt.Y - px / 2, b.Top, b.Bottom - px);
        _gaugeShown = true;
        Gauge.Update(x, y, progress, ExpandedWindow.Hex("Accent"));
    }

    // ----- 바 열기·닫기 -----

    private static void OpenBarOn(Screens.Monitor screen, ScreenEdge edge)
    {
        var mgr = _mgr!;
        double scale = ScaleOf(screen);
        var bar = new OpenBar { Mgr = mgr, Edge = edge, Scale = scale, Bounds = screen.Bounds, WorkArea = screen.WorkArea, Final = FinalRect(screen, edge, mgr.BarSize) };
        bar.Attach();
        bar.View = BarView.Open(bar, new BarOptions(ExpandedWindow.CurrentPalette(), (int)edge, ToPx(bar.Final), ToPx(bar.Bounds), scale, ToPx(bar.FloatBody())), bar.Content());
        _bar = bar;
        _lastPlace = (screen.DeviceName, edge);
    }

    private static PixelRect ToPx(Rectangle r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private static double ScaleOf(Screens.Monitor screen)
    {
        var wa = screen.WorkArea;
        return Native.MonitorScaleAt(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
    }

    /// <summary>다 나왔을 때 바의 자리(물리 픽셀): 작업 영역 안, 가장자리에서 틈(8)만큼 떨어져, 두께는 작업 영역의 sizePercent%.</summary>
    public static Rectangle FinalRect(Screens.Monitor screen, ScreenEdge edge, int sizePercent)
    {
        var wa = screen.WorkArea;
        int gap = (int)Math.Round(Gap * ScaleOf(screen));
        bool side = edge is ScreenEdge.Left or ScreenEdge.Right;
        int thick = (int)Math.Round((side ? wa.Width : wa.Height) * sizePercent / 100.0);
        return edge switch
        {
            ScreenEdge.Left => new(wa.Left + gap, wa.Top + gap, thick, wa.Height - 2 * gap),
            ScreenEdge.Right => new(wa.Right - gap - thick, wa.Top + gap, thick, wa.Height - 2 * gap),
            ScreenEdge.Top => new(wa.Left + gap, wa.Top + gap, wa.Width - 2 * gap, thick),
            _ => new(wa.Left + gap, wa.Bottom - gap - thick, wa.Width - 2 * gap, thick),
        };
    }

    /// <summary>
    /// 마우스가 바(와 가장자리 사이 틈) 밖으로 나가면 들어간다.
    /// 펼친 창·우클릭 메뉴가 떠 있거나 항목을 끌어내는 중에는 기다린다.
    /// </summary>
    /// <summary>왼쪽 버튼을 누르고 있는지(항목을 바 밖으로 끌어내는 동안 바가 닫히지 않게).</summary>
    private static bool LeftButtonDown() => (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;

    private static void CheckLeave(OpenBar bar)
    {
        if (bar.Closing || bar.Editing || LeftButtonDown() || FluentMenu.IsOpen || ExpandedWindow.IsOpen) return;
        if (!Native.GetCursorPos(out var pt)) return;
        var r = bar.Final;
        var b = bar.Bounds;
        int l = r.Left, t = r.Top, rr = r.Right, bb = r.Bottom;
        switch (bar.Edge)
        {
            case ScreenEdge.Left: l = b.Left; break;
            case ScreenEdge.Right: rr = b.Right; break;
            case ScreenEdge.Top: t = b.Top; break;
            default: bb = b.Bottom; break;
        }
        if (pt.X >= l && pt.X < rr && pt.Y >= t && pt.Y < bb) return;
        // 바 옆 설정 버튼 근처로 가는 중에는 바를 접지 않는다.
        // (메뉴가 늘어난 만큼 아래로, 아래 바는 위로 칸 두 개 더)
        var fb = bar.FloatBody();
        int m = (int)Math.Round(40 * bar.Scale), ext = fb.Height * 2;
        int top = bar.Edge == ScreenEdge.Bottom ? fb.Top - ext : fb.Top, bottom = bar.Edge == ScreenEdge.Bottom ? fb.Bottom : fb.Bottom + ext;
        if (pt.X >= fb.Left - m && pt.X < fb.Right + m && pt.Y >= top - m && pt.Y < bottom + m) return;
        bar.Closing = true;
        bar.View.BeginClose();
    }

    /// <summary>바를 열 조건(설정 켜짐, 조합키, 정한 가장자리, 작업 표시줄 쪽 아님, 전체 화면 아님, 버튼 안 누름)이 맞는지.</summary>
    private static bool AtEdge(Native.POINT pt, out Screens.Monitor screen, out ScreenEdge edge)
    {
        screen = Screens.FromPoint(pt.X, pt.Y);
        var mgr = _mgr!;
        edge = default;
        // 디스플레이마다 여는 가장자리가 다르다(열지 않는 디스플레이도 있다).
        if (mgr.BarEdgeFor(screen.DeviceName) is not { } chosen) return false;
        edge = chosen;
        if (!mgr.BarEnabled || _suspended || mgr.Editing || !KeyCombo.IsDown(mgr.BarKeys)) return false;
        if (Native.GetAsyncKeyState(0x01) < 0 || Native.GetAsyncKeyState(0x02) < 0) return false; // 마우스 왼쪽·오른쪽 버튼
        if (edge == Native.TaskbarEdgeOn(screen) || ExpandedWindow.IsOpen || FluentMenu.IsOpen) return false;
        return ZoneRect(screen, edge, mgr.BarZone).Contains(pt.X, pt.Y) && !Native.IsFullScreenBusy();
    }

    /// <summary>
    /// 마우스를 대면 게이지가 도는 띠(물리 픽셀). 가장자리에 딱 붙은 1픽셀부터, 인식 영역(zone, 0.1% 단위)만큼 안쪽까지.
    /// 가장자리를 따라서는 작업 영역 안(작업 표시줄 옆은 빼고)만.
    /// </summary>
    public static Rectangle ZoneRect(Screens.Monitor screen, ScreenEdge edge, int zone)
    {
        var b = screen.Bounds;
        var wa = screen.WorkArea;
        bool side = edge is ScreenEdge.Left or ScreenEdge.Right;
        int depth = 1 + (int)Math.Round((side ? b.Width : b.Height) * zone / 1000.0);
        return edge switch
        {
            ScreenEdge.Left => new(b.Left, wa.Top, depth, wa.Height),
            ScreenEdge.Right => new(b.Right - depth, wa.Top, depth, wa.Height),
            ScreenEdge.Top => new(wa.Left, b.Top, wa.Width, depth),
            _ => new(wa.Left, b.Bottom - depth, wa.Width, depth),
        };
    }

    /// <summary>WM_HOTKEY만 받는 보이지 않는 메시지 전용 창.</summary>
    private sealed class MessageWindow : IDisposable
    {
        private delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        private readonly WndProc _proc;
        private readonly Action<int, IntPtr> _onMessage;
        public IntPtr Handle { get; }

        public MessageWindow(Action<int, IntPtr> onMessage)
        {
            _onMessage = onMessage;
            _proc = Proc;
            var cls = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), lpszClassName = "DeskCards.Hotkey", hInstance = GetModuleHandle(null) };
            RegisterClass(ref cls);
            Handle = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        }

        private IntPtr Proc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            _onMessage(msg, wParam);
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        public void Dispose() { if (Handle != IntPtr.Zero) DestroyWindow(Handle); }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS cls);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    }
}
