using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskCards;

/// <summary>
/// 통합 설정 창(Windows 11 설정 앱 모양). 왼쪽 메뉴: 카드 / 카드 바 / 일반 / 정보.
/// 카드 페이지에서 편집을 시작하면 이 창은 잠시 숨고 화면 위쪽에 편집 막대가 뜬다.
/// </summary>
internal partial class SettingsWindow : Window
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const string RepoUrl = "https://github.com/hhsshoo12/desk-cards";
    private const string DeveloperUrl = "https://github.com/hhsshoo12";

    private enum PageKind { Cards, Card, Bar, BarKeys, General, About, Libraries }

    private static readonly (string Glyph, string Text, PageKind Page)[] NavItems =
    {
        ("", "카드", PageKind.Cards),
        ("", "카드 바", PageKind.Bar),
        ("", "일반", PageKind.General),
        ("", "정보", PageKind.About),
    };

    private static SettingsWindow? _win;

    private readonly GroupManager _mgr;
    private PageKind _page = PageKind.Cards;
    private CardWindow? _card;      // 카드 상세 페이지의 대상
    private string? _cardName;      // 그 페이지를 만들 때의 이름(바뀌면 다시 그린다)
    private bool _hiddenForEdit, _buildQueued, _closed;
    private readonly List<Action> _refreshControls = new();
    private Action? _updateRefresh; // 정보 페이지의 업데이트 줄을 지금 상태로

    private string _cardsSignature = "";

    private string? _barDisplay; // 카드 바 페이지에서 고른 디스플레이(Screen.DeviceName)
    private KeyComboEditor? _keyEditor;

    private bool _first;

    private SettingsWindow(GroupManager mgr)
    {
        InitializeComponent();
        _mgr = mgr;
        AppIcon.Source = LoadIcon(16);
        BigIcon.Source = LoadIcon(256);

        _mgr.Changed += OnChanged;
        _mgr.EditChanged += OnEditChanged;
        if (Updater.Instance is { } updater) updater.Changed += OnUpdateChanged;
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        // 항목 수 등이 바뀌었을 수 있으니 돌아올 때 목록을 확인한다. 누르는 중인 버튼이 바뀌지 않도록 달라졌을 때만 다시 그린다.
        Activated += (_, _) => { if (_page == PageKind.Cards && CardsSignature() != _cardsSignature) ScheduleBuild(); };
        PreviewKeyDown += (_, e) =>
        {
            if (_keyEditor is { IsRecording: true })
            {
                _keyEditor.HandleKey(e);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && _page is PageKind.Card or PageKind.Libraries or PageKind.BarKeys && Keyboard.FocusedElement is not TextBox)
            {
                Go(_page switch { PageKind.Card => PageKind.Cards, PageKind.BarKeys => PageKind.Bar, _ => PageKind.About });
                e.Handled = true;
            }
        };
        Deactivated += (_, _) => _keyEditor?.Cancel();
        Build();
    }

    /// <summary>설정 창을 띄운다. 카드를 주면 그 카드의 설정 페이지로 연다.</summary>
    public static void Open(GroupManager mgr, CardWindow? card = null)
    {
        _win ??= new SettingsWindow(mgr);
        if (card != null) _win.ShowCard(card);
        _win._hiddenForEdit = false;
        _win.Topmost = mgr.Editing; // 편집 막대에서 열면 어두운 막 위에 뜬다.
        if (!_win.IsVisible) _win.Show();
        if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
        _win.Activate();
    }

    private void ShowCard(CardWindow card)
    {
        _card = card;
        Go(PageKind.Card);
    }

    private void Go(PageKind page)
    {
        _page = page;
        PageScroller.ScrollToVerticalOffset(0);
        Build();
    }

    private void OnChanged()
    {
        // 상세 페이지는 입력 중일 수 있으니 카드가 없어졌거나 이름이 바뀐 경우만 다시 그린다.
        if (_page == PageKind.Card && _card != null && _mgr.Cards.Contains(_card) && _card.Group.Name == _cardName)
        {
            UpdateSummary();
            foreach (var refresh in _refreshControls) refresh();
            return;
        }
        ScheduleBuild();
    }

    /// <summary>편집을 시작할 때 떠 있던 설정 창은 숨겼다가, 편집이 끝나면 되살린다.</summary>
    public static void HideForEdit()
    {
        if (_win == null || !_win.IsVisible) return;
        _win._hiddenForEdit = true;
        _win.Hide();
    }

    private void OnEditChanged()
    {
        if (!_mgr.Editing) Topmost = false;
        if (_mgr.Editing || _mgr.IsShuttingDown || !_hiddenForEdit) return;
        _hiddenForEdit = false;
        Show();
        Activate();
        ScheduleBuild();
    }

    /// <summary>클릭 처리 도중에 화면을 갈아엎지 않도록 한 박자 늦게, 여러 번 와도 한 번만 다시 그린다.</summary>
    private void ScheduleBuild()
    {
        if (_buildQueued || _closed) return;
        _buildQueued = true;
        Dispatcher.BeginInvoke(() => { _buildQueued = false; if (!_closed) Build(); });
    }

    private void OnUpdateChanged() => _updateRefresh?.Invoke();

    private void UpdateSummary() => Summary.Text = $"카드 {_mgr.Cards.Count}개";

    private void Build()
    {
        if (_page == PageKind.Card && (_card == null || !_mgr.Cards.Contains(_card))) _page = PageKind.Cards;
        double offset = PageScroller.VerticalOffset;
        UpdateSummary();
        _refreshControls.Clear();
        _updateRefresh = null;
        _keyEditor?.Cancel();
        _keyEditor = null;
        BuildNav();
        Page.Children.Clear();
        Breadcrumb.Children.Clear();
        _first = true;
        switch (_page)
        {
            case PageKind.Cards: BuildCards(); break;
            case PageKind.Card: BuildCard(_card!); break;
            case PageKind.Bar: BuildBar(); break;
            case PageKind.BarKeys: BuildBarKeys(); break;
            case PageKind.General: BuildGeneral(); break;
            case PageKind.Libraries: BuildLibraries(); break;
            default: BuildAbout(); break;
        }
        PageScroller.ScrollToVerticalOffset(offset);
    }

    // ----- 왼쪽 메뉴 -----

    private void BuildNav()
    {
        Nav.Children.Clear();
        var current = _page switch { PageKind.Card => PageKind.Cards, PageKind.Libraries => PageKind.About, PageKind.BarKeys => PageKind.Bar, _ => _page };
        foreach (var (glyph, text, page) in NavItems)
        {
            bool on = page == current;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var pill = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = on ? Visibility.Visible : Visibility.Hidden,
            };
            pill.SetResourceReference(Border.BackgroundProperty, "Accent");
            var icon = Glyph(glyph, 16);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            var label = new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            Grid.SetColumn(label, 1);
            grid.Children.Add(pill);
            grid.Children.Add(icon);
            grid.Children.Add(label);

            var item = new Border
            {
                Height = 36,
                Margin = new Thickness(0, 2, 0, 2),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Child = grid,
            };
            if (on) item.SetResourceReference(Border.BackgroundProperty, "HoverBg");
            else
            {
                item.MouseEnter += (_, _) => item.SetResourceReference(Border.BackgroundProperty, "HoverBg");
                item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            }
            var target = page;
            item.MouseLeftButtonUp += (_, _) => { if (_page != target) Go(target); };
            Nav.Children.Add(item);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Mica는 22H2부터. 그 전에는 Theme의 SettingsBg가 불투명한 바탕을 칠한다.
        Hwnd.ApplyFluent(this, Theme.HasBackdrop ? Hwnd.Backdrop.Mica : Hwnd.Backdrop.None, roundCorners: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _mgr.Changed -= OnChanged;
        _mgr.EditChanged -= OnEditChanged;
        if (Updater.Instance is { } updater) updater.Changed -= OnUpdateChanged;
        if (_win == this) _win = null;
        base.OnClosed(e);
    }
}
