using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace DeskCards.Expanded;

/// <summary>
/// 카드를 눌렀을 때 펼쳐지는 전체 목록 창(아크릴 배경). 보통 WPF 창이라 Windows가 여는 효과를 그대로 입혀 준다.
/// 항목 칸·페이지(위아래 3줄 단위)·꺼내기·순서 바꾸기·끌어 넣기·이름 바꾸기를 한다.
/// </summary>
internal sealed class TileWindow : Window
{
    /// <summary>WPF 라이브러리를 미리 올려 두는 용도.</summary>
    internal sealed class Probe
    {
        public Probe()
        {
            _ = new Window { Content = new Grid { Children = { new TextBox(), new ScrollViewer { Content = new WrapPanel() } } } };
        }
    }

    private const int Columns = 4, RowsPerPage = 3;
    private const double TileW = 96, TileH = 104, PageH = RowsPerPage * TileH;
    private const double SidePad = 26, TitleH = 80, BottomPad = 28;

    private readonly IExpandedHost _host;
    private readonly ExpandedOptions _options;
    private readonly Brush _fg, _subFg, _hoverBg, _popupBg;
    private readonly TextBox _title = new();
    private readonly ScrollViewer _scroller = new();
    private readonly WrapPanel _items = new();
    private readonly StackPanel _dots = new();
    private readonly TextBlock _empty = new();
    private readonly Grid _body = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private DispatcherTimer? _leaveTimer;
    private IReadOnlyList<TileData> _data;
    private string _name, _folder;
    private bool _entered, _busy, _closing, _committing;

    // 페이지
    private int _page, _pages = 1, _wheelAcc;
    private double _animFrom, _animTo;
    private DateTime _animStart;
    private bool _animating;

    // 항목 누름·끌기
    private Point _downPos;
    private TileData? _downEntry;
    private bool _pending;
    private string? _dragPath; // 창 안에서 순서를 바꾸는 중인 항목
    private bool _dragMoved, _dropped;
    private DateTime _lastFlip;

    public TileWindow(IExpandedHost host, ExpandedOptions options, string title, string folder, IReadOnlyList<TileData> items)
    {
        _host = host;
        _options = options;
        _data = items;
        _name = title;
        _folder = folder;
        _fg = Frozen(options.Palette.Fg);
        _subFg = Frozen(options.Palette.SubFg);
        _hoverBg = Frozen(options.Palette.HoverBg);
        _popupBg = Frozen(options.Palette.PopupBg);

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        AllowDrop = true;
        UseLayoutRounding = true;
        Title = "Desk Cards";
        Topmost = options.Topmost;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -20000; // 자리가 정해지기 전에 보이지 않게 화면 밖에 둔다.
        Width = SidePad * 2 + Columns * TileW;
        Height = TitleH + TileH + BottomPad;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        BuildBody();
        Rebuild();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += (_, _) => { if (!_busy) SafeClose(); };
        PreviewKeyDown += OnKey;
        PreviewMouseWheel += OnWheel;
        DpiChanged += (_, _) => PlaceNow();
        Closed += (_, _) => OnClosedCore();
    }

