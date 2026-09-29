using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace DeskCards;

/// <summary>
/// Windows 11 바탕화면 우클릭 메뉴 모양의 메뉴: 아크릴 배경, 둥근 모서리, 왼쪽 아이콘.
/// WPF ContextMenu는 팝업이라 시스템 배경(아크릴)을 못 쓰므로 작은 창으로 만든다.
/// 활성 창이 되어 키보드를 받고, 다른 곳을 누르면(비활성이 되면) 닫힌다.
/// </summary>
internal sealed class FluentMenu : Window
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    private static FluentMenu? _open;

    /// <summary>떠 있는 메뉴가 있는지.</summary>
    public static bool IsOpen => _open != null;

    private sealed class Row
    {
        public required Border Ui;
        public Action? Act;
        public bool Enabled;
    }

    private readonly StackPanel _list = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly Border _body;
    private readonly TranslateTransform _slide = new();
    private readonly List<Row> _rows = new();
    private Row? _hot;
    private Func<Size, Point>? _place;
    private Point _anchor; // 띄울 자리(물리 픽셀). 이 모니터의 배율로 크기를 계산한다.
    private bool _closing;

    public FluentMenu()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        Title = "Desk Cards";
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _body = new Border { Child = _list, MinWidth = 200, RenderTransform = _slide };
        var root = new Border { Child = _body };
        root.SetResourceReference(Border.BackgroundProperty, "PopupBg");
        Content = root;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += (_, _) => CloseMenu();
        PreviewKeyDown += (_, e) => e.Handled = HandleKey(e.Key == Key.System ? e.SystemKey : e.Key);
    }

    // ----- 만들기 -----

    /// <param name="glyph">Segoe Fluent Icons 글자. image가 있으면 그림을 대신 쓴다.</param>
    public FluentMenu Item(string glyph, string text, Action act, bool enabled = true, ImageSource? image = null, bool danger = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement icon;
        if (image != null)
        {
            var img = new Image { Source = image, Width = 16, Height = 16 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            icon = img;
        }
        else icon = Glyph(glyph, 16, danger);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(12, 0, 0, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);

        var label = new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 1) };
        label.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Danger" : "Fg");
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var ui = new Border
        {
            Height = 34,
            Margin = new Thickness(4, 1, 4, 1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Child = grid,
            Opacity = enabled ? 1 : 0.4,
        };
        var row = new Row { Ui = ui, Act = act, Enabled = enabled };
        if (enabled)
        {
            ui.MouseEnter += (_, _) => SetHot(row);
            ui.MouseLeftButtonUp += (_, e) => { e.Handled = true; Invoke(row); };
        }
        _rows.Add(row);
        _list.Children.Add(ui);
        return this;
    }

    public FluentMenu Separator()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
        line.SetResourceReference(Border.BackgroundProperty, "MenuLine");
        _list.Children.Add(line);
        return this;
    }

    private static TextBlock Glyph(string glyph, double size, bool danger)
    {
        var t = new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Danger" : "Fg");
        return t;
    }

    // ----- 띄우기 -----

    /// <summary>마우스 커서 자리에 띄운다. 이미 떠 있는 다른 메뉴는 닫는다.</summary>
    public void ShowAtCursor()
    {
        Native.GetCursorPos(out var pt);
        ShowAt(pt.X, pt.Y);
    }

    /// <summary>화면 좌표(물리 픽셀)에 왼쪽 위를 맞춰 띄운다. 화면 밖으로 나가면 반대쪽으로 뒤집는다.</summary>
    public void ShowAt(int x, int y)
    {
        _open?.CloseMenu();
        _open = this;
        _anchor = new Point(x, y);
        _place = size =>
        {
            var wa = DesktopGrid.WorkAreaAt(x, y);
            double left = x + size.Width > wa.Right ? x - size.Width : x;
            double top = y + size.Height > wa.Bottom ? y - size.Height : y;
            return new Point(Math.Max(wa.Left, Math.Min(left, wa.Right - size.Width)),
                             Math.Max(wa.Top, Math.Min(top, wa.Bottom - size.Height)));
        };
        FitToContent();
        Show();
        Activate();
    }

    /// <summary>
    /// 내용 크기에 창을 맞춘다. WindowChrome으로 틀을 없앤 창은 SizeToContent가 없어진 틀만큼 크게 잡아서
    /// 아래에 빈 자리가 남고 화면 끝 계산도 틀어지므로, 직접 재서 정한다.
    /// </summary>
    private void FitToContent()
    {
        var content = (UIElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Width = Math.Ceiling(content.DesiredSize.Width);
        Height = Math.Ceiling(content.DesiredSize.Height);
    }

    /// <summary>띄울 모니터 기준 물리 픽셀 크기.</summary>
    private Size PhysicalSize()
    {
        double s = Native.MonitorScaleAt((int)_anchor.X, (int)_anchor.Y);
        return new Size(Math.Ceiling(Width * s), Math.Ceiling(Height * s));
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Hwnd.Of(this);
        Hwnd.RemoveSysMenu(hwnd);
        Hwnd.MakeTool(hwnd);
        Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        // 처음부터 띄울 자리의 모니터에 제 크기로 둔다(그 모니터 배율로 크기가 정해진다).
        if (_place != null)
        {
            var size = PhysicalSize();
            var p = _place(size);
            Native.SetWindowPos(Hwnd.Of(this), IntPtr.Zero, (int)p.X, (int)p.Y, (int)size.Width, (int)size.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_place != null && Native.GetWindowRect(Hwnd.Of(this), out var r))
            Move(_place(new Size(r.Right - r.Left, r.Bottom - r.Top)));

        _body.BeginAnimation(OpacityProperty, Motion.In(0, 1, Motion.Fast));
        _slide.BeginAnimation(TranslateTransform.YProperty, Motion.In(-8, 0, Motion.Normal));
        Keyboard.Focus(this);
    }

    private void Move(Point p) =>
        Native.SetWindowPos(Hwnd.Of(this), IntPtr.Zero, (int)p.X, (int)p.Y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    // ----- 마우스 · 키보드 -----

    private void SetHot(Row? row)
    {
        if (_hot == row) return;
        _hot?.Ui.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        _hot = row;
        _hot?.Ui.SetResourceReference(Border.BackgroundProperty, "HoverBg");
    }

    private void Invoke(Row row)
    {
        if (!row.Enabled || _closing) return;
        var act = row.Act;
        CloseMenu();
        // 메뉴가 사라진 다음에 실행해야 새로 뜨는 창(대화 상자 등)이 활성 창이 된다.
        if (act != null) Dispatcher.BeginInvoke(act);
    }

    private bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Down: MoveHot(+1); return true;
            case Key.Up: MoveHot(-1); return true;
            case Key.Enter or Key.Space:
                if (_hot != null) Invoke(_hot);
                return true;
            case Key.Escape or Key.F10 or Key.LeftAlt or Key.RightAlt:
                CloseMenu();
                return true;
        }
        return false;
    }

    private void MoveHot(int dir)
    {
        var enabled = _rows.FindAll(r => r.Enabled);
        if (enabled.Count == 0) return;
        int i = _hot == null ? (dir > 0 ? 0 : enabled.Count - 1)
            : (enabled.IndexOf(_hot) + dir + enabled.Count) % enabled.Count;
        SetHot(enabled[i]);
    }

    // ----- 닫기 -----

    private void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        if (_open == this) _open = null;
        Dispatcher.BeginInvoke(Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_open == this) _open = null;
        base.OnClosed(e);
    }
}
