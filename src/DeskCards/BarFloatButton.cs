using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DeskCards;

/// <summary>
/// 카드 바 옆(화면 안쪽, 바의 시작 모서리)에 떠 있는 작은 설정 버튼. 바 안의 카드를 가리지 않으려고 바 밖에 따로 띄운다.
/// 바가 다 나오면 나타나고, 들어가기 시작하면 흐려지며 사라진다. 활성 창을 빼앗지 않는다.
/// </summary>
internal sealed class BarFloatButton : Window
{
    public const double Size = 36;
    /// <summary>그림자가 잘리지 않게 창 가장자리에 두는 여백.</summary>
    private const double Shadow = 12;

    private readonly Border _hover;
    private readonly TextBlock _icon;

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
        Opacity = 0;
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

    /// <summary>버튼 본체(그림자 제외)의 물리 픽셀 자리.</summary>
    public Native.RECT BodyRect { get; private set; }

    /// <summary>버튼 본체의 왼쪽 위를 (x, y) 물리 픽셀에 둔다.</summary>
    public void PlaceBody(int x, int y, double scale)
    {
        int pad = (int)Math.Round(Shadow * scale), size = (int)Math.Round(Size * scale);
        BodyRect = new Native.RECT { Left = x, Top = y, Right = x + size, Bottom = y + size };
        if (!IsVisible) Show();
        Native.SetWindowPos(Hwnd.Of(this), Hwnd.Topmost, x - pad, y - pad, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void FadeIn() => BeginAnimation(OpacityProperty, Motion.In(null, 1, Motion.Fast));

    public void FadeOut() => BeginAnimation(OpacityProperty, Motion.In(null, 0, Motion.Fast));

    /// <summary>보이는 중이고 (x, y)가 버튼 위인지.</summary>
    public bool Contains(int x, int y) =>
        IsVisible && Opacity > 0.01 && x >= BodyRect.Left && x < BodyRect.Right && y >= BodyRect.Top && y < BodyRect.Bottom;
}
