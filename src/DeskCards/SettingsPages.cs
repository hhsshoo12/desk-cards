using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskCards;

/// <summary>설정 페이지의 공통 틀: 제목(경로) + 스크롤되는 설정 카드 목록. 만드는 방법은 각 페이지가 Build에서 정한다.</summary>
internal abstract class SettingsPage : UserControl
{
    private const string RepoUrlValue = "https://github.com/hhsshoo12/desk-cards";
    protected const string DeveloperUrl = "https://github.com/hhsshoo12";
    protected static string RepoUrl => RepoUrlValue;

    private const double MaxContentWidth = 1000, SideMargin = 40; // 카드 폭은 최대 1000, 좁은 창에서도 양쪽에 40씩 둔다(Windows 설정과 같다)

    protected readonly SettingsPageArgs Args;
    private readonly StackPanel _list = new() { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 12, 0, 48) };
    private readonly Border _header = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    private readonly List<Action> _refreshers = new();

    protected GroupManager Mgr => Args.Mgr;
    protected SettingsWindow Host => Args.Host;

    protected SettingsPage(SettingsPageArgs args)
    {
        Args = args;
        // Windows 설정(PowerToys 설정 앱과 같은 구성): 제목은 위에 고정하고 그 아래 카드만 스크롤한다.
        // 폭은 최대 1000으로 가운데에 둔다. ScrollViewer 안에서 MaxWidth만 걸면 메뉴가 펼쳐질 때 옛 폭 기준으로 밀려서 폭을 직접 정한다.
        var scroll = new ScrollViewer { Content = new Grid { Children = { _list } }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var page = new Grid { RowSpacing = 12 };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(scroll, 1);
        page.Children.Add(_header);
        page.Children.Add(scroll);
        page.SizeChanged += (_, e) => _list.Width = _header.Width = Math.Max(1, Math.Min(MaxContentWidth, e.NewSize.Width - 2 * SideMargin));
        _list.ChildrenTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection
        {
            new Microsoft.UI.Xaml.Media.Animation.EntranceThemeTransition { FromVerticalOffset = 50 },
            new Microsoft.UI.Xaml.Media.Animation.RepositionThemeTransition { IsStaggeringEnabled = false },
        };
        Content = page;
        Loaded += (_, _) => { Mgr.Changed += OnMgrChanged; };
        Unloaded += (_, _) => { Mgr.Changed -= OnMgrChanged; OnUnloaded(); };
    }

    /// <summary>페이지를 만든다. 파생 클래스 생성자 끝에서 부른다(생성자 안에서 가상 함수를 부르지 않으려고 따로 둔다).</summary>
    protected abstract void Build();

    protected virtual void OnUnloaded() { }


    protected void Start() => Build();

    /// <summary>설정이 바뀌었을 때 값만 다시 맞춘다(카드 목록처럼 모양이 바뀌는 페이지는 Rebuild를 쓴다).</summary>
    protected virtual void OnMgrChanged() => Ui.Post(() => { foreach (var r in _refreshers.ToArray()) r(); });

    protected void Rebuild()
    {
        _list.Children.Clear();
        _refreshers.Clear();
        Build();
    }

    protected void OnRefresh(Action refresh) => _refreshers.Add(refresh);

    // ----- 만들기 도구 -----

    protected void Crumbs(params (string Text, SettingsPageKind? Page)[] items)
    {
        var bar = new BreadcrumbBar { ItemsSource = items.Select(i => i.Text).ToList() };
        // 제목 크기로 키운 마지막 칸을 쓰려면 템플릿이 필요하다. 기본 모양을 쓰되 글자만 키운다.
        bar.Resources["BreadcrumbBarItemThemeFontSize"] = 28.0;
        bar.ItemClicked += (_, e) =>
        {
            if (e.Index < items.Length - 1 && items[e.Index].Page is { } page) Host.Go(page);
        };
        _header.Child = bar;
    }

    protected void Header(string text) =>
        _list.Children.Add(new TextBlock { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(1, 22, 0, 6) });

    protected SettingsCard Row(string glyph, string header, string? description = null, UIElement? control = null, Action? onClick = null)
    {
        var card = new SettingsCard { Header = header, Description = description!, HeaderIcon = new FontIcon { Glyph = glyph }, Content = control };
        if (onClick != null)
        {
            card.IsClickEnabled = true;
            card.Click += (_, _) => onClick();
        }
        _list.Children.Add(card);
        return card;
    }

    protected void Add(UIElement element) => _list.Children.Add(element);

    protected static ToggleSwitch Switch(bool on, Action<bool> set)
    {
        var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => set(toggle.IsOn);
        return toggle;
    }

    protected static Button Btn(string text, Action click)
    {
        var button = new Button { Content = text, MinWidth = 80 };
        button.Click += (_, _) => click();
        return button;
    }

    protected static Button AccentBtn(string text, Action click)
    {
        var button = Btn(text, click);
        button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        return button;
    }

    /// <summary>− 값 + 로 한 칸씩 바꾸는 입력. 값을 읽는 쪽과 쓰는 쪽을 받고, 쓴 뒤 실제 값으로 다시 맞춘다.</summary>
    protected FrameworkElement Stepper(Func<int> get, Action<int> set, int min, int max, Func<int, string> format, int step = 1)
    {
        var value = new TextBlock { MinWidth = 64, TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var minus = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Width = 36 };
        var plus = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Width = 36 };
        void Refresh()
        {
            int v = get();
            value.Text = format(v);
            minus.IsEnabled = v > min;
            plus.IsEnabled = v < max;
        }
        minus.Click += (_, _) => { set(Math.Max(min, get() - step)); Refresh(); };
        plus.Click += (_, _) => { set(Math.Min(max, get() + step)); Refresh(); };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { minus, value, plus } };
        Refresh();
        OnRefresh(Refresh);
        return panel;
    }

    protected static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 기본 브라우저가 없으면 무시 */ }
    }
}

