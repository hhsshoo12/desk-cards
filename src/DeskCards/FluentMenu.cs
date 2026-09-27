using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace DeskCards;

/// <summary>
/// Windows 11 바탕화면 우클릭 메뉴 모양의 메뉴: 아크릴 배경, 둥근 모서리, 왼쪽 아이콘, 오른쪽 › 하위 메뉴.
/// WPF ContextMenu는 팝업이라 시스템 배경(아크릴)을 못 쓰므로 작은 창으로 만든다.
/// 맨 위 메뉴는 활성 창이 되어 키보드를 받고, 하위 메뉴는 활성화하지 않는 창으로 옆에 붙는다.
/// </summary>
internal sealed class FluentMenu : Window
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private const int SubDelayMs = 250;

    private static FluentMenu? _open;

    private sealed class Row
    {
        public required Border Ui;
        public Action? Act;
        public Action<FluentMenu>? Fill;
        public bool Enabled;
    }

    private readonly StackPanel _list = new() { Margin = new Thickness(0, 4, 0, 4) };
    private readonly Border _body;
    private readonly TranslateTransform _slide = new();
    private readonly List<Row> _rows = new();
    private readonly FluentMenu? _parent;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(SubDelayMs) };
    private Row? _hot, _hoverTarget, _subRow;
    private FluentMenu? _sub;
    private Func<Size, Point>? _place;
    private Point _anchor; // 띄울 자리(물리 픽셀). 이 모니터의 배율로 크기를 계산한다.
    private bool _closing, _running;

    public FluentMenu() : this(null) { }

    private FluentMenu(FluentMenu? parent)
    {
        _parent = parent;
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
        ShowActivated = parent == null;
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

        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); ApplyHover(); };
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        if (parent == null)
        {
            Deactivated += (_, _) => { if (!_running) CloseAll(); };
            PreviewKeyDown += (_, e) => e.Handled = Deepest().HandleKey(e.Key == Key.System ? e.SystemKey : e.Key);
        }
        else
        {
            // 하위 메뉴로 마우스가 들어오면 부모에서는 그 하위 메뉴를 연 줄이 계속 강조된다.
            MouseEnter += (_, _) => { parent._hoverTimer.Stop(); parent.SetHot(parent._subRow); };
        }
    }

    // ----- 만들기 -----

    /// <param name="glyph">Segoe Fluent Icons 글자. image가 있으면 그림을 대신 쓴다.</param>
    public FluentMenu Item(string glyph, string text, Action act, bool enabled = true, ImageSource? image = null, bool danger = false)
    {
        AddRow(glyph, text, image, danger, enabled, act, null);
        return this;
    }

    /// <summary>› 가 붙은 줄. 올려 두거나 누르면 옆에 하위 메뉴를 열고, fill이 그때 내용을 채운다.</summary>
    public FluentMenu Sub(string glyph, string text, Action<FluentMenu> fill, bool enabled = true)
    {
        AddRow(glyph, text, null, false, enabled, null, fill);
        return this;
    }

    public FluentMenu Separator()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
        line.SetResourceReference(Border.BackgroundProperty, "MenuLine");
        _list.Children.Add(line);
        return this;
    }

    private void AddRow(string glyph, string text, ImageSource? image, bool danger, bool enabled, Action? act, Action<FluentMenu>? fill)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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

        var label = new TextBlock { Text = text, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 1) };
        label.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Danger" : "Fg");
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (fill != null)
        {
            var chevron = Glyph("", 12, false);
            chevron.Margin = new Thickness(24, 0, 12, 0);
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }

        var ui = new Border
        {
            Height = 34,
            Margin = new Thickness(4, 1, 4, 1),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Child = grid,
            Opacity = enabled ? 1 : 0.4,
        };
        var row = new Row { Ui = ui, Act = act, Fill = fill, Enabled = enabled };
        if (enabled)
        {
            ui.MouseEnter += (_, _) => Hover(row);
            ui.MouseLeftButtonUp += (_, e) => { e.Handled = true; Invoke(row); };
        }
        _rows.Add(row);
        _list.Children.Add(ui);
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
        _open?.CloseAll();
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
        if (_parent != null) Hwnd.MakeNoActivateTool(hwnd);
        else
        {
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        }
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

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(160);
        _body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_parent == null ? -8 : 0, 0, dur) { EasingFunction = ease });
        if (_parent == null) Keyboard.Focus(this);
    }

    private void Move(Point p) =>
        Native.SetWindowPos(Hwnd.Of(this), IntPtr.Zero, (int)p.X, (int)p.Y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    // ----- 마우스 · 키보드 -----

    private void Hover(Row row)
    {
        SetHot(row);
        _hoverTarget = row;
        _hoverTimer.Stop();
        if (_sub != null && _subRow == row) return;
        if (row.Fill != null || _sub != null) _hoverTimer.Start();
    }

    /// <summary>잠깐 머문 줄 기준으로 하위 메뉴를 열거나 닫는다.</summary>
    private void ApplyHover()
    {
        if (_closing || _hoverTarget == null) return;
        if (_hoverTarget.Fill != null) OpenSub(_hoverTarget, keyboard: false);
        else CloseSub();
    }

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
        if (row.Fill != null) { OpenSub(row, keyboard: true); return; }
        var act = row.Act;
        var root = RootMenu();
        // 메뉴를 먼저 숨기고 실행해야 새로 뜨는 창(대화 상자, 탐색기 메뉴 등)이 앞에 온다.
        // 닫기(Closed)는 실행이 끝난 뒤에 알려서, 메뉴를 띄운 창이 그동안 스스로 닫히지 않게 한다.
        root._running = true;
        root.HideAll();
        Dispatcher.BeginInvoke(() =>
        {
            try { act?.Invoke(); }
            finally { root._running = false; root.CloseAll(); }
        });
    }

    private void HideAll()
    {
        _hoverTimer.Stop();
        _sub?.HideAll();
        Hide();
    }

    private void OpenSub(Row row, bool keyboard)
    {
        _hoverTimer.Stop();
        if (_sub != null && _subRow == row)
        {
            if (keyboard) _sub.MoveHot(+1, fromStart: true);
            return;
        }
        CloseSub();
        var sub = new FluentMenu(this);
        row.Fill!(sub);
        if (sub._rows.Count == 0) { sub.Close(); return; }
        _sub = sub;
        _subRow = row;
        SetHot(row);
        var anchor = row.Ui.PointToScreen(new Point(0, 0));
        var far = row.Ui.PointToScreen(new Point(row.Ui.ActualWidth, 0));
        Native.GetWindowRect(Hwnd.Of(this), out var me);
        sub._place = size =>
        {
            var wa = DesktopGrid.WorkAreaAt((int)far.X, (int)far.Y);
            double left = me.Right - 4 + size.Width <= wa.Right ? me.Right - 4 : me.Left - size.Width + 4;
            double top = Math.Min(anchor.Y - 5, wa.Bottom - size.Height);
            return new Point(Math.Max(wa.Left, left), Math.Max(wa.Top, top));
        };
        sub._anchor = far;
        sub.FitToContent();
        sub.Owner = this;
        sub.Show();
        if (keyboard) sub.MoveHot(+1, fromStart: true);
    }

    private void CloseSub()
    {
        if (_sub == null) return;
        _sub.CloseAll(parentToo: false);
        _sub = null;
        _subRow = null;
    }

    private FluentMenu RootMenu() => _parent?.RootMenu() ?? this;

    /// <summary>키보드를 받을 메뉴: 하위 메뉴 안에서 고른 줄이 있으면 그 하위 메뉴.</summary>
    private FluentMenu Deepest()
    {
        var m = this;
        while (m._sub is { _hot: not null } s) m = s;
        return m;
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
            case Key.Right:
                if (_hot?.Fill != null) OpenSub(_hot, keyboard: true);
                return true;
            case Key.Left or Key.Escape:
                if (_parent != null) _parent.CloseSub();
                else if (key == Key.Escape) CloseAll();
                return true;
            case Key.F10 or Key.LeftAlt or Key.RightAlt:
                RootMenu().CloseAll();
                return true;
        }
        return false;
    }

    private void MoveHot(int dir, bool fromStart = false)
    {
        var enabled = _rows.FindAll(r => r.Enabled);
        if (enabled.Count == 0) return;
        int i = fromStart || _hot == null ? (dir > 0 ? 0 : enabled.Count - 1)
            : (enabled.IndexOf(_hot) + dir + enabled.Count) % enabled.Count;
        SetHot(enabled[i]);
    }

    // ----- 닫기 -----

    private void CloseAll(bool parentToo = true)
    {
        if (_closing) return;
        _closing = true;
        _hoverTimer.Stop();
        CloseSub();
        if (_open == this) _open = null;
        Dispatcher.BeginInvoke(Close);
        if (parentToo) _parent?.CloseAll();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hoverTimer.Stop();
        if (_open == this) _open = null;
        base.OnClosed(e);
    }
}
