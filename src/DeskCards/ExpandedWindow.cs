using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace DeskCards;

/// <summary>
/// 카드를 눌렀을 때 펼쳐지는 전체 목록 창(아크릴 배경). 이름 바꾸기·설정 열기도 여기서 한다.
/// 이 파일은 창의 수명·배치·제목 입력을, ExpandedWindow.Tiles.cs는 항목 칸·페이지·끌기를 맡는다.
/// </summary>
internal sealed partial class ExpandedWindow : ToolWindow
{
    private const int Columns = 4;
    private const int RowsPerPage = 3;
    private const double TileW = 96, TileH = 104;
    private const double PageH = RowsPerPage * TileH;
    private const double SidePad = 26, TitleH = 80, BottomPad = 28;
    private const double WindowW = SidePad * 2 + Columns * TileW;

    private static ExpandedWindow? _current;

    private readonly CardWindow _card;
    private readonly GroupManager _mgr;
    private readonly bool _hover;
    private readonly Func<int, int, Point>? _place;
    private readonly IntPtr _anchor;
    private readonly TextBox _title;
    private DispatcherTimer? _leaveTimer, _openTimer;
    private Windows.Graphics.RectInt32 _final;
    private const double GrowMs = 400;
    private bool _entered, _busy, _closing, _committing;

