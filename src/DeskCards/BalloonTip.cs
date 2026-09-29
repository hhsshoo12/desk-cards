using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DeskCards;

/// <summary>
/// 카드를 가리키는 안내(WinUI TeachingTip 모양): 제목 · 닫기(×) / 설명 / [동작 버튼] ...... 다시 보지 않기.
/// 꼬리는 카드 가운데를 가리킨다. 활성 창을 빼앗지 않고, 닫기·버튼·다시 보지 않기를 누르거나 다음 안내가 뜨면 닫힌다.
/// 한 번에 하나만 뜬다.
/// </summary>
internal sealed class BalloonTip : Window
{
    /// <summary>안내 내용. neverShow가 있으면 오른쪽 아래에 "다시 보지 않기"를 둔다.</summary>
    public sealed record Tip(string Title, string Subtitle, string Action, Action OnAction, Action? NeverShow = null);

    private const double TailH = 8, TailW = 16;
    /// <summary>그림자가 잘리지 않게 창 가장자리에 두는 여백.</summary>
    private const double Pad = 16;
    private const double BodyWidth = 320;
    private const string FontUi = "Segoe UI Variable Text, Segoe UI, Malgun Gothic";

    private static BalloonTip? _current;
    private readonly bool _tailUp;
    private readonly Grid _tail;
    private readonly Grid _root;
    private readonly TranslateTransform _shift = new();
    private bool _closing;

    private BalloonTip(Tip c, bool tailUp)
    {
        _tailUp = tailUp;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily(FontUi);
        Title = "Desk Cards Tip";

        // 제목 · 닫기
        var title = new TextBlock
        {
            Text = c.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 8, 0),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var close = IconButton("", "닫기", Dismiss);
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, -4, -8, 0);
        Grid.SetColumn(close, 1);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(title);
        head.Children.Add(close);

