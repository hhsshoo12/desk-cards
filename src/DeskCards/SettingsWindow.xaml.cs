using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DeskCards;

/// <summary>
/// 통합 설정 창(Windows 11 설정 앱 모양). 왼쪽 메뉴: 카드 / 카드 바 / 일반 / 정보.
/// 카드 페이지에서 편집을 시작하면 이 창은 잠시 숨고 화면 위쪽에 편집 막대가 뜬다.
/// 페이지 전환은 설정 앱과 같다: 메뉴를 바꾸면 아래에서, 하위 페이지(›)는 오른쪽에서, 상위로 가면 왼쪽에서 들어온다.
/// </summary>
internal partial class SettingsWindow : Window
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const string RepoUrl = "https://github.com/hhsshoo12/desk-cards";
    private const string DeveloperUrl = "https://github.com/hhsshoo12";

    /// <summary>이 너비보다 좁으면 왼쪽 메뉴를 숨기고 제목 표시줄의 ☰ 버튼으로 펼친다.</summary>
    private const double CompactWidth = 900;
    private const double NavWidth = 300;
    /// <summary>메뉴 한 줄의 높이(36)와 위아래 여백(2+2).</summary>
    private const double NavSlot = 40;

    private enum PageKind { Cards, FolderCards, Card, WidgetCards, Widget, Bar, BarKeys, General, About, DevInfo, Libraries }

    private enum Slide { None, Up, FromRight, FromLeft }

    private static readonly (string Glyph, string Text, PageKind Page)[] NavItems =
    {
        ("", "카드", PageKind.Cards),
        ("", "카드 바", PageKind.Bar),
        ("", "일반", PageKind.General),
        ("", "정보", PageKind.About),
    };

    /// <summary>하위 페이지의 위 페이지. 맨 위(왼쪽 메뉴에 있는 것)는 null.</summary>
    private static PageKind? ParentOf(PageKind page) => page switch
    {
        PageKind.FolderCards or PageKind.WidgetCards => PageKind.Cards,
        PageKind.Card => PageKind.FolderCards,
        PageKind.Widget => PageKind.WidgetCards,
        PageKind.BarKeys => PageKind.Bar,
        PageKind.DevInfo => PageKind.About,
        PageKind.Libraries => PageKind.DevInfo,
        _ => null,
    };

    private static PageKind TopOf(PageKind page)
    {
        while (ParentOf(page) is { } up) page = up;
        return page;
    }

    private static bool IsAbove(PageKind above, PageKind page)
    {
        for (var p = ParentOf(page); p != null; p = ParentOf(p.Value))
            if (p == above) return true;
        return false;
    }

    private static SettingsWindow? _win;

    private readonly GroupManager _mgr;
    private PageKind _page = PageKind.Cards;
    private CardWindow? _card;      // 카드 상세 페이지의 대상
    private string? _cardName;      // 그 페이지를 만들 때의 이름(바뀌면 다시 그린다)
    private string? _dardPath;      // 위젯 상세 페이지의 대상(.dard 파일)
    private readonly Stack<(PageKind Page, CardWindow? Card, string? Dard)> _history = new();
    private bool _hiddenForEdit, _buildQueued, _closed;
    private readonly List<Action> _refreshControls = new();
    private Action? _updateRefresh; // 정보 페이지의 업데이트 줄을 지금 상태로
    private bool _quiet;            // 스위치를 누른 직후: 페이지를 다시 그리지 않고 값만 맞춘다(스위치가 미끄러지는 게 보이게)

    private string _cardsSignature = "";

    private string? _barDisplay; // 카드 바 페이지에서 고른 디스플레이(Screen.DeviceName)
    private KeyComboEditor? _keyEditor;

    private bool _first;

    // 왼쪽 메뉴
    private readonly List<(Border Item, PageKind Page)> _navItems = new();
    private int _navIndex = -1;
    private bool _compact, _paneOpen;

    // 선택 막대: 위아래 끝을 따로 움직여 늘어났다 줄어드는 것처럼 보이게 한다.
    private double _barTop, _barBottom, _barFromTop, _barFromBottom, _barToTop, _barToBottom;
    private readonly Stopwatch _barClock = new();
    private bool _barMoving;

    private SettingsWindow(GroupManager mgr)
    {
        InitializeComponent();
        _mgr = mgr;
        AppIcon.Source = LoadIcon(16);

        _mgr.Changed += OnChanged;
        _mgr.EditChanged += OnEditChanged;
        if (Updater.Instance is { } updater) updater.Changed += OnUpdateChanged;
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        SizeChanged += (_, _) => SetCompact(ActualWidth < CompactWidth);
        // 항목 수 등이 바뀌었을 수 있으니 돌아올 때 목록을 확인한다. 누르는 중인 버튼이 바뀌지 않도록 달라졌을 때만 다시 그린다.
        Activated += (_, _) => { if (ShowsCardList && CardsSignature() != _cardsSignature) ScheduleBuild(); };
        PreviewKeyDown += OnPreviewKeyDown;
        MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; } };
        Deactivated += (_, _) => _keyEditor?.Cancel();
        BuildNav();
        Build();
        SetCompact(Width < CompactWidth);
    }

    /// <summary>설정 창을 띄운다. 카드를 주면 그 카드의 설정 페이지로 연다.</summary>
    public static void Open(GroupManager mgr, CardWindow? card = null)
    {
        bool fresh = _win == null;
        _win ??= new SettingsWindow(mgr);
        if (card != null) _win.ShowCard(card);
        if (fresh) _win._history.Clear();
        _win.Reveal(mgr);
    }

    /// <summary>설정 창을 위젯 카드(.dard) 페이지로 연다.</summary>
    public static void OpenWidget(GroupManager mgr, string path)
    {
        bool fresh = _win == null;
        _win ??= new SettingsWindow(mgr);
        _win.GoTo(PageKind.Widget, _win._card, path);
        if (fresh) _win._history.Clear();
        _win.Reveal(mgr);
    }

    /// <summary>설정 창을 일반 페이지로 연다(붙이기 안내 말풍선에서).</summary>
    public static void OpenGeneral(GroupManager mgr)
    {
        Open(mgr);
        _win!.Go(PageKind.General);
        // 실험 설정은 페이지 맨 아래에 있다.
        _win.Dispatcher.BeginInvoke(() => _win?.PageScroller.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Reveal(GroupManager mgr)
    {
        _hiddenForEdit = false;
        Topmost = mgr.Editing; // 편집 막대에서 열면 어두운 막 위에 뜬다.
        if (!IsVisible)
        {
            Show();
            Play(Slide.Up); // 설정 앱처럼 처음 열 때 왼쪽 메뉴는 그대로, 내용만 아래에서 올라온다.
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowCard(CardWindow card) => GoTo(PageKind.Card, card, _dardPath);

    private void ShowWidget(string path) => GoTo(PageKind.Widget, _card, path);

    private void Go(PageKind page) => GoTo(page, _card, _dardPath);

    /// <summary>페이지를 옮긴다. 지금 페이지는 뒤로 가기 기록에 남긴다.</summary>
    private void GoTo(PageKind page, CardWindow? card, string? dard)
    {
        if (page == _page && card == _card && dard == _dardPath) return;
        _history.Push((_page, _card, _dardPath));
        Navigate(page, card, dard);
    }

    /// <summary>제목 표시줄의 ← · Alt+← · 마우스 뒤로 버튼. 없어진 카드의 페이지는 건너뛴다.</summary>
    private void GoBack()
    {
        while (_history.Count > 0)
        {
            var (page, card, dard) = _history.Pop();
            if (page == PageKind.Card && (card == null || !_mgr.Cards.Contains(card))) continue;
            if (page == _page && card == _card && dard == _dardPath) continue;
            Navigate(page, card, dard);
            return;
        }
        UpdateBackButton();
    }

    private void Navigate(PageKind page, CardWindow? card, string? dard)
    {
        var from = _page;
        var slide = IsAbove(page, from) ? Slide.FromLeft
            : IsAbove(from, page) ? Slide.FromRight
            : Slide.Up;
        _page = page;
        _card = card;
        _dardPath = dard;
        if (slide != Slide.Up) CaptureOldPage();
        PageScroller.ScrollToVerticalOffset(0);
        Build();
        Play(slide);
        if (_paneOpen) ClosePane();
    }

    private void UpdateBackButton() => BackButton.IsEnabled = _history.Count > 0;

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_keyEditor is { IsRecording: true })
        {
            _keyEditor.HandleKey(e);
            e.Handled = true;
            return;
        }
        bool typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.Escape && _paneOpen)
        {
            ClosePane();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !typing && ParentOf(_page) is { } up)
        {
            Go(up);
            e.Handled = true;
        }
        else if ((e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) || (e.Key == Key.Back && !typing))
        {
            GoBack();
            e.Handled = true;
        }
    }

    private void OnChanged()
    {
        // 스위치를 누른 결과로 온 알림이면 페이지를 그대로 두고 값만 맞춘다.
        if (_quiet)
        {
            foreach (var refresh in _refreshControls) refresh();
            return;
        }
        // 상세 페이지는 입력 중일 수 있으니 카드가 없어졌거나 이름이 바뀐 경우만 다시 그린다.
        // 위젯 페이지도 위젯 상태(켬·끔·버전·카드 수)가 그대로면 값만 맞춘다.
        if ((_page == PageKind.Card && _card != null && _mgr.Cards.Contains(_card) && _card.Group.Name == _cardName) ||
            (_page == PageKind.Widget && WidgetState(_dardPath) == _widgetState))
        {
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

    private bool ShowsCardList => _page is PageKind.Cards or PageKind.FolderCards or PageKind.WidgetCards or PageKind.Widget;

    private void Build()
    {
        if (_page == PageKind.Card && (_card == null || !_mgr.Cards.Contains(_card))) _page = PageKind.FolderCards;
        if (_page == PageKind.Widget && !_mgr.DardEntries().Any(d => SamePath(d.Path, _dardPath))) _page = PageKind.WidgetCards;
        double offset = PageScroller.VerticalOffset;
        _refreshControls.Clear();
        _updateRefresh = null;
        _keyEditor?.Cancel();
        _keyEditor = null;
        Page.Children.Clear();
        Breadcrumb.Children.Clear();
        _first = true;
        switch (_page)
        {
            case PageKind.Cards: BuildCards(); break;
            case PageKind.FolderCards: BuildFolderCards(); break;
            case PageKind.Card: BuildCard(_card!); break;
            case PageKind.WidgetCards: BuildWidgetCards(); break;
            case PageKind.Widget: _widgetState = WidgetState(_dardPath); BuildWidget(_dardPath!); break;
            case PageKind.Bar: BuildBar(); break;
            case PageKind.BarKeys: BuildBarKeys(); break;
            case PageKind.General: BuildGeneral(); break;
            case PageKind.DevInfo: BuildDevInfo(); break;
            case PageKind.Libraries: BuildLibraries(); break;
            default: BuildAbout(); break;
        }
        PageScroller.ScrollToVerticalOffset(offset);
        SelectNav();
        UpdateBackButton();
    }

    private string? _widgetState; // 위젯 페이지를 만들 때의 상태

    private string WidgetState(string? path) =>
        _mgr.DardEntries().FirstOrDefault(d => SamePath(d.Path, path)) is { } d
            ? $"{d.State}/{d.Runtime?.Package.Version}/{d.Runtime?.Windows.Count}"
            : "";

    private static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ----- 페이지 전환 -----

    /// <summary>들어오는 페이지가 출발하는 거리: 메뉴를 바꾸면 아래에서, 하위·상위로 가면 옆에서. 둘 다 250ms 감속.</summary>
    private const double PageRise = 32, PageSlide = 48;
    /// <summary>나가는 페이지가 밀려나는 거리. 167ms 동안 바로 움직이기 시작하며 흐려진다.</summary>
    private const double PageExit = 24;

    /// <summary>
    /// 옛 페이지를 그림으로 찍어 둔다. Build로 내용을 갈아엎기 전에 불러야, 하위·상위 이동 때 옛 페이지를 밀어낼 수 있다.
    /// </summary>
    private void CaptureOldPage()
    {
        OldPage.Source = null;
        if (!Motion.Enabled || !IsLoaded || PageHost.ActualWidth < 1 || PageHost.ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(PageHost.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(PageHost.ActualHeight * dpi.DpiScaleY);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // 지금 움직이는 중이면 그 자리 그대로 찍힌다(연달아 눌러도 끊기지 않게).
        bitmap.Render(PageHost);
        bitmap.Freeze();
        OldPage.Source = bitmap;
        OldPage.Width = PageHost.ActualWidth;
        OldPage.Height = PageHost.ActualHeight;
    }

    /// <summary>
    /// 새 페이지(제목 포함)를 들여보낸다(들어오는 것은 250ms, 나가는 것은 167ms, 둘 다 감속 곡선).
    /// 메뉴를 바꿀 때(아래에서): 옛 페이지는 바로 사라지고 새 페이지가 살짝 올라오며 나타난다.
    /// 하위로(오른쪽에서)·상위로(왼쪽에서): 옛 페이지가 반대쪽으로 조금 밀리며 흐려지고, 새 페이지가 옆에서 들어온다.
    /// </summary>
    private void Play(Slide slide)
    {
        PageShift.BeginAnimation(TranslateTransform.XProperty, null);
        PageShift.BeginAnimation(TranslateTransform.YProperty, null);
        PageScroller.BeginAnimation(OpacityProperty, null);
        OldShift.BeginAnimation(TranslateTransform.XProperty, null);
        OldPage.BeginAnimation(OpacityProperty, null);
        PageShift.X = PageShift.Y = 0;
        var old = OldPage.Source;
        OldPage.Source = null;
        OldPage.Visibility = Visibility.Collapsed;
        if (slide == Slide.None || !Motion.Enabled) return;

        PageScroller.BeginAnimation(OpacityProperty, Motion.In(0, 1, Motion.Fast));
        if (slide == Slide.Up)
        {
            PageShift.BeginAnimation(TranslateTransform.YProperty, Motion.In(PageRise, 0, Motion.Normal));
            return;
        }

        double dir = slide == Slide.FromRight ? 1 : -1;
        PageShift.BeginAnimation(TranslateTransform.XProperty, Motion.In(dir * PageSlide, 0, Motion.Normal));
        if (old == null) return;
        OldPage.Source = old;
        OldPage.Visibility = Visibility.Visible;
        // 흐려지는 건 감속 곡선으로: 가속 곡선(1,0,1,1)은 끝 무렵까지 거의 그대로라 "있다가 툭 사라지는" 것처럼 보인다.
        var fade = Motion.In(1, 0, Motion.Fast);
        fade.Completed += (_, _) =>
        {
            if (OldPage.Source != old) return;
            OldPage.Source = null;
            OldPage.Visibility = Visibility.Collapsed;
        };
        OldShift.BeginAnimation(TranslateTransform.XProperty, Motion.In(0, -dir * PageExit, Motion.Fast));
        OldPage.BeginAnimation(OpacityProperty, fade);
    }

    // ----- 왼쪽 메뉴 -----

    private void BuildNav()
    {
        Nav.Children.Clear();
        _navItems.Clear();
        foreach (var (glyph, text, page) in NavItems)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = Glyph(glyph, 16);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            var label = new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            Grid.SetColumn(label, 1);
            grid.Children.Add(icon);
            grid.Children.Add(label);

            var item = new Border
            {
                Height = NavSlot - 4,
                Margin = new Thickness(0, 2, 0, 2),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Child = grid,
            };
            var target = page;
            HoverFade(item, () => TopOf(_page) == target ? "HoverBg" : null);
            item.MouseLeftButtonUp += (_, _) =>
            {
                if (_page != target) Go(target);
                else if (_paneOpen) ClosePane();
            };
            _navItems.Add((item, page));
            Nav.Children.Add(item);
        }
    }

    /// <summary>지금 페이지가 속한 메뉴를 고른 모양으로 두고, 선택 막대를 그리로 옮긴다.</summary>
    private void SelectNav()
    {
        var top = TopOf(_page);
        int index = _navItems.FindIndex(n => n.Page == top);
        if (index == _navIndex) return;
        bool animate = _navIndex >= 0 && IsLoaded;
        for (int i = 0; i < _navItems.Count; i++)
        {
            var item = _navItems[i].Item;
            string? rest = i == index ? "HoverBg" : null;
            if (item.IsMouseOver) continue; // 마우스를 뗄 때 알맞은 색으로 돌아간다
            if (animate) FadeBackground(item, rest);
            else if (rest != null) item.SetResourceReference(Border.BackgroundProperty, rest);
            else item.Background = Brushes.Transparent;
        }
        _navIndex = index;
        MoveBar(index, animate);
    }

    /// <summary>
    /// 선택 막대를 index 줄로 옮긴다. 가는 쪽 끝이 먼저 쭉 늘어나고(250ms), 83ms 늦게 반대쪽 끝이 따라와 줄어든다.
    /// </summary>
    private void MoveBar(int index, bool animate)
    {
        double top = index * NavSlot + NavSlot / 2 - 8, bottom = top + 16;
        Indicator.Visibility = index < 0 ? Visibility.Hidden : Visibility.Visible;
        if (!animate || !Motion.Enabled)
        {
            StopBar();
            _barTop = top;
            _barBottom = bottom;
            PlaceBar();
            return;
        }
        _barFromTop = _barTop;
        _barFromBottom = _barBottom;
        _barToTop = top;
        _barToBottom = bottom;
        _barClock.Restart();
        if (_barMoving) return;
        _barMoving = true;
        CompositionTarget.Rendering += OnBarFrame;
    }

    private void OnBarFrame(object? sender, EventArgs e)
    {
        double ms = _barClock.Elapsed.TotalMilliseconds;
        double lead = Motion.Decel(ms / Motion.Normal);
        double trail = Motion.Decel((ms - Motion.Faster) / Motion.Normal);
        bool down = _barToTop > _barFromTop;
        _barTop = _barFromTop + (_barToTop - _barFromTop) * (down ? trail : lead);
        _barBottom = _barFromBottom + (_barToBottom - _barFromBottom) * (down ? lead : trail);
        PlaceBar();
        if (ms >= Motion.Faster + Motion.Normal) StopBar();
    }

    private void StopBar()
    {
        if (!_barMoving) return;
        _barMoving = false;
        CompositionTarget.Rendering -= OnBarFrame;
    }

    private void PlaceBar()
    {
        Canvas.SetTop(Indicator, _barTop);
        Indicator.Height = Math.Max(0, _barBottom - _barTop);
    }

    // ----- 좁은 창: 메뉴를 숨기고 ☰로 펼친다 -----

    private void SetCompact(bool compact)
    {
        if (compact == _compact && _navLaidOut) return;
        _navLaidOut = true;
        _compact = compact;
        _paneOpen = false;
        Scrim.Visibility = Visibility.Collapsed;
        NavShift.BeginAnimation(TranslateTransform.XProperty, null);
        NavShift.X = 0;
        NavPane.BeginAnimation(OpacityProperty, null);
        NavPane.Opacity = 1;

        NavColumn.Width = new GridLength(compact ? 0 : NavWidth);
        // 좁은 창의 펼친 메뉴는 설정 앱처럼 제목 표시줄까지 덮는다. 버튼(←, ☰)은 판 위에 그대로 보인다.
        Grid.SetRow(NavPane, compact ? 0 : 1);
        Grid.SetRowSpan(NavPane, compact ? 2 : 1);
        NavPane.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PaneButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        if (compact)
        {
            // 내용 위에 뜨는 판: 불투명한 바탕, 오른쪽만 둥글게, 옅은 그림자.
            NavPane.SetResourceReference(Border.BackgroundProperty, "FlyoutBg");
            NavPane.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            NavPane.BorderThickness = new Thickness(0, 0, 1, 0);
            NavPane.CornerRadius = new CornerRadius(0, 8, 8, 0);
            NavPane.Padding = new Thickness(8, 48 + 4, 8, 16);
            NavPane.Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 8, Direction = 0, Opacity = 0.18 };
        }
        else
        {
            NavPane.ClearValue(Border.BackgroundProperty);
            NavPane.ClearValue(Border.BorderBrushProperty);
            NavPane.BorderThickness = new Thickness(0);
            NavPane.CornerRadius = new CornerRadius(0);
            NavPane.Padding = new Thickness(16, 4, 12, 16);
            NavPane.Effect = null;
        }
    }

    private bool _navLaidOut;

    private void OnPaneClick(object sender, RoutedEventArgs e)
    {
        if (_paneOpen) ClosePane();
        else OpenPane();
    }

    private void OnScrimDown(object sender, MouseButtonEventArgs e)
    {
        ClosePane();
        e.Handled = true;
    }

    private void OpenPane()
    {
        if (!_compact || _paneOpen) return;
        _paneOpen = true;
        NavPane.Visibility = Visibility.Visible;
        Scrim.Visibility = Visibility.Visible;
        NavShift.BeginAnimation(TranslateTransform.XProperty, Motion.In(-NavWidth, 0, Motion.Normal));
        NavPane.BeginAnimation(OpacityProperty, Motion.In(0, 1, Motion.Fast));
    }

    private void ClosePane()
    {
        if (!_paneOpen) return;
        _paneOpen = false;
        Scrim.Visibility = Visibility.Collapsed;
        var slide = Motion.Out(null, -NavWidth, Motion.Fast);
        slide.Completed += (_, _) =>
        {
            if (_paneOpen || !_compact) return;
            NavPane.Visibility = Visibility.Collapsed;
            NavShift.BeginAnimation(TranslateTransform.XProperty, null);
            NavShift.X = 0;
        };
        NavShift.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Mica는 22H2부터. 그 전에는 Theme의 SettingsBg가 불투명한 바탕을 칠한다.
        Hwnd.ApplyFluent(this, Theme.HasBackdrop ? Hwnd.Backdrop.Mica : Hwnd.Backdrop.None, roundCorners: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        StopBar();
        _mgr.Changed -= OnChanged;
        _mgr.EditChanged -= OnEditChanged;
        if (Updater.Instance is { } updater) updater.Changed -= OnUpdateChanged;
        if (_win == this) _win = null;
        base.OnClosed(e);
    }
}
