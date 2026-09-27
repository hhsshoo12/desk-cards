using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DeskCards;

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
    private bool _pending, _busy, _closing, _committing;

    // 페이지 스크롤 상태
    private int _page, _pages = 1, _wheelAcc;
    private double _animFrom, _animTo;
    private DateTime _animStart;
    private bool _animating;

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
        // 이 창이 닫히는 도중에 설정 창을 처음 만들면 설정 창 내용이 그려지지 않고 하얗게 남는다.
        // 그래서 이 창이 완전히 닫힌 다음에 연다.
        GearButton.Click += (_, _) =>
        {
            Closed += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => SettingsWindow.Open(_mgr, _card));
            SafeClose();
        };
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
        w.Topmost = mgr.Editing; // 편집 막대의 이름 바꾸기: 어두운 막 위에 뜬다.
        w.Show();
        w.Activate();
    }

    public static void CloseCurrent() => _current?.SafeClose();

    public static void CloseFor(CardWindow card)
    {
        if (_current != null && _current._card == card) _current.SafeClose();
    }

    private void Rebuild()
    {
        if (!TitleBox.IsKeyboardFocusWithin) TitleBox.Text = Group.Name;
        ItemsPanel.Children.Clear();
        foreach (var entry in Group.Items)
            ItemsPanel.Children.Add(MakeTile(entry));
        Empty.Visibility = Group.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 한 번에 4×3까지 보이고, 넘치면 3줄 단위 페이지로 위아래 스크롤한다.
        int rows = Math.Max(1, (int)Math.Ceiling(Group.Items.Count / (double)Columns));
        _pages = Math.Max(1, (int)Math.Ceiling(rows / (double)RowsPerPage));
        double viewH = Math.Min(rows, RowsPerPage) * TileH;
        ItemsPanel.MinHeight = _pages > 1 ? _pages * PageH : 0; // 마지막 페이지도 딱 맞게 멈추도록
        Scroller.Height = viewH;
        Height = TitleH + viewH + BottomPad;

        _page = Math.Min(_page, _pages - 1);
        StopScrollAnimation();
        Scroller.ScrollToVerticalOffset(_page * PageH);
        BuildDots();
        if (IsLoaded) PlaceNearCard();
    }

    private void BuildDots()
    {
        Dots.Children.Clear();
        Dots.Visibility = _pages > 1 ? Visibility.Visible : Visibility.Collapsed;
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

    private void PlaceNearCard()
    {
        // 카드 가운데를 기준으로 펼치고, 모니터 작업 영역 안으로 맞춘다.
        var dpi = VisualTreeHelper.GetDpi(_card);
        var wa = System.Windows.Forms.Screen.FromHandle(Hwnd.Of(_card)).WorkingArea;
        double waL = wa.Left / dpi.DpiScaleX, waT = wa.Top / dpi.DpiScaleY;
        double waR = wa.Right / dpi.DpiScaleX, waB = wa.Bottom / dpi.DpiScaleY;

        var position = _card.ActualPosition;
        double cx = position.X + _card.Width / 2;
        double cy = position.Y + 8 + 88;
        double left = cx - Width / 2, top = cy - Height / 2;
        Left = Math.Max(waL + 8, Math.Min(left, waR - Width - 8));
        Top = Math.Max(waT + 8, Math.Min(top, waB - Height - 8));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Hwnd.Of(this);
        Hwnd.RemoveSysMenu(hwnd);
        Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
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
        if (!TitleBox.IsKeyboardFocused)
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
        if (_committing || !_mgr.Cards.Contains(_card)) return;
        string name = TitleBox.Text.Trim();
        if (name.Length == 0 || name == Group.Name) { TitleBox.Text = Group.Name; return; }
        bool wasBusy = _busy;
        _committing = _busy = true;
        try { if (!_mgr.RenameGroup(_card, name)) TitleBox.Text = Group.Name; }
        finally { _committing = false; _busy = wasBusy; }
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
        if (!IsActive) SafeClose();
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
