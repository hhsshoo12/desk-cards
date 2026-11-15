using System;
using System.Collections.Generic;
using System.Linq;
using DeskCards.Expanded;
using Windows.Foundation;

namespace DeskCards;

/// <summary>
/// 바탕화면에 붙어 있는 폴더(그룹) 카드 하나의 앱 쪽 주인. 창은 웹 카드와 같은 틀(DeskCards.Expanded의 DesktopCardWindow)이 그리고,
/// 이 클래스는 크기·위치·편집·저장 같은 앱의 규칙만 맡는다. 창 스레드와 부딪히지 않게 카드 자리는 여기서 따로 알고 있다.
///
///   CardWindow.Geometry.cs 크기·배율·위치
///   CardWindow.Editing.cs  편집 상태·끌기 마무리·안내
/// </summary>
internal sealed partial class CardWindow : IFolderCardHost
{
    public const double LabelH = 28;

    private readonly IFolderCardHandle _h;
    private PixelRect _rect; // 카드 내용 영역(물리 픽셀). 이름 줄은 아래에 따로 붙는다.
    private bool _closed, _visible, _pushQueued;
    private List<ShellEntry> _watched = new();

    public CardWindow(GroupModel group, GroupManager mgr)
    {
        Mgr = mgr;
        Group = group;
        _layout = mgr.GetLayout(Key);
        var pw = Screens.Primary.WorkArea;
        var (w, h) = SizeFor(Native.PrimaryScale(), pw.Width, pw.Height);
        _rect = new PixelRect(0, 0, w, h);
        _h = DesktopCards.OpenFolder(new FolderCardOptions(group.Name, 0, 0, w, h, ExpandedWindow.CurrentPalette(), Data()), this);
        Theme.Changed += OnThemeChanged;
        Group.Changed += OnGroupChanged;
        Watch();
        ApplySize();
    }

    public GroupManager Mgr { get; }
    public GroupModel Group { get; }

    /// <summary>창 핸들(창 스레드가 만든 것). 펼친 창·편집 막대가 위치를 잡을 때 쓴다.</summary>
    public IntPtr Handle => _h.Hwnd;

    /// <summary>위치·모양을 저장하는 이름(config.json의 PositionsPx·Layouts 키).</summary>
    public string Key => Group.Name;

    /// <summary>편집 막대 등에 보여 주는 카드 이름.</summary>
    public string CardName => Group.Name;

    /// <summary>앱이 카드를 닫는다. 쓰는 쪽이 ClosingByManager를 먼저 켜 둔다.</summary>
    public bool ClosingByManager { get; set; }

    /// <summary>카드 창이 닫혔을 때(앱이 직접 닫는 경우 포함). 쓰는 쪽은 ClosingByManager로 구분한다.</summary>
    public event EventHandler? CardClosed;

    public bool IsVisible => _visible;

    /// <summary>활성화하지 않고 바탕화면 층에 보인다.</summary>
    public void Show()
    {
        if (_closed) return;
        _visible = true;
        _h.SetVisible(true);
    }

    public void Close()
    {
        if (_closed) return;
        _h.Close();
    }

    /// <summary>바탕화면 층으로 다시 내린다.</summary>
    public void RestackAtBottom()
    {
        if (!_closed && !_editing && _visible) _h.Restack();
    }

    // ----- 내용 -----

    private FolderCardData Data() => new(Group.Name, Group.Name, Group.Folder, _layout.Cols, _layout.Rows, _appliedScale,
        Mgr.HoverExpand, Mgr.HoverExpandDelay,
        Group.Items.Select(e => new TileData(e.Path, e.Name, e.IconPixels?.Width ?? 0, e.IconPixels?.Height ?? 0, e.IconPixels?.Pixels)).ToList());

    private void PushData() { if (!_closed) _h.SetFolder(Data()); }

    /// <summary>항목이 바뀌거나 아이콘이 도착하면 한꺼번에 모아 한 번만 보낸다.</summary>
    private void OnGroupChanged()
    {
        if (_closed || _pushQueued) return;
        _pushQueued = true;
        Ui.Post(() =>
        {
            _pushQueued = false;
            if (_closed) return;
            Watch();
            PushData();
        });
    }

    private void Watch()
    {
        foreach (var e in _watched) e.PropertyChanged -= OnEntryChanged;
        _watched = Group.Items.ToList();
        foreach (var e in _watched) e.PropertyChanged += OnEntryChanged;
    }

    private void OnEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => OnGroupChanged();

    private void OnThemeChanged() => Ui.Post(() =>
    {
        if (!_closed) _h.SetPalette(ExpandedWindow.CurrentPalette());
    }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

    private void OnClosed()
    {
        _closed = true;
        Theme.Changed -= OnThemeChanged;
        Group.Changed -= OnGroupChanged;
        foreach (var e in _watched) e.PropertyChanged -= OnEntryChanged;
        _watched.Clear();
        CardClosed?.Invoke(this, EventArgs.Empty);
    }

    // ----- IFolderCardHost: WPF 창 스레드에서 불린다. 앱 일은 모두 UI 스레드로 넘긴다 -----

    public void Launch(string path) => Ui.Post(() => FileOps.Launch(path));

    public void ItemMenu(string path) => Ui.Post(() =>
    {
        var entry = Group.Items.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (entry != null) Menus.ForEntry(entry).ShowAtCursor();
    });

    public void CardMenu(string group) => Menu();

    public void Menu() => Ui.Post(() => { if (!_closed) Menus.ForCard(this, Mgr).ShowAtCursor(); });

    public void Expand(string group, bool hover, PixelRect cardRect) => Ui.Post(() =>
    {
        if (_closed || _editing) return;
        if (hover && ExpandedWindow.IsOpenFor(this)) return;
        ExpandedWindow.Open(this, Mgr, hover: hover);
    });

    public void AddToGroup(IReadOnlyList<string> paths) => Ui.Post(() => FileOps.AddToGroup(paths, Group.Folder, Mgr.Root));

    public void Selected() => Ui.Post(() => { if (!_closed) Mgr.Select(this); });

    public void Activated() => Ui.Post(() => Mgr.RestoreDesktopOrder());

    public void Closed() => Ui.Post(OnClosed);

    public void DpiChanged(int x, int y, int width, int height) => Ui.Post(() =>
    {
        if (_closed) return;
        _rect = new PixelRect(x, y, x + width, y + height);
        Refit();
    }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
}