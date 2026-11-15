using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace DeskCards.Expanded;

/// <summary>
/// 바탕화면 카드(폴더 카드·웹 카드)가 같이 쓰는 창 틀. 폴더 카드와 웹 카드가 똑같이 움직이도록 창 규칙은 전부 여기에 둔다.
/// 보통 WPF 창에 Windows(DWM)가 그리는 미카·둥근 모서리·그림자를 쓰고, 눌러도 활성화되지 않으며,
/// 바탕화면 층(Progman 소유, 맨 아래)에 머문다. 편집 중에는 가장자리에 띠가 생겨 끌어 옮기고 크기를 바꾼다.
/// 위치·크기는 모두 화면 물리 픽셀이다(내용 영역 기준, 편집 띠는 빠진다).
/// </summary>
internal abstract class DesktopCardWindow : Window
{
    private const int Band = 12, EdgeBand = 6; // 편집 띠 두께(DIP): 바깥쪽 EdgeBand는 크기 조절, 안쪽은 끌기
    private const int BackdropType = 2; // DWMSBT_MAINWINDOW = 미카. 폴더·웹 카드가 같은 값을 쓴다.

    private readonly IDesktopCardHost _host;
    private readonly string _id;
    private readonly Border _outline;
    private IntPtr _zTarget;
    private bool _dragging, _movingZ, _visibleWanted, _priming, _hlSelected, _hlInvalid;
    private PixelRect _sizeStart;
    private Palette _palette;

    protected int X, Y, W, H; // 내용 영역
    protected bool Editing, Dead;

    /// <summary>카드 내용을 담는 칸(편집 중에는 띠만큼 안쪽으로 들어간다).</summary>
    protected Grid Surface { get; } = new();

    protected DesktopCardWindow(IDesktopCardHost host, string id, string title, Palette palette, int x, int y, int w, int h, bool visible)
    {
        _host = host;
        _id = id;
        _palette = palette;
        _visibleWanted = visible;
        (X, Y, W, H) = (x, y, Math.Max(1, w), Math.Max(1, h));

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        Width = Height = 1;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        Title = title;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        ApplyResources(palette);

        _outline = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var root = new Grid();
        root.Children.Add(Surface);
        root.Children.Add(_outline);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => { Dead = true; OnClosedCore(); _host.Closed(); };
    }

    /// <summary>파생 클래스의 준비가 끝난 뒤 부른다: 창을 만들고(핸들) 보여야 하면 띄운다.</summary>
    internal void Prepare()
    {
        new WindowInteropHelper(this).EnsureHandle();
        if (_visibleWanted) { Show(); Prime(); }
    }

