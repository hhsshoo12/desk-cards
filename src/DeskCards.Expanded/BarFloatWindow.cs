using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DeskCards.Expanded;

/// <summary>
/// 카드 바 옆(화면 안쪽, 바의 시작 모서리)에 떠 있는 작은 설정 버튼. 바 안의 카드를 가리지 않으려고 바 밖에 따로 띄운다.
/// 바가 다 나오면 가장 가까운 화면 모서리 쪽에서 미끄러져 들어오고, 바가 들어가기 시작하면 그쪽으로 나간다. 활성 창을 빼앗지 않는다.
/// </summary>
internal sealed class BarFloatWindow : Window
{
    public const double Size = 36;
    private const double Shadow = 12; // 그림자가 잘리지 않게 두는 여백

    private readonly Border _body;
    private readonly Stopwatch _clock = new();
    private (int X, int Y) _home, _away, _from, _to, _now; // 창 왼쪽 위(물리 픽셀)
    private double _ms;
    private bool _moving, _leaving;
    private IntPtr _handle;

    public BarFloatWindow(Palette palette, Action click)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = Size + 2 * Shadow;
        Left = Top = -32000;
        Title = "Desk Cards 카드 바 설정";
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");

        var fg = Brush(palette.Fg);
        var icon = new TextBlock
        {
            Text = "\uE713",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = fg,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hover = Brush(palette.HoverBg);
        var popup = (Color)ColorConverter.ConvertFromString(palette.PopupBg);
        popup.A = 0xF2;
        _body = new Border
        {
            Width = Size,
            Height = Size,
            Margin = new Thickness(Shadow),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(popup),
            BorderBrush = Brush(palette.CardBorder),
            Child = icon,
            ToolTip = "카드 바 설정",
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.18 },
        };
        var normal = _body.Background;
        _body.MouseEnter += (_, _) => _body.Background = Blend(popup, hover);
        _body.MouseLeave += (_, _) => { _body.Background = normal; icon.Opacity = 1; };
        _body.MouseLeftButtonDown += (_, e) => { e.Handled = true; icon.Opacity = 0.7; };
        _body.MouseLeftButtonUp += (_, e) => { e.Handled = true; icon.Opacity = 1; click(); };
        Content = _body;
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            // 활성 창을 빼앗지 않는 도구 창
            long ex = GetWindowLongPtr(_handle, -20).ToInt64();
            SetWindowLongPtr(_handle, -20, new IntPtr(ex | 0x80L | 0x08000000L));
        };
    }

    private static Brush Brush(string argb)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
        b.Freeze();
        return b;
    }

    private static Brush Blend(Color baseColor, Brush overlay)
    {
        var o = ((SolidColorBrush)overlay).Color;
        double a = o.A / 255.0;
        byte Mix(byte x, byte y) => (byte)Math.Round(x * (1 - a) + y * a);
        return new SolidColorBrush(Color.FromArgb(baseColor.A, Mix(baseColor.R, o.R), Mix(baseColor.G, o.G), Mix(baseColor.B, o.B)));
    }

    /// <summary>버튼 본체(그림자 제외)의 왼쪽 위가 body.Left/Top에 오도록 들어온다. bounds에서 가장 가까운 모서리 바깥에서 출발한다.</summary>
    public void Enter(PixelRect body, PixelRect bounds, double scale)
    {
        int pad = (int)Math.Round(Shadow * scale), size = (int)Math.Round(Size * scale);
        _home = (body.Left - pad, body.Top - pad);
        int toLeft = body.Left - bounds.Left, toRight = bounds.Right - (body.Left + size), toTop = body.Top - bounds.Top, toBottom = bounds.Bottom - (body.Top + size);
        int min = Math.Min(Math.Min(toLeft, toRight), Math.Min(toTop, toBottom));
        _away = min == toTop ? (_home.X, bounds.Top - size - 2 * pad)
            : min == toBottom ? (_home.X, bounds.Bottom)
            : min == toLeft ? (bounds.Left - size - 2 * pad, _home.Y)
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