// ----- 카드 -----

/// <summary>카드: 편집 시작과 폴더 카드 목록.</summary>
internal sealed class CardsPage : SettingsPage
{
    public CardsPage(SettingsPageArgs a) : base(a) => Start();

    protected override void Build()
    {
        Crumbs(("카드", null));
        Row("", "편집 시작", "카드를 옮기고 크기를 바꾸고 이름·칸 수를 고쳐요. 끝나면 편집 막대의 완료를 눌러요.", AccentBtn("편집", () => Mgr.BeginEditMode()));
        Row("", "폴더 카드", "바탕화면에 놓인 카드 목록이에요.", null, () => Host.Go(SettingsPageKind.FolderCards));
    }
}

/// <summary>폴더 카드 목록과 새 그룹.</summary>
internal sealed class FolderCardsPage : SettingsPage
{
    public FolderCardsPage(SettingsPageArgs a) : base(a) => Start();

    protected override void OnMgrChanged() => Ui.Post(Rebuild);

    protected override void Build()
    {
        Crumbs(("카드", SettingsPageKind.Cards), ("폴더 카드", null));
        Row("", "새 그룹", "새 폴더 카드를 만들어요.", Btn("만들기", () => Mgr.NewGroup()));
        Header("카드");
        foreach (var card in Mgr.Cards)
        {
            var c = card;
            Row("", c.CardName, null, null, () => Host.GoTo(SettingsPageKind.Card, c));
        }
    }
}

/// <summary>카드 하나: 이름·크기·칸 수·편집·폴더·삭제.</summary>
internal sealed class CardPage : SettingsPage
{
    private readonly CardWindow? _card;

    public CardPage(SettingsPageArgs a) : base(a)
    {
        _card = a.Card;
        Start();
    }

