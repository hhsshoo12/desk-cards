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
using System.Windows.Threading;

namespace DeskCards.Expanded;

/// <summary>
/// 카드를 눌렀을 때 펼쳐지는 전체 목록 창(아크릴 배경). WPF 시절의 ExpandedWindow를 그대로 옮긴 것이고,
/// 앱(카드·그룹 관리자·파일 작업)에 닿는 부분만 IExpandedHost 호출로 바꿨다.
/// </summary>
internal partial class TileWindow : Window
{
    /// <summary>WPF 라이브러리를 미리 올려 두는 용도.</summary>
    internal sealed class Probe
    {
        public Probe() { _ = new Window { Content = new Grid { Children = { new TextBox(), new ScrollViewer { Content = new WrapPanel() } } } }; }
    }

    private const int Columns = 4;
    private const int RowsPerPage = 3;
    private const double TileW = 96, TileH = 104;
    private const double PageH = RowsPerPage * TileH;
    private const double SidePad = 26, TitleH = 80, BottomPad = 28;

    private readonly IExpandedHost _host;
    private readonly ExpandedOptions _options;
    private IReadOnlyList<TileData> _data;
    private string _name, _folder;
    private DispatcherTimer? _leaveTimer;
    private bool _entered;
    private Point _downPos;
    private TileData? _downEntry;
    private bool _pending, _busy, _closing, _committing;

    // 페이지 스크롤 상태
    private int _page, _pages = 1, _wheelAcc;
    private double _animFrom, _animTo;
    private DateTime _animStart;
    private bool _animating;

    public TileWindow(IExpandedHost host, ExpandedOptions options, string title, string folder, IReadOnlyList<TileData> items)
    {
        _host = host;
        _options = options;
        _data = items;
        _name = title;
        _folder = folder;
        InitializeComponent();
        var p = options.Palette;
        Resources["Fg"] = Frozen(p.Fg);
        Resources["SubFg"] = Frozen(p.SubFg);
        Resources["HoverBg"] = Frozen(p.HoverBg);
        Resources["PopupBg"] = Frozen(p.PopupBg);
        Focusable = true;
        Topmost = options.Topmost;
        Width = SidePad * 2 + Columns * TileW;

        Rebuild();
        PlaceNearCard();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += (_, _) => { if (!_busy) SafeClose(); };
        PreviewKeyDown += OnKey;
        PreviewMouseWheel += OnWheel;
        GearButton.Click += (_, _) => { _host.OpenSettings(); SafeClose(); };
        ItemsPanel.PreviewMouseLeftButtonDown += OnDown;
        ItemsPanel.PreviewMouseMove += OnMove;
        ItemsPanel.PreviewMouseLeftButtonUp += OnUp;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        Closed += (_, _) => OnClosedCore();

        TitleBox.Text = _name;
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

    private static Brush Frozen(string argb)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
        b.Freeze();
        return b;
    }

    /// <summary>앱 쪽 내용이 바뀌었다(항목 추가·삭제·이름·아이콘).</summary>
    public void SetContent(string title, string folder, IReadOnlyList<TileData> items)
    {
        _data = items;
        _name = title;
        _folder = folder;
        if (_closing || _dragEntry != null) return; // 끄는 중에는 끝난 뒤에 맞춘다
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
        if (!TitleBox.IsKeyboardFocusWithin) TitleBox.Text = _name;
        ItemsPanel.Children.Clear();
        foreach (var entry in _data)
            ItemsPanel.Children.Add(MakeTile(entry));
        Empty.Visibility = _data.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 한 번에 4×3까지 보이고, 넘치면 3줄 단위 페이지로 위아래 스크롤한다.
        int rows = Math.Max(1, (int)Math.Ceiling(_data.Count / (double)Columns));
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
        double ms = SystemParameters.ClientAreaAnimation ? 333 : 0;
        double t = ms <= 0 ? 1 : Math.Min(1, (DateTime.Now - _animStart).TotalMilliseconds / ms);
        double k = Decel(t);
        Scroller.ScrollToVerticalOffset(_animFrom + (_animTo - _animFrom) * k);
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
        return 3 * (1 - t) * t * t + t * t * t; // y1 = 0, y2 = 1
    }

