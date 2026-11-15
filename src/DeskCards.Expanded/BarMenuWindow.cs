using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace DeskCards.Expanded;

/// <summary>
/// 카드 바 옆(화면 안쪽, 바의 시작 모서리)에 떠 있는 메뉴 하나. 평소엔 햄버거 칸 하나뿐이고,
/// 누르면 같은 판이 아래로 늘어나며 설정·편집 칸이 나온다(아래 바는 위로). 편집을 누르면 판이 햄버거로 도로 들어갔다가
/// 다시 늘어나며 편집 단추가 나온다. 따로 창을 띄우지 않고 한 판이 늘고 줄 뿐이다.
/// 바가 다 나오면 가장 가까운 화면 모서리 쪽에서 미끄러져 들어오고, 바가 들어가기 시작하면 그쪽으로 나간다. 활성 창을 빼앗지 않는다.
/// </summary>
internal sealed class BarMenuWindow : Window
{
    public const double Cell = 52;       // 햄버거·메뉴 칸(정사각형)
    private const double Shadow = 12;    // 그림자가 잘리지 않게 두는 여백
    private const double BtnW = 40, BtnH = 36, Pad = 6; // 편집 단추(폭 40 + 안쪽 여백 6×2 = 판 폭 52)

    private readonly IBarHost _host;
    private readonly Palette _p;
    private readonly bool _growUp;
    private readonly Border _body;
    private readonly Grid _slot = new();
    private readonly StackPanel _menuPanel = new(), _editPanel = new() { Margin = new Thickness(Pad) };
    private readonly Border _rename, _grid, _remove, _guides;
    private readonly TextBlock _guidesIcon;
    private readonly Stopwatch _clock = new();
    private readonly double _editHeight;
    private (int X, int Y) _home, _away, _from, _to, _now; // 창 왼쪽 위(물리 픽셀)
    private double _ms;
    private bool _moving, _leaving, _editing, _open, _guidesOn;
    private int _gen;
    private string? _selected;
    private IntPtr _handle;

