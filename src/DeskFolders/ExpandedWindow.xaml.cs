using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeskFolders;

/// <summary>카드를 눌렀을 때 펼쳐지는 전체 목록 창(아크릴 배경).</summary>
internal partial class ExpandedWindow : Window
{
    private const int Columns = 4;
    private const int RowsPerPage = 3;
    private const double TileW = 96, TileH = 104;
    private const double PageH = RowsPerPage * TileH;
    private const double SidePad = 26, TitleH = 80, BottomPad = 28;

    private static ExpandedWindow? _current;

    private readonly CardWindow _card;
    private readonly GroupManager _mgr;
    private readonly bool _editTitle;
    private Point _downPos;
    private ShellEntry? _downEntry;
    private bool _pending, _busy, _closing;

    // 페이지 스크롤 상태
    private int _page, _pages = 1, _wheelAcc;
    private double _animFrom, _animTo;
    private DateTime _animStart;
    private bool _animating;

    // 설정 화면
    private const double SettingRowH = 40;
    private const double TabBarH = 48;
    private bool _settings;

    private ExpandedWindow(CardWindow card, GroupManager mgr, bool editTitle)
    {
        InitializeComponent();
        Focusable = true;
        _card = card;
        _mgr = mgr;
        _editTitle = editTitle;
        Width = SidePad * 2 + Columns * TileW;

        Group.Changed += Rebuild;
        Rebuild();
        PlaceNearCard();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += (_, _) => { if (!_busy) SafeClose(); };
        PreviewKeyDown += OnKey;
        PreviewMouseWheel += OnWheel;
        GearButton.Click += (_, _) => ShowSettings(!_settings);
        ItemsPanel.PreviewMouseLeftButtonDown += OnDown;
        ItemsPanel.PreviewMouseMove += OnMove;
        ItemsPanel.PreviewMouseLeftButtonUp += OnUp;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        Drop += OnDrop;

        TitleBox.Text = Group.Name;
        TitleBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTitle(); Keyboard.Focus(this); e.Handled = true; }
        };
        TitleBox.LostKeyboardFocus += (_, _) => CommitTitle();
        // 제목을 처음 누르면 전체 선택해서 바로 새 이름을 칠 수 있게 한다.
        TitleBox.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (TitleBox.IsKeyboardFocusWithin) return;
            TitleBox.Focus();
            e.Handled = true;
        };
        TitleBox.GotKeyboardFocus += (_, _) => TitleBox.SelectAll();
    }

    private GroupModel Group => _card.Group;

    public static void Open(CardWindow card, GroupManager mgr, bool editTitle = false)
    {
        _current?.SafeClose();
        var w = new ExpandedWindow(card, mgr, editTitle);
        _current = w;
        w.Show();
        w.Activate();
    }

    public static void CloseFor(CardWindow card)
    {
        if (_current != null && _current._card == card) _current.SafeClose();
    }

    private void Rebuild()
    {
        ItemsPanel.Children.Clear();
        foreach (var entry in Group.Items)
            ItemsPanel.Children.Add(MakeTile(entry));
        Empty.Visibility = Group.Items.Count == 0 && !_settings ? Visibility.Visible : Visibility.Collapsed;

        // 한 번에 4×3까지 보이고, 넘치면 3줄 단위 페이지로 위아래 스크롤한다.
        int rows = Math.Max(1, (int)Math.Ceiling(Group.Items.Count / (double)Columns));
        _pages = Math.Max(1, (int)Math.Ceiling(rows / (double)RowsPerPage));
        double viewH = Math.Min(rows, RowsPerPage) * TileH;
        ItemsPanel.MinHeight = _pages > 1 ? _pages * PageH : 0; // 마지막 페이지도 딱 맞게 멈추도록
        Scroller.Height = viewH;
        double height = TitleH + viewH + BottomPad;
        if (_settings)
        {
            // 설정은 한 페이지(4×3) 높이까지만 늘리고, 넘치면 안에서 스크롤한다.
            SettingsPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double maxView = RowsPerPage * TileH + BottomPad - 20;
            double view = Math.Min(SettingsPanel.DesiredSize.Height, maxView);
            SettingsScroller.Height = view;
            height = Math.Max(height, TitleH + TabBarH + view + 20);
        }
        Height = height;

        _page = Math.Min(_page, _pages - 1);
        StopScrollAnimation();
        Scroller.ScrollToVerticalOffset(_page * PageH);
        BuildDots();
    }

    private void BuildDots()
    {
        Dots.Children.Clear();
        Dots.Visibility = _pages > 1 && !_settings ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _pages; i++)
        {
            int index = i;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = i == _page ? 6 : 5,
                Height = i == _page ? 6 : 5,
                Opacity = i == _page ? 0.85 : 0.35,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Fg");
            // 점이 작아서 누르기 쉽도록 투명한 여백을 준다.
            var hit = new Border { Background = Brushes.Transparent, Width = 14, Height = 14, Child = dot, Cursor = Cursors.Hand };
            hit.MouseLeftButtonUp += (_, e) => { GoToPage(index); e.Handled = true; };
            Dots.Children.Add(hit);
        }
    }

    private void GoToPage(int page)
    {
        page = Math.Max(0, Math.Min(page, _pages - 1));
        if (page == _page) return;
        _page = page;
        BuildDots();
        AnimateScrollTo(_page * PageH);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_settings) return; // 설정 화면은 스크롤 뷰어가 직접 굴린다.
        e.Handled = true;
        if (_pages <= 1) return;
        // 터치패드는 작은 값이 여러 번 오므로 한 칸(120)이 모일 때마다 한 페이지씩 넘긴다.
        _wheelAcc += e.Delta;
        if (Math.Abs(_wheelAcc) < 120) return;
        int dir = _wheelAcc > 0 ? -1 : 1;
        _wheelAcc = 0;
        GoToPage(_page + dir);
    }

    private void AnimateScrollTo(double y)
    {
        _animFrom = Scroller.VerticalOffset;
        _animTo = y;
        _animStart = DateTime.Now;
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnScrollFrame;
    }

    private void OnScrollFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, (DateTime.Now - _animStart).TotalMilliseconds / 280);
        double k = 1 - Math.Pow(1 - t, 3);
        Scroller.ScrollToVerticalOffset(_animFrom + (_animTo - _animFrom) * k);
        if (t >= 1) StopScrollAnimation();
    }

    private void StopScrollAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnScrollFrame;
    }

    private Border MakeTile(ShellEntry entry)
    {
        var img = new Image { Source = entry.Icon, Width = 40, Height = 40, Margin = new Thickness(0, 14, 0, 0) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var text = new TextBlock
        {
            Text = entry.Name,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxHeight = 38,
            Margin = new Thickness(6, 8, 6, 0),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        var panel = new StackPanel();
        panel.Children.Add(img);
        panel.Children.Add(text);

        var b = new Border
        {
            Width = TileW - 4,
            Height = TileH - 4,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Tag = entry,
            Child = panel,
            ContextMenu = BuildItemMenu(entry),
        };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.ContextMenu.Opened += (_, _) => _busy = true;
        b.ContextMenu.Closed += (_, _) => { _busy = false; if (!IsActive) SafeClose(); else Keyboard.Focus(this); };
        return b;
    }

    private ContextMenu BuildItemMenu(ShellEntry entry)
    {
        var m = new ContextMenu();
        m.Items.Add(Item("열기", () => { FileOps.Launch(entry.Path); SafeClose(); }));
        m.Items.Add(Item("관리자 권한으로 실행", () => { FileOps.Launch(entry.Path, admin: true); SafeClose(); }));
        m.Items.Add(Item("파일 위치 열기", () => FileOps.Reveal(entry.Path)));
        m.Items.Add(new Separator());

        var move = new MenuItem { Header = "다른 그룹으로 이동" };
        foreach (var g in _mgr.Groups.Where(g => g != Group).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var target = g;
            move.Items.Add(Item(g.Name, () => FileOps.MoveTo(entry.Path, target.Folder)));
        }
        move.IsEnabled = move.Items.Count > 0;
        m.Items.Add(move);
        m.Items.Add(Item("바탕화면으로 꺼내기", () => FileOps.MoveTo(entry.Path, FileOps.UserDesktop)));
        m.Items.Add(Item("휴지통으로 이동", () => FileOps.Recycle(entry.Path)));
        return m;
    }

    private static MenuItem Item(string header, Action act)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => act();
        return mi;
    }

    // ----- 설정 화면 -----

    private void ShowSettings(bool on)
    {
        _settings = on;
        Scroller.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        SettingsScroller.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TabBar.Visibility = SettingsScroller.Visibility;
        GearGlyph.SetResourceReference(TextBlock.ForegroundProperty, on ? "Accent" : "Fg");
        if (on) BuildSettings();
        Rebuild();
        KeepOnScreen();
    }

    private static readonly string[] Tabs = { "카드", "그룹", "전체" };
    private static int _tab; // 마지막으로 본 탭을 기억한다.

    private void BuildSettings()
    {
        BuildTabs();
        SettingsPanel.Children.Clear();
        _section = null;

        switch (_tab)
        {
            case 0:
                AddSetting("", "위치 옮기기 · 크기 조절", null, () => { SafeClose(); _card.BeginEdit(); });
                AddPercent("", "크기");
                var layout = _card.CurrentLayout;
                AddStepper("", "미리보기 칸 (가로)", layout.Cols, v => _card.SetGrid(v, _card.CurrentLayout.Rows));
                AddStepper("", "미리보기 칸 (세로)", layout.Rows, v => _card.SetGrid(_card.CurrentLayout.Cols, v));
                break;
            case 1:
                AddSetting("", "이름 바꾸기", null, () => { ShowSettings(false); TitleBox.Focus(); });
                AddSetting("", "폴더 열기", null, () => { FileOps.OpenFolder(Group.Folder); SafeClose(); });
                AddSetting("", "새 그룹 만들기", null, () => { SafeClose(); _mgr.NewGroup(); });
                AddSetting("", "이 그룹 삭제", null, () =>
                {
                    _busy = true; // 확인 창이 떠도 펼침 창이 닫히지 않게
                    _mgr.DeleteGroup(_card);
                    _busy = false;
                    SafeClose();
                });
                break;
            default:
                AddSetting("", "Windows 배율 따라가기", _mgr.FollowWindowsScale ? "켬" : "끔", () =>
                {
                    _mgr.FollowWindowsScale = !_mgr.FollowWindowsScale;
                    BuildSettings();
                });
                break;
        }
    }

    /// <summary>설정 화면 위쪽 메뉴명. 누르면 그 메뉴의 설정만 보여 준다.</summary>
    private void BuildTabs()
    {
        TabBar.Children.Clear();
        for (int i = 0; i < Tabs.Length; i++)
        {
            int index = i;
            bool on = i == _tab;
            var text = new TextBlock
            {
                Text = Tabs[i],
                FontSize = 14,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, on ? "Fg" : "SubFg");
            var bar = new Border
            {
                Width = 16,
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(0, 5, 0, 0),
                Visibility = on ? Visibility.Visible : Visibility.Hidden,
            };
            bar.SetResourceReference(Border.BackgroundProperty, "Accent");
            var stack = new StackPanel { Margin = new Thickness(12, 7, 12, 3) };
            stack.Children.Add(text);
            stack.Children.Add(bar);

            var tab = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = stack,
            };
            tab.MouseEnter += (_, _) => tab.SetResourceReference(Border.BackgroundProperty, "HoverBg");
            tab.MouseLeave += (_, _) => tab.Background = Brushes.Transparent;
            tab.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (index == _tab) return;
                _tab = index;
                BuildSettings();
                SettingsScroller.ScrollToVerticalOffset(0);
                Rebuild();
                KeepOnScreen();
            };
            TabBar.Children.Add(tab);
        }
    }

    private StackPanel? _section;

    /// <summary>둥근 묶음 카드. 설정 줄은 이 안에 들어간다.</summary>
    private void AddSection()
    {
        _section = new StackPanel();
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            Child = _section,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SectionBg");
        card.SetResourceReference(Border.BorderBrushProperty, "SectionLine");
        SettingsPanel.Children.Add(card);
    }

    /// <summary>묶음에 줄을 넣고, 앞 줄과는 얇은 선으로 나눈다.</summary>
    private void AddRow(UIElement row)
    {
        if (_section == null) AddSection();
        if (_section!.Children.Count > 0)
        {
            var line = new Border { Height = 1, Margin = new Thickness(40, 0, 8, 0) };
            line.SetResourceReference(Border.BackgroundProperty, "SectionLine");
            _section.Children.Add(line);
        }
        _section.Children.Add(row);
    }

    private void AddSetting(string glyph, string text, string? trailing, Action act)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var label = new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        Grid.SetColumn(label, 1);
        grid.Children.Add(icon);
        grid.Children.Add(label);
        if (trailing != null)
        {
            var tail = new TextBlock { Text = trailing, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            tail.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
            Grid.SetColumn(tail, 2);
            grid.Children.Add(tail);
        }

        var row = new Border
        {
            Height = SettingRowH - 4,
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid,
        };
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
        AddRow(row);
    }

    /// <summary>카드 크기(%) 줄. [−]/[+]는 5%씩, 숫자 칸을 눌러 직접 입력할 수도 있다.</summary>
    private void AddPercent(string glyph, string text)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var label = new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        Grid.SetColumn(label, 1);

        var box = new TextBox
        {
            Text = _card.SizePercent.ToString(),
            FontSize = 14,
            Width = 44,
            MaxLength = 3,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 2, 1, 2),
        };
        box.SetResourceReference(TextBox.ForegroundProperty, "Fg");
        box.SetResourceReference(TextBox.CaretBrushProperty, "Fg");
        box.SetResourceReference(TextBox.BorderBrushProperty, "SectionLine");
        var unit = new TextBlock { Text = "%", FontSize = 14, Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");

        void Apply(int percent) => box.Text = _card.SetSizePercent(Math.Clamp(percent, 10, 999)).ToString();
        void Commit()
        {
            if (int.TryParse(box.Text.Trim().TrimEnd('%'), out int v)) Apply(v);
            else box.Text = _card.SizePercent.ToString();
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

        Button Step(string g, int dir)
        {
            var b = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = new TextBlock { Text = g, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 },
            };
            b.Click += (_, _) =>
            {
                // 5% 단위로 맞춰 가며 움직인다(103 → 105, 103 → 100).
                int cur = _card.SizePercent;
                int next = dir > 0 ? (cur / 5 + 1) * 5 : (cur % 5 == 0 ? cur - 5 : cur / 5 * 5);
                Apply(next);
            };
            return b;
        }

        var stepper = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 0) };
        stepper.Children.Add(Step("", -1)); // −
        stepper.Children.Add(box);
        stepper.Children.Add(unit);
        stepper.Children.Add(Step("", +1)); // +
        Grid.SetColumn(stepper, 2);

        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(stepper);
        AddRow(new Border { Height = SettingRowH - 4, Margin = new Thickness(0, 2, 0, 2), Child = grid });
    }

    /// <summary>[−] 값 [+] 로 1~8을 고르는 설정 줄.</summary>
    private void AddStepper(string glyph, string text, int value, Action<int> set)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var label = new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        Grid.SetColumn(label, 1);

        var num = new TextBlock
        {
            Text = value.ToString(),
            FontSize = 14,
            Width = 28,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        num.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        int current = value;
        Button Step(string g, int delta)
        {
            var b = new Button
            {
                Style = (Style)FindResource("IconButton"),
                Content = new TextBlock { Text = g, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11 },
            };
            b.Click += (_, _) =>
            {
                int next = Math.Clamp(current + delta, CardLayout.MinCells, CardLayout.MaxCells);
                if (next == current) return;
                current = next;
                num.Text = current.ToString();
                set(current);
            };
            return b;
        }

        var stepper = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 0) };
        stepper.Children.Add(Step("", -1)); // −
        stepper.Children.Add(num);
        stepper.Children.Add(Step("", +1)); // +
        Grid.SetColumn(stepper, 2);

        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(stepper);
        AddRow(new Border { Height = SettingRowH - 4, Margin = new Thickness(0, 2, 0, 2), Child = grid });
    }

    /// <summary>설정 화면으로 바뀌며 창이 길어졌을 때 화면 아래로 넘치지 않게 올린다.</summary>
    private void KeepOnScreen()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var wa = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
        double waT = wa.Top / dpi.DpiScaleY, waB = wa.Bottom / dpi.DpiScaleY;
        Top = Math.Max(waT + 8, Math.Min(Top, waB - Height - 8));
    }

    private void PlaceNearCard()
    {
        // 카드 가운데를 기준으로 펼치고, 모니터 작업 영역 안으로 맞춘다.
        var dpi = VisualTreeHelper.GetDpi(_card);
        var hwnd = new WindowInteropHelper(_card).Handle;
        var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
        double waL = wa.Left / dpi.DpiScaleX, waT = wa.Top / dpi.DpiScaleY;
        double waR = wa.Right / dpi.DpiScaleX, waB = wa.Bottom / dpi.DpiScaleY;

        double cx = _card.Left + _card.Width / 2;
        double cy = _card.Top + 8 + 88;
        double left = cx - Width / 2, top = cy - Height / 2;
        Left = Math.Max(waL + 8, Math.Min(left, waR - Width - 8));
        Top = Math.Max(waT + 8, Math.Min(top, waB - Height - 8));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // 시스템 메뉴를 빼서 캡션의 × 버튼이 그려지지 않게 한다.
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(style & ~Native.WS_SYSMENU));
        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        src?.AddHook(WndProc);
        Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsLight ? 0 : 1);
        Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2); // 둥근 모서리
        Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 3);      // 아크릴
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, VK_ESCAPE = 0x1B;
        if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && wParam.ToInt32() == VK_ESCAPE && !_busy)
        {
            if (TitleBox.IsKeyboardFocused) TitleBox.Text = Group.Name;
            SafeClose();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(180);
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = ease });

        if (_editTitle)
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        }
        else
        {
            FocusManager.SetFocusedElement(this, this);
            Keyboard.Focus(this);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!TitleBox.IsKeyboardFocused && !_settings)
        {
            int dir = e.Key switch { Key.Down or Key.PageDown => 1, Key.Up or Key.PageUp => -1, _ => 0 };
            if (dir != 0) { GoToPage(_page + dir); e.Handled = true; return; }
        }
        if (e.Key == Key.Escape)
        {
            if (TitleBox.IsKeyboardFocused) TitleBox.Text = Group.Name;
            SafeClose();
            e.Handled = true;
        }
    }

    private void CommitTitle()
    {
        string name = TitleBox.Text.Trim();
        if (name.Length == 0 || name == Group.Name) { TitleBox.Text = Group.Name; return; }
        if (!_mgr.RenameGroup(_card, name)) TitleBox.Text = Group.Name;
    }

    // ----- 항목 클릭/끌기 -----

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _downPos = e.GetPosition(this);
        _downEntry = FindEntry(e.OriginalSource as DependencyObject);
        _pending = _downEntry != null;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pending || e.LeftButton != MouseButtonState.Pressed || _downEntry == null) return;
        var d = e.GetPosition(this) - _downPos;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pending = false;
        _busy = true;
        FileOps.DragOut(this, _downEntry.Path);
        _busy = false;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pending || _downEntry == null) return;
        _pending = false;
        FileOps.Launch(_downEntry.Path);
        SafeClose();
    }

    private ShellEntry? FindEntry(DependencyObject? d)
    {
        while (d != null && d != ItemsPanel)
        {
            if (d is Border { Tag: ShellEntry entry }) return entry;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = FileOps.CanAccept(e.Data, Group.Folder) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            FileOps.AddToGroup(paths, Group.Folder, _mgr.Root);
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        Group.Changed -= Rebuild;
        StopScrollAnimation();
        if (_current == this) _current = null;
        base.OnClosed(e);
    }
}