    /// <summary>
    /// 창 배경(미카·아크릴)은 창이 한 번도 활성화된 적 없으면 단색 회색으로 그려진다. 보일 때 한 번 활성 창으로 만든 뒤 바로 바탕화면 층으로 돌려 놓는다.
    /// 같은 스레드의 다른 WPF 창(펼친 창·카드 바)이 지금 활성이면 그 창의 활성을 뺏지 않도록 건너뛴다.
    /// </summary>
    private void Prime() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
    {
        if (Dead || Editing || !IsVisible) return;
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != Handle && GetWindowThreadProcessId(fg, out _) == GetCurrentThreadId()) return;
        _priming = true;
        try { SetActiveWindow(Handle); SendToBottom(); }
        finally { _priming = false; }
    });

    // ----- 파생 클래스가 정하는 것 -----

    /// <summary>크기를 비율 고정으로 바꾸는지(폴더 카드). 아니면 자유롭게 바꾼다(웹 카드).</summary>
    protected virtual bool LockAspect => false;

    protected virtual void OnBoundsChanged() { }
    protected virtual void OnEditingChanged(bool editing) { }
    protected virtual void OnClosedCore() { }
    /// <summary>창이 옮겨지거나 크기가 바뀌었다(끄는 도중에도 불린다).</summary>
    protected virtual void OnMoved() { }

    // ----- 창 -----

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    protected double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    protected int Pad => Editing ? (int)Math.Round(Band * Scale) : 0;

    public PixelRect ContentRect => new(X, Y, X + W, Y + H);

    /// <summary>창의 실제 자리에서 편집 띠를 뺀 내용 영역(물리 픽셀). 끄는 도중에도 맞다.</summary>
    protected PixelRect CurrentContent()
    {
        if (!GetWindowRect(Handle, out var r)) return ContentRect;
        int pad = Pad;
        return new PixelRect(r.Left + pad, r.Top + pad, r.Right - pad, r.Bottom - pad);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF가 새 배율의 제안 자리로 옮긴 뒤에 읽어서 앱에 알린다.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            if (Dead) return;
            var c = CurrentContent();
            (X, Y, W, H) = (c.Left, c.Top, Math.Max(1, c.Width), Math.Max(1, c.Height));
            OnBoundsChanged();
            _host.DpiChanged(X, Y, W, H);
        });
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Handle;
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, new IntPtr(style & ~0x00080000L)); // WS_SYSMENU 제거
        long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new IntPtr(ex | 0x80L | 0x08000000L)); // 도구 창 + 눌러도 활성화 안 됨
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        SetDwm(hwnd, 20, _palette.Light ? 0 : 1);
        SetDwm(hwnd, 33, 2);
        SetDwm(hwnd, 38, BackdropType);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        SetWindowLongPtr(hwnd, -8, FindWindow("Progman", null)); // 소유자 = 바탕화면(Win+D에도 안 숨겨짐)
        ApplyBounds();
        SendToBottom();
    }

    private void ApplyBounds()
    {
        int pad = Pad;
        double k = Scale;
        SetWindowPos(Handle, IntPtr.Zero, X - pad, Y - pad, W + 2 * pad, H + 2 * pad, 0x0004 | 0x0010);
        Surface.Margin = new Thickness(pad / k);
        _outline.Margin = new Thickness(pad / k - 2);
        OnBoundsChanged();
    }

    internal void SetBounds(int x, int y, int w, int h)
    {
        if (Dead) return;
        (X, Y, W, H) = (x, y, Math.Max(1, w), Math.Max(1, h));
        ApplyBounds();
    }

    internal void SetEditing(bool editing)
    {
        if (Dead || (Editing == editing && !editing)) return;
        bool changed = Editing != editing;
        Editing = editing;
        _outline.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        if (!editing || changed) SetHighlight(false, false);
        ApplyBounds();
        if (changed) OnEditingChanged(editing);
        if (editing) SetZ(new IntPtr(-1)); // 어두운 막 위로
        else SetZ(new IntPtr(1)); // HWND_BOTTOM이 최상위도 함께 푼다
    }

    /// <summary>편집 중 강조: 고른 카드는 강조색, 안 되는 자리는 빨강 테두리.</summary>
    internal void SetHighlight(bool selected, bool invalid)
    {
        if (Dead) return;
        (_hlSelected, _hlInvalid) = (selected, invalid);
        var c = invalid ? Color.FromRgb(0xE8, 0x11, 0x23) : (Color)ColorConverter.ConvertFromString(_palette.Accent);
        _outline.BorderBrush = new SolidColorBrush(c);
        _outline.BorderThickness = new Thickness(invalid || selected ? 3 : 2);
    }

    internal void SetVisible(bool visible)
    {
        if (Dead) return;
        _visibleWanted = visible;
        if (visible) { if (!IsVisible) { Show(); SendToBottom(); Prime(); } }
        else if (IsVisible) Hide();
        OnVisibilityChanged(visible);
    }

    protected virtual void OnVisibilityChanged(bool visible) { }

    internal void Restack()
    {
        if (!Dead && !Editing && IsVisible) SendToBottom();
    }

    internal void SetPalette(Palette palette)
    {
        _palette = palette;
        ApplyResources(palette);
        var hwnd = Handle;
        SetDwm(hwnd, 20, palette.Light ? 0 : 1);
        SetDwm(hwnd, 38, BackdropType);
        if (Editing) SetHighlight(_hlSelected, _hlInvalid); // 강조색도 새 테마로
    }

    private void ApplyResources(Palette p)
    {
        Resources["Fg"] = Frozen(p.Fg);
        Resources["SubFg"] = Frozen(p.SubFg);
        Resources["HoverBg"] = Frozen(p.HoverBg);
        Resources["CardBg"] = Frozen(p.CardBg);
        Resources["CardBorder"] = Frozen(p.CardBorder);
        Resources["Accent"] = Frozen(p.Accent);
    }

    private static Brush Frozen(string argb)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
        b.Freeze();
        return b;
    }

    protected void SendToBottom() => SetZ(new IntPtr(1));

    private void SetZ(IntPtr after)
    {
        _zTarget = after;
        _movingZ = true;
        try { SetWindowPos(Handle, after, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200); }
        finally { _movingZ = false; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0084 when Editing: // WM_NCHITTEST: 편집 띠 = 크기 조절(바깥)·끌기(안쪽)
                handled = true;
                return new IntPtr(HitTest(lParam));
            case 0x0086: // WM_NCACTIVATE: 늘 활성인 것처럼 그려서 미카가 단색으로 바뀌지 않게(실제 활성·초점은 그대로)
                handled = true;
                return DefWindowProc(hwnd, msg, new IntPtr(1), new IntPtr(-1));
            case 0x0006 when !Editing && (wParam.ToInt64() & 0xFFFF) != 0: // WM_ACTIVATE: 눌려서 활성화되면 바탕화면이 카드 앞으로 나온다. 끝난 뒤 순서를 바로잡는다
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { if (Dead || Editing) return; SendToBottom(); if (!_priming) _host.Activated(); });
                break;
            case 0x0021: // WM_MOUSEACTIVATE: 눌러도 활성화하지 않는다
                handled = true;
                return new IntPtr(3);
            case 0x00A1 when Editing: // WM_NCLBUTTONDOWN: 편집 중 누르면 이 카드를 고른다
                _host.Selected();
                break;
            case 0x00A5 when Editing: // WM_NCRBUTTONUP: 편집 중 우클릭도 카드 메뉴
            case 0x007B when Editing: // WM_CONTEXTMENU
                handled = true;
                _host.Menu();
                return IntPtr.Zero;
            case 0x0047: // WM_WINDOWPOSCHANGED
                if (!Dead) OnMoved();
                break;
            case 0x0231: // WM_ENTERSIZEMOVE
                _dragging = true;
                _sizeStart = ContentRect;
                break;
            case 0x0046: // WM_WINDOWPOSCHANGING
                OnPosChanging(hwnd, lParam);
                break;
            case 0x0232: // WM_EXITSIZEMOVE
                _dragging = false;
                _host.GuidesOff();
                ReadBounds();
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>눌러도 앞으로 나오지 않게(앞뒤는 앱이 정할 때만 바뀐다). 옮기는 중이면 맞추고, 크기를 바꾸는 중이면 비율을 지킨다.</summary>
    private void OnPosChanging(IntPtr hwnd, IntPtr lParam)
    {
        var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        if ((wp.flags & 0x0004) == 0)
        {
            if (_movingZ) wp.hwndInsertAfter = _zTarget;
            else if (!Editing) wp.flags |= 0x0004; // 앱이 직접 옮길 때 말고는 z 변경을 모두 막는다
        }
        if (Editing && _dragging && GetWindowRect(hwnd, out var cur))
        {
            int pad = Pad;
            bool sizing = (wp.flags & 0x0001) == 0 && (wp.cx != cur.Right - cur.Left || wp.cy != cur.Bottom - cur.Top);
            if (sizing && LockAspect)
            {
                // 오른쪽 아래 모서리로만 크기를 바꾼다. 가로·세로 늘어난 비율의 평균으로 카드 전체를 같은 비율로 키운다.
                double w0 = _sizeStart.Width, h0 = _sizeStart.Height;
                double g = ((wp.cx - 2 * pad) / w0 + (wp.cy - 2 * pad) / h0) / 2;
                g = Math.Max(g, 48 / Math.Min(w0, h0));
                g = _host.SnapScale(_id, _sizeStart, g);
                wp.cx = (int)Math.Round(w0 * g) + 2 * pad;
                wp.cy = (int)Math.Round(h0 * g) + 2 * pad;
            }
            else if (!sizing && (wp.flags & 0x0002) == 0 && wp.cx == cur.Right - cur.Left && wp.cy == cur.Bottom - cur.Top)
            {
                var snapped = _host.Snap(_id, new PixelRect(wp.x + pad, wp.y + pad, wp.x + wp.cx - pad, wp.y + wp.cy - pad));
                wp.x = snapped.Left - pad;
                wp.y = snapped.Top - pad;
            }
        }
        Marshal.StructureToPtr(wp, lParam, false);
    }

    private int HitTest(IntPtr lParam)
    {
        GetWindowRect(Handle, out var r);
        int px = (short)(lParam.ToInt64() & 0xFFFF), py = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        int band = Pad, edge = (int)Math.Round(EdgeBand * Scale), corner = (int)Math.Round(24 * Scale);
        bool l = px < r.Left + band, rt = px >= r.Right - band, t = py < r.Top + band, b = py >= r.Bottom - band;
        if (!(l || rt || t || b)) return 2; // 띠 안쪽(내용)은 끌어서 옮기기(HTCAPTION). 웹 화면은 app-region으로 같은 값을 넘겨 준다
        bool cr = px >= r.Right - corner, cb = py >= r.Bottom - corner;
        if (LockAspect) return cr && cb ? 17 : 2; // 비율을 지키는 카드: 오른쪽 아래 모서리만 크기 조절
        bool el = px < r.Left + edge, er = px >= r.Right - edge, et = py < r.Top + edge, eb = py >= r.Bottom - edge;
        bool cl = px < r.Left + corner, ct = py < r.Top + corner;
        if ((el || et) && cl && ct) return 13;
        if ((er || et) && cr && ct) return 14;
        if ((el || eb) && cl && cb) return 16;
        if ((er || eb) && cr && cb) return 17;
        if (el) return 10;
        if (er) return 11;
        if (et) return 12;
        if (eb) return 15;
        return 2; // HTCAPTION: 끌어서 옮기기
    }

    private void ReadBounds()
    {
        if (Dead || !GetWindowRect(Handle, out var r)) return;
        int pad = Pad;
        (X, Y, W, H) = (r.Left + pad, r.Top + pad, Math.Max(1, r.Right - r.Left - 2 * pad), Math.Max(1, r.Bottom - r.Top - 2 * pad));
        OnBoundsChanged();
        _host.Bounds(X, Y, W, H);
    }

    // ----- 네이티브 -----

    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private static void SetDwm(IntPtr hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}