    private ExpandedWindow(CardWindow card, GroupManager mgr, bool editTitle, bool hover, Func<int, int, Point>? place, IntPtr anchor)
        : base("Desk Cards", new UserControl { Background = Theme.Brush("PopupBg") }, Motion.Enabled ? Hwnd.Backdrop.None : Hwnd.Backdrop.Acrylic)
    {
        _card = card;
        _mgr = mgr;
        _hover = hover;
        _place = place;
        _anchor = anchor;
        _title = BuildTitleBox();
        BuildBody();
        Group.Changed += Rebuild;
        Rebuild();

        var root = (FrameworkElement)Content;
        root.PreviewKeyDown += OnKey;
        root.PointerWheelChanged += OnWheel;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && !_busy && !FluentMenu.IsOpen) SafeClose();
        };
        Closed += (_, _) => OnClosed();

        PlaceNearCard();
        PlayOpen();
        AppWindow.Show();
        Activate();
        if (hover) StartLeaveWatch();
        if (editTitle) { _title.Focus(FocusState.Programmatic); _title.SelectAll(); }
        else ((Control)Content).Focus(FocusState.Programmatic);
    }

    private GroupModel Group => _card.Group;

    /// <summary>
    /// 열릴 때(Windows 시작 메뉴의 폴더처럼): 카드 자리에서 작게 시작해 제 크기로 커진다(333ms, 감속).
    /// 창과 내용이 같은 비율로 커지고, 내용은 창의 왼쪽 위에 붙여 늘 창 안에 꼭 맞는다.
    /// 커지는 동안은 아크릴 없이 불투명한 바탕을 쓰고(크기를 바꿀 때 아크릴이 따라오지 못해 어긋난다),
    /// 다 커진 뒤에 아크릴을 켜면서 바탕을 서서히 투명하게 해 아크릴이 비쳐 보이게 한다(167ms).
    /// </summary>
    private void PlayOpen()
    {
        if (!Motion.Enabled) return;
        var root = (FrameworkElement)Content;
        var visual = ElementCompositionPreview.GetElementVisual(root);
        var final = _final;
        double fx = final.X + final.Width / 2.0, fy = final.Y + final.Height / 2.0;
        // 시작: 카드(또는 연 창)의 가운데, 카드 크기에 가까운 비율.
        double sx = fx, sy = fy, from = 0.5;
        if (Native.GetWindowRect(_anchor != IntPtr.Zero ? _anchor : Hwnd.Of(_card), out var r))
        {
            sx = (r.Left + r.Right) / 2.0;
            sy = (r.Top + r.Bottom) / 2.0;
            from = Math.Clamp(Math.Min((r.Right - r.Left) / (double)final.Width, (r.Bottom - r.Top) / (double)final.Height), 0.3, 0.8);
        }
        visual.CenterPoint = Vector3.Zero;
        var tint = ((SolidColorBrush)Theme.Brush("PopupBg")).Color;
        var cover = new SolidColorBrush(Windows.UI.Color.FromArgb(255, tint.R, tint.G, tint.B));
        ((Control)root).Background = cover;
        bool acrylicOn = false;

        // 창은 제 크기로 두고 보이는 영역(Window Region)만 키운다. 창 크기를 프레임마다 바꾸면 내용이 한 프레임씩 늦게 따라와 어긋난다.
        Native.GetWindowRect(Handle, out var outer);
        Native.GetClientRect(Handle, out var client);
        int border = Math.Max(0, ((outer.Right - outer.Left) - client.Right) / 2);
        int top = Math.Max(0, ((outer.Bottom - outer.Top) - client.Bottom) - border);
        int radius = (int)Math.Round(16 * Native.MonitorScaleOf(Handle));
        void Apply(double t, double opacity)
        {
            if (_closing) return;
            double s = from + (1 - from) * t;
            double w = final.Width * s, h = final.Height * s;
            double cx = sx + (fx - sx) * t, cy = sy + (fy - sy) * t;
            double left = cx - w / 2 - outer.Left, topY = cy - h / 2 - outer.Top; // 창 바깥 왼쪽 위 기준
            SetWindowRgn(Handle, CreateRoundRectRgn((int)Math.Round(left), (int)Math.Round(topY), (int)Math.Round(left + w), (int)Math.Round(topY + h), radius, radius), true);
            visual.Offset = new Vector3((float)(left - border), (float)(topY - top), 0);
            visual.Scale = new Vector3((float)s, (float)s, 1);
            visual.Opacity = (float)opacity;
        }
        void EndRegion()
        {
            SetWindowRgn(Handle, IntPtr.Zero, true);
            visual.Offset = Vector3.Zero;
        }        Apply(0, 0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
        _openTimer.Tick += (_, _) =>
        {
            double ms = clock.Elapsed.TotalMilliseconds;
            if (ms < GrowMs)
            {
                // 감속이 너무 강하면(0,0,0,1) 처음 한두 프레임에 거의 다 커져서 툭 뜨는 것처럼 보인다. 3차 ease-out으로 고르게 키운다.
                double p = Math.Clamp(ms / GrowMs, 0, 1);
                Apply(1 - Math.Pow(1 - p, 3), Math.Clamp(ms / 150, 0, 1));
                return;
            }
            if (!acrylicOn)
            {
                acrylicOn = true;
                Apply(1, 1);
                EndRegion();
                Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
            }
            double k = Motion.Decel((ms - GrowMs) / Motion.Fast);
            cover.Color = Windows.UI.Color.FromArgb((byte)Math.Round(255 + (tint.A - 255) * k), tint.R, tint.G, tint.B);
            if (ms >= GrowMs + Motion.Fast)
            {
                _openTimer?.Stop();
                _openTimer = null;
                ((Control)root).Background = Theme.Brush("PopupBg");
            }
        };
        _openTimer.Start();
    }
    /// <param name="hover">더보기 칸에 올려 두어서 연 경우. 마우스가 창 밖으로 나가면 바로 닫힌다.</param>
    /// <param name="place">창 크기(물리 픽셀)를 받아 띄울 자리(물리 픽셀)를 정한다. 없으면 카드 가운데에 펼친다.</param>
    /// <param name="anchor">바탕화면 카드 대신 다른 창에서 열었을 때의 창. 그 창이 있는 쪽에 뜨고 그 위에 보인다.</param>
    public static void Open(CardWindow card, GroupManager mgr, bool editTitle = false, bool hover = false,
        Func<int, int, Point>? place = null, IntPtr anchor = default)
    {
        _current?.SafeClose();
        _current = new ExpandedWindow(card, mgr, editTitle, hover, place, anchor);
    }

    public static bool IsOpen => _current is { _closing: false };

    public static bool IsOpenFor(CardWindow card) => _current is { _closing: false } w && w._card == card;

    public static void CloseCurrent() => _current?.SafeClose();

    public static void CloseFor(CardWindow card)
    {
        if (_current != null && _current._card == card) _current.SafeClose();
    }

    // ----- 제목(이름 바꾸기) -----

    private TextBox BuildTitleBox()
    {
        var box = new TextBox
        {
            FontSize = 22,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MinWidth = 120,
            MaxWidth = 300,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(6, 2, 6, 2),
            MaxLength = 80,
            Text = Group.Name,
        };
        // 눌러도 모양이 바뀌지 않게(평소 제목처럼 보이다가 쓰는 동안만 입력 칸).
        foreach (var key in new[] { "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            box.Resources[key] = new SolidColorBrush(Colors.Transparent);
        box.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            CommitTitle();
            ((FrameworkElement)Content).Focus(FocusState.Programmatic);
            e.Handled = true;
        };
        box.LostFocus += (_, _) => CommitTitle();
        box.GotFocus += (_, _) => box.SelectAll();
        return box;
    }

    private void CommitTitle()
    {
        if (_committing || !_mgr.Cards.Contains(_card)) return;
        string name = _title.Text.Trim();
        if (name.Length == 0 || name == Group.Name) { _title.Text = Group.Name; return; }
        bool wasBusy = _busy;
        _committing = _busy = true;
        try { if (!_mgr.RenameGroup(_card, name)) _title.Text = Group.Name; }
        finally { _committing = false; _busy = wasBusy; }
    }

    // ----- 배치 -----

    /// <summary>카드 가운데를 기준으로 펼치고, 모니터 작업 영역 안으로 맞춘다.</summary>
    private void PlaceNearCard()
    {
        var root = (FrameworkElement)Content;
        var hwnd = Hwnd.Of(_anchor != IntPtr.Zero ? null : _card);
        var reference = _anchor != IntPtr.Zero ? _anchor : hwnd;
        double scale = Native.MonitorScaleOf(reference);
        int w = (int)Math.Round(root.Width * scale), h = (int)Math.Round(root.Height * scale);
        Point p;
        if (_place != null) p = _place(w, h);
        else
        {
            Native.GetWindowRect(reference, out var rect);
            var wa = Screens.FromHandle(reference).WorkArea;
            p = PixelPlacement.Beside(rect, new Rect(wa.Left, wa.Top, wa.Width, wa.Height), new Size(root.Width, root.Height), scale, centered: true);
        }
        _final = new Windows.Graphics.RectInt32((int)Math.Round(p.X), (int)Math.Round(p.Y), w, h);
        Resize(_final);
        Hwnd.SetZOrder(Handle, _mgr.Editing || _anchor != IntPtr.Zero ? Hwnd.Topmost : IntPtr.Zero);
    }

    /// <summary>
    /// 올려 두어서 연 창: 마우스가 창 안에 한 번 들어온 뒤 밖으로 나가면 닫는다.
    /// 창이 더보기 칸을 덮지 않았을 수도 있으니, 들어오기 전에는 카드 위에 있는 동안도 기다린다.
    /// 메뉴·끌기·이름 입력 중에는 닫지 않는다.
    /// </summary>
    private void StartLeaveWatch()
    {
        _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _leaveTimer.Tick += (_, _) =>
        {
            if (_closing || _busy || _title.FocusState != FocusState.Unfocused || !Native.GetCursorPos(out var pt)) return;
            if (Inside(Handle, pt)) { _entered = true; return; }
            if (!_entered && Inside(_anchor != IntPtr.Zero ? _anchor : Hwnd.Of(_card), pt)) return;
            SafeClose();
        };
        _leaveTimer.Start();
    }

    private static bool Inside(IntPtr hwnd, Native.POINT pt) =>
        hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var r) &&
        pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;

    // ----- 입력·닫기 -----

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        bool typing = _title.FocusState != FocusState.Unfocused;
        if (!typing)
        {
            int dir = e.Key switch { VirtualKey.Down or VirtualKey.PageDown => 1, VirtualKey.Up or VirtualKey.PageUp => -1, _ => 0 };
            if (dir != 0) { GoToPage(_page + dir); e.Handled = true; return; }
        }
        if (e.Key == VirtualKey.Escape && !_busy)
        {
            if (typing) _title.Text = Group.Name;
            SafeClose();
            e.Handled = true;
        }
    }

    private void OpenSettings()
    {
        // 이 창이 닫히는 도중에 설정 창을 만들면 내용이 그려지지 않는다. 완전히 닫힌 다음에 연다.
        Closed += (_, _) => Ui.Post(() => SettingsWindow.Open(_mgr, _card), Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
        SafeClose();
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Ui.Post(Close);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    private void OnClosed()
    {
        Group.Changed -= Rebuild;
        _leaveTimer?.Stop();
        _openTimer?.Stop();
        if (_current == this) _current = null;
    }
}

