using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DeskCards;

/// <summary>
/// 카드를 가리키는 말풍선. 링크처럼 보이는 문구를 누르면 click을 부르고 닫힌다.
/// 활성 창을 빼앗지 않고, 몇 초 뒤 저절로 사라진다. 한 번에 하나만 뜬다.
/// </summary>
internal sealed class BalloonTip : Window
{
    private const double Tail = 8;
    private static BalloonTip? _current;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };

    private BalloonTip(string text, string hint, Action click, bool tailUp)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        Title = "Desk Cards Tip";

        var link = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        link.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var sub = new TextBlock { Text = hint, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
        var body = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 360,
            Child = new StackPanel { Children = { link, sub } },
        };
        body.SetResourceReference(Border.BackgroundProperty, "FlyoutBg");
        body.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

        // 꼬리: 몸통과 같은 색의 삼각형. 몸통 테두리 1px과 겹쳐 이어져 보이게 한다.
        var tail = new Polygon
        {
            Points = tailUp
                ? new PointCollection { new(0, Tail + 1), new(Tail, 0), new(Tail * 2, Tail + 1) }
                : new PointCollection { new(0, 0), new(Tail, Tail + 1), new(Tail * 2, 0) },
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = tailUp ? new Thickness(0, 0, 0, -1) : new Thickness(0, -1, 0, 0),
        };
        tail.SetResourceReference(Shape.FillProperty, "FlyoutBg");

        var stack = new StackPanel { Margin = new Thickness(4) };
        if (tailUp) { stack.Children.Add(tail); stack.Children.Add(body); }
        else { stack.Children.Add(body); stack.Children.Add(tail); }
        Content = stack;

        Cursor = Cursors.Hand;
        MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        MouseLeave += (_, _) => link.TextDecorations = null;
        MouseLeftButtonUp += (_, _) => { Close(); click(); };
        _timer.Tick += (_, _) => Close();
        SourceInitialized += (_, _) => Hwnd.MakeNoActivateTool(Hwnd.Of(this));
        Closed += (_, _) => { _timer.Stop(); if (_current == this) _current = null; };
    }

    /// <summary>카드 자리(물리 픽셀) 아래 가운데에 띄운다. 아래에 자리가 없으면 위에.</summary>
    public static void Show(Native.RECT target, string text, string hint, Action click)
    {
        CloseCurrent();
        var wa = DesktopGrid.WorkAreaAt((target.Left + target.Right) / 2, (target.Top + target.Bottom) / 2);
        // 크기는 띄워 봐야 알 수 있으니, 먼저 아래 방향으로 재 보고 자리가 없으면 위 방향으로 다시 만든다.
        bool up = true;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var tip = new BalloonTip(text, hint, click, tailUp: up);
            tip.Left = -10000;
            tip.Top = -10000;
            tip.Show();
            double s = VisualTreeHelper.GetDpi(tip).DpiScaleX;
            int w = (int)Math.Ceiling(tip.ActualWidth * s), h = (int)Math.Ceiling(tip.ActualHeight * s);
            int y = up ? target.Bottom : target.Top - h;
            if (up && y + h > wa.Bottom && attempt == 0) { tip.Close(); up = false; continue; }
            int x = Math.Max(wa.Left, Math.Min((target.Left + target.Right) / 2 - w / 2, wa.Right - w));
            Native.SetWindowPos(Hwnd.Of(tip), Hwnd.Topmost, x, Math.Max(wa.Top, y), 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            tip._timer.Start();
            _current = tip;
            return;
        }
    }

    public static void CloseCurrent() => _current?.Close();
}
