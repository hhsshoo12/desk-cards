using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace DeskCards;

/// <summary>
/// 카드를 가리키는 안내(WinUI TeachingTip 모양): 제목 · 닫기(×) / 설명 / [동작 버튼] ...... 다시 보지 않기.
/// 카드 아래 가운데에(자리가 없으면 위에) 띄운다. 활성 창을 빼앗지 않고, 닫기·버튼·다시 보지 않기를 누르거나
/// 다음 안내가 뜨면 닫힌다. 한 번에 하나만 뜬다.
/// </summary>
internal sealed class BalloonTip : ToolWindow
{
    /// <summary>안내 내용. NeverShow가 있으면 오른쪽 아래에 "다시 보지 않기"를 둔다.</summary>
    public sealed record Tip(string Title, string Subtitle, string Action, Action OnAction, Action? NeverShow = null);

    private const double BodyWidth = 320, Gap = 8;

    private static BalloonTip? _current;
    private bool _closing;

    private BalloonTip(Tip tip) : base("Desk Cards Tip", Build(tip, out var dismiss))
    {
        dismiss.Target = this;
        Hwnd.MakeNoActivateTool(Handle);
        Closed += (_, _) => { if (_current == this) _current = null; };
    }

    /// <summary>안내 내용 화면. 닫는 동작은 창이 만들어진 뒤 연결하므로 바구니(Dismisser)를 같이 돌려준다.</summary>
    private static FrameworkElement Build(Tip tip, out Dismisser dismiss)
    {
        var d = dismiss = new Dismisser();

        var title = new TextBlock
        {
            Text = tip.Title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 8, 0),
            Foreground = Theme.Brush("Fg"),
        };
        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -8, 0),
        };
        ToolTipService.SetToolTip(close, "닫기");
        close.Click += (_, _) => d.Dismiss();
        Grid.SetColumn(close, 1);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(title);
        head.Children.Add(close);

        var subtitle = new TextBlock
        {
            Text = tip.Subtitle,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Theme.Brush("Fg"),
        };

        var action = new Button { Content = tip.Action, MinWidth = 96, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        action.Click += (_, _) => { d.Dismiss(); tip.OnAction(); };
        var foot = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.Children.Add(action);
        if (tip.NeverShow is { } never)
        {
            var mute = new HyperlinkButton
            {
                Content = "다시 보지 않기",
                FontSize = 12,
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            mute.Click += (_, _) => { d.Dismiss(); never(); };
            Grid.SetColumn(mute, 1);
            foot.Children.Add(mute);
        }

        var stack = new StackPanel { Children = { head, subtitle, foot } };
        return new Border
        {
            Width = BodyWidth,
            Padding = new Thickness(12),
            Background = Theme.Brush("PopupBg"),
            Child = stack,
        };
    }

    private sealed class Dismisser
    {
        public BalloonTip? Target;
        public void Dismiss() => Target?.Dismiss();
    }

    /// <summary>카드 자리(물리 픽셀) 아래 가운데에 띄운다. 아래에 자리가 없으면 위에.</summary>
    public static void Show(Native.RECT target, Tip content)
    {
        CloseNow();
        var tip = new BalloonTip(content);
        tip.PresentNear(target);
        _current = tip;
    }

    private void PresentNear(Native.RECT target)
    {
        int cx = (target.Left + target.Right) / 2;
        var wa = DesktopGrid.WorkAreaAt(cx, (target.Top + target.Bottom) / 2);
        var root = (FrameworkElement)Content;
        root.Measure(new Windows.Foundation.Size(BodyWidth, double.PositiveInfinity));
        double s = Native.MonitorScaleAt(cx, target.Bottom);
        int w = (int)Math.Ceiling(root.DesiredSize.Width * s), h = (int)Math.Ceiling(root.DesiredSize.Height * s);
        int gap = (int)Math.Round(Gap * s);
        int y = target.Bottom + gap;
        if (y + h > wa.Bottom) y = target.Top - gap - h;
        int x = Math.Max(wa.Left, Math.Min(cx - w / 2, wa.Right - w));
        Resize(new RectInt32(x, Math.Max(wa.Top, y), w, h));
        AppWindow.Show(false);
        Hwnd.SetZOrder(Handle, Hwnd.Topmost);
    }

    /// <summary>떠 있는 안내를 닫는다(편집을 끝내거나 다른 카드를 옮기기 시작할 때).</summary>
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
        Close();
    }
}