        var subtitle = new TextBlock { Text = c.Subtitle, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        // [동작 버튼] ...... 다시 보지 않기
        var action = AccentButton(c.Action, () => { Dismiss(); c.OnAction(); });
        var foot = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.Children.Add(action);
        if (c.NeverShow is { } never)
        {
            var mute = new TextBlock
            {
                Text = "다시 보지 않기",
                FontSize = 12,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 1),
                Background = Brushes.Transparent,
            };
            mute.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
            mute.MouseEnter += (_, _) => mute.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            mute.MouseLeave += (_, _) => mute.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
            mute.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dismiss(); never(); };
            Grid.SetColumn(mute, 1);
            foot.Children.Add(mute);
        }

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(subtitle);
        stack.Children.Add(foot);
        var body = new Border
        {
            Width = BodyWidth,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 12, 12, 12),
            Child = stack,
        };
        body.SetResourceReference(Border.BackgroundProperty, "FlyoutBg");
        body.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

        // 꼬리: 몸통과 같은 색의 삼각형. 몸통 테두리 1px 위에 겹쳐 이어져 보이게 하고, 비스듬한 두 변에만 테두리를 긋는다.
        var fill = new Polygon
        {
            Points = tailUp
                ? new PointCollection { new(0, TailH + 1), new(TailW / 2, 0), new(TailW, TailH + 1) }
                : new PointCollection { new(0, 0), new(TailW / 2, TailH + 1), new(TailW, 0) },
        };
        fill.SetResourceReference(Shape.FillProperty, "FlyoutBg");
        var edge = new Polyline
        {
            Points = tailUp
                ? new PointCollection { new(0, TailH), new(TailW / 2, 0), new(TailW, TailH) }
                : new PointCollection { new(0, 1), new(TailW / 2, TailH + 1), new(TailW, 1) },
            StrokeThickness = 1,
        };
        edge.SetResourceReference(Shape.StrokeProperty, "CardBorder");
        _tail = new Grid { Width = TailW, Height = TailH + 1, HorizontalAlignment = HorizontalAlignment.Left };
        _tail.Children.Add(fill);
        _tail.Children.Add(edge);
        Panel.SetZIndex(_tail, 1); // 몸통 테두리 위에 그려 꼬리와 몸통 사이 선을 가린다
        AimTail(Pad + BodyWidth / 2);

        var column = new StackPanel
        {
            // Fluent 플라이아웃 그림자(아래로 옅게). 글씨가 아닌 판에만 준다.
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.16 },
        };
        if (tailUp) { column.Children.Add(_tail); column.Children.Add(body); }
        else { column.Children.Add(body); column.Children.Add(_tail); }

        _root = new Grid { Margin = new Thickness(Pad), RenderTransform = _shift };
        _root.Children.Add(column);
        Content = _root;

        SourceInitialized += (_, _) => Hwnd.MakeNoActivateTool(Hwnd.Of(this));
        Closed += (_, _) => { if (_current == this) _current = null; };
    }

    /// <summary>꼬리 끝이 창 왼쪽에서 x(DIP)에 오도록 옮긴다. 몸통 모서리 둥근 곳에는 가지 않게 한다.</summary>
    private void AimTail(double x)
    {
        double left = Math.Clamp(x - Pad - TailW / 2, 12, BodyWidth - 12 - TailW);
        _tail.Margin = _tailUp ? new Thickness(left, 0, 0, -1) : new Thickness(left, -1, 0, 0);
    }

    /// <summary>카드 자리(물리 픽셀) 아래 가운데에 띄운다. 아래에 자리가 없으면 위에.</summary>
    public static void Show(Native.RECT target, Tip content)
    {
        CloseNow();
        var wa = DesktopGrid.WorkAreaAt((target.Left + target.Right) / 2, (target.Top + target.Bottom) / 2);
        int cx = (target.Left + target.Right) / 2;
        // 크기는 띄워 봐야 알 수 있으니, 먼저 아래쪽으로 재 보고 자리가 없으면 위쪽으로 다시 만든다.
        bool up = true;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var tip = new BalloonTip(content, tailUp: up) { Left = -10000, Top = -10000, Opacity = 0 };
            tip.Show();
            double s = VisualTreeHelper.GetDpi(tip).DpiScaleX;
            int w = (int)Math.Ceiling(tip.ActualWidth * s), h = (int)Math.Ceiling(tip.ActualHeight * s), pad = (int)Math.Round(Pad * s);
            // 그림자 여백만큼 창을 바깥으로 내어, 꼬리 끝이 카드 가장자리에 닿게 한다.
            int y = up ? target.Bottom - pad : target.Top - h + pad;
            if (up && y + h - pad > wa.Bottom && attempt == 0) { tip.Close(); up = false; continue; }
            int x = Math.Max(wa.Left - pad, Math.Min(cx - w / 2, wa.Right - w + pad));
            tip.AimTail((cx - x) / s);
            Native.SetWindowPos(Hwnd.Of(tip), Hwnd.Topmost, x, Math.Max(wa.Top - pad, y), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            _current = tip;
            // 꼬리 쪽(카드)에서 조금 떨어져 나오며 나타난다.
            tip.BeginAnimation(OpacityProperty, Motion.In(0, 1, Motion.Fast));
            tip._shift.BeginAnimation(TranslateTransform.YProperty, Motion.In(up ? -8 : 8, 0, Motion.Normal));
            return;
        }
    }

    /// <summary>떠 있는 안내를 흐려지며 닫는다(편집을 끝내거나 다른 카드를 옮기기 시작할 때).</summary>
    public static void CloseCurrent() => _current?.Dismiss();

    private static void CloseNow()
    {
        var tip = _current;
        _current = null;
        tip?.Close();
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        if (_current == this) _current = null;
        var fade = Motion.In(null, 0, Motion.Fast);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    // ----- 버튼 (설정 창 스타일은 이 창에 없으니 같은 모양으로 직접 그린다) -----

    private static Border AccentButton(string text, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 14, Margin = new Thickness(12, 0, 12, 1), VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "AccentFg");
        var b = new Border { MinWidth = 96, Height = 32, CornerRadius = new CornerRadius(4), Child = label, Cursor = Cursors.Arrow };
        b.SetResourceReference(Border.BackgroundProperty, "Accent");
        label.HorizontalAlignment = HorizontalAlignment.Center;
        b.MouseEnter += (_, _) => b.BeginAnimation(OpacityProperty, Motion.In(null, 0.9, Motion.Faster));
        b.MouseLeave += (_, _) => b.BeginAnimation(OpacityProperty, Motion.In(null, 1, Motion.Faster));
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; label.Opacity = 0.75; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            label.Opacity = 1;
            click();
        };
        return b;
    }

    private static Border IconButton(string glyph, string tip, Action click)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var hover = new Border { CornerRadius = new CornerRadius(4), Opacity = 0 };
        hover.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        var grid = new Grid { Background = Brushes.Transparent };
        grid.Children.Add(hover);
        grid.Children.Add(icon);
        var b = new Border { Width = 32, Height = 32, Child = grid, ToolTip = tip };
        b.MouseEnter += (_, _) => hover.BeginAnimation(OpacityProperty, Motion.In(null, 1, Motion.Faster));
        b.MouseLeave += (_, _) => hover.BeginAnimation(OpacityProperty, Motion.In(null, 0, Motion.Faster));
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }
}