    protected override void Build()
    {
        if (_card == null || !Mgr.Cards.Contains(_card))
        {
            Ui.Post(Host.GoUp);
            return;
        }
        var card = _card;
        Crumbs(("카드", SettingsPageKind.Cards), ("폴더 카드", SettingsPageKind.FolderCards), (card.CardName, null));

        var name = new TextBox { Text = card.CardName, MinWidth = 220, MaxLength = 80 };
        void Commit()
        {
            string text = name.Text.Trim();
            if (text.Length == 0 || text == card.CardName || !Mgr.Cards.Contains(card)) { name.Text = card.CardName; return; }
            if (!Mgr.RenameGroup(card, text)) name.Text = card.CardName;
        }
        name.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Commit(); e.Handled = true; } };
        name.LostFocus += (_, _) => Commit();
        Row("", "이름", "폴더 이름도 함께 바뀌어요.", name);

        Row("", "크기", "카드 전체를 같은 비율로 키우고 줄여요.",
            Stepper(() => card.SizePercent, v => card.SetSizePercent(v), 20, 400, v => $"{v}%", step: 5));
        Row("", "가로 칸", "미리보기 칸 수예요.",
            Stepper(() => card.CurrentLayout.Cols, v => card.SetGrid(v, card.CurrentLayout.Rows), CardLayout.MinCells, CardLayout.MaxCells, v => $"{v}칸"));
        Row("", "세로 칸", null,
            Stepper(() => card.CurrentLayout.Rows, v => card.SetGrid(card.CurrentLayout.Cols, v), CardLayout.MinCells, CardLayout.MaxCells, v => $"{v}칸"));
        Row("", "편집", "이 카드를 골라 편집 모드를 시작해요.", Btn("편집", () => Mgr.BeginEditMode(card)));
        Row("", "폴더 열기", card.Group.Folder, Btn("열기", () => FileOps.OpenFolder(card.Group.Folder)));
        Row("", "카드 삭제", "카드와 그룹 폴더를 지워요.", Btn("삭제", () =>
        {
            var answer = Dialogs.Show("이 카드를 삭제할까요?", MessageBoxButton.OKCancel, $"'{card.CardName}' 삭제", "삭제");
            if (answer != MessageBoxResult.OK || !Mgr.DeleteGroup(card)) return;
            Host.GoUp();
        }));
    }
}

// ----- 일반 -----

internal sealed class GeneralPage : SettingsPage
{
    public GeneralPage(SettingsPageArgs a) : base(a) => Start();

    protected override void Build()
    {
        Crumbs(("일반", null));
        Row("", "Windows 시작 시 실행", "로그인하면 카드가 바로 바탕화면에 나타나요.", Switch(AutoStart.Enabled, v => AutoStart.Enabled = v));
        Row("", "Windows 배율 따라가기",
            "Windows 디스플레이 배율을 바꾸면 카드도 같은 비율로 커지고 작아져요. 켜고 끄는 순간에는 지금 크기가 그대로 남아요.",
            Switch(Mgr.FollowWindowsScale, v => Mgr.FollowWindowsScale = v));
        Row("", "안내선 · 자동 맞춤",
            "카드를 옮기거나 크기를 바꿀 때 다른 카드·화면 가운데와 줄이 맞으면 보라색 선을 보여 주고 붙여요. Alt를 누르고 있으면 잠시 꺼져요.",
            Switch(Mgr.ShowGuides, v => Mgr.ShowGuides = v));

        // 한 칸 = 0.1초. 0 = 즉시, 1~10 = 0.1~1초, 11 = 꺼짐.
        const int hoverOff = Config.HoverDelayMax / 100 + 1;
        Row("", "더보기에 올려 두면 펼치기",
            "카드의 더보기 칸에 마우스를 올려 두면 이 시간 뒤에 누르지 않아도 펼쳐지고, 펼쳐진 창 밖으로 옮기면 바로 접혀요. 1초에서 더 늘리면 꺼져요.",
            Stepper(() => Mgr.HoverExpand ? Mgr.HoverExpandDelay / 100 : hoverOff,
                v =>
                {
                    Mgr.HoverExpand = v < hoverOff;
                    if (v < hoverOff) Mgr.HoverExpandDelay = v * 100;
                },
                0, hoverOff, v => v == 0 ? "즉시" : v == hoverOff ? "꺼짐" : $"{v / 10.0:0.0}초"));
        if (Updater.Instance is { } updater)
            Row("", "자동 업데이트", "새 버전이 나오면 미리 받아 두었다가, 다음에 앱을 켤 때(보통 PC를 다시 켤 때) 새 버전으로 열어요.",
                Switch(updater.AutoUpdate, v => updater.AutoUpdate = v));

        int hidden = Mgr.HiddenTipCount;
        var tips = Btn("다시 보기", () => { Mgr.ShowAllTips(); Rebuild(); });
        tips.IsEnabled = hidden > 0;
        Row("", "숨긴 안내", hidden > 0 ? $"'다시 보지 않기'를 누른 안내 {hidden}개를 다시 보여 줘요." : "'다시 보지 않기'를 누른 안내가 없어요.", tips);

        Header("폴더");
        Row("", "그룹 폴더", Mgr.Root, Btn("열기", () => FileOps.OpenFolder(Mgr.Root)));

        Header("실험");
        var flush = new ToggleSwitch { IsOn = Mgr.FlushSnap, OnContent = "", OffContent = "", MinWidth = 0, IsEnabled = !Mgr.AllowOverlap };
        var flushCard = new SettingsCard { Header = "완전히 붙이기", HeaderIcon = new FontIcon { Glyph = "" }, Content = flush };
        void RefreshFlush()
        {
            // 겹치기를 켜면 완전히 붙이기도 켜진 채로 잠긴다.
            flush.IsOn = Mgr.FlushSnap;
            flush.IsEnabled = !Mgr.AllowOverlap;
            flushCard.Description = Mgr.AllowOverlap
                ? "겹치기 · 레이어를 켜 두는 동안은 늘 켜져 있어요."
                : "카드끼리 간격 없이 딱 붙여 둘 수 있어요. 그림자가 옆 카드에 겹쳐 보이거나, 카드를 누를 때마다 그림자 방향이 바뀌는 등 모양이 어색해질 수 있어요.";
        }
        flush.Toggled += (_, _) => { if (flush.IsEnabled) Mgr.FlushSnap = flush.IsOn; };
        Row("", "겹치기 · 레이어",
            "카드를 다른 카드 위에 겹쳐 둘 수 있어요. 그림자가 꼬이거나 카드 앞뒤가 바뀌는 등 여러 오류가 생길 수 있어요. 켜면 완전히 붙이기도 함께 켜져요.",
            Switch(Mgr.AllowOverlap, v => { Mgr.AllowOverlap = v; RefreshFlush(); }));
        Add(flushCard);
        RefreshFlush();
        OnRefresh(RefreshFlush);
    }
}