    /// <param name="growUp">아래 바처럼 판이 위로 늘어나야 하면 true(햄버거가 아래에 있다).</param>
    public BarMenuWindow(Palette palette, IBarHost host, bool growUp)
    {
        _p = palette;
        _host = host;
        _growUp = growUp;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Left = Top = -32000;
        Title = "Desk Cards 카드 바 메뉴";
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");

        var hamburger = Cell0("\uE700", "카드 바 메뉴", Cell, Cell, Toggle);
        _menuPanel.Children.Add(Cell0("\uE713", "카드 바 설정", Cell, Cell, () => _host.SettingsRequested()));
        _menuPanel.Children.Add(Cell0("\uE70F", "카드 바 편집", Cell, Cell, () => _host.EditRequested()));

        // 편집 단추: 화면 편집 막대와 같은 크기(40×36)를 세로로.
        var add = Cell0("\uE710", "카드 넣기", BtnW, BtnH, () => _host.AddRequested());
        (_guides, _guidesIcon) = Toggle0("\uE80A", "안내선 · 자동 맞춤 (Alt를 누르고 있으면 잠시 꺼짐)", () => { _guidesOn = !_guidesOn; ApplyGuides(); _host.GuidesChanged(_guidesOn); });
        _rename = Cell0("\uE8AC", "이름 바꾸기", BtnW, BtnH, () => { if (_selected is { } g) _host.RenameRequested(g, ScreenRectOf(_rename!)); });
        _grid = Cell0("\uECA5", "미리보기 칸 수", BtnW, BtnH, () => { if (_selected is { } g) _host.GridRequested(g, ScreenRectOf(_grid!)); });
        _remove = Cell0("\uE74D", "카드 바에서 빼기 (Delete)", BtnW, BtnH, () => { if (_selected is { } g) _host.RemoveRequested(g); });
        var settings = Cell0("\uE713", "설정", BtnW, BtnH, () => _host.SettingsRequested());
        var done = Cell0("\uE73E", "편집 끝내기 (Esc)", BtnW, 32, () => _host.EditEnded(), accent: true);
        done.Margin = new Thickness(0, 4, 0, 0);
        _editPanel.Children.Add(add);
        _editPanel.Children.Add(_guides);
        _editPanel.Children.Add(Sep());
        _editPanel.Children.Add(_rename);
        _editPanel.Children.Add(_grid);
        _editPanel.Children.Add(_remove);
        _editPanel.Children.Add(Sep());
        _editPanel.Children.Add(settings);
        _editPanel.Children.Add(done);
        _editPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _editHeight = _editPanel.DesiredSize.Height;
        SetSelected(null);

        Width = Cell + 2 * Shadow;
        Height = Cell + Math.Max(_editHeight, 2 * Cell) + 2 * Shadow;
        _slot.Children.Add(_menuPanel);

        var stack = new StackPanel { VerticalAlignment = growUp ? VerticalAlignment.Bottom : VerticalAlignment.Top };
        if (growUp) { stack.Children.Add(_slot); stack.Children.Add(hamburger); }
        else { stack.Children.Add(hamburger); stack.Children.Add(_slot); }

        var popup = (Color)ColorConverter.ConvertFromString(palette.PopupBg);
        popup.A = 0xF2;
        _body = new Border
        {
            Width = Cell,
            Height = Cell,
            Margin = new Thickness(Shadow),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = growUp ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(popup),
            BorderBrush = Brush(palette.CardBorder),
            ClipToBounds = true,
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.18 },
        };
        Content = _body;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(_handle, -20).ToInt64();
            SetWindowLongPtr(_handle, -20, new IntPtr(ex | 0x80L | 0x08000000L)); // 도구 창 + 활성 창을 빼앗지 않음
        };
    }

    // ----- 단추 만들기 -----

    private Border Cell0(string glyph, string tip, double w, double h, Action click, bool accent = false, bool chevron = false)
    {
        var icon = Glyph(glyph, w >= Cell ? 22 : 16, accent ? Brushes.White : Brush(_p.Fg));
        UIElement content = icon;
        if (chevron)
        {
            var chev = Glyph("\uE70D", 9, Brush(_p.Fg));
            chev.Margin = new Thickness(6, 2, 0, 0);
            content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { icon, chev } };
        }
        var cell = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(w >= Cell ? 0 : 4), Background = Brushes.Transparent, Child = content, ToolTip = tip };
        if (accent) cell.Background = Brush(_p.Accent);
        var hover = accent ? Brush(_p.Accent) : Brush(_p.HoverBg);
        cell.MouseEnter += (_, _) => { if (!accent) cell.Background = hover; else cell.Opacity = 0.9; };
        cell.MouseLeave += (_, _) => { cell.Background = accent ? hover : Brushes.Transparent; cell.Opacity = cell.IsHitTestVisible ? 1 : 0.4; };
        cell.MouseLeftButtonDown += (_, e) => { e.Handled = true; cell.Opacity = 0.7; };
        cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; cell.Opacity = 1; click(); };
        return cell;
    }

    private (Border Cell, TextBlock Icon) Toggle0(string glyph, string tip, Action click)
    {
        var cell = Cell0(glyph, tip, BtnW, BtnH, click);
        return (cell, (TextBlock)cell.Child);
    }

    private static TextBlock Glyph(string glyph, double size, Brush fg) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = size,
        Foreground = fg,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Border Sep() => new() { Height = 1, Margin = new Thickness(6, 4, 6, 4), Background = Brush(_p.CardBorder) };

    private static Brush Brush(string argb)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
        b.Freeze();
        return b;
    }

    private PixelRect ScreenRectOf(FrameworkElement e)
    {
        var tl = e.PointToScreen(new Point(0, 0));
        var br = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new PixelRect((int)Math.Round(tl.X), (int)Math.Round(tl.Y), (int)Math.Round(br.X), (int)Math.Round(br.Y));
    }

    // ----- 상태 -----

    /// <summary>바 안에서 고른 카드(편집 단추의 켜짐·꺼짐).</summary>
    public void SetSelected(string? group)
    {
        _selected = group;
        foreach (var b in new[] { _rename, _grid, _remove })
        {
            b.IsHitTestVisible = group != null;
            b.Opacity = group != null ? 1 : 0.4;
        }
    }

    public void SetGuides(bool on)
    {
        _guidesOn = on;
        ApplyGuides();
    }

    private void ApplyGuides()
    {
        _guides.Background = _guidesOn ? Brush(_p.Accent) : Brushes.Transparent;
        _guidesIcon.Foreground = _guidesOn ? Brushes.White : Brush(_p.Fg);
    }

    private void Toggle()
    {
        if (_open) { Close0(null); return; }
        Open0();
    }

    private double FullHeight => Cell + (_editing ? _editHeight : 2 * Cell);

    private void Open0()
    {
        _open = true;
        _slot.Children.Clear();
        _slot.Children.Add(_editing ? _editPanel : _menuPanel);
        Grow(FullHeight, 260, null);
    }

    private void Close0(Action? then)
    {
        _open = false;
        Grow(Cell, 180, then);
    }

    /// <summary>편집이 시작·끝나면 판이 햄버거로 들어갔다가, 편집 중이면 단추를 담아 다시 늘어난다.</summary>
    public void SetEditing(bool editing)
    {
        if (_editing == editing) return;
        _editing = editing;
        if (editing)
        {
            _open = true;
            Grow(Cell, 180, () =>
            {
                _slot.Children.Clear();
                _slot.Children.Add(_editPanel);
                Grow(FullHeight, 280, null);
            });
        }
        else if (_open)
        {
            _open = false;
            Grow(Cell, 180, () => { _slot.Children.Clear(); _slot.Children.Add(_menuPanel); });
        }
        else
        {
            _slot.Children.Clear();
            _slot.Children.Add(_menuPanel);
        }
    }

    private void Grow(double to, double ms, Action? done)
    {
        int gen = ++_gen;
        var dur = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? ms : 0);
        var anim = new DoubleAnimation(_body.ActualHeight > 0 ? _body.ActualHeight : Cell, to, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        anim.Completed += (_, _) =>
        {
            if (gen != _gen) return;
            _body.Height = to;
            done?.Invoke();
        };
        _body.BeginAnimation(HeightProperty, anim);
    }

    // ----- 나오고 들어가기 -----

    /// <summary>햄버거 칸이 body 자리에 오도록 들어온다. bounds에서 가장 가까운 모서리 바깥에서 출발한다.</summary>
    public void Enter(PixelRect body, PixelRect bounds, double scale)
    {
        int pad = (int)Math.Round(Shadow * scale);
        int ww = (int)Math.Round(Width * scale), hh = (int)Math.Round(Height * scale);
        _home = _growUp ? (body.Left - pad, body.Bottom + pad - hh) : (body.Left - pad, body.Top - pad);
        int toLeft = body.Left - bounds.Left, toRight = bounds.Right - body.Right, toTop = body.Top - bounds.Top, toBottom = bounds.Bottom - body.Bottom;
        int min = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
        _away = min == toTop ? (_home.X, bounds.Top - hh)
            : min == toBottom ? (_home.X, bounds.Bottom)
            : min == toLeft ? (bounds.Left - ww, _home.Y)
            : (bounds.Right, _home.Y);
        _leaving = false;
        if (!IsVisible)
        {
            _now = _away;
            Show();
            Move(_now);
        }
        Slide(_home, 250);
    }

    public void Leave()
    {
        if (!IsVisible || _leaving) return;
        _leaving = true;
        Slide(_away, 167);
    }

    private void Slide((int X, int Y) to, double ms)
    {
        _from = _now;
        _to = to;
        _ms = SystemParameters.ClientAreaAnimation ? ms : 0;
        _clock.Restart();
        if (_moving) return;
        _moving = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double t = _ms <= 0 ? 1 : Math.Min(1, _clock.Elapsed.TotalMilliseconds / _ms);
        double k = Decel(t);
        _now = ((int)Math.Round(_from.X + (_to.X - _from.X) * k), (int)Math.Round(_from.Y + (_to.Y - _from.Y) * k));
        Move(_now);
        if (t < 1) return;
        _moving = false;
        CompositionTarget.Rendering -= OnFrame;
        if (_leaving) Hide();
    }

    private static double Decel(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double t = Math.Cbrt(x);
        return 3 * (1 - t) * t * t + t * t * t;
    }

    private void Move((int X, int Y) p)
    {
        if (_handle != IntPtr.Zero) SetWindowPos(_handle, new IntPtr(-1), p.X, p.Y, 0, 0, 0x0001 | 0x0010); // NOSIZE | NOACTIVATE, 맨 위
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_moving) CompositionTarget.Rendering -= OnFrame;
        base.OnClosed(e);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}