    private static Brush Frozen(string argb)
    {
        var c = (Color)ColorConverter.ConvertFromString(argb);
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ----- 만들기 -----

    private void BuildBody()
    {
        var root = new Border { Background = _popupBg, Child = _body };
        Content = root;
        _body.RenderTransformOrigin = new Point(0.5, 0.5);
        _body.RenderTransform = _scale;
        _body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleH) });
        _body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _title.FontSize = 22;
        _title.Background = Brushes.Transparent;
        _title.BorderThickness = new Thickness(0);
        _title.Foreground = _fg;
        _title.CaretBrush = _fg;
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.TextAlignment = TextAlignment.Center;
        _title.MinWidth = 120;
        _title.MaxWidth = 300;
        _title.Margin = new Thickness(0, 12, 0, 0);
        _title.Padding = new Thickness(6, 2, 6, 2);
        _title.MaxLength = 80;
        _title.Text = _name;
        _title.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTitle(); Keyboard.Focus(this); e.Handled = true; }
        };
        _title.LostKeyboardFocus += (_, _) => CommitTitle();
        // 제목을 처음 누르면 전체 선택해서 바로 새 이름을 칠 수 있게 한다.
        _title.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_title.IsKeyboardFocusWithin) return;
            _title.Focus();
            e.Handled = true;
        };
        _title.GotKeyboardFocus += (_, _) => _title.SelectAll();

        var gearGlyph = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = _fg,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var gear = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "설정",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 14, 0),
            Child = gearGlyph,
        };
        gear.MouseEnter += (_, _) => gear.Background = _hoverBg;
        gear.MouseLeave += (_, _) => gear.Background = Brushes.Transparent;
        gear.MouseLeftButtonUp += (_, e) => { e.Handled = true; _host.OpenSettings(); SafeClose(); };

        _scroller.Margin = new Thickness(SidePad, 0, SidePad, BottomPad);
        _scroller.VerticalAlignment = VerticalAlignment.Top;
        _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroller.CanContentScroll = false;
        _scroller.Focusable = false;
        _scroller.Content = _items;
        _items.VerticalAlignment = VerticalAlignment.Top;
        _items.PreviewMouseLeftButtonDown += OnDown;
        _items.PreviewMouseMove += OnMove;
        _items.PreviewMouseLeftButtonUp += OnUp;
        Grid.SetRow(_scroller, 1);

        _dots.HorizontalAlignment = HorizontalAlignment.Right;
        _dots.VerticalAlignment = VerticalAlignment.Center;
        _dots.Margin = new Thickness(0, 0, 7, BottomPad);
        Grid.SetRow(_dots, 1);

        _empty.Text = "바로가기나 파일을 여기로 끌어다 놓으세요";
        _empty.Foreground = _subFg;
        _empty.FontSize = 13;
        _empty.Margin = new Thickness(0, 0, 0, BottomPad);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Visibility = Visibility.Collapsed;
        Grid.SetRow(_empty, 1);

        foreach (UIElement e in new UIElement[] { _title, gear, _scroller, _dots, _empty }) _body.Children.Add(e);

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
    }

    /// <summary>앱 쪽 내용이 바뀌었다(항목 추가·삭제·이름·아이콘).</summary>
    public void SetContent(string title, string folder, IReadOnlyList<TileData> items)
    {
        if (_closing) return;
        if (_dragPath != null) { _data = items; _name = title; _folder = folder; return; } // 끄는 중에는 끝난 뒤에 맞춘다
        _data = items;
        _name = title;
        _folder = folder;
        Rebuild();
    }

    public void MenuClosed()
    {
        _busy = false;
        if (_closing) return;
        if (!IsActive) SafeClose();
        else Keyboard.Focus(this);
    }

    private void Rebuild()
    {
        if (!_title.IsKeyboardFocusWithin) _title.Text = _name;
        _items.Children.Clear();
        foreach (var d in _data) _items.Children.Add(MakeTile(d));
        _empty.Visibility = _data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 한 번에 4×3까지 보이고, 넘치면 3줄 단위 페이지로 위아래 스크롤한다.
        int rows = Math.Max(1, (int)Math.Ceiling(_data.Count / (double)Columns));
        _pages = Math.Max(1, (int)Math.Ceiling(rows / (double)RowsPerPage));
        double viewH = Math.Min(rows, RowsPerPage) * TileH;
        _items.MinHeight = _pages > 1 ? _pages * PageH : 0; // 마지막 페이지도 딱 맞게 멈추도록
        _scroller.Height = viewH;
        Height = TitleH + viewH + BottomPad;

        _page = Math.Min(_page, _pages - 1);
        StopScrollAnimation();
        _scroller.ScrollToVerticalOffset(_page * PageH);
        BuildDots();
        if (IsLoaded) PlaceNow();
    }

    private Border MakeTile(TileData d)
    {
        var img = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 14, 0, 0) };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        if (d.Icon is { Length: > 0 } px && d.IconWidth > 0 && d.IconHeight > 0 && px.Length >= d.IconWidth * d.IconHeight * 4)
        {
            var bmp = BitmapSource.Create(d.IconWidth, d.IconHeight, 96, 96, PixelFormats.Pbgra32, null, px, d.IconWidth * 4);
            bmp.Freeze();
            img.Source = bmp;
        }
        var text = new TextBlock
        {
            Text = d.Name,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxHeight = 38,
            Margin = new Thickness(6, 8, 6, 0),
            Foreground = _fg,
        };
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
            Tag = d,
            Child = panel,
        };
        b.MouseEnter += (_, _) => b.Background = _hoverBg;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            _busy = true; // 메뉴가 떠 있는 동안은 이 창이 비활성이 되어도 닫지 않는다
            _host.ItemMenu(d.Path);
        };
        return b;
    }

    // ----- 페이지 -----

    private void BuildDots()
    {
        _dots.Children.Clear();
        _dots.Visibility = _pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _pages; i++)
        {
            int index = i;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = i == _page ? 6 : 5,
                Height = i == _page ? 6 : 5,
                Opacity = i == _page ? 0.85 : 0.35,
                Fill = _fg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 점이 작아서 누르기 쉽도록 투명한 여백을 준다.
            var hit = new Border { Background = Brushes.Transparent, Width = 14, Height = 14, Child = dot, Cursor = Cursors.Hand };
            hit.MouseLeftButtonUp += (_, e) => { GoToPage(index); e.Handled = true; };
            _dots.Children.Add(hit);
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
        _animFrom = _scroller.VerticalOffset;
        _animTo = y;
        _animStart = DateTime.Now;
        if (!SystemParameters.ClientAreaAnimation) { _scroller.ScrollToVerticalOffset(y); return; }
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnScrollFrame;
    }

    private void OnScrollFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, (DateTime.Now - _animStart).TotalMilliseconds / 333);
        _scroller.ScrollToVerticalOffset(_animFrom + (_animTo - _animFrom) * Decel(t));
        if (t >= 1) StopScrollAnimation();
    }

    private void StopScrollAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnScrollFrame;
    }

    /// <summary>cubic-bezier(0,0,0,1): 툭 나와서 스르륵 멈추는 감속.</summary>
    private static double Decel(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double t = Math.Cbrt(x); // x(t) = t³ (x1 = x2 = 0)
        return 3 * (1 - t) * t * t + t * t * t; // y(t), y1 = 0, y2 = 1
    }

    // ----- 창 -----

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Hwnd;
        // 시스템 메뉴(Alt+Space)는 쓰지 않는다.
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, new IntPtr(style & ~0x00080000L));
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        SetDwm(hwnd, 20 /* USE_IMMERSIVE_DARK_MODE */, _options.Palette.Light ? 0 : 1);
        SetDwm(hwnd, 33 /* WINDOW_CORNER_PREFERENCE */, 2);
        if (Environment.OSVersion.Version.Build >= 22621) SetDwm(hwnd, 38 /* SYSTEMBACKDROP_TYPE */, 3); // 아크릴
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        PlaceNow();
    }

    /// <summary>앱이 정해 주는 자리로 옮긴다(크기가 바뀌었을 때도).</summary>
    private void PlaceNow()
    {
        var hwnd = Hwnd;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        var (x, y) = _host.Place(r.Right - r.Left, r.Bottom - r.Top);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, VK_ESCAPE = 0x1B;
        if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && wParam.ToInt32() == VK_ESCAPE && !_busy)
        {
            if (_title.IsKeyboardFocused) _title.Text = _name;
            SafeClose();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceNow();
        if (SystemParameters.ClientAreaAnimation)
        {
            // 창 자체는 Windows가 열어 주고, 안의 내용은 살짝 커지며 나타난다.
            var spline = new KeySpline(0, 0, 0, 1);
            DoubleAnimationUsingKeyFrames Anim(double from, double to, int ms)
            {
                var a = new DoubleAnimationUsingKeyFrames();
                a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), spline));
                return a;
            }
            _body.BeginAnimation(OpacityProperty, Anim(0, 1, 167));
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.94, 1, 250));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.94, 1, 250));
        }

        if (_options.Hover)
        {
            _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _leaveTimer.Tick += (_, _) => CheckLeave();
            _leaveTimer.Start();
        }
        if (_options.EditTitle) { _title.Focus(); _title.SelectAll(); }
        else { FocusManager.SetFocusedElement(this, this); Keyboard.Focus(this); }
    }

    /// <summary>
    /// 올려 두어서 연 창: 마우스가 창 안에 한 번 들어온 뒤 밖으로 나가면 닫는다.
    /// 창이 더보기 칸을 덮지 않았을 수도 있으니, 들어오기 전에는 카드 위에 있는 동안도 기다린다.
    /// 메뉴·끌기·이름 입력 중에는 닫지 않는다.
    /// </summary>
    private void CheckLeave()
    {
        if (_closing || _busy || _title.IsKeyboardFocusWithin || !GetCursorPos(out var pt)) return;
        if (Inside(Hwnd, pt)) { _entered = true; return; }
        if (!_entered && Inside(_options.Reference, pt)) return;
        SafeClose();
    }

    private static bool Inside(IntPtr hwnd, POINT pt) =>
        hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r) && pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_title.IsKeyboardFocused)
        {
            int dir = e.Key switch { Key.Down or Key.PageDown => 1, Key.Up or Key.PageUp => -1, _ => 0 };
            if (dir != 0) { GoToPage(_page + dir); e.Handled = true; return; }
        }
        if (e.Key == Key.Escape && !_busy)
        {
            if (_title.IsKeyboardFocused) _title.Text = _name;
            SafeClose();
            e.Handled = true;
        }
    }

    private void CommitTitle()
    {
        if (_committing) return;
        string name = _title.Text.Trim();
        if (name.Length == 0 || name == _name) { _title.Text = _name; return; }
        bool wasBusy = _busy;
        _committing = _busy = true;
        try { if (!_host.Rename(name)) _title.Text = _name; }
        finally { _committing = false; _busy = wasBusy; }
    }

    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(Close);
    }

    private void OnClosedCore()
    {
        _closing = true;
        _leaveTimer?.Stop();
        StopScrollAnimation();
        _host.Closed();
    }

    // ----- 항목 클릭·끌기 -----

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
        _dragPath = _downEntry.Path;
        _dragMoved = _dropped = false;
        try { DragDrop.DoDragDrop(this, new DataObject(DataFormats.FileDrop, new[] { _downEntry.Path }), DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link); }
        catch { }
        _dragPath = null;
        _busy = false;
        if (_dropped && _dragMoved)
        {
            _host.SetOrder(_items.Children.OfType<Border>().Select(b => b.Tag).OfType<TileData>().Select(t => t.Path).ToList());
            if (!IsActive) SafeClose();
            return;
        }
        Rebuild(); // 끄는 동안 바뀐 앱 쪽 내용과, 미리 본 순서를 원래대로
        if (!IsActive) SafeClose();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pending || _downEntry == null) return;
        _pending = false;
        _host.Launch(_downEntry.Path);
        SafeClose();
    }

    private TileData? FindEntry(DependencyObject? d)
    {
        while (d != null && d != _items)
        {
            if (d is Border { Tag: TileData entry }) return entry;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ----- 끌어 넣기·순서 바꾸기 -----
    // 끄는 동안 칸이 마우스 아래로 바로 옮겨 가서 놓았을 때의 모습을 미리 보여 준다.

    private bool IsSelfDrag(IDataObject data) =>
        _dragPath != null && data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths &&
        string.Equals(paths[0], _dragPath, StringComparison.OrdinalIgnoreCase);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (IsSelfDrag(e.Data))
        {
            e.Effects = DragDropEffects.Move;
            PreviewReorder(e);
            e.Handled = true;
            return;
        }
        // 이미 이 그룹 폴더에 있는 것만 끌어 왔다면 받지 않는다(자세한 판단은 앱이 놓을 때 한다).
        bool ok = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths &&
            paths.Any(p => !string.Equals(System.IO.Path.GetDirectoryName(p), _folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void PreviewReorder(DragEventArgs e)
    {
        // 위·아래 끝에 대고 있으면 페이지를 넘긴다(너무 빨리 넘어가지 않게 0.6초에 한 번).
        double y = e.GetPosition(_scroller).Y;
        if ((y < 18 || y > _scroller.ActualHeight - 18) && DateTime.Now - _lastFlip > TimeSpan.FromMilliseconds(600))
        {
            int before = _page;
            GoToPage(_page + (y < 18 ? -1 : 1));
            if (_page != before) _lastFlip = DateTime.Now;
        }

        var p = e.GetPosition(_items);
        int count = _items.Children.Count;
        int col = Math.Clamp((int)(p.X / TileW), 0, Columns - 1);
        int row = Math.Max(0, (int)(p.Y / TileH));
        int target = Math.Min(row * Columns + col, count - 1);
        var tile = _items.Children.OfType<Border>().FirstOrDefault(b => b.Tag is TileData t && t.Path == _dragPath);
        if (tile == null) return;
        int current = _items.Children.IndexOf(tile);
        if (target == current) return;
        _items.Children.RemoveAt(current);
        _items.Children.Insert(target, tile);
        _dragMoved = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // 창 밖으로 나가면 미리 보기를 되돌린다(자식 요소 사이를 지날 때도 이 이벤트가 오므로 위치로 판단).
        if (_dragPath == null || !_dragMoved) return;
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X < ActualWidth && p.Y < ActualHeight) return;
        _dragMoved = false;
        Rebuild();
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
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _host.AddToGroup(paths);
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    // ----- Win32 -----

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private static void SetDwm(IntPtr hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}
