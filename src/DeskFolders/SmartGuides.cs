using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeskFolders;

/// <summary>
/// 카드를 자유롭게 옮기되, 다른 카드나 화면 가운데와 줄이 맞으면 살짝 붙고 보라색 안내선을 보여 준다.
/// (Canva·Figma의 스마트 가이드처럼.) 좌표는 모두 물리 픽셀이다.
/// </summary>
internal static class SmartGuides
{
    /// <summary>안내선 하나. Vertical이면 x = Pos에 세로로 From~To, 아니면 y = Pos에 가로로.</summary>
    public readonly record struct Line(bool Vertical, int Pos, int From, int To);

    private readonly record struct Target(int Pos, int From, int To, bool Draw);

    /// <summary>붙는 거리(DIP). 이 안으로 들어오면 줄에 맞춘다.</summary>
    private const double SnapDip = 8;

    private static GuideOverlay? _overlay;

    /// <summary>
    /// 끌고 있는 위치(x, y)와 크기로 붙을 위치를 정한다. 작업 영역 밖으로는 나가지 않는다.
    /// Alt를 누르고 있으면 붙지 않는다.
    /// </summary>
    public static (int X, int Y, List<Line> Lines) Snap(int x, int y, int w, int h,
        IReadOnlyList<Native.RECT> others, System.Drawing.Rectangle wa, double dpiScale)
    {
        x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
        y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
        if ((Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0) return (x, y, new List<Line>());

        var (xs, ys) = Targets(others, wa);

        int threshold = (int)Math.Round(SnapDip * dpiScale);
        x += Nearest(x, w, xs, threshold);
        y += Nearest(y, h, ys, threshold);
        x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
        y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));

        return (x, y, LinesFor(x, y, w, h, xs, ys));
    }

    /// <summary>
    /// 비율 고정 크기 조절용. 왼쪽 위(start)는 고정하고 배율 g만큼 키울 때, 오른쪽·아래 끝이나 가운데가
    /// 다른 카드·화면 가운데 줄에 가까우면 거기 맞는 배율로 바꾼다.
    /// </summary>
    public static (double G, List<Line> Lines) SnapScale(Native.RECT start, double g,
        IReadOnlyList<Native.RECT> others, System.Drawing.Rectangle wa, double dpiScale)
    {
        int x = start.Left, y = start.Top;
        double w0 = start.Right - start.Left, h0 = start.Bottom - start.Top;
        if ((Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0) return (g, new List<Line>());

        var (xs, ys) = Targets(others, wa);
        double threshold = SnapDip * dpiScale;
        double bestG = g, bestD = double.MaxValue;
        void Try(List<Target> targets, int origin, double size)
        {
            foreach (var t in targets)
                foreach (double frac in new[] { 1.0, 0.5 })
                {
                    double d = Math.Abs(t.Pos - (origin + size * g * frac));
                    if (d <= threshold && d < bestD && t.Pos > origin)
                    {
                        bestD = d;
                        bestG = (t.Pos - origin) / (size * frac);
                    }
                }
        }
        Try(xs, x, w0);
        Try(ys, y, h0);

        int w = (int)Math.Round(w0 * bestG), h = (int)Math.Round(h0 * bestG);
        return (bestG, LinesFor(x, y, w, h, xs, ys));
    }

    /// <summary>화면 가장자리는 붙기만 하고(선은 안 보여 준다), 화면 가운데와 다른 카드의 가장자리·가운데는 선도 보여 준다.</summary>
    private static (List<Target> Xs, List<Target> Ys) Targets(IReadOnlyList<Native.RECT> others, System.Drawing.Rectangle wa)
    {
        var xs = new List<Target>
        {
            new(wa.Left, wa.Top, wa.Bottom, false),
            new(wa.Right, wa.Top, wa.Bottom, false),
            new((wa.Left + wa.Right) / 2, wa.Top, wa.Bottom, true),
        };
        var ys = new List<Target>
        {
            new(wa.Top, wa.Left, wa.Right, false),
            new(wa.Bottom, wa.Left, wa.Right, false),
            new((wa.Top + wa.Bottom) / 2, wa.Left, wa.Right, true),
        };
        foreach (var o in others)
        {
            xs.Add(new(o.Left, o.Top, o.Bottom, true));
            xs.Add(new(o.Right, o.Top, o.Bottom, true));
            xs.Add(new((o.Left + o.Right) / 2, o.Top, o.Bottom, true));
            ys.Add(new(o.Top, o.Left, o.Right, true));
            ys.Add(new(o.Bottom, o.Left, o.Right, true));
            ys.Add(new((o.Top + o.Bottom) / 2, o.Left, o.Right, true));
        }
        return (xs, ys);
    }

    /// <summary>실제로 맞은 줄마다 선을 긋는다. 선은 카드와 맞춘 대상을 모두 덮는 길이로.</summary>
    private static List<Line> LinesFor(int x, int y, int w, int h, List<Target> xs, List<Target> ys)
    {
        var lines = new List<Line>();
        int[] offX = { 0, w / 2, w }, offY = { 0, h / 2, h };
        var seen = new HashSet<(bool, int)>();
        foreach (var t in xs)
        {
            if (!t.Draw || !Hits(x, offX, t.Pos) || !seen.Add((true, t.Pos))) continue;
            lines.Add(new Line(true, t.Pos, Math.Min(t.From, y), Math.Max(t.To, y + h)));
        }
        foreach (var t in ys)
        {
            if (!t.Draw || !Hits(y, offY, t.Pos) || !seen.Add((false, t.Pos))) continue;
            lines.Add(new Line(false, t.Pos, Math.Min(t.From, x), Math.Max(t.To, x + w)));
        }
        return lines;
    }

    /// <summary>카드의 앞·가운데·뒤 줄 중 대상 줄에 가장 가까운 것까지의 이동량(없으면 0).</summary>
    private static int Nearest(int start, int size, List<Target> targets, int threshold)
    {
        int best = 0, bestAbs = int.MaxValue;
        int[] offs = { 0, size / 2, size };
        foreach (var t in targets)
            foreach (int off in offs)
            {
                int d = t.Pos - (start + off);
                if (Math.Abs(d) <= threshold && Math.Abs(d) < bestAbs) { best = d; bestAbs = Math.Abs(d); }
            }
        return best;
    }

    private static bool Hits(int start, int[] offs, int pos)
    {
        foreach (int off in offs)
            if (Math.Abs(start + off - pos) <= 1) return true;
        return false;
    }

    public static void Show(System.Drawing.Rectangle wa, List<Line> lines)
    {
        _overlay ??= new GuideOverlay();
        _overlay.Draw(wa, lines);
    }

    public static void Hide() => _overlay?.Clear();
}

