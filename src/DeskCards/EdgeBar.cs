using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskCards;

/// <summary>
/// 카드 바 여는 조건을 지켜본다: 조합키를 누른 채 정한 가장자리에 마우스를 대고 있으면
/// 커서 둘레의 게이지가 12시부터 시계방향으로 차오르고, 한 바퀴가 되는 순간 바가 나온다.
/// 작업 표시줄이 있는 가장자리와 전체 화면 앱 위에서는 열지 않는다.
/// 조합키는 Windows 단축키로 등록해 둬서, 누르는 동안 앞의 앱에 키가 가지 않는다.
/// </summary>
internal static class EdgeBar
{
    private const int HotkeyId = 1, WM_HOTKEY = 0x0312;
    private static HwndSource? _hotkeyWindow;
    private static string _registeredFor = "";
    private static bool _suspended;

    /// <summary>조합키 등록 결과. 카드 바가 꺼져 있으면 null, 다른 곳에서 이미 쓰는 조합이면 false.</summary>
    public static bool? HotkeyRegistered { get; private set; }
    private static GroupManager? _mgr;
    private static DispatcherTimer? _timer;
    private static GaugeOverlay? _gauge;
    private static BarWindow? _bar;
    private static readonly Stopwatch _dwell = new();
    private static bool _armed = true;

