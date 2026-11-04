using System;
using System.Runtime.InteropServices;

namespace DeskCards;

/// <summary>
/// 픽셀 하나하나에 투명도를 주는 창(UpdateLayeredWindow). 안내선·어두운 막·글자처럼 판 없이 그림만 올릴 때 쓴다.
/// XAML 창은 픽셀 단위 투명을 못 해서 이런 그림은 이 창으로 그린다. 클릭은 아래로 통과하고 활성화되지 않는다.
/// </summary>
internal sealed class LayeredWindow : IDisposable
{
    private readonly IntPtr _hwnd;
    private IntPtr _memDc, _dib;
    private bool _disposed;

    /// <param name="owner">소유자 창. 소유자가 있으면 z 순서가 그 창을 따라간다.</param>
    /// <param name="topmost">다른 모든 창 위에 둘지.</param>
    public LayeredWindow(IntPtr owner = default, bool topmost = false)
    {
        const uint WS_POPUP = 0x80000000;
        long ex = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        if (topmost) ex |= 0x8; // WS_EX_TOPMOST
        _hwnd = CreateWindowEx((uint)ex, "Static", "Desk Cards Layer", WS_POPUP, 0, 0, 1, 1, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    public IntPtr Handle => _hwnd;
    public bool IsShown { get; private set; }

    public void Show()
    {
        if (_disposed) return;
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
        IsShown = true;
    }

    public void Hide()
    {
        if (_disposed) return;
        ShowWindow(_hwnd, 0);
        IsShown = false;
    }

    /// <summary>화면 위치·크기(물리 픽셀)와 그림(미리 곱한 BGRA, 위에서 아래로)을 한꺼번에 바꾼다.</summary>
    public void Update(int x, int y, int w, int h, byte[] pixels)
    {
        if (_disposed || w <= 0 || h <= 0 || pixels.Length < w * h * 4) return;
        var screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (_memDc == IntPtr.Zero) _memDc = CreateCompatibleDC(screenDc);
            var info = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
            };
            var bitmap = CreateDIBSection(screenDc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) return;
            var old = SelectObject(_memDc, bitmap);
            Marshal.Copy(pixels, 0, bits, w * h * 4);

            var dest = new Native.POINT { X = x, Y = y };
            var size = new SIZE { cx = w, cy = h };
            var src = new Native.POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(_hwnd, screenDc, ref dest, ref size, _memDc, ref src, 0, ref blend, 2);
            SelectObject(_memDc, old);
            if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
            _dib = bitmap;
        }
        finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    /// <summary>같은 그림을 다른 자리에 옮긴다.</summary>
    public void Move(int x, int y, int w, int h)
    {
        if (_disposed || _dib == IntPtr.Zero) return;
        var dest = new Native.POINT { X = x, Y = y };
        var size = new SIZE { cx = w, cy = h };
        var screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            var src = new Native.POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            var old = SelectObject(_memDc, _dib);
            UpdateLayeredWindow(_hwnd, screenDc, ref dest, ref size, _memDc, ref src, 0, ref blend, 2);
            SelectObject(_memDc, old);
        }
        finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    /// <summary>한 가지 색으로 칠한 직사각형 그림(미리 곱한 BGRA)을 만든다.</summary>
    public static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[w * h * 4];
        byte pr = (byte)(r * a / 255), pg = (byte)(g * a / 255), pb = (byte)(b * a / 255);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pb; pixels[i + 1] = pg; pixels[i + 2] = pr; pixels[i + 3] = a;
        }
        return pixels;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero) Native.DestroyWindow(_hwnd);
        if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref Native.POINT pptDst, ref SIZE size, IntPtr src, ref Native.POINT pptSrc, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref Native.BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
