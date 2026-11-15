using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeskCards.Expanded;

/// <summary>커서 둘레에 그리는 원형 게이지. 클릭이 통과하는 투명 창이라 커서 자체는 그대로다.</summary>
internal sealed class GaugeWindow : Window
{
    private const double Size = 44, Radius = 17, Thick = 4;
    private readonly Path _arc = new() { StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private string _accent = "";

    public GaugeWindow(string accent)
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
        canvas.Children.Add(shade);
        canvas.Children.Add(track);
        canvas.Children.Add(_arc);
        Content = canvas;
        SetAccent(accent);

        SourceInitialized += (_, _) =>
        {
            // 눌러도 활성화되지 않고 마우스가 통과하는 도구 창.
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
            SetWindowLongPtr(hwnd, -20, new IntPtr(ex | 0x80L | 0x08000000L | 0x20L | 0x80000L)); // TOOLWINDOW | NOACTIVATE | TRANSPARENT | LAYERED
        };
    }

    private void SetAccent(string accent)
    {
        if (accent == _accent) return;
        _accent = accent;
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent));
        b.Freeze();
        _arc.Stroke = b;
    }

    /// <summary>창 왼쪽 위(물리 픽셀)에 progress(0~1)만큼 찬 게이지를 그린다.</summary>
    public void Update(int x, int y, double progress, string accent)
    {
        SetAccent(accent);
        if (!IsVisible) Show();
        SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010); // TOPMOST, NOSIZE | NOACTIVATE
        _arc.Data = Arc(Math.Clamp(progress, 0, 1));
    }

    public void HideNow()
    {
        if (IsVisible) Hide();
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
        fig.Segments.Add(new ArcSegment(end, new Size(Radius, Radius), 0, progress > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig });
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
