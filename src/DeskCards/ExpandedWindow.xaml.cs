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
    private readonly bool _hover;
    private readonly Func<Size, Point>? _place;
    private readonly Window? _anchor;
    private DispatcherTimer? _leaveTimer;
    private bool _entered;
    private Point _downPos;
    private ShellEntry? _downEntry;
    private bool _pending, _busy, _closing, _committing;

    // 페이지 스크롤 상태
    private int _page, _pages = 1, _wheelAcc;
    private double _animFrom, _animTo;
    private DateTime _animStart;
    private bool _animating;

    private ExpandedWindow(CardWindow card, GroupManager mgr, bool editTitle, bool hover, Func<Size, Point>? place, Window? anchor)
    {
        _place = place;
        _anchor = anchor;
        InitializeComponent();
        Focusable = true;
        _card = card;
        _mgr = mgr;
        _editTitle = editTitle;
        _hover = hover;
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
        DragLeave += OnDragLeave;
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

    /// <param name="hover">더보기 칸에 올려 두어서 연 경우. 마우스가 창 밖으로 나가면 바로 닫힌다.</param>
    /// <param name="place">창 크기(DIP)를 받아 띄울 자리(DIP)를 정한다. 없으면 카드 가운데에 펼친다.</param>
    /// <param name="anchor">바탕화면 카드 대신 이 창(카드 바)에서 열었을 때. 그 창보다 위에 뜬다.</param>
    public static void Open(CardWindow card, GroupManager mgr, bool editTitle = false, bool hover = false,
        Func<Size, Point>? place = null, Window? anchor = null)
    {
        _current?.SafeClose();
        var w = new ExpandedWindow(card, mgr, editTitle, hover, place, anchor);
        _current = w;
        // 편집 막대의 이름 바꾸기는 어두운 막 위에, 카드 바에서 열면 바 위에 뜬다.
        w.Topmost = mgr.Editing || anchor != null;
        w.Show();
        w.Activate();
    }

    public static bool IsOpen => _current is { _closing: false };

    public static bool IsOpenFor(CardWindow card) => _current is { _closing: false } w && w._card == card;

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
        };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowItemMenu(entry); };
        return b;
    }

    /// <summary>타일 우클릭 메뉴. 떠 있는 동안은 이 창이 비활성이 되어도 닫지 않는다.</summary>
    private void ShowItemMenu(ShellEntry entry)
    {
        _busy = true;
        var menu = Menus.ForEntry(entry, launched: SafeClose);
        menu.Closed += (_, _) =>
        {
            _busy = false;
            if (!IsActive) SafeClose();
            else Keyboard.Focus(this);
        };
        menu.ShowAtCursor();
    }

    private void PlaceNearCard()
    {
        if (_place != null)
        {
            var p = _place(new Size(Width, Height));
            Left = p.X;
            Top = p.Y;
            return;
        }
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

        if (_hover)
        {
            _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _leaveTimer.Tick += (_, _) => CheckLeave();
            _leaveTimer.Start();
        }

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

    /// <summary>
    /// 올려 두어서 연 창: 마우스가 창 안에 한 번 들어온 뒤 밖으로 나가면 닫는다.
    /// 창이 더보기 칸을 덮지 않았을 수도 있으니, 들어오기 전에는 카드 위에 있는 동안도 기다린다.
    /// 메뉴·끌기·이름 입력 중에는 닫지 않는다.
    /// </summary>
    private void CheckLeave()
    {
        if (_closing || _busy || TitleBox.IsKeyboardFocusWithin) return;
        if (!Native.GetCursorPos(out var pt)) return;
        if (Inside(Hwnd.Of(this), pt)) { _entered = true; return; }
        if (!_entered && Inside(Hwnd.Of(_anchor ?? _card), pt)) return;
        SafeClose();
    }

    private static bool Inside(IntPtr hwnd, Native.POINT pt) =>
        hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var r) &&
        pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;

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
        // 창 안에서 놓으면 순서 바꾸기, 창 밖(바탕화면·탐색기·다른 카드)에 놓으면 꺼내기.
        _dragEntry = _downEntry;
        _dragMoved = _dropped = false;
        FileOps.DragOut(this, _downEntry.Path);
        _dragEntry = null;
        _busy = false;
        if (_dropped && _dragMoved)
            _mgr.SetOrder(Group, ItemsPanel.Children.OfType<Border>().Select(b => b.Tag).OfType<ShellEntry>());
        else if (_dragMoved) Rebuild();
        if (!IsActive) SafeClose();
    }

    // ----- 순서 바꾸기 -----
    // 끄는 동안 타일이 마우스 아래 칸으로 바로 옮겨 가서 놓았을 때의 모습을 미리 보여 준다.

    private ShellEntry? _dragEntry;
    private bool _dragMoved, _dropped;
    private DateTime _lastFlip;

    private bool IsSelfDrag(IDataObject data) =>
        _dragEntry != null && data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths &&
        string.Equals(paths[0], _dragEntry.Path, StringComparison.OrdinalIgnoreCase);

    private void PreviewReorder(DragEventArgs e)
    {
        // 위·아래 끝에 대고 있으면 페이지를 넘긴다(너무 빨리 넘어가지 않게 0.6초에 한 번).
        double y = e.GetPosition(Scroller).Y;
        if ((y < 18 || y > Scroller.ActualHeight - 18) && DateTime.Now - _lastFlip > TimeSpan.FromMilliseconds(600))
        {
            int before = _page;
            GoToPage(_page + (y < 18 ? -1 : 1));
            if (_page != before) _lastFlip = DateTime.Now;
        }

        var p = e.GetPosition(ItemsPanel);
        int count = ItemsPanel.Children.Count;
        int col = Math.Clamp((int)(p.X / TileW), 0, Columns - 1);
        int row = Math.Max(0, (int)(p.Y / TileH));
        int target = Math.Min(row * Columns + col, count - 1);
        var tile = ItemsPanel.Children.OfType<Border>().FirstOrDefault(b => b.Tag == _dragEntry);
        if (tile == null) return;
        int current = ItemsPanel.Children.IndexOf(tile);
        if (target == current) return;
        ItemsPanel.Children.RemoveAt(current);
        ItemsPanel.Children.Insert(target, tile);
        _dragMoved = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // 창 밖으로 나가면 미리 보기를 되돌린다(자식 요소 사이를 지날 때도 이 이벤트가 오므로 위치로 판단).
        if (_dragEntry == null || !_dragMoved) return;
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X < ActualWidth && p.Y < ActualHeight) return;
        _dragMoved = false;
        Rebuild();
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
        if (IsSelfDrag(e.Data))
        {
            e.Effects = DragDropEffects.Move;
            PreviewReorder(e);
            e.Handled = true;
            return;
        }
        e.Effects = FileOps.CanAccept(e.Data, Group.Folder) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (IsSelfDrag(e.Data))
        {
            _dropped = true;
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
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
        _leaveTimer?.Stop();
        StopScrollAnimation();
        if (_current == this) _current = null;
        base.OnClosed(e);
    }
}
