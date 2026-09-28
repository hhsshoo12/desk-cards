using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskCards;

/// <summary>
/// 카드 바 설정을 바꾸는 동안 화면에 잠깐 보여 주는 안내:
/// 바 두께 → 바가 나올 자리의 테두리, 인식 영역 → 가장자리의 어두운 띠, 식별 → 디스플레이마다 큰 번호.
/// 모두 클릭이 통과하는 창이고, 마지막으로 바꾼 뒤 잠시 지나면 사라진다.
/// </summary>
internal static class BarPreview
{
    private static readonly List<Window> _shown = new();
    private static readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    static BarPreview() => _hide.Tick += (_, _) => Clear();

    /// <summary>바가 다 나왔을 때의 자리를 강조색 테두리로.</summary>
    public static void Outline(Forms.Screen screen, ScreenEdge edge, int sizePercent)
    {
        var r = BarWindow.FinalRect(screen, edge, sizePercent);
        var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(3) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        Show(new[] { (r, (UIElement)frame) });
    }

    private static ZoneBand? _zone;

    /// <summary>
    /// 인식 영역(마우스를 대면 게이지가 도는 띠)을 반투명한 어두운 띠로. 카드 바처럼 가장자리에서 부드럽게 나오고,
    /// 값을 바꾸면 새 두께로 부드럽게 바뀌며, 마지막으로 바꾼 뒤 1.5초 있다가 들어간다.
    /// </summary>
    public static void Zone(Forms.Screen screen, ScreenEdge edge, int zone)
    {
        Clear();
        if (_zone == null || !_zone.Matches(screen, edge))
        {
            _zone?.Close();
            _zone = new ZoneBand(screen, edge);
        }
        _zone.ShowDepth(zone);
    }

    /// <summary>디스플레이마다 왼쪽 아래에 큰 번호(Windows 설정의 [식별]처럼).</summary>
    public static void Identify(IReadOnlyList<Forms.Screen> screens)
    {
        var items = new List<(System.Drawing.Rectangle, UIElement)>();
        for (int i = 0; i < screens.Count; i++)
        {
            var wa = screens[i].WorkingArea;
            double s = Native.MonitorScaleAt(wa.Left + 1, wa.Top + 1);
            int w = (int)(150 * s), h = (int)(170 * s), m = (int)(40 * s);
            var number = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 110,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            };
            var plate = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)), Child = number };
            items.Add((new(wa.Left + m, wa.Bottom - m - h, w, h), plate));
        }
        Show(items, TimeSpan.FromSeconds(2.5));
    }

    public static void Clear()
    {
        // 인식 영역 띠는 따로 들어간다(두께를 바꾸는 동안에는 테두리만 지운다).
        _hide.Stop();
        foreach (var w in _shown) w.Close();
        _shown.Clear();
    }

    private static void Show(IEnumerable<(System.Drawing.Rectangle rect, UIElement content)> items, TimeSpan? duration = null)
    {
        Clear();
        foreach (var (rect, content) in items)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                Left = -32000,
                Top = -32000,
                Width = 1,
                Height = 1,
                Content = content,
                Title = "Desk Cards 안내",
            };
            var r = rect;
            w.SourceInitialized += (_, _) =>
            {
                var hwnd = Hwnd.Of(w);
                Hwnd.MakeClickThrough(hwnd);
                Native.SetWindowPos(hwnd, Hwnd.Topmost, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);
            };
            w.Show();
            // 다른 배율의 모니터로 옮겨지면서 크기가 다시 잡힐 수 있어 한 번 더 맞춘다.
            Native.SetWindowPos(Hwnd.Of(w), Hwnd.Topmost, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE);
            _shown.Add(w);
        }
        _hide.Interval = duration ?? TimeSpan.FromMilliseconds(1500);
        _hide.Start();
    }
}