// ----- 정보 -----

internal sealed class AboutPage : SettingsPage
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly ProgressBar _bar = new() { Width = 260, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _button = new() { MinWidth = 110, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing _ring = new() { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly Updater? _updater = Updater.Instance;

    public AboutPage(SettingsPageArgs a) : base(a)
    {
        _button.Click += (_, _) => OnUpdateClicked();
        Start();
        if (_updater != null) _updater.Changed += OnUpdaterChanged;
    }

    protected override void OnUnloaded()
    {
        if (_updater != null) _updater.Changed -= OnUpdaterChanged;
    }

    private void OnUpdaterChanged() => Ui.Post(RefreshUpdate);

    protected override void Build()
    {
        Crumbs(("정보", null));
        Add(AppCard());
        Row("", "개발 정보", "개발자 · 함께 만든 AI · 저작권 · 사용한 라이브러리", null, () => Host.Go(SettingsPageKind.DevInfo));
        Header("링크");
        Row("", "GitHub", RepoUrl, Btn("열기", () => OpenUrl(RepoUrl)));
        Row("", "설정 폴더", AppPaths.ConfigDir, Btn("열기", () => FileOps.OpenFolder(AppPaths.ConfigDir)));
        RefreshUpdate();
    }

    private UIElement AppCard()
    {
        var name = new TextBlock { Text = "Desk Cards", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { name, _status, _bar } };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { _ring, _button } };
        var grid = new Grid { ColumnSpacing = 20, Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = "", FontSize = 44, Foreground = Theme.Brush("Accent") };
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(icon);
        grid.Children.Add(texts);
        grid.Children.Add(right);
        return new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
    }

    private bool _spinHold;

    private void OnUpdateClicked()
    {
        var u = _updater;
        switch (u?.State)
        {
            case UpdateState.Available: _ = u.DownloadAsync(); break;
            case UpdateState.Ready: u.Restart(); break;
            case UpdateState.Idle or UpdateState.UpToDate or UpdateState.Failed:
                // 확인이 금방 끝나도 누른 게 보이도록 로딩 동그라미를 잠깐은 보여 준다.
                _spinHold = true;
                var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                timer.Tick += (_, _) => { timer.Stop(); _spinHold = false; RefreshUpdate(); };
                timer.Start();
                _ = u.CheckAsync();
                RefreshUpdate();
                break;
        }
    }

    private void RefreshUpdate()
    {
        var u = _updater;
        var state = u?.State ?? UpdateState.UpToDate;
        var current = u?.Current ?? Updater.RunningVersion;
        string version = u?.Latest is { } latest && latest > current
            ? $"현재 버전 {current.ToString(3)} → 최신 버전 {latest.ToString(3)}"
            : $"버전 {current.ToString(3)}";
        bool downloading = state == UpdateState.Downloading;
        _bar.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        _bar.Value = Math.Clamp(u?.Progress ?? 0, 0, 1) * 100;
        _status.Text = state switch
        {
            UpdateState.Preparing => "업데이트 준비 중…",
            UpdateState.Ready => "업데이트 준비 완료",
            UpdateState.Downloading => $"{version} · {Math.Round(_bar.Value)}%",
            UpdateState.UpToDate => version + "  최신" + (u?.LastChecked is { } at ? "  " + at.ToString("yyMMdd-HHmm") : ""),
            UpdateState.Failed => version + " · " + u?.Error,
            _ => version,
        };

        bool spinning = state == UpdateState.Checking || _spinHold;
        _ring.IsActive = spinning;
        _ring.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        (string label, bool accent, bool enabled) = state switch
        {
            _ when spinning => ("확인 중", false, false),
            UpdateState.Available when u?.CanUpdate != true => ("개발 빌드", false, false),
            UpdateState.Available => ("업데이트", true, true),
            UpdateState.Downloading => ("받는 중", false, false),
            UpdateState.Preparing => ("준비 중", false, false),
            UpdateState.Ready => ("앱 재시작", true, true),
            _ => ("버전 확인", false, u != null),
        };
        _button.Content = label;
        _button.IsEnabled = enabled;
        _button.Style = accent ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"];
    }
}

internal sealed class DevInfoPage : SettingsPage
{
    public DevInfoPage(SettingsPageArgs a) : base(a) => Start();

    protected override void Build()
    {
        Crumbs(("정보", SettingsPageKind.About), ("개발 정보", null));
        Row("", "개발자", "hhsshoo12", Btn("프로필", () => OpenUrl(DeveloperUrl)));
        Row("", "함께 만든 AI", "Claude (Anthropic) · Codex (OpenAI)");
        Row("", "저작권", "Copyright © 2026 hhsshoo12. All rights reserved.");
        Row("", "사용한 라이브러리", ".NET 10 · Windows App SDK · WinUI 3", null, () => Host.Go(SettingsPageKind.Libraries));
    }
}

internal sealed class LibrariesPage : SettingsPage
{
    public LibrariesPage(SettingsPageArgs a) : base(a) => Start();

    protected override void Build()
    {
        Crumbs(("정보", SettingsPageKind.About), ("개발 정보", SettingsPageKind.DevInfo), ("사용한 라이브러리", null));
        Header("앱에 포함");
        Lib(".NET 10 런타임", "MIT 라이선스 · .NET Foundation, Microsoft", "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT");
        Lib("Windows App SDK · WinUI 3", "MIT 라이선스 · Microsoft", "https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE");
        Lib("Windows Community Toolkit (설정 컨트롤)", "MIT 라이선스 · .NET Foundation", "https://github.com/CommunityToolkit/Windows/blob/main/License.md");
        Lib(".NET 런타임에 포함된 제3자 구성 요소", "런타임이 함께 싣고 있는 오픈 소스 목록이에요.", "https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT", "목록");
        Header("Windows 구성 요소 (앱에 포함하지 않음)");
        Row("", "Segoe Fluent Icons", "아이콘 글꼴 · Windows 11 기본 글꼴");
        Row("", ".NET Framework 4.8", "설치기가 사용 · Windows 기본 구성 요소");
    }

    private void Lib(string name, string desc, string url, string button = "라이선스") =>
        Row("", name, desc, Btn(button, () => OpenUrl(url)));
}