    public static void Start(GroupManager mgr)
    {
        _mgr = mgr;
        _hotkeyWindow = new HwndSource(new HwndSourceParameters("DeskCards.Hotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        _hotkeyWindow.AddHook(HotkeyHook);
        ApplyHotkey();
        mgr.Changed += ApplyHotkey;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public static void Stop()
    {
        _timer?.Stop();
        if (_mgr != null) _mgr.Changed -= ApplyHotkey;
        Unregister();
        _hotkeyWindow?.Dispose();
        _hotkeyWindow = null;
        _gauge?.Close();
        _bar?.Close();
        _gauge = null;
        _bar = null;
    }

    // ----- 조합키 등록 -----

    /// <summary>설정(켜기·조합)에 맞게 단축키를 등록하거나 푼다. 바뀐 게 없으면 그대로 둔다.</summary>
    private static void ApplyHotkey()
    {
        if (_mgr == null || _hotkeyWindow == null) return;
        string want = _mgr.BarEnabled && !_suspended ? string.Join(",", _mgr.BarKeys) : "";
        if (want == _registeredFor) return;
        Unregister();
        _registeredFor = want;
        if (want.Length == 0) { HotkeyRegistered = _mgr.BarEnabled ? HotkeyRegistered : null; return; }
        // 보조키만 있는 조합은 등록할 수 없고 그럴 필요도 없다(앱에 아무 일도 일으키지 않는다).
        if (!KeyCombo.NeedsHotkey(_mgr.BarKeys)) { HotkeyRegistered = true; _registeredFor = ""; return; }
        var (mods, key) = KeyCombo.ToHotkey(_mgr.BarKeys);
        const uint MOD_NOREPEAT = 0x4000;
        HotkeyRegistered = RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, mods | MOD_NOREPEAT, key);
    }

    private static void Unregister()
    {
        if (_hotkeyWindow != null && _registeredFor.Length > 0) UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        _registeredFor = "";
    }

    /// <summary>이 조합을 지금 단축키로 등록할 수 있는지(Windows나 다른 앱이 쓰고 있지 않은지) 잠깐 등록해 보고 푼다.</summary>
    public static bool IsHotkeyFree(IEnumerable<int> keys)
    {
        if (_hotkeyWindow == null) return true;
        const int ProbeId = 2;
        var (mods, key) = KeyCombo.ToHotkey(keys);
        if (!RegisterHotKey(_hotkeyWindow.Handle, ProbeId, mods | 0x4000, key)) return false;
        UnregisterHotKey(_hotkeyWindow.Handle, ProbeId);
        return true;
    }

    /// <summary>설정에서 새 조합을 누르는 동안에는 지금 조합을 풀어 둔다(안 그러면 그 키가 설정 창에 오지 않는다).</summary>
    public static void SuspendHotkey(bool on)
    {
        _suspended = on;
        ApplyHotkey();
    }

    private static IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 조합을 누른 순간 빈 키를 한 번 눌러, 나중에 Alt·Win을 뗄 때 앱 메뉴·시작 메뉴가 열리지 않게 한다.
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId && _mgr != null)
        {
            KeyCombo.SuppressRelease(_mgr.BarKeys);
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // ----- 가장자리 지켜보기 -----

    private static void Tick()
    {
        if (_mgr == null || _mgr.IsShuttingDown) return;
        if (_bar != null)
        {
            if (_bar.IsGone) _bar = null;
            else { _bar.CheckLeave(); return; }
        }

        if (!Native.GetCursorPos(out var pt) || !AtEdge(pt, out var screen, out var edge))
        {
            Reset();
            _armed = true; // 한 번 조건에서 벗어나야 다시 열 수 있다(닫히자마자 또 열리지 않게).
            return;
        }
        if (!_armed) return;

        if (!_dwell.IsRunning) _dwell.Restart();
        double progress = _mgr.BarDelay <= 0 ? 1 : _dwell.ElapsedMilliseconds / (double)_mgr.BarDelay;
        if (progress >= 1)
        {
            Reset();
            _armed = false;
            _bar = new BarWindow(_mgr, screen, edge);
            _bar.Open();
            return;
        }
        _gauge ??= new GaugeOverlay();
        _gauge.Update(pt, progress);
        _timer!.Interval = TimeSpan.FromMilliseconds(15); // 게이지가 도는 동안은 부드럽게
    }

    private static void Reset()
    {
        _dwell.Reset();
        _gauge?.Hide();
        if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(40);
    }

    /// <summary>바를 열 조건(설정 켜짐, 조합키, 정한 가장자리, 작업 표시줄 쪽 아님, 전체 화면 아님, 버튼 안 누름)이 맞는지.</summary>
    private static bool AtEdge(Native.POINT pt, out Forms.Screen screen, out ScreenEdge edge)
    {
        screen = Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
        var mgr = _mgr!;
        edge = default;
        // 디스플레이마다 여는 가장자리가 다르다(열지 않는 디스플레이도 있다).
        if (mgr.BarEdgeFor(screen.DeviceName) is not { } chosen) return false;
        edge = chosen;
        if (!mgr.BarEnabled || _suspended || mgr.Editing || !KeyCombo.IsDown(mgr.BarKeys)) return false;
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed) return false;
        if (edge == Native.TaskbarEdgeOn(screen) || ExpandedWindow.IsOpen || FluentMenu.IsOpen) return false;
        var b = screen.Bounds;
        var wa = screen.WorkingArea;
        bool at = edge switch
        {
            ScreenEdge.Left => pt.X <= b.Left && pt.Y >= wa.Top && pt.Y < wa.Bottom,
            ScreenEdge.Right => pt.X >= b.Right - 1 && pt.Y >= wa.Top && pt.Y < wa.Bottom,
            ScreenEdge.Top => pt.Y <= b.Top && pt.X >= wa.Left && pt.X < wa.Right,
            _ => pt.Y >= b.Bottom - 1 && pt.X >= wa.Left && pt.X < wa.Right,
        };
        return at && !Native.IsFullScreenBusy();
    }
}

/// <summary>커서 둘레에 그리는 원형 게이지. 클릭이 통과하는 투명 창이라 커서 자체는 그대로다.</summary>
internal sealed class GaugeOverlay : Window
{
    private const double Size = 44, Radius = 17, Thick = 4;
    private readonly Path _arc = new() { StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };

    public GaugeOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Width = Height = Size;
        Left = Top = -32000;

        var canvas = new Canvas();
        // 어떤 배경 위에서도 보이도록 어두운 테두리를 두른 옅은 트랙 위에 강조색 호를 그린다.
        var shade = new Ellipse { Width = 2 * Radius + Thick + 2, Height = 2 * Radius + Thick + 2, Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), StrokeThickness = Thick + 2 };
        Canvas.SetLeft(shade, Size / 2 - Radius - Thick / 2 - 1);
        Canvas.SetTop(shade, Size / 2 - Radius - Thick / 2 - 1);
        var track = new Ellipse { Width = 2 * Radius, Height = 2 * Radius, Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), StrokeThickness = Thick };
        Canvas.SetLeft(track, Size / 2 - Radius);
        Canvas.SetTop(track, Size / 2 - Radius);
        _arc.SetResourceReference(Shape.StrokeProperty, "Accent");
        canvas.Children.Add(shade);
        canvas.Children.Add(track);
        canvas.Children.Add(_arc);
        Content = canvas;

        SourceInitialized += (_, _) => Hwnd.MakeNoActivateTool(Hwnd.Of(this), 0x20 | 0x80000); // 클릭 통과 + 레이어드
    }

    /// <summary>커서(물리 픽셀) 둘레에 progress(0~1)만큼 찬 게이지를 그린다. 화면 밖으로 잘리지 않게 안쪽으로 당긴다.</summary>
    public void Update(Native.POINT pt, double progress)
    {
        if (!IsVisible) Show();
        double s = Native.MonitorScaleAt(pt.X, pt.Y);
        int px = (int)Math.Round(Size * s);
        var b = Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y)).Bounds;
        int x = Math.Clamp(pt.X - px / 2, b.Left, b.Right - px);
        int y = Math.Clamp(pt.Y - px / 2, b.Top, b.Bottom - px);
        Native.SetWindowPos(Hwnd.Of(this), Hwnd.Topmost, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _arc.Data = Arc(Math.Clamp(progress, 0, 1));
    }

    /// <summary>12시에서 시계방향으로 progress만큼의 호.</summary>
    private static Geometry Arc(double progress)
    {
        var c = new Point(Size / 2, Size / 2);
        if (progress >= 0.999) return new EllipseGeometry(c, Radius, Radius);
        double a = progress * 2 * Math.PI;
        var start = new Point(c.X, c.Y - Radius);
        var end = new Point(c.X + Radius * Math.Sin(a), c.Y - Radius * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new System.Windows.Size(Radius, Radius), 0, progress > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig });
    }
}

/// <summary>
/// 화면 가장자리에서 미끄러져 나오는 카드 줄(아크릴). 바탕화면 카드와 같은 CardView를 바 두께에 맞춰 늘어놓는다.
/// 나올 때와 들어갈 때 모두 처음엔 빠르고 끝으로 갈수록 느려진다. 바 밖으로 마우스가 나가면 들어간다.
/// </summary>
internal sealed class BarWindow : Window
{
    private const double Gap = 8, Pad = 12, Spacing = 12, MaxCardScale = 2.5;
    private const double OpenMs = 280, CloseMs = 220;

    private readonly GroupManager _mgr;
    private readonly ScreenEdge _edge;
    private readonly Forms.Screen _screen;
    private readonly double _scale;
    private readonly System.Drawing.Rectangle _final; // 다 나왔을 때 자리(물리 픽셀)
    private readonly StackPanel _list;
    private readonly ScrollViewer _scroller;
    private readonly Border _panel;
    private IntPtr _previous;
    private double _from, _to, _offset; // 가장자리 바깥으로 밀려난 정도(0 = 다 나옴, 1 = 다 들어감)
    private double _animMs;
    private readonly Stopwatch _anim = new();
    private bool _animating, _closing;

