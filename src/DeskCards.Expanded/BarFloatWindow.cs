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
/// 카드 바 옆(화면 안쪽, 바의 시작 모서리)에 떠 있는 알약: 설정(톱니)과 편집(연필) 두 칸. 바 안의 카드를 가리지 않으려고 바 밖에 따로 띄운다.
/// 바가 다 나오면 가장 가까운 화면 모서리 쪽에서 미끄러져 들어오고, 바가 들어가기 시작하면 그쪽으로 나간다. 활성 창을 빼앗지 않는다.
/// 편집을 시작하면 연필이 톱니 쪽으로 들어가 한 칸이 되고, 그 톱니가 편집 막대를 펼치고 접는 단추가 된다.
/// </summary>
internal sealed class BarFloatWindow : Window
{
    public const double Size = 36;   // 칸 높이
    public const double PillWidth = 52; // 편집 막대(단추 40 + 안쪽 여백 6×2)와 같은 폭
    private const double Shadow = 12; // 그림자가 잘리지 않게 두는 여백

    private readonly bool _gearFirst;
    private readonly Border _body, _gearCell, _penCell;
    private readonly TextBlock _gear, _pen;
    private readonly TranslateTransform _penShift = new();
    private readonly Stopwatch _clock = new();
    private readonly Action _settings, _edit, _toggle;
    private (int X, int Y) _home, _away, _from, _to, _now; // 창 왼쪽 위(물리 픽셀)
    private double _ms;
    private bool _moving, _leaving, _editing;
    private IntPtr _handle;

    /// <param name="gearFirst">톱니가 위 칸이면 true(편집 막대가 아래로 펼쳐짐). 아래 바는 막대가 위로 펼쳐지므로 톱니가 아래 칸.</param>
    public BarFloatWindow(Palette palette, bool gearFirst, Action settings, Action edit, Action toggle)
    {
        _gearFirst = gearFirst;
        _settings = settings;
        _edit = edit;
        _toggle = toggle;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = PillWidth + 2 * Shadow;
        Height = 2 * Size + 2 * Shadow;
        Left = Top = -32000;
        Title = "Desk Cards 카드 바 설정";
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");

        var fg = Brush(palette.Fg);
        var hover = Brush(palette.HoverBg);
        _gear = Glyph("\uE713", fg);
        _pen = Glyph("\uE70F", fg);
        _pen.RenderTransform = _penShift;
        _gearCell = Cell(_gear, hover, () => { if (_editing) _toggle(); else _settings(); });
        _penCell = Cell(_pen, hover, () => _edit());
        _gearCell.ToolTip = "카드 바 설정";
        _penCell.ToolTip = "카드 바 편집";

        var grid = new Grid { Width = PillWidth, Height = 2 * Size };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Size) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Size) });
        Grid.SetRow(gearFirst ? _penCell : _gearCell, 1);
        grid.Children.Add(_gearCell);
        grid.Children.Add(_penCell);

        var popup = (Color)ColorConverter.ConvertFromString(palette.PopupBg);
        popup.A = 0xF2;
        _body = new Border
        {
            Width = grid.Width,
            Height = grid.Height,
            Margin = new Thickness(Shadow),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = gearFirst ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(popup),
            BorderBrush = Brush(palette.CardBorder),
            ClipToBounds = true,
            Child = grid,
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

    private static TextBlock Glyph(string glyph, Brush fg) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 16,
        Foreground = fg,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border Cell(TextBlock icon, Brush hover, Action click)
    {
        var cell = new Border { Width = PillWidth, Height = Size, Background = Brushes.Transparent, Child = icon, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        cell.MouseEnter += (_, _) => cell.Background = hover;
        cell.MouseLeave += (_, _) => { cell.Background = Brushes.Transparent; icon.Opacity = cell.Tag is true ? 0 : 1; };
        cell.MouseLeftButtonDown += (_, e) => { e.Handled = true; icon.Opacity = 0.7; };
        cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; icon.Opacity = 1; click(); };
        return cell;
    }

    private static Brush Brush(string argb)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
        b.Freeze();
        return b;
    }

    /// <summary>편집 중이면 연필이 톱니 칸으로 들어가 알약이 한 칸이 된다. 끝나면 다시 두 칸.</summary>
    public void SetEditing(bool editing)
    {
        if (_editing == editing) return;
        _editing = editing;
        _gearCell.ToolTip = editing ? "편집 막대 펼치기 · 접기" : "카드 바 설정";
        double len = Size;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 220 : 0);
        var side = Border.HeightProperty;
        var shift = TranslateTransform.YProperty;
        double dir = _gearFirst ? -1 : 1; // 연필이 톱니 칸 쪽으로 움직이는 방향
        _body.BeginAnimation(side, new DoubleAnimation(editing ? 2 * len : len, editing ? len : 2 * len, dur) { EasingFunction = ease });
        _penShift.BeginAnimation(shift, new DoubleAnimation(editing ? 0 : dir * len, editing ? dir * len : 0, dur) { EasingFunction = ease });
        _pen.BeginAnimation(OpacityProperty, new DoubleAnimation(editing ? 1 : 0, editing ? 0 : 1, dur) { EasingFunction = ease });
        _penCell.IsHitTestVisible = !editing;
    }

    /// <summary>알약 전체(두 칸)의 왼쪽 위가 body.Left/Top에 오도록 들어온다. bounds에서 가장 가까운 모서리 바깥에서 출발한다.</summary>
    public void Enter(PixelRect body, PixelRect bounds, double scale)
    {
        int pad = (int)Math.Round(Shadow * scale), w = body.Width, h = body.Height;
        _home = (body.Left - pad, body.Top - pad);
        int toLeft = body.Left - bounds.Left, toRight = bounds.Right - (body.Left + w), toTop = body.Top - bounds.Top, toBottom = bounds.Bottom - (body.Top + h);
        int min = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
        _away = min == toTop ? (_home.X, bounds.Top - h - 2 * pad)
            : min == toBottom ? (_home.X, bounds.Bottom)
            : min == toLeft ? (bounds.Left - w - 2 * pad, _home.Y)
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