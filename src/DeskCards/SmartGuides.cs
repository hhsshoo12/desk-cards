using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeskCards;

/// <summary>
/// 카드를 자유롭게 옮기되, 다른 카드나 화면 가운데와 줄이 맞으면 살짝 붙고 보라색 안내선을 보여 준다.
/// (Canva·Figma의 스마트 가이드처럼.) 좌표는 모두 물리 픽셀이다.
/// </summary>
internal static class SmartGuides
{
    /// <summary>안내선 하나. Vertical이면 x = Pos에 세로로 From~To, 아니면 y = Pos에 가로로.</summary>
    public readonly record struct Line(bool Vertical, int Pos, int From, int To);

    /// <summary>줄에 맞출 수 있는 카드 쪽 줄(앞·가운데·뒤).</summary>
    [Flags]
    private enum Edge { Start = 1, Mid = 2, End = 4, All = 7 }

    private readonly record struct Target(int Pos, int From, int To, bool Draw, Edge Edges = Edge.All);

    /// <summary>붙는 거리(DIP). 이 안으로 들어오면 줄에 맞춘다.</summary>
    private const double SnapDip = 8;

    /// <summary>
    /// 나란히 붙일 때 카드 사이에 두는 간격(DIP). Windows가 그리는 카드 그림자가 옆 카드 위에 드리우지 않게 한다.
    /// 완전히 붙이기(실험)를 켜면 0.
    /// </summary>
    public const double GapDip = 12;

    /// <summary>카드끼리 간격 없이 딱 붙일지(설정 › 일반 › 실험).</summary>
    public static bool Flush { get; set; }

    /// <summary>지금 설정에서 나란한 카드 사이 간격(물리 픽셀).</summary>
    public static int GapPx(double dpiScale) => Flush ? 0 : (int)Math.Round(GapDip * dpiScale);

    private static GuideOverlay? _overlay;

    /// <summary>안내선·자동 맞춤을 쓸지. 편집 막대나 설정에서 끄고 켠다.</summary>
    public static bool Enabled { get; set; } = true;

