using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace DeskCards;

/// <summary>
/// .dard 카드의 설정 화면(settings.html). 카드 옆에 아크릴 창으로 뜨고, 밖을 누르거나 Esc를 누르면 닫힌다.
/// 크기는 매니페스트의 settings.ratio로 정하고, 카드와 같은 주소에서 떠서 카드의 브라우저 저장소를 같이 본다.
/// </summary>
internal sealed class DardSettingsWindow : Window
{
    private const double HeaderH = 48, Gap = 8;

    private static DardSettingsWindow? _current;

    private readonly DardRuntime _runtime;
    private readonly DardView _view;
    private readonly DeskCard? _anchor;
    private bool _closing;

    private DardSettingsWindow(DardRuntime runtime, DardCardInfo card, DeskCard? anchor)
    {
        _runtime = runtime;
        _anchor = anchor;
        var ratio = runtime.Package.SettingsRatio!.Value;
        var size = DardPackage.SizeFor(ratio.W, ratio.H, DardPackage.SettingsArea);

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        Title = $"{card.Name} 설정";
        Width = Math.Round(size.Width);
        Height = Math.Round(size.Height) + HeaderH;
        Topmost = runtime.WindowFor(card.Id) is { IsEditing: true };
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        var title = new TextBlock
        {
            Text = Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(16, 0, 8, 0),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var close = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
            Width = 34,
            Height = 34,
            Margin = new Thickness(0, 0, 8, 0),
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = "닫기 (Esc)",
            Template = CloseTemplate(),
        };
        close.SetResourceReference(ForegroundProperty, "Fg");
        close.Click += (_, _) => SafeClose();
        var header = new DockPanel { Height = HeaderH };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);

        _view = new DardView(runtime, card.Id, settings: true, new Size(Width, Height - HeaderH)) { CloseRequested = SafeClose };
        var body = new Grid();
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderH) });
        body.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(_view, 1);
        body.Children.Add(header);
        body.Children.Add(_view);
        var root = new Border { Child = body };
        root.SetResourceReference(Border.BackgroundProperty, "PopupBg");
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = Hwnd.Of(this);
            Hwnd.RemoveSysMenu(hwnd);
            Hwnd.MakeTool(hwnd);
            Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        };
        Loaded += (_, _) => _view.FocusPage();
        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SafeClose(); e.Handled = true; } };
        Place();
    }

    public static void Open(DardRuntime runtime, DardCardInfo card, DeskCard? anchor)
    {
        _current?.SafeClose();
        var w = new DardSettingsWindow(runtime, card, anchor);
        _current = w;
        w.Show();
        Native.ForceForeground(Hwnd.Of(w));
        w.Activate();
    }

    /// <summary>이 .dard의 설정 화면이 떠 있으면 닫는다(카드를 지우거나 다시 불러올 때).</summary>
    public static void CloseFor(DardRuntime runtime)
    {
        if (_current != null && _current._runtime == runtime) _current.SafeClose();
    }

    /// <summary>카드 오른쪽에(자리가 없으면 왼쪽에), 위쪽을 맞춰 띄운다. 작업 영역 밖으로 나가지 않는다.</summary>
    private void Place()
    {
        var wa = SystemParameters.WorkArea;
        if (_anchor == null || Hwnd.Of(_anchor) == IntPtr.Zero || !Native.GetWindowRect(Hwnd.Of(_anchor), out var r))
        {
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
            return;
        }
        double s = Native.MonitorScaleOf(Hwnd.Of(_anchor));
        var screen = System.Windows.Forms.Screen.FromHandle(Hwnd.Of(_anchor)).WorkingArea;
        double l = screen.Left / s, t = screen.Top / s, rr = screen.Right / s, bb = screen.Bottom / s;
        double cardL = r.Left / s, cardR = r.Right / s, cardT = r.Top / s;
        double x = cardR + Gap + Width <= rr ? cardR + Gap : cardL - Gap - Width;
        Left = Math.Clamp(x, l + Gap, Math.Max(l + Gap, rr - Gap - Width));
        Top = Math.Clamp(cardT, t + Gap, Math.Max(t + Gap, bb - Gap - Height));
    }

    private static ControlTemplate CloseTemplate()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = bd };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("HoverBg"), "Bd"));
        template.Triggers.Add(hover);
        return template;
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        _view.Close();
        if (_current == this) _current = null;
        base.OnClosed(e);
    }
}
