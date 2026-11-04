using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DeskCards;

/// <summary>
/// 카드 바 옆(화면 안쪽, 바의 시작 모서리)에 떠 있는 작은 설정 버튼. 바 안의 카드를 가리지 않으려고 바 밖에 따로 띄운다.
/// 바가 다 나오면 버튼에서 가장 가까운 화면 모서리 쪽에서 미끄러져 들어오고, 바가 들어가기 시작하면 그쪽으로 나간다.
/// 활성 창을 빼앗지 않는다.
/// </summary>
internal sealed class BarFloatButton : Window
{
    public const double Size = 36;
    /// <summary>그림자가 잘리지 않게 창 가장자리에 두는 여백.</summary>
    private const double Shadow = 12;

    private readonly Border _hover;
    private readonly TextBlock _icon;
    private readonly Stopwatch _clock = new();
    private (int X, int Y) _home, _away, _from, _to, _now; // 창 왼쪽 위(물리 픽셀)
    private double _ms;
    private bool _moving, _leaving;

    public BarFloatButton(Window owner, Action click)
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
        new WindowInteropHelper(this).Owner = new WindowInteropHelper(owner).Handle;

        _icon = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _hover = new Border { CornerRadius = new CornerRadius(7), Opacity = 0 };
        _hover.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        var body = new Border
        {
            Width = Size,
            Height = Size,
            Margin = new Thickness(Shadow),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new Grid { Children = { _hover, _icon } },
            ToolTip = "카드 바 설정",
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.18 },
        };
        body.SetResourceReference(Border.BackgroundProperty, "FlyoutBg");
        body.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        body.MouseEnter += (_, _) => _hover.BeginAnimation(OpacityProperty, Motion.In(null, 1, Motion.Faster));
        body.MouseLeave += (_, _) => { _hover.BeginAnimation(OpacityProperty, Motion.In(null, 0, Motion.Faster)); _icon.Opacity = 1; };
        body.MouseLeftButtonDown += (_, e) => { e.Handled = true; _icon.Opacity = 0.7; };
        body.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _icon.Opacity = 1;
            click();
        };
        Content = body;
        SourceInitialized += (_, _) => Hwnd.MakeNoActivateTool(Hwnd.Of(this));
    }

    /// <summary>버튼 본체(그림자 제외)가 다 나왔을 때의 물리 픽셀 자리.</summary>
    public Native.RECT BodyRect { get; private set; }

    /// <summary>들어와 있는 중이거나 들어와 있는지(나가는 중이 아니고).</summary>
    public bool IsShown => IsVisible && !_leaving;

    /// <summary>
    /// 버튼 본체의 왼쪽 위가 (x, y) 물리 픽셀에 오도록 들어온다. 화면(bounds)에서 가장 가까운 모서리 바깥에서 출발한다.
    /// </summary>
    public void Enter(int x, int y, double scale, System.Drawing.Rectangle bounds)
    {
        int pad = (int)Math.Round(Shadow * scale), size = (int)Math.Round(Size * scale);
        BodyRect = new Native.RECT { Left = x, Top = y, Right = x + size, Bottom = y + size };
        _home = (x - pad, y - pad);
        // 가장 가까운 화면 모서리 바깥(그림자까지 다 숨는 곳).
        int toLeft = x - bounds.Left, toRight = bounds.Right - (x + size), toTop = y - bounds.Top, toBottom = bounds.Bottom - (y + size);
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
        Slide(_home, Motion.Normal);
    }

    /// <summary>들어온 모서리 쪽으로 나가고 숨는다.</summary>
    public void Leave()
    {
        if (!IsVisible || _leaving) return;
        _leaving = true;
        Slide(_away, Motion.Fast);
    }

    private void Slide((int X, int Y) to, double ms)
    {
        _from = _now;
        _to = to;
        _ms = Motion.Ms(ms);
        _clock.Restart();
        if (_moving) return;
        _moving = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double t = _ms <= 0 ? 1 : Math.Min(1, _clock.Elapsed.TotalMilliseconds / _ms);
        double k = Motion.Decel(t); // 나갈 때도 감속: 가속 곡선은 끝 무렵까지 멈춰 있다가 툭 사라져 보인다
        _now = ((int)Math.Round(_from.X + (_to.X - _from.X) * k), (int)Math.Round(_from.Y + (_to.Y - _from.Y) * k));
        Move(_now);
        if (t < 1) return;
        _moving = false;
        CompositionTarget.Rendering -= OnFrame;
        if (_leaving) Hide();
    }

    private void Move((int X, int Y) p) =>
        Native.SetWindowPos(Hwnd.Of(this), Hwnd.Topmost, p.X, p.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    /// <summary>보이는 중이고 (x, y)가 버튼 본체에서 margin 픽셀 안인지.</summary>
    public bool Near(int x, int y, int margin) =>
        IsShown && x >= BodyRect.Left - margin && x < BodyRect.Right + margin && y >= BodyRect.Top - margin && y < BodyRect.Bottom + margin;

    protected override void OnClosed(EventArgs e)
    {
        if (_moving) CompositionTarget.Rendering -= OnFrame;
        base.OnClosed(e);
    }
}
