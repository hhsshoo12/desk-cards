using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskCards;

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
        _scale = ScaleOf(screen);
        _final = FinalRect(screen, edge, mgr.BarSize);
        bool side = edge is ScreenEdge.Left or ScreenEdge.Right;

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

    private static double ScaleOf(Forms.Screen screen)
    {
        var wa = screen.WorkingArea;
        return Native.MonitorScaleAt(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
    }

    /// <summary>다 나왔을 때 바의 자리(물리 픽셀): 작업 영역 안, 가장자리에서 틈(8)만큼 떨어져, 두께는 작업 영역의 sizePercent%.</summary>
    public static System.Drawing.Rectangle FinalRect(Forms.Screen screen, ScreenEdge edge, int sizePercent)
    {
        var wa = screen.WorkingArea;
        int gap = (int)Math.Round(Gap * ScaleOf(screen));
        bool side = edge is ScreenEdge.Left or ScreenEdge.Right;
        int thick = (int)Math.Round((side ? wa.Width : wa.Height) * sizePercent / 100.0);
        return edge switch
        {
            ScreenEdge.Left => new(wa.Left + gap, wa.Top + gap, thick, wa.Height - 2 * gap),
            ScreenEdge.Right => new(wa.Right - gap - thick, wa.Top + gap, thick, wa.Height - 2 * gap),
            ScreenEdge.Top => new(wa.Left + gap, wa.Top + gap, wa.Width - 2 * gap, thick),
            _ => new(wa.Left + gap, wa.Bottom - gap - thick, wa.Width - 2 * gap, thick),
        };
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
        Hwnd.MakeTool(hwnd);
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
        double k = Motion.EaseOut(t, 4);
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

