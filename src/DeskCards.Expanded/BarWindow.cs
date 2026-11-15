using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Linq;
using System.Windows.Shell;

namespace DeskCards.Expanded;

/// <summary>
/// 화면 가장자리에서 미끄러져 나오는 카드 판(아크릴). 나올 때와 들어갈 때 모두 처음엔 빠르고 끝으로 갈수록 느려진다.
/// 옛 WPF 카드 바의 창 부분을 옮긴 것이다. 안의 카드는 이후 단계에서 채운다.
/// </summary>
internal sealed class BarWindow : Window
{
    private const double OpenMs = 333, CloseMs = 250, Pad = 12;

    private readonly IBarHost _host;
    private readonly BarOptions _o;
    private readonly Border _panel;
    private readonly Canvas _canvas = new();
    private readonly Border _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
    private readonly System.Collections.Generic.List<(BarCardView View, Grid Box, Border Frame, BarCardData Data)> _entries = new();
    private IntPtr _previous;
    private double _from, _to, _offset; // 가장자리 바깥으로 밀려난 정도(0 = 다 나옴, 1 = 다 들어감)
    private double _animMs;
    private readonly Stopwatch _anim = new();
    private bool _animating, _closing;

    public BarWindow(IBarHost host, BarOptions options)
    {
        _host = host;
        _o = options;
        int edge = options.Edge;

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        Width = Height = 1;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        Title = "Desk Cards 카드 바";
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // 창은 가장자리에서 잘린 만큼만 보이고, 안의 판은 늘 다 나온 크기다. 먼저 보이는 쪽(가장자리 반대쪽)에 붙인다.
        _panel = new Border
        {
            Width = options.Final.Width / options.Scale,
            Height = options.Final.Height / options.Scale,
            Child = CreateSurface(),
            HorizontalAlignment = edge == 0 ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = edge == 1 ? VerticalAlignment.Bottom : VerticalAlignment.Top,
        };
        var p = options.Palette;
        Resources["Fg"] = Frozen(p.Fg);
        Resources["SubFg"] = Frozen(p.SubFg);
        Resources["HoverBg"] = Frozen(p.HoverBg);
        Resources["CardBg"] = Frozen(p.CardBg);
        Resources["CardBorder"] = Frozen(p.CardBorder);
        Resources["Accent"] = Frozen(p.Accent);
        var root = new Grid { ClipToBounds = true, Background = Frozen(options.Palette.PopupBg) };
        root.Children.Add(_panel);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            BeginClose();
            e.Handled = true;
        };
        Closed += (_, _) =>
        {
            if (_animating) CompositionTarget.Rendering -= OnFrame;
            _host.Closed();
        };
    }

    private static Brush Frozen(string argb)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(argb));
        b.Freeze();
        return b;
    }

    private volatile IntPtr _handle;

    /// <summary>창 핸들(다른 스레드에서 읽어도 안전하게 창이 만들어질 때 적어 둔다).</summary>
    public IntPtr HandleValue => _handle;

    private bool Side => _o.Edge is 0 or 2;

    /// <summary>바 두께(안쪽, DIP). 저장된 위치·크기의 단위.</summary>
    private double U => Math.Max(1, (Side ? _o.Final.Width : _o.Final.Height) / _o.Scale - 2 * Pad);

    private Size Inner => new(Math.Max(1, _o.Final.Width / _o.Scale - 2 * Pad), Math.Max(1, _o.Final.Height / _o.Scale - 2 * Pad));

    private UIElement CreateSurface()
    {
        _canvas.Width = Inner.Width;
        _canvas.Height = Inner.Height;
        _empty.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _host.EmptyClicked(_o.Final);
        };
        var grid = new Grid { Margin = new Thickness(Pad) };
        grid.Children.Add(_canvas);
        grid.Children.Add(_empty);
        return grid;
    }

    /// <summary>바 안 카드를 새 내용으로 다시 늘어놓는다.</summary>
    public void SetContent(BarContent content)
    {
        if (_closing && !IsVisible) return;
        _canvas.Children.Clear();
        _entries.Clear();
        double u = U;
        foreach (var data in content.Cards)
        {
            var view = new BarCardView(_host, data);
            view.Apply(data, content.HoverExpand, content.HoverDelayMs);
            double w = data.W * u;
            double aspect = view.Height / Math.Max(1, view.Width);
            double k = w / Math.Max(1, view.Width);
            var scaled = new Border { Child = view, LayoutTransform = new ScaleTransform(k, k), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var frame = new Border
            {
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(-4),
                IsHitTestVisible = false,
                Visibility = Visibility.Hidden,
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
            var box = new Grid { Width = w, Height = w * aspect };
            box.Children.Add(scaled);
            box.Children.Add(frame);
            // 바탕화면 카드처럼 파일·아이콘을 끌어다 놓으면 그 그룹에 넣는다. 놓을 수 있는 동안 파란 테두리.
            box.AllowDrop = true;
            void Over(object? _, DragEventArgs a)
            {
                bool ok = a.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths &&
                    paths.Any(p => !string.Equals(System.IO.Path.GetDirectoryName(p), data.Folder.TrimEnd((char)92), StringComparison.OrdinalIgnoreCase));
                a.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
                frame.Visibility = ok ? Visibility.Visible : Visibility.Hidden;
                a.Handled = true;
            }
            box.DragEnter += Over;
            box.DragOver += Over;
            box.DragLeave += (_, _) => frame.Visibility = Visibility.Hidden;
            box.Drop += (_, a) =>
            {
                frame.Visibility = Visibility.Hidden;
                if (a.Data.GetData(DataFormats.FileDrop) is string[] paths) _host.AddToGroup(data.Group, paths);
                // 이동은 앱이 직접 하므로 원본 쪽에서 삭제하지 않도록 None을 돌려준다.
                a.Effects = DragDropEffects.None;
                a.Handled = true;
            };
            Canvas.SetLeft(box, data.X * u);
            Canvas.SetTop(box, data.Y * u);
            _canvas.Children.Add(box);
            _entries.Add((view, box, frame, data));
        }
        UpdateEmpty();
    }

    private void UpdateEmpty()
    {
        if (_entries.Count > 0) { _empty.Visibility = Visibility.Collapsed; return; }
        _empty.Visibility = Visibility.Visible;
        var text = new TextBlock
        {
            Text = "클릭하여 나만의 카드 바 만들기",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = Math.Max(80, Inner.Width - 16),
            Foreground = (Brush)Resources["Fg"],
        };
        _empty.Cursor = Cursors.Hand;
        _empty.MouseEnter += (_, _) => text.TextDecorations = TextDecorations.Underline;
        _empty.MouseLeave += (_, _) => text.TextDecorations = null;
        _empty.Child = text;
    }

    public void Open()
    {
        _previous = GetForegroundWindow();
        _offset = 1;
        Show();
        // 활성 창이어야 아크릴이 흐려지지 않고 Esc를 받는다. 다른 앱을 쓰는 중이라 그냥은 앞으로 못 오므로 끌어온다.
        ForceForeground(new WindowInteropHelper(this).Handle);
        Activate();
        Animate(0, OpenMs);
    }

    public void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        // 바에서 앱을 열었으면 그 앱이 앞에 있으니 그대로 두고, 아니면 원래 쓰던 창으로 돌려준다.
        if (GetForegroundWindow() == new WindowInteropHelper(this).Handle && _previous != IntPtr.Zero) SetForegroundWindow(_previous);
        Animate(1, CloseMs);
    }

    // ----- 나오고 들어가기 -----

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _handle = hwnd;
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, new IntPtr(style & ~0x00080000L)); // WS_SYSMENU 제거
        long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new IntPtr(ex | 0x80L)); // WS_EX_TOOLWINDOW
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        SetDwm(hwnd, 20 /* USE_IMMERSIVE_DARK_MODE */, _o.Palette.Light ? 0 : 1);
        SetDwm(hwnd, 33 /* WINDOW_CORNER_PREFERENCE */, 2);
        SetDwm(hwnd, 38 /* SYSTEMBACKDROP_TYPE */, 3);
        PlaceAt(_offset);
    }

    /// <summary>
    /// offset(0~1)만큼 가장자리 바깥으로 밀린 자리에 둔다. 모니터 밖으로 나간 부분은 잘라서
    /// 옆 모니터에 비치지 않게 한다(창 크기를 줄이고 안의 판은 제자리).
    /// </summary>
    private void PlaceAt(double offset)
    {
        var f = _o.Final;
        var b = _o.Bounds;
        int travel = _o.Edge switch
        {
            0 => f.Right - b.Left,
            2 => b.Right - f.Left,
            1 => f.Bottom - b.Top,
            _ => b.Bottom - f.Top,
        };
        int d = (int)Math.Round(travel * offset);
        int x = f.Left, y = f.Top, w = f.Width, h = f.Height;
        switch (_o.Edge)
        {
            case 0: x -= d; if (x < b.Left) { w -= b.Left - x; x = b.Left; } break;
            case 2: x += d; w = Math.Min(w, b.Right - x); break;
            case 1: y -= d; if (y < b.Top) { h -= b.Top - y; y = b.Top; } break;
            default: y += d; h = Math.Min(h, b.Bottom - y); break;
        }
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, Math.Max(1, w), Math.Max(1, h), 0x0004 | 0x0010); // NOZORDER | NOACTIVATE
    }

    private void Animate(double to, double ms)
    {
        _from = _offset;
        _to = to;
        _animMs = SystemParameters.ClientAreaAnimation ? ms : 0;
        _anim.Restart();
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double t = _animMs <= 0 ? 1 : Math.Min(1, _anim.ElapsedMilliseconds / _animMs);
        _offset = _from + (_to - _from) * Decel(t);
        PlaceAt(_offset);
        if (t < 1) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
        if (_closing) Close();
    }

    /// <summary>cubic-bezier(0,0,0,1): 툭 나와서 스르륵 멈추는 감속.</summary>
    private static double Decel(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double t = Math.Cbrt(x);
        return 3 * (1 - t) * t * t + t * t * t;
    }

    // ----- Win32 -----

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private static void SetDwm(IntPtr hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    /// <summary>다른 앱이 앞에 있을 때도 이 창을 활성 창으로 가져온다(잠깐 앞 앱의 입력 큐에 붙었다가 뗀다).</summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fgThread, false);
    }
}
