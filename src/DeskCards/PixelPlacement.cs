using System;
using System.Windows;

namespace DeskCards;

internal static class PixelPlacement
{
    public static void Move(Window window, Point pixels) => Native.SetWindowPos(Hwnd.Of(window), IntPtr.Zero,
        (int)Math.Round(pixels.X), (int)Math.Round(pixels.Y), 0, 0,
        Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

    public static Point Beside(Native.RECT card, Rect work, Size size, double scale, bool centered)
    {
        double width = size.Width * scale, height = size.Height * scale, gap = 8 * scale;
        double x = centered ? (card.Left + card.Right - width) / 2
            : card.Right + gap + width <= work.Right ? card.Right + gap : card.Left - gap - width;
        double y = centered ? card.Top + 96 * scale - height / 2 : card.Top;
        return new Point(Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - gap - width)),
            Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - gap - height)));
    }
}