    public BarWindow(GroupManager mgr, Forms.Screen screen, ScreenEdge edge)
    {
        _mgr = mgr;
        _edge = edge;
        _screen = screen;
        var wa = screen.WorkingArea;
        _scale = Native.MonitorScaleAt(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
        int gap = (int)Math.Round(Gap * _scale);
        bool side = edge is ScreenEdge.Left or ScreenEdge.Right;
        int thick = (int)Math.Round((side ? wa.Width : wa.Height) * mgr.BarSize / 100.0);
        _final = edge switch
        {
            ScreenEdge.Left => new(wa.Left + gap, wa.Top + gap, thick, wa.Height - 2 * gap),
            ScreenEdge.Right => new(wa.Right - gap - thick, wa.Top + gap, thick, wa.Height - 2 * gap),
            ScreenEdge.Top => new(wa.Left + gap, wa.Top + gap, wa.Width - 2 * gap, thick),
            _ => new(wa.Left + gap, wa.Bottom - gap - thick, wa.Width - 2 * gap, thick),
        };

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        Width = Height = 1;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        Title = "Desk Cards 카드 바";
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _list = new StackPanel { Orientation = side ? Orientation.Vertical : Orientation.Horizontal, Margin = new Thickness(Pad) };
        _scroller = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = side ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = side ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
            Focusable = false,
        };
        // 위·아래 바는 휠로 옆으로 넘긴다.
        if (!side)
            _scroller.PreviewMouseWheel += (_, e) =>
            {
                _scroller.ScrollToHorizontalOffset(_scroller.HorizontalOffset - e.Delta);
                e.Handled = true;
            };

        // 창은 가장자리에서 잘린 만큼만 보이고, 안의 판은 늘 다 나온 크기다. 먼저 보이는 쪽(가장자리 반대쪽)에 붙인다.
        _panel = new Border
        {
            Width = _final.Width / _scale,
            Height = _final.Height / _scale,
            Child = _scroller,
            HorizontalAlignment = edge == ScreenEdge.Left ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = edge == ScreenEdge.Top ? VerticalAlignment.Bottom : VerticalAlignment.Top,
        };
        var root = new Grid { ClipToBounds = true };
        root.SetResourceReference(Panel.BackgroundProperty, "PopupBg");
        root.Children.Add(_panel);
        Content = root;

        BuildCards();
        _mgr.Changed += OnGroupsChanged;
        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { BeginClose(); e.Handled = true; } };
    }

    /// <summary>다 들어가서 닫혔는지.</summary>
    public bool IsGone { get; private set; }

    public void Open()
    {
        _previous = Native.GetForegroundWindow();
        _offset = 1;
        Show();
        // 활성 창이어야 아크릴이 흐려지지 않고 Esc를 받는다. 다른 앱을 쓰는 중이라 그냥은 앞으로 못 오므로 끌어온다.
        Native.ForceForeground(Hwnd.Of(this));
        Activate();
        Animate(0, OpenMs);
    }

    // ----- 카드 -----

    private void OnGroupsChanged()
    {
        // 그룹이 생기거나 없어졌을 때만 다시 늘어놓는다(항목 변화는 각 CardView가 알아서 그린다).
        var shown = Views().Select(v => v.Group).ToList();
        if (!shown.SequenceEqual(_mgr.Cards.Select(c => c.Group))) Dispatcher.BeginInvoke(BuildCards);
    }

    private void BuildCards()
    {
        foreach (var v in Views().ToList()) v.Detach();
        _list.Children.Clear();
        bool side = _edge is ScreenEdge.Left or ScreenEdge.Right;
        double room = (side ? _final.Width : _final.Height) / _scale - 2 * Pad;
        double cell = _mgr.CellSize;

        foreach (var card in _mgr.Cards)
        {
            var c = card;
            var layout = _mgr.GetLayout(c.Group.Name);
            var view = new CardView(c.Group, _mgr, onDesktop: false);
            view.Apply(layout, cell);
            view.Expand = hover => OpenExpanded(c, view, hover);
            view.CardMenu = () => Menus.ForCard(c, _mgr);

            // 바 두께에 맞춰 카드 전체를 같은 비율로 키우거나 줄인다.
            double k = Math.Min(MaxCardScale, room / (side ? view.Width : view.Height));
            var holder = new Border
            {
                Child = view,
                Tag = view,
                LayoutTransform = new ScaleTransform(k, k),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = side ? new Thickness(0, 0, 0, Spacing) : new Thickness(0, 0, Spacing, 0),
            };
            _list.Children.Add(holder);
        }
        if (_list.Children.Count == 0)
        {
            var empty = new TextBlock { Text = "카드가 없어요", FontSize = 13, Margin = new Thickness(4) };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
            _list.Children.Add(new Border { Child = empty, Tag = null });
        }
    }

    private System.Collections.Generic.IEnumerable<CardView> Views() =>
        _list.Children.OfType<FrameworkElement>().Select(f => f.Tag).OfType<CardView>();

    /// <summary>더보기로 펼친 창은 바 옆(화면 안쪽)에, 누른 카드 높이(또는 가로 위치)에 맞춰 띄운다.</summary>
    private void OpenExpanded(CardWindow card, CardView view, bool hover)
    {
        if (hover && ExpandedWindow.IsOpenFor(card)) return;
        var topLeft = view.PointToScreen(new Point(0, 0));
        var bottomRight = view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight));
        var wa = _screen.WorkingArea;
        double s = _scale, gap = Gap;
        ExpandedWindow.Open(card, _mgr, hover: hover, anchor: this, place: size =>
        {
            double cx = (topLeft.X + bottomRight.X) / 2 / s, cy = (topLeft.Y + bottomRight.Y) / 2 / s;
            double bl = _final.Left / s, bt = _final.Top / s, br = _final.Right / s, bb = _final.Bottom / s;
            double x = _edge switch
            {
                ScreenEdge.Left => br + gap,
                ScreenEdge.Right => bl - gap - size.Width,
                _ => cx - size.Width / 2,
            };
            double y = _edge switch
            {
                ScreenEdge.Top => bb + gap,
                ScreenEdge.Bottom => bt - gap - size.Height,
                _ => cy - size.Height / 2,
            };
            x = Math.Clamp(x, wa.Left / s + gap, Math.Max(wa.Left / s + gap, wa.Right / s - gap - size.Width));
            y = Math.Clamp(y, wa.Top / s + gap, Math.Max(wa.Top / s + gap, wa.Bottom / s - gap - size.Height));
            return new Point(x, y);
        });
    }

    // ----- 나오고 들어가기 -----

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Hwnd.Of(this);
        Hwnd.RemoveSysMenu(hwnd);
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        PlaceAt(_offset);
    }

    /// <summary>
    /// offset(0~1)만큼 가장자리 바깥으로 밀린 자리에 둔다. 모니터 밖으로 나간 부분은 잘라서
    /// 옆 모니터에 비치지 않게 한다(창 크기를 줄이고 안의 판은 제자리).
    /// </summary>
    private void PlaceAt(double offset)
    {
        var f = _final;
        var b = _screen.Bounds;
        int travel = _edge switch
        {
            ScreenEdge.Left => f.Right - b.Left,
            ScreenEdge.Right => b.Right - f.Left,
            ScreenEdge.Top => f.Bottom - b.Top,
            _ => b.Bottom - f.Top,
        };
        int d = (int)Math.Round(travel * offset);
        int x = f.Left, y = f.Top, w = f.Width, h = f.Height;
        switch (_edge)
        {
            case ScreenEdge.Left: x -= d; if (x < b.Left) { w -= b.Left - x; x = b.Left; } break;
            case ScreenEdge.Right: x += d; w = Math.Min(w, b.Right - x); break;
            case ScreenEdge.Top: y -= d; if (y < b.Top) { h -= b.Top - y; y = b.Top; } break;
            default: y += d; h = Math.Min(h, b.Bottom - y); break;
        }
        Native.SetWindowPos(Hwnd.Of(this), IntPtr.Zero, x, y, Math.Max(1, w), Math.Max(1, h),
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void Animate(double to, double ms)
    {
        _from = _offset;
        _to = to;
        _animMs = ms;
        _anim.Restart();
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, _anim.ElapsedMilliseconds / _animMs);
        double k = 1 - Math.Pow(1 - t, 4); // 처음엔 빠르고 끝으로 갈수록 느려진다
        _offset = _from + (_to - _from) * k;
        PlaceAt(_offset);
        if (t < 1) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
        if (_closing) Close();
    }

    /// <summary>
    /// 마우스가 바(와 가장자리 사이 틈) 밖으로 나가면 들어간다.
    /// 펼친 창·우클릭 메뉴가 떠 있거나 항목을 끌어내는 중에는 기다린다.
    /// </summary>
    public void CheckLeave()
    {
        if (_closing || FileOps.Dragging || FluentMenu.IsOpen || ExpandedWindow.IsOpen) return;
        if (!Native.GetCursorPos(out var pt)) return;
        var r = _final;
        var b = _screen.Bounds;
        int l = r.Left, t = r.Top, rr = r.Right, bb = r.Bottom;
        switch (_edge)
        {
            case ScreenEdge.Left: l = b.Left; break;
            case ScreenEdge.Right: rr = b.Right; break;
            case ScreenEdge.Top: t = b.Top; break;
            default: bb = b.Bottom; break;
        }
        if (pt.X >= l && pt.X < rr && pt.Y >= t && pt.Y < bb) return;
        BeginClose();
    }

    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        // 바에서 앱을 열었으면 그 앱이 앞에 있으니 그대로 두고, 아니면 원래 쓰던 창으로 돌려준다.
        if (Native.GetForegroundWindow() == Hwnd.Of(this) && _previous != IntPtr.Zero) Native.SetForegroundWindow(_previous);
        Animate(1, CloseMs);
    }

    protected override void OnClosed(EventArgs e)
    {
        IsGone = true;
        if (_animating) CompositionTarget.Rendering -= OnFrame;
        _mgr.Changed -= OnGroupsChanged;
        foreach (var v in Views().ToList()) v.Detach();
        base.OnClosed(e);
    }
}