    private static bool Off => !Enabled || (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;

    /// <summary>
    /// 끌고 있는 위치(x, y)와 크기로 붙을 위치를 정한다. 작업 영역 밖으로는 나가지 않는다.
    /// 꺼져 있거나 Alt를 누르고 있으면 붙지 않는다.
    /// </summary>
    public static (int X, int Y, List<Line> Lines) Snap(int x, int y, int w, int h,
        IReadOnlyList<Native.RECT> others, System.Drawing.Rectangle wa, double dpiScale)
    {
        x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
        y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
        if (Off) return (x, y, new List<Line>());

        var (xs, ys) = Targets(others, wa, GapPx(dpiScale));

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
        if (Off) return (g, new List<Line>());

        var (xs, ys) = Targets(others, wa, GapPx(dpiScale));
        double threshold = SnapDip * dpiScale;
        double bestG = g, bestD = double.MaxValue;
        void Try(List<Target> targets, int origin, double size)
        {
            foreach (var t in targets)
                foreach (double frac in new[] { 1.0, 0.5 })
                {
                    if ((t.Edges & (frac == 1 ? Edge.End : Edge.Mid)) == 0) continue;
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

    /// <summary>
    /// 화면 가장자리는 붙기만 하고(선은 안 보여 준다), 화면 가운데와 다른 카드의 가장자리·가운데는 선도 보여 준다.
    /// 간격이 있으면 다른 카드 옆에 나란히 놓을 때 가장자리끼리 맞닿지 않고 간격만큼 떨어진 줄에 붙는다.
    /// (가장자리끼리 줄 맞춤 — 왼쪽끼리·오른쪽끼리 — 은 그대로 된다.)
    /// </summary>
    private static (List<Target> Xs, List<Target> Ys) Targets(IReadOnlyList<Native.RECT> others, System.Drawing.Rectangle wa, int gap)
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
            AddEdges(xs, o.Left, o.Right, o.Top, o.Bottom, gap);
            AddEdges(ys, o.Top, o.Bottom, o.Left, o.Right, gap);
        }
        return (xs, ys);
    }

    private static void AddEdges(List<Target> list, int lo, int hi, int from, int to, int gap)
    {
        list.Add(new((lo + hi) / 2, from, to, true));
        if (gap <= 0)
        {
            list.Add(new(lo, from, to, true));
            list.Add(new(hi, from, to, true));
            return;
        }
        // 맞닿는 짝(내 뒤 ↔ 상대 앞, 내 앞 ↔ 상대 뒤)은 빼고, 대신 간격만큼 떨어진 줄을 둔다.
        list.Add(new(lo, from, to, true, Edge.Start | Edge.Mid));
        list.Add(new(hi, from, to, true, Edge.Mid | Edge.End));
        list.Add(new(lo - gap, from, to, false, Edge.End));
        list.Add(new(hi + gap, from, to, false, Edge.Start));
    }

    /// <summary>
    /// 다른 카드와 나란히(겹치는 폭이 있게) 놓였을 때 마주 보는 가장자리 사이 거리 중 가장 짧은 것.
    /// 겹쳐 있으면 음수. 나란한 카드가 없으면 null. 가로(x)·세로(y) 방향을 따로 잰다.
    /// </summary>
    public static (int? X, int? Y) Separation(Native.RECT me, IReadOnlyList<Native.RECT> others)
    {
        int? bx = null, by = null;
        foreach (var o in others)
        {
            bool rowsOverlap = Math.Min(me.Bottom, o.Bottom) > Math.Max(me.Top, o.Top);
            bool colsOverlap = Math.Min(me.Right, o.Right) > Math.Max(me.Left, o.Left);
            if (rowsOverlap && !colsOverlap)
            {
                int d = Math.Max(o.Left - me.Right, me.Left - o.Right);
                if (bx == null || d < bx) bx = d;
            }
            if (colsOverlap && !rowsOverlap)
            {
                int d = Math.Max(o.Top - me.Bottom, me.Top - o.Bottom);
                if (by == null || d < by) by = d;
            }
        }
        return (bx, by);
    }

    /// <summary>
    /// 간격보다 가까이 붙어 있는 카드에서 간격만큼 떨어지도록 옮길 양(물리 픽셀). 겹친 경우는 건드리지 않는다.
    /// </summary>
    public static (int Dx, int Dy) PushApart(Native.RECT me, IReadOnlyList<Native.RECT> others, int gap)
    {
        int dx = 0, dy = 0, best = int.MaxValue, bestY = int.MaxValue;
        foreach (var o in others)
        {
            bool rowsOverlap = Math.Min(me.Bottom, o.Bottom) > Math.Max(me.Top, o.Top);
            bool colsOverlap = Math.Min(me.Right, o.Right) > Math.Max(me.Left, o.Left);
            if (rowsOverlap && !colsOverlap)
            {
                bool rightOf = me.Left >= o.Right;
                int d = rightOf ? me.Left - o.Right : o.Left - me.Right;
                if (d < gap && d < best) { best = d; dx = rightOf ? gap - d : d - gap; }
            }
            if (colsOverlap && !rowsOverlap)
            {
                bool below = me.Top >= o.Bottom;
                int d = below ? me.Top - o.Bottom : o.Top - me.Bottom;
                if (d < gap && d < bestY) { bestY = d; dy = below ? gap - d : d - gap; }
            }
        }
        return (dx, dy);
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
        (int Off, Edge Edge)[] offs = { (0, Edge.Start), (size / 2, Edge.Mid), (size, Edge.End) };
        foreach (var t in targets)
            foreach (var (off, edge) in offs)
            {
                if ((t.Edges & edge) == 0) continue;
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
            Hwnd.MakeClickThrough(Hwnd.Of(this));
        };
    }

    public void Draw(System.Drawing.Rectangle wa, List<SmartGuides.Line> lines)
    {
        if (lines.Count == 0) { Clear(); return; }
        if (!IsVisible) Show();
        var hwnd = Hwnd.Of(this);
        if (_area != wa)
        {
            _area = wa;
            Native.SetWindowPos(hwnd, Hwnd.Topmost, wa.Left, wa.Top, wa.Width, wa.Height, Native.SWP_NOACTIVATE);
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
