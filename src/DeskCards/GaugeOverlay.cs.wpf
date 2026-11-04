using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace DeskCards;

/// <summary>커서 둘레에 그리는 원형 게이지. 클릭이 통과하는 투명 창이라 커서 자체는 그대로다.</summary>
internal sealed class GaugeOverlay : Window
{
    private const double Size = 44, Radius = 17, Thick = 4;
    private readonly Path _arc = new() { StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };

    public GaugeOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Width = Height = Size;
        Left = Top = -32000;

        var canvas = new Canvas();
        // 어떤 배경 위에서도 보이도록 어두운 테두리를 두른 옅은 트랙 위에 강조색 호를 그린다.
        var shade = new Ellipse { Width = 2 * Radius + Thick + 2, Height = 2 * Radius + Thick + 2, Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), StrokeThickness = Thick + 2 };
        Canvas.SetLeft(shade, Size / 2 - Radius - Thick / 2 - 1);
        Canvas.SetTop(shade, Size / 2 - Radius - Thick / 2 - 1);
        var track = new Ellipse { Width = 2 * Radius, Height = 2 * Radius, Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), StrokeThickness = Thick };
        Canvas.SetLeft(track, Size / 2 - Radius);
        Canvas.SetTop(track, Size / 2 - Radius);
        _arc.SetResourceReference(Shape.StrokeProperty, "Accent");
        canvas.Children.Add(shade);
        canvas.Children.Add(track);
        canvas.Children.Add(_arc);
        Content = canvas;

        SourceInitialized += (_, _) => Hwnd.MakeClickThrough(Hwnd.Of(this));
    }

    /// <summary>커서(물리 픽셀) 둘레에 progress(0~1)만큼 찬 게이지를 그린다. 화면 밖으로 잘리지 않게 안쪽으로 당긴다.</summary>
    public void Update(Native.POINT pt, double progress)
    {
        if (!IsVisible) Show();
        double s = Native.MonitorScaleAt(pt.X, pt.Y);
        int px = (int)Math.Round(Size * s);
        var b = Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y)).Bounds;
        int x = Math.Clamp(pt.X - px / 2, b.Left, b.Right - px);
        int y = Math.Clamp(pt.Y - px / 2, b.Top, b.Bottom - px);
        Native.SetWindowPos(Hwnd.Of(this), Hwnd.Topmost, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _arc.Data = Arc(Math.Clamp(progress, 0, 1));
    }

    /// <summary>12시에서 시계방향으로 progress만큼의 호.</summary>
    private static Geometry Arc(double progress)
    {
        var c = new Point(Size / 2, Size / 2);
        if (progress >= 0.999) return new EllipseGeometry(c, Radius, Radius);
        double a = progress * 2 * Math.PI;
        var start = new Point(c.X, c.Y - Radius);
        var end = new Point(c.X + Radius * Math.Sin(a), c.Y - Radius * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new System.Windows.Size(Radius, Radius), 0, progress > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig });
    }
}

