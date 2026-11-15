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
    private DispatcherTimer? _leaveTimer;
    private bool _entered, _busy, _closing, _committing;

    private ExpandedWindow(CardWindow card, GroupManager mgr, bool editTitle, bool hover, Func<int, int, Point>? place, IntPtr anchor)
        : base("Desk Cards", new UserControl { Background = Theme.Brush("PopupBg") })
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

    private DispatcherTimer? _openTimer;
    private LayeredWindow? _ghost;
    private Windows.Graphics.RectInt32 _final;

    /// <summary>
    /// 열릴 때: 진짜 창은 투명하게 제 자리에 두고, 그 모습을 그림으로 떠서 별도의 그림 창(DWM이 평소처럼 합성)이
    /// 카드 자리에서 작게 시작해 커지게 한다(333ms). 다 커지면 진짜 창(아크릴)을 페이드로 드러내고 그림 창을 걷는다(167ms).
    /// 진짜 창은 크기·영역·DWM 설정을 건드리지 않는다.
    /// </summary>
    private async void PlayOpen()
    {
        if (!Motion.Enabled) return;
        var hwnd = Handle;
        // 진짜 창은 그림 창이 다 커질 때까지 DWM이 그리지 않게 숨겨 둔다(CLOAK). 투명도로는 처음 몇 프레임이 비쳐 보였다.
        SetCloak(hwnd, true);
        bool uncloaked = false;

        void Restore()
        {
            _ghost?.Dispose();
            _ghost = null;
            if (!uncloaked) { uncloaked = true; SetCloak(hwnd, false); }
        }

        try
        {
            await System.Threading.Tasks.Task.Delay(40); // 창이 화면에 올라와 내용이 그려지길 기다린다.
            if (_closing) return;
            var root = (FrameworkElement)Content;
            // 진짜 창이 실제로 놓인 안쪽(클라이언트) 자리·크기에 맞춘다.
            Native.GetClientRect(hwnd, out var client);
            var origin = new Native.POINT();
            ClientToScreen(hwnd, ref origin);
            int pw = client.Right, ph = client.Bottom, left = origin.X, topY = origin.Y;
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(root); // 크기를 주면 DIP로 읽혀 배율만큼 커지므로, 요소 크기 그대로 물리 픽셀 1:1로 뜬다.
            if (_closing) return;
            var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(await rtb.GetPixelsAsync());
            // 떠 낸 그림의 실제 크기(요청한 크기와 다를 수 있다)를 쓴다.
            int bw = rtb.PixelWidth, bh = rtb.PixelHeight;
            if (bw <= 0 || bh <= 0 || pixels.Length < bw * bh * 4) throw new InvalidOperationException();
            DumpBake(pixels, bw, bh, "raw", root);
            BakeGlass(pixels, bw, bh);
            DumpBake(pixels, bw, bh, "baked", root);

            _ghost = new LayeredWindow(topmost: true);
            _ghost.SetSource(pixels, bw, bh);
            double fx = left + pw / 2.0, fy = topY + ph / 2.0, sx = fx, sy = fy, from = 0.5;
            if (Native.GetWindowRect(_anchor != IntPtr.Zero ? _anchor : Hwnd.Of(_card), out var r))
            {
                sx = (r.Left + r.Right) / 2.0;
                sy = (r.Top + r.Bottom) / 2.0;
                from = Math.Clamp(Math.Min((r.Right - r.Left) / (double)pw, (r.Bottom - r.Top) / (double)ph), 0.3, 0.8);
            }
            void Frame(double t, byte alpha)
            {
                double s = from + (1 - from) * t;
                int w = Math.Max(1, (int)Math.Round(pw * s)), h = Math.Max(1, (int)Math.Round(ph * s));
                double cx = sx + (fx - sx) * t, cy = sy + (fy - sy) * t;
                _ghost!.DrawScaled((int)Math.Round(cx - w / 2.0), (int)Math.Round(cy - h / 2.0), w, h, alpha);
            }
            Frame(0, 0);
            _ghost.Show();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _openTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
            _openTimer.Tick += (_, _) =>
            {
                if (_closing || _ghost == null) { _openTimer?.Stop(); _openTimer = null; return; }
                double ms = clock.Elapsed.TotalMilliseconds;
                if (ms < Motion.Slow)
                {
                    Frame(Motion.Decel(ms / Motion.Slow), (byte)Math.Round(255 * Math.Clamp(ms / 50, 0, 1)));
                    return;
                }
                // 맞게 떠 낸 그림이라 페이드 없이 바로 바꾼다: 진짜 창을 드러내고, 한두 프레임 뒤 그림 창을 걷는다.
                _openTimer?.Stop();
                _openTimer = null;
                Frame(1, 255);
                if (!uncloaked) { uncloaked = true; SetCloak(hwnd, false); }
                DwmFlush();
                DwmFlush();
                Restore();
            };            _openTimer.Start();
        }
        catch
        {
            // 그림을 뜨지 못하면 애니메이션 없이 바로 보여 준다.
            Restore();
        }
    }

    /// <summary>진단용: DESKCARDS_DUMP_OPEN이 설정돼 있으면 떠 낸 그림을 임시 폴더에 BMP로 저장한다.</summary>
    private void DumpBake(byte[] px, int w, int h, string tag, FrameworkElement root)
    {
        if (Environment.GetEnvironmentVariable("DESKCARDS_DUMP_OPEN") == null) return;
        try
        {
            using var fs = System.IO.File.Create(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"deskcards-open-{tag}.bmp"));
            using var bw = new System.IO.BinaryWriter(fs);
            bw.Write((ushort)0x4D42); bw.Write(54 + w * h * 4); bw.Write(0); bw.Write(54);
            bw.Write(40); bw.Write(w); bw.Write(-h); bw.Write((ushort)1); bw.Write((ushort)32); bw.Write(0); bw.Write(w * h * 4); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            bw.Write(px, 0, w * h * 4);
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deskcards-open-info.txt"), $"bake {w}x{h}; final {_final.Width}x{_final.Height}; root {root.Width}x{root.Height} actual {root.ActualWidth}x{root.ActualHeight}");
        }
        catch { }
    }

    /// <summary>
    /// 떠 낸 그림(미리 곱한 BGRA)을 아크릴 비슷한 반투명 바탕 위에 올리고 모서리를 둥글게 깎는다.
    /// 진짜 아크릴(흐림)은 그림에 담을 수 없어서, 같은 색조의 거의 불투명한 바탕을 깐다.
    /// </summary>
    private void BakeGlass(byte[] px, int w, int h)
    {
        var tint = ((Microsoft.UI.Xaml.Media.SolidColorBrush)Theme.Brush("PopupBg")).Color;
        const int baseAlpha = 0xEE;
        int br = tint.R * baseAlpha / 255, bg = tint.G * baseAlpha / 255, bb = tint.B * baseAlpha / 255;
        double r = 8 * Native.MonitorScaleOf(Handle);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4, inv = 255 - px[i + 3];
                px[i] = (byte)Math.Min(255, px[i] + bb * inv / 255);
                px[i + 1] = (byte)Math.Min(255, px[i + 1] + bg * inv / 255);
                px[i + 2] = (byte)Math.Min(255, px[i + 2] + br * inv / 255);
                px[i + 3] = (byte)Math.Min(255, px[i + 3] + baseAlpha * inv / 255);
                double dx = Math.Max(0, Math.Max(r - x, x - (w - 1 - r))), dy = Math.Max(0, Math.Max(r - y, y - (h - 1 - r)));
                if (dx * dx + dy * dy > r * r) px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0;
            }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Native.POINT point);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private static void SetCloak(IntPtr hwnd, bool on) { int v = on ? 1 : 0; Native.DwmSetWindowAttribute(hwnd, 13 /* DWMWA_CLOAK */, ref v, sizeof(int)); }

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

    private void OnClosed()
    {
        _closing = true;
        Group.Changed -= Rebuild;
        _leaveTimer?.Stop();
        _openTimer?.Stop();
        _ghost?.Dispose();
        if (_current == this) _current = null;
    }
}