/// <summary>안내선을 그리는 투명 창. 클릭은 그대로 아래로 통과한다.</summary>
internal sealed class GuideOverlay : Window
{
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly Canvas _canvas = new();
    private System.Drawing.Rectangle _area;

    public GuideOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Width = Height = 1;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
                new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        };
    }

    public void Draw(System.Drawing.Rectangle wa, List<SmartGuides.Line> lines)
    {
        if (lines.Count == 0) { Clear(); return; }
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (_area != wa)
        {
            _area = wa;
            Native.SetWindowPos(hwnd, HWND_TOPMOST, wa.Left, wa.Top, wa.Width, wa.Height, Native.SWP_NOACTIVATE);
        }

        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _canvas.Children.Clear();
        const double thick = 1.5;
        foreach (var l in lines)
        {
            var r = new Rectangle { Fill = GuideBrush };
            if (l.Vertical)
            {
                r.Width = thick;
                r.Height = (l.To - l.From) / s;
                Canvas.SetLeft(r, (l.Pos - wa.Left) / s - thick / 2);
                Canvas.SetTop(r, (l.From - wa.Top) / s);
            }
            else
            {
                r.Height = thick;
                r.Width = (l.To - l.From) / s;
                Canvas.SetTop(r, (l.Pos - wa.Top) / s - thick / 2);
                Canvas.SetLeft(r, (l.From - wa.Left) / s);
            }
            _canvas.Children.Add(r);
        }
    }

    public void Clear()
    {
        _canvas.Children.Clear();
        if (IsVisible) Hide();
        _area = default;
    }

    private static readonly Brush GuideBrush = MakeBrush();

    private static Brush MakeBrush()
    {
        var b = new SolidColorBrush(Color.FromRgb(0xA8, 0x55, 0xF7)); // 보라
        b.Freeze();
        return b;
    }
}
