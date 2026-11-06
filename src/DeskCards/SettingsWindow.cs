using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace DeskCards;

/// <summary>설정 창의 페이지. 왼쪽 메뉴에 있는 것이 맨 위(카드·일반·정보)이고, 나머지는 그 아래 하위 페이지다.</summary>
internal enum SettingsPageKind { Cards, FolderCards, Card, General, About, DevInfo, Libraries }

/// <summary>페이지가 만들어질 때 받는 값.</summary>
internal sealed record SettingsPageArgs(SettingsWindow Host, GroupManager Mgr, CardWindow? Card);

/// <summary>
/// 통합 설정 창(Windows 11 설정 앱 모양). 왼쪽 메뉴(NavigationView)로 카드 / 일반 / 정보를 고르고,
/// 오른쪽 Frame에 페이지를 띄운다. 하위 페이지(›)는 오른쪽에서, 위로 가면 왼쪽에서, 메뉴를 바꾸면 아래에서 들어온다.
/// 카드 페이지에서 편집을 시작하면 이 창은 잠시 숨고 화면 위쪽에 편집 막대가 뜬다.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly (string Glyph, string Text, SettingsPageKind Page)[] NavItems =
    {
        ("", "카드", SettingsPageKind.Cards),
        ("", "일반", SettingsPageKind.General),
        ("", "정보", SettingsPageKind.About),
    };

    private static readonly Dictionary<SettingsPageKind, Func<SettingsPageArgs, SettingsPage>> PageFactories = new()
    {
        [SettingsPageKind.Cards] = a => new CardsPage(a),
        [SettingsPageKind.FolderCards] = a => new FolderCardsPage(a),
        [SettingsPageKind.Card] = a => new CardPage(a),
        [SettingsPageKind.General] = a => new GeneralPage(a),
        [SettingsPageKind.About] = a => new AboutPage(a),
        [SettingsPageKind.DevInfo] = a => new DevInfoPage(a),
        [SettingsPageKind.Libraries] = a => new LibrariesPage(a),
    };

    private static SettingsWindow? _win;

    private readonly GroupManager _mgr;
    private readonly NavigationView _nav;
    private readonly TitleBar _titleBar;
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly Stack<(SettingsPageKind Page, CardWindow? Card)> _history = new();
    private SettingsPageKind _page = SettingsPageKind.Cards;
    private CardWindow? _card;
    private bool _hiddenForEdit, _syncingNav, _closed;

    private SettingsWindow(GroupManager mgr)
    {
        _mgr = mgr;
        Title = "Desk Cards 설정";
        AppWindow.IsShownInSwitchers = true;
        Hwnd.ApplyFluent(this, Theme.HasBackdrop ? Hwnd.Backdrop.Mica : Hwnd.Backdrop.None, roundCorners: false);

        _nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
            IsSettingsVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsPaneToggleButtonVisible = false,
            OpenPaneLength = 320,
            CompactModeThresholdWidth = 1007,
            ExpandedModeThresholdWidth = 1007,
            Content = _host,
        };
        // Windows 설정처럼 본문을 별도 판(둥근 테두리)으로 두지 않고 Mica 배경 위에 그대로 올린다.
        _nav.Resources["NavigationViewContentBackground"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _nav.Resources["NavigationViewContentGridBorderBrush"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var (glyph, text, page) in NavItems)
            _nav.MenuItems.Add(new NavigationViewItem { Content = text, Icon = new FontIcon { Glyph = glyph }, Tag = page });
        _nav.SelectionChanged += (_, e) =>
        {
            if (_syncingNav || e.SelectedItemContainer?.Tag is not SettingsPageKind page || page == TopOf(_page)) return;
            Go(page);
        };
        // 제목 줄: 뒤로 가기는 앱 이름 바로 왼쪽에, 메뉴 펼치기·접기 버튼은 창이 좁아 메뉴가 접혔을 때만 둔다.
        _titleBar = new TitleBar { Title = "Desk Cards 설정", IsBackButtonVisible = true, IsBackButtonEnabled = false, IsPaneToggleButtonVisible = false };
        BigTitleBar(_titleBar);
        _titleBar.BackRequested += (_, _) => GoBack();
        _titleBar.PaneToggleRequested += (_, _) => _nav.IsPaneOpen = !_nav.IsPaneOpen;
        _nav.DisplayModeChanged += (_, _) => _titleBar.IsPaneToggleButtonVisible = _nav.DisplayMode != NavigationViewDisplayMode.Expanded;
        _nav.Loaded += (_, _) => _titleBar.IsPaneToggleButtonVisible = _nav.DisplayMode != NavigationViewDisplayMode.Expanded;
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(_nav, 1);
        layout.Children.Add(_titleBar);
        layout.Children.Add(_nav);
        layout.RequestedTheme = Theme.Element;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(_titleBar);
        Content = layout;

        var area = Screens.Primary.WorkArea;
        double scale = Native.MonitorScaleAt(area.Left + area.Width / 2, area.Top + area.Height / 2);
        int w = (int)Math.Min(area.Width * 0.9, 1000 * scale), h = (int)Math.Min(area.Height * 0.9, 720 * scale);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h));

        _mgr.EditChanged += OnEditChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _mgr.EditChanged -= OnEditChanged;
            if (_win == this) _win = null;
        };
        Navigate(SettingsPageKind.Cards, null, new EntranceThemeTransition { FromVerticalOffset = 32 });
    }

    /// <summary>
    /// Windows 설정 앱과 같은 제목 줄: 높이 48(컨트롤 기본은 32), 뒤로 가기 버튼 40, 제목 12는 컨트롤 기본값 그대로다.
    /// </summary>
    private static void BigTitleBar(TitleBar bar)
    {
        bar.Resources["TitleBarExpandedHeight"] = 48.0;
        bar.Resources["TitleBarCompactHeight"] = 48.0;
    }
    // ----- 열기·닫기 -----

    /// <summary>설정 창을 띄운다. 카드를 주면 그 카드의 설정 페이지로 연다.</summary>
    public static void Open(GroupManager mgr, CardWindow? card = null)
    {
        if (mgr.IsShuttingDown) return;
        bool fresh = _win == null;
        _win ??= new SettingsWindow(mgr);
        if (card != null) _win.GoTo(SettingsPageKind.Card, card);
        if (fresh) _win._history.Clear();
        _win.Reveal();
    }

    /// <summary>설정 창을 일반 페이지로 연다(붙이기 안내 말풍선에서).</summary>
    public static void OpenGeneral(GroupManager mgr)
    {
        Open(mgr);
        _win?.Go(SettingsPageKind.General);
    }

    /// <summary>편집을 시작할 때 떠 있던 설정 창은 숨겼다가, 편집이 끝나면 되살린다.</summary>
    public static void HideForEdit()
    {
        if (_win == null || !_win.AppWindow.IsVisible) return;
        _win._hiddenForEdit = true;
        _win.AppWindow.Hide();
    }

    private void Reveal()
    {
        _hiddenForEdit = false;
        if (!AppWindow.IsVisible) AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) p.Restore();
        Activate();
        // 편집 막대에서 열면 어두운 막 위에 뜬다.
        if (_mgr.Editing) Hwnd.SetZOrder(Hwnd.Of(this), Hwnd.Topmost);
    }

    private void OnEditChanged()
    {
        if (_mgr.Editing)
        {
            return;
        }
        Hwnd.SetZOrder(Hwnd.Of(this), new IntPtr(-2));
        if (_mgr.IsShuttingDown || !_hiddenForEdit) return;
        _hiddenForEdit = false;
        AppWindow.Show();
        Activate();
    }

    // ----- 이동 -----

    private static SettingsPageKind? ParentOf(SettingsPageKind page) => page switch
    {
        SettingsPageKind.FolderCards => SettingsPageKind.Cards,
        SettingsPageKind.Card => SettingsPageKind.FolderCards,
        SettingsPageKind.DevInfo => SettingsPageKind.About,
        SettingsPageKind.Libraries => SettingsPageKind.DevInfo,
        _ => null,
    };

    private static SettingsPageKind TopOf(SettingsPageKind page)
    {
        while (ParentOf(page) is { } up) page = up;
        return page;
    }

    private static bool IsAbove(SettingsPageKind above, SettingsPageKind page)
    {
        for (var p = ParentOf(page); p != null; p = ParentOf(p.Value))
            if (p == above) return true;
        return false;
    }

    /// <summary>페이지를 옮긴다(페이지가 부른다). 지금 페이지는 뒤로 가기 기록에 남긴다.</summary>
    public void Go(SettingsPageKind page) => GoTo(page, _card);

    public void GoTo(SettingsPageKind page, CardWindow? card)
    {
        if (page == _page && card == _card) return;
        _history.Push((_page, _card));
        Navigate(page, card, TransitionFor(_page, page));
    }

    private void GoBack()
    {
        while (_history.Count > 0)
        {
            var (page, card) = _history.Pop();
            if (page == SettingsPageKind.Card && (card == null || !_mgr.Cards.Contains(card))) continue;
            if (page == _page && card == _card) continue;
            Navigate(page, card, TransitionFor(_page, page));
            return;
        }
    }

    private static Transition TransitionFor(SettingsPageKind from, SettingsPageKind to) =>
        IsAbove(to, from) ? new EntranceThemeTransition { FromHorizontalOffset = -48, FromVerticalOffset = 0 }
        : IsAbove(from, to) ? new EntranceThemeTransition { FromHorizontalOffset = 48, FromVerticalOffset = 0 }
        : new EntranceThemeTransition { FromVerticalOffset = 32 };

    private void Navigate(SettingsPageKind page, CardWindow? card, Transition transition)
    {
        if (_closed) return;
        _page = page;
        _card = card;
        _host.ContentTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection { transition };
        _host.Content = PageFactories[page](new SettingsPageArgs(this, _mgr, card));
        _titleBar.IsBackButtonEnabled = _history.Count > 0;
        _syncingNav = true;
        _nav.SelectedItem = _nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (SettingsPageKind)i.Tag == TopOf(page));
        _syncingNav = false;
    }

    /// <summary>카드가 없어졌을 때처럼 지금 페이지를 그대로 둘 수 없을 때, 한 단계 위로 올린다.</summary>
    public void GoUp()
    {
        if (ParentOf(_page) is { } up) Go(up);
    }
}








