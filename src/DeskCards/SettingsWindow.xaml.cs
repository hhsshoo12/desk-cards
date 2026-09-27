using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskCards;

/// <summary>
/// 통합 설정 창(Windows 11 설정 앱 모양). 왼쪽 메뉴: 카드 / 일반 / 정보.
/// 카드 페이지에서 편집을 시작하면 이 창은 잠시 숨고 화면 위쪽에 편집 막대가 뜬다.
/// </summary>
internal partial class SettingsWindow : Window
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const string RepoUrl = "https://github.com/hhsshoo12/desk-cards";

    private enum PageKind { Cards, Card, General, About }

    private static readonly (string Glyph, string Text, PageKind Page)[] NavItems =
    {
        ("", "카드", PageKind.Cards),
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

    private SettingsWindow(GroupManager mgr)
    {
        InitializeComponent();
        _mgr = mgr;
        AppIcon.Source = LoadIcon(16);
        BigIcon.Source = LoadIcon(256);

        _mgr.Changed += OnChanged;
        _mgr.EditChanged += OnEditChanged;
        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        // 항목 수 등이 바뀌었을 수 있으니 돌아올 때 목록을 확인한다. 누르는 중인 버튼이 바뀌지 않도록 달라졌을 때만 다시 그린다.
        Activated += (_, _) => { if (_page == PageKind.Cards && CardsSignature() != _cardsSignature) ScheduleBuild(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _page == PageKind.Card && Keyboard.FocusedElement is not TextBox)
            {
                Go(PageKind.Cards);
                e.Handled = true;
            }
        };
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

    private void StartEdit(CardWindow? card) => _mgr.BeginEditMode(card);

    /// <summary>클릭 처리 도중에 화면을 갈아엎지 않도록 한 박자 늦게, 여러 번 와도 한 번만 다시 그린다.</summary>
    private void ScheduleBuild()
    {
        if (_buildQueued || _closed) return;
        _buildQueued = true;
        Dispatcher.BeginInvoke(() => { _buildQueued = false; if (!_closed) Build(); });
    }

    private void UpdateSummary() => Summary.Text = $"카드 {_mgr.Cards.Count}개";

    private void Build()
    {
        if (_page == PageKind.Card && (_card == null || !_mgr.Cards.Contains(_card))) _page = PageKind.Cards;
        double offset = PageScroller.VerticalOffset;
        UpdateSummary();
        _refreshControls.Clear();
        BuildNav();
        Page.Children.Clear();
        Breadcrumb.Children.Clear();
        _first = true;
        switch (_page)
        {
            case PageKind.Cards: BuildCards(); break;
            case PageKind.Card: BuildCard(_card!); break;
            case PageKind.General: BuildGeneral(); break;
            default: BuildAbout(); break;
        }
        PageScroller.ScrollToVerticalOffset(offset);
    }

    // ----- 페이지 -----

    private string _cardsSignature = "";

    private string CardsSignature() => string.Join("|", _mgr.Cards.Select(c =>
        $"{c.Group.Name}/{c.Group.Items.Count}/{c.CurrentLayout.Cols}x{c.CurrentLayout.Rows}/{c.SizePercent}"));

    private void BuildCards()
    {
        _cardsSignature = CardsSignature();
        Crumb("카드");
        AddRow(Row("", "카드 편집",
            "화면 위쪽에 편집 막대가 떠요. 카드를 끌어 옮기고, 오른쪽 아래 모서리로 크기를 바꾸고, 골라서 지울 수 있어요.",
            Button("편집 시작", () => StartEdit(null), accent: true)));
        AddRow(Row("", "새 그룹", "빈 카드를 하나 만들고 이름을 정해요.",
            Button("만들기", () => _mgr.NewGroup())));

        var cards = _mgr.Cards;
        Header($"그룹 {cards.Count}개");
        foreach (var card in cards)
        {
            var c = card;
            var l = c.CurrentLayout;
            string desc = $"항목 {c.Group.Items.Count}개 · 칸 {l.Cols}×{l.Rows} · 크기 {c.SizePercent}%";
            UIElement? icon = null;
            if (c.Group.Items.Count > 0)
            {
                var img = new Image { Source = c.Group.Items[0].Icon, Width = 24, Height = 24 };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                icon = img;
            }
            AddRow(Row("", c.Group.Name, desc, null, () => ShowCard(c), icon));
        }
    }

    private void BuildCard(CardWindow card)
    {
        _cardName = card.Group.Name;
        Crumb("카드", () => Go(PageKind.Cards));
        Crumb(card.Group.Name);

        AddRow(Row("", "이름", "그룹 폴더 이름도 같이 바뀌어요.", NameBox(card)));
        AddRow(Row("", "크기", "기본 크기 대비 비율이에요. [−] [+]는 5%씩, 숫자를 눌러 직접 입력할 수도 있어요.", Percent(card)));
        AddRow(Row("", "미리보기 칸 (가로)", "카드에 아이콘이 몇 칸 보일지 정해요. 넘치는 항목은 마지막 칸에 묶여요.",
            Stepper(() => card.CurrentLayout.Cols, v => card.SetGrid(v, card.CurrentLayout.Rows))));
        AddRow(Row("", "미리보기 칸 (세로)", null,
            Stepper(() => card.CurrentLayout.Rows, v => card.SetGrid(card.CurrentLayout.Cols, v))));

        Header("관리");
        AddRow(Row("", "위치 옮기기 · 크기 조절", "편집 막대를 띄우고 이 카드를 골라 둬요.",
            Button("편집", () => StartEdit(card))));
        AddRow(Row("", "폴더 열기", card.Group.Folder,
            Button("열기", () => FileOps.OpenFolder(card.Group.Folder))));
        var delete = Button("삭제", () =>
        {
            if (_mgr.DeleteGroup(card)) Go(PageKind.Cards);
        });
        delete.SetResourceReference(ForegroundProperty, "Danger");
        AddRow(Row("", "이 그룹 삭제", "안에 있는 항목은 바탕화면으로 옮겨져요.", delete));
    }

    private void BuildGeneral()
    {
        Crumb("일반");
        AddRow(Row("", "Windows 시작 시 실행", "로그인하면 카드가 바로 바탕화면에 나타나요.",
            Switch(AutoStart.Enabled, v => AutoStart.Enabled = v)));
        AddRow(Row("", "Windows 배율 따라가기",
            "Windows 디스플레이 배율을 바꾸면 카드도 같은 비율로 커지고 작아져요. 켜고 끄는 순간에는 지금 크기가 그대로 남아요.",
            Switch(_mgr.FollowWindowsScale, v => _mgr.FollowWindowsScale = v)));
        AddRow(Row("", "안내선 · 자동 맞춤",
            "카드를 옮기거나 크기를 바꿀 때 다른 카드·화면 가운데와 줄이 맞으면 보라색 선을 보여 주고 붙여요. Alt를 누르고 있으면 잠시 꺼져요.",
            Switch(_mgr.ShowGuides, v => _mgr.ShowGuides = v)));

        Header("폴더");
        AddRow(Row("", "그룹 폴더", _mgr.Root, Button("열기", () => FileOps.OpenFolder(_mgr.Root))));
    }

    private void BuildAbout()
    {
        Crumb("정보");
        var icon = new Image { Source = LoadIcon(32), Width = 24, Height = 24 };
        string version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        AddRow(Row("", "Desk Cards", $"버전 {version}", null, null, icon));
        AddRow(Row("", "GitHub", RepoUrl, Button("열기", () => OpenUrl(RepoUrl))));
        AddRow(Row("", "설정 폴더", AppPaths.ConfigDir, Button("열기", () => FileOps.OpenFolder(AppPaths.ConfigDir))));
    }

    // ----- 왼쪽 메뉴 -----

    private void BuildNav()
    {
        Nav.Children.Clear();
        var current = _page == PageKind.Card ? PageKind.Cards : _page;
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

    // ----- 줄 만들기 -----

    private bool _first;

    /// <summary>페이지 제목(이동 경로). 누를 수 있으면 흐린 색으로, 사이에 › 를 넣는다.</summary>
    private void Crumb(string text, Action? click = null)
    {
        if (Breadcrumb.Children.Count > 0)
        {
            var chevron = Glyph("", 16);
            chevron.Margin = new Thickness(12, 6, 12, 0);
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
            Breadcrumb.Children.Add(chevron);
        }
        var t = new TextBlock
        {
            Text = text,
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Malgun Gothic"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 460,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, click != null ? "SubFg" : "Fg");
        if (click != null)
        {
            t.Cursor = Cursors.Hand;
            t.MouseEnter += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            t.MouseLeave += (_, _) => t.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
            t.MouseLeftButtonUp += (_, _) => click();
        }
        Breadcrumb.Children.Add(t);
    }

    private void Header(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, _first ? 0 : 24, 0, 8) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        Page.Children.Add(t);
        _first = false;
    }

    private void AddRow(UIElement row)
    {
        Page.Children.Add(row);
        _first = false;
    }

    /// <summary>
    /// 설정 한 줄: [아이콘] 제목 / 설명 ........ [컨트롤]. click이 있으면 줄 전체를 누를 수 있고 오른쪽에 › 가 붙는다.
    /// </summary>
    private static Border Row(string glyph, string title, string? desc, UIElement? control, Action? click = null, UIElement? icon = null)
    {
        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var head = icon ?? Glyph(glyph, 18);
        if (head is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Left;
            fe.VerticalAlignment = VerticalAlignment.Center;
        }
        grid.Children.Add(head);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        var t = new TextBlock { Text = title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        texts.Children.Add(t);
        if (!string.IsNullOrEmpty(desc))
        {
            var d = new TextBlock { Text = desc, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            d.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
            texts.Children.Add(d);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        UIElement? right = control;
        if (click != null)
        {
            var chevron = Glyph("", 12);
            chevron.Margin = new Thickness(8, 0, 4, 0);
            right = chevron;
        }
        if (right != null)
        {
            if (right is FrameworkElement r) r.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
            if (control != null && click == null)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                bool? stacked = null;
                grid.SizeChanged += (_, _) =>
                {
                    bool narrow = grid.ActualWidth < 460;
                    if (stacked == narrow) return;
                    stacked = narrow;
                    Grid.SetColumnSpan(texts, narrow ? 2 : 1);
                    Grid.SetRow(control, narrow ? 1 : 0);
                    Grid.SetColumn(control, narrow ? 1 : 2);
                    Grid.SetColumnSpan(control, narrow ? 2 : 1);
                    if (control is FrameworkElement field)
                    {
                        field.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                        field.Margin = new Thickness(0, narrow ? 10 : 0, 0, 0);
                    }
                };
            }
        }

        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 11, 16, 11),
            Margin = new Thickness(0, 0, 0, 4),
            Child = grid,
        };
        row.SetResourceReference(Border.BackgroundProperty, "RowBg");
        row.SetResourceReference(Border.BorderBrushProperty, "RowBorder");
        if (click != null)
        {
            row.Cursor = Cursors.Hand;
            row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "HoverBg");
            row.MouseLeave += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "RowBg");
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        }
        return row;
    }

    private Button Button(string text, Action act, bool accent = false)
    {
        var b = new Button { Content = text, Style = (Style)FindResource(accent ? "AccentButton" : "StdButton") };
        b.Click += (_, _) => act();
        return b;
    }

    private CheckBox Switch(bool value, Action<bool> set)
    {
        var s = new CheckBox { IsChecked = value, Style = (Style)FindResource("Switch") };
        s.Click += (_, _) => set(s.IsChecked == true);
        return s;
    }

    private static TextBlock Glyph(string glyph, double size)
    {
        var t = new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        return t;
    }

    /// <summary>둥근 입력 칸. 입력 중에는 아래쪽에 강조색 줄이 생긴다.</summary>
    private static Border Field(TextBox box, double width)
    {
        box.FontSize = 14;
        box.Background = Brushes.Transparent;
        box.BorderThickness = new Thickness(0);
        box.Padding = new Thickness(8, 4, 8, 5);
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.SetResourceReference(TextBox.ForegroundProperty, "Fg");
        box.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        var line = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(0, 0, 4, 4), Visibility = Visibility.Hidden };
        line.SetResourceReference(Border.BackgroundProperty, "Accent");
        box.GotKeyboardFocus += (_, _) => line.Visibility = Visibility.Visible;
        box.LostKeyboardFocus += (_, _) => line.Visibility = Visibility.Hidden;
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(line);
        var field = new Border { Width = width, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = grid };
        field.SetResourceReference(Border.BackgroundProperty, "ControlBg");
        field.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        return field;
    }

    private Border NameBox(CardWindow card)
    {
        var box = new TextBox { Text = card.Group.Name, MaxLength = 80 };
        bool committing = false;
        void Commit()
        {
            if (committing || _closed || !_mgr.Cards.Contains(card)) return;
            string name = box.Text.Trim();
            if (name.Length == 0 || name == card.Group.Name) { box.Text = card.Group.Name; return; }
            committing = true;
            try { if (!_mgr.RenameGroup(card, name)) box.Text = card.Group.Name; }
            finally { committing = false; }
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { box.Text = card.Group.Name; Keyboard.ClearFocus(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => Commit();
        return Field(box, 220);
    }

    /// <summary>카드 크기(%): [−] 숫자 % [+]. 5%씩 움직이고 숫자를 직접 입력할 수도 있다.</summary>
    private UIElement Percent(CardWindow card)
    {
        var box = new TextBox { Text = card.SizePercent.ToString(), MaxLength = 3, TextAlignment = TextAlignment.Right };
        _refreshControls.Add(() => { if (!box.IsKeyboardFocusWithin) box.Text = card.SizePercent.ToString(); });
        void Apply(int percent) => box.Text = card.SetSizePercent(Math.Clamp(percent, 10, 999)).ToString();
        void Commit()
        {
            if (_closed || !_mgr.Cards.Contains(card)) return;
            if (int.TryParse(box.Text.Trim().TrimEnd('%'), out int v)) Apply(v);
            else box.Text = card.SizePercent.ToString();
        }
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (box.IsKeyboardFocusWithin) return;
            e.Handled = true;
            box.Focus();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Commit();
            box.SelectAll();
        };
        box.LostKeyboardFocus += (_, _) => Commit();

        var unit = new TextBlock { Text = "%", FontSize = 14, Margin = new Thickness(6, 0, 6, 1), VerticalAlignment = VerticalAlignment.Center };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(StepButton("", () =>
        {
            int cur = card.SizePercent;
            Apply(cur % 5 == 0 ? cur - 5 : cur / 5 * 5); // 103 → 100, 100 → 95
        }));
        panel.Children.Add(Field(box, 64));
        panel.Children.Add(unit);
        panel.Children.Add(StepButton("", () => Apply((card.SizePercent / 5 + 1) * 5)));
        return panel;
    }

    /// <summary>[−] 값 [+] 로 1~8을 고른다.</summary>
    private UIElement Stepper(Func<int> get, Action<int> set)
    {
        var num = new TextBlock { Text = get().ToString(), FontSize = 14, Width = 32, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _refreshControls.Add(() => num.Text = get().ToString());
        num.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        void Step(int delta)
        {
            int next = Math.Clamp(get() + delta, CardLayout.MinCells, CardLayout.MaxCells);
            if (next == get()) return;
            set(next);
            num.Text = get().ToString();
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(StepButton("", () => Step(-1)));
        panel.Children.Add(num);
        panel.Children.Add(StepButton("", () => Step(+1)));
        return panel;
    }

    private Button StepButton(string glyph, Action act)
    {
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = Glyph(glyph, 12) };
        b.Click += (_, _) => act();
        return b;
    }

    // ----- 기타 -----

    private static BitmapSource? LoadIcon(int size)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/app.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();
        }
        catch
        {
            return null;
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 기본 브라우저가 없으면 무시 */ }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsLight ? 0 : 1);
        if (Environment.OSVersion.Version.Build >= 22621)
            Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 2); // Mica
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _mgr.Changed -= OnChanged;
        _mgr.EditChanged -= OnEditChanged;
        if (_win == this) _win = null;
        base.OnClosed(e);
    }
}