    /// <summary>들어오는 애니메이션: from에서 to로 ms 동안 감속 곡선(0,0,0,1).</summary>
    private static DoubleAnimationUsingKeyFrames In(double from, double to, double ms)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new KeySpline(0, 0, 0, 1)));
        return a;
    }

    private Border MakeTile(TileData entry)
    {
        var img = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 14, 0, 0) };
        if (entry.Icon is { Length: > 0 } px && entry.IconWidth > 0 && entry.IconHeight > 0 && px.Length >= entry.IconWidth * entry.IconHeight * 4)
        {
            var bmp = BitmapSource.Create(entry.IconWidth, entry.IconHeight, 96, 96, PixelFormats.Pbgra32, null, px, entry.IconWidth * 4);
            bmp.Freeze();
            img.Source = bmp;
        }
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
    private void ShowItemMenu(TileData entry)
    {
        _busy = true;
        _host.ItemMenu(entry.Path); // 닫히면 앱이 MenuClosed()를 불러 준다
    }

    private void PlaceNearCard()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return;
        var (x, y) = _host.Place(r.Right - r.Left, r.Bottom - r.Top);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, new IntPtr(style & ~0x00080000L)); // WS_SYSMENU 제거
        // Hwnd.ApplyFluent(this, Backdrop.Acrylic)와 같다: WPF 바탕을 비우고 테마 틀·둥근 모서리·아크릴.
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        SetDwm(hwnd, 20 /* USE_IMMERSIVE_DARK_MODE */, _options.Palette.Light ? 0 : 1);
        SetDwm(hwnd, 33 /* WINDOW_CORNER_PREFERENCE */, 2);
        SetDwm(hwnd, 38 /* SYSTEMBACKDROP_TYPE */, 3);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        PlaceNearCard();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, VK_ESCAPE = 0x1B;
        if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && wParam.ToInt32() == VK_ESCAPE && !_busy)
        {
            if (TitleBox.IsKeyboardFocused) TitleBox.Text = _name;
            SafeClose();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (SystemParameters.ClientAreaAnimation)
        {
            Body.BeginAnimation(OpacityProperty, In(0, 1, 167));
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, In(0.94, 1, 250));
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, In(0.94, 1, 250));
        }

        if (_options.Hover)
        {
            _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _leaveTimer.Tick += (_, _) => CheckLeave();
            _leaveTimer.Start();
        }

        if (_options.EditTitle)
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
        if (!GetCursorPos(out var pt)) return;
        if (Inside(new WindowInteropHelper(this).Handle, pt)) { _entered = true; return; }
        if (!_entered && Inside(_options.Reference, pt)) return;
        SafeClose();
    }

    private static bool Inside(IntPtr hwnd, POINT pt) =>
        hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r) &&
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
            if (TitleBox.IsKeyboardFocused) TitleBox.Text = _name;
            SafeClose();
            e.Handled = true;
        }
    }

    private void CommitTitle()
    {
        if (_committing) return;
        string name = TitleBox.Text.Trim();
        if (name.Length == 0 || name == _name) { TitleBox.Text = _name; return; }
        bool wasBusy = _busy;
        _committing = _busy = true;
        try { if (!_host.Rename(name)) TitleBox.Text = _name; }
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
        try { DragDrop.DoDragDrop(this, new DataObject(DataFormats.FileDrop, new[] { _downEntry.Path }), DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link); }
        catch { }
        _dragEntry = null;
        _busy = false;
        if (_dropped && _dragMoved)
            _host.SetOrder(ItemsPanel.Children.OfType<Border>().Select(b => b.Tag).OfType<TileData>().Select(t => t.Path).ToList());
        else Rebuild(); // 미리 본 순서를 되돌리고, 끄는 동안 밀린 앱 쪽 변경도 반영
        if (!IsActive) SafeClose();
    }

    // ----- 순서 바꾸기 -----
    // 끄는 동안 타일이 마우스 아래 칸으로 바로 옮겨 가서 놓았을 때의 모습을 미리 보여 준다.

    private TileData? _dragEntry;
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
        var tile = ItemsPanel.Children.OfType<Border>().FirstOrDefault(b => ReferenceEquals(b.Tag, _dragEntry));
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
        _host.Launch(_downEntry.Path);
        SafeClose();
    }

    private TileData? FindEntry(DependencyObject? d)
    {
        while (d != null && d != ItemsPanel)
        {
            if (d is Border { Tag: TileData entry }) return entry;
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
        // 이미 이 그룹 폴더에 있는 것만 끌어 왔다면 받지 않는다(자세한 판단은 앱이 놓을 때 한다).
        bool ok = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths &&
            paths.Any(p => !string.Equals(System.IO.Path.GetDirectoryName(p), _folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
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
            _host.AddToGroup(paths);
        e.Effects = DragDropEffects.None;
        e.Handled = true;
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
