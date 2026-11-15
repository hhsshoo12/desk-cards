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
    /// <param name="clickThrough">true면 클릭이 아래로 통과한다. false면 눌림을 <see cref="Clicked"/>로 알리고 아래로 보내지 않는다.</param>
    public LayeredWindow(IntPtr owner = default, bool topmost = false, bool clickThrough = true)
    {
        const uint WS_POPUP = 0x80000000;
        long ex = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        if (clickThrough) ex |= Native.WS_EX_TRANSPARENT;
        if (topmost) ex |= 0x8; // WS_EX_TOPMOST
        _hwnd = CreateWindowEx((uint)ex, "Static", "Desk Cards Layer", WS_POPUP, 0, 0, 1, 1, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (!clickThrough)
        {
            // Static 컨트롤은 마우스를 통과시키므로(HTTRANSPARENT) 직접 받는다.
            _subclass = new Native.Subclass(_hwnd, (IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result) =>
            {
                result = IntPtr.Zero;
                switch (msg)
                {
                    case 0x0084: result = new IntPtr(1); return true; // WM_NCHITTEST → HTCLIENT
                    case 0x0201: Clicked?.Invoke(); return true; // WM_LBUTTONDOWN
                    case 0x0204: case 0x0205: return true; // 오른쪽 누름·뗌: 아무 일 없음
                    case 0x0046 when LockZOrder: // WM_WINDOWPOSCHANGING
                        var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
                        wp.flags |= Native.SWP_NOZORDER;
                        Marshal.StructureToPtr(wp, lParam, false);
                        return false;
                }
                return false;
            });
        }
    }

    private readonly Native.Subclass? _subclass;

    public event Action? Clicked;

    /// <summary>true인 동안은 눌러도 다른 창 위로 올라오지 않게 z 순서를 고정한다.</summary>
    public bool LockZOrder { get; set; }

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
    public void Update(int x, int y, int w, int h, byte[] pixels, byte alpha = 255)
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
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
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

    private byte[]? _srcPx, _halfPx;
    private int _srcW, _srcH, _halfW, _halfH;

    /// <summary>나중에 <see cref="DrawScaled"/>로 줄여 그릴 원본 그림(미리 곱한 BGRA, 위에서 아래로). 반으로 줄인 그림도 같이 만든다.</summary>
    public void SetSource(byte[] pixels, int w, int h)
    {
        if (_disposed || w <= 0 || h <= 0 || pixels.Length < w * h * 4) return;
        _srcPx = pixels; _srcW = w; _srcH = h;
        _halfW = Math.Max(1, w / 2); _halfH = Math.Max(1, h / 2);
        _halfPx = Resample(pixels, w, h, _halfW, _halfH);
    }

    /// <summary>
    /// 원본 그림을 (x, y, w, h)(물리 픽셀)에 맞춰 줄이거나 늘려 보여 준다. alpha는 전체 투명도(0~255).
    /// GDI AlphaBlend는 늘 최근접 방식이라 거칠어서, 미리 곱한 값 그대로 바이리니어로 직접 줄인다.
    /// 절반보다 작게 줄일 때는 반으로 줄여 둔 그림에서 가져와 덜 거칠다. 크기가 같으면 그대로 복사한다.
    /// </summary>
    public void DrawScaled(int x, int y, int w, int h, byte alpha)
    {
        if (_disposed || _srcPx == null || w <= 0 || h <= 0) return;
        byte[] px;
        if (w == _srcW && h == _srcH) px = _srcPx;
        else if (_halfPx != null && w < _srcW * 0.55) px = Resample(_halfPx, _halfW, _halfH, w, h);
        else px = Resample(_srcPx, _srcW, _srcH, w, h);
        Update(x, y, w, h, px, alpha);
    }

    /// <summary>미리 곱한 BGRA를 바이리니어로 sw×sh에서 dw×dh로 바꾼다.</summary>
    private static byte[] Resample(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        var x0 = new int[dw]; var x1 = new int[dw]; var fx = new int[dw];
        for (int dx = 0; dx < dw; dx++)
        {
            double sx = Math.Clamp((dx + 0.5) * sw / dw - 0.5, 0, sw - 1);
            int i = (int)sx;
            x0[dx] = i; x1[dx] = Math.Min(i + 1, sw - 1); fx[dx] = (int)((sx - i) * 256);
        }
        for (int dy = 0; dy < dh; dy++)
        {
            double sy = Math.Clamp((dy + 0.5) * sh / dh - 0.5, 0, sh - 1);
            int j = (int)sy, j1 = Math.Min(j + 1, sh - 1), fy = (int)((sy - j) * 256);
            int r0 = j * sw * 4, r1 = j1 * sw * 4, o = dy * dw * 4;
            for (int dx = 0; dx < dw; dx++, o += 4)
            {
                int a = x0[dx] * 4, b = x1[dx] * 4, f = fx[dx];
                for (int k = 0; k < 4; k++)
                {
                    int top = src[r0 + a + k] * (256 - f) + src[r0 + b + k] * f;
                    int bot = src[r1 + a + k] * (256 - f) + src[r1 + b + k] * f;
                    dst[o + k] = (byte)((top * (256 - fy) + bot * fy + 32768) >> 16);
                }
            }
        }
        return dst;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _subclass?.Dispose();
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
