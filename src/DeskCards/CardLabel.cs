using System;
using System.Runtime.InteropServices;
using Gdi = System.Drawing;

namespace DeskCards;

/// <summary>
/// 카드 아래에 떠 있는 이름 줄. 바탕화면 아이콘 이름처럼 판 없이 글자만 보인다.
/// Windows가 그리는 카드 창(아크릴·모서리·그림자)은 한 창 안에서 일부만 투명하게 할 수 없어서 따로 띄운다.
/// 카드 창이 소유자라 z 순서는 카드를 따라가고, 위치·크기는 카드가 옮길 때마다 Place로 맞춘다.
/// 클릭은 아래로 그대로 통과한다.
///
/// XAML 창은 픽셀 단위 투명을 못 하므로, 글자를 그림으로 구워서 알파가 있는 창(UpdateLayeredWindow)에 바로 올린다.
/// 글자는 바탕화면 아이콘 이름처럼 흰 글자 오른쪽 아래에 짙은 그림자가 살짝 번지게 한다.
/// 선명하게 보이도록 힌팅(획을 픽셀 격자에 맞춤)을 켠 GDI+로 실제 픽셀 크기에 바로 그리고,
/// 그림자는 그 글자 모양을 비켜 흐려서 만든다. 투명한 창이라 ClearType 대신 흑백 부드럽게를 쓴다.
/// 글자·크기·배율이 바뀔 때만 다시 굽는다.
/// </summary>
internal sealed class CardLabel : IDisposable
{
    private const double BaseFont = 13;
    /// <summary>바탕화면 아이콘 이름과 같은 글꼴. 한글은 Windows 글꼴 연결로 맑은 고딕이 쓰인다.</summary>
    private const string FontName = "Segoe UI";

    private readonly IntPtr _hwnd;
    private string _text = "";
    private (int X, int Y, int W, int H, double T, double Dpi) _placed;
    private string? _bakedKey;
    private Baked? _baked;
    private IntPtr _dib;
    private IntPtr _memDc;
    private bool _disposed;

    /// <summary>구운 글자 그림. 픽셀은 알파가 곱해진 BGRA다.</summary>
    internal sealed record Baked(int Width, int Height, byte[] Pixels, int Left, int Top);

    public CardLabel(IntPtr owner)
    {
        const uint WS_POPUP = 0x80000000;
        long ex = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
        _hwnd = CreateWindowEx((uint)ex, "Static", "Desk Cards Label", WS_POPUP, 0, 0, 1, 1, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

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

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            Bake();
            Compose();
        }
    }

    /// <summary>구운 글자 그림(테스트·확인용).</summary>
    internal Baked? Result => _baked;

    /// <summary>물리 픽셀 위치·크기로 옮기고, 카드 확대 비율 t에 맞춰 글자 크기를 정한다.</summary>
    public void Place(int x, int y, int w, int h, double t, double dpi)
    {
        if (_disposed) return;
        _placed = (x, y, w, h, t, dpi);
        Bake();
        Compose();
    }

    private void Bake()
    {
        var (_, _, w, _, t, dpi) = _placed;
        if (w <= 0 || dpi <= 0) return;
        string key = $"{_text}|{w}|{t:0.####}|{dpi:0.####}";
        if (key == _bakedKey) return;
        _bakedKey = key;
        _baked = null;
        if (string.IsNullOrEmpty(_text)) return;

        double k = t * dpi; // DIP → 물리 픽셀
        float fontPx = (float)(BaseFont * k);
        double blur = Math.Max(1, 1.2 * k);
        int drop = Math.Max(1, (int)Math.Round(0.8 * k));
        int pad = (int)Math.Ceiling(blur * 3) + drop;
        int side = (int)Math.Round(4 * k), top = (int)Math.Round(5 * k);

        using var font = new Gdi.Font(FontName, fontPx, Gdi.FontStyle.Regular, Gdi.GraphicsUnit.Pixel);
        using var format = new Gdi.StringFormat(Gdi.StringFormat.GenericTypographic)
        {
            Trimming = Gdi.StringTrimming.EllipsisCharacter,
            FormatFlags = Gdi.StringFormatFlags.NoWrap | Gdi.StringFormatFlags.LineLimit | Gdi.StringFormatFlags.MeasureTrailingSpaces,
        };
        int maxW = Math.Max(1, w - 2 * side);
        Gdi.SizeF size;
        using (var probe = new Gdi.Bitmap(1, 1))
        using (var g = Gdi.Graphics.FromImage(probe))
        {
            g.TextRenderingHint = Gdi.Text.TextRenderingHint.AntiAliasGridFit;
            size = g.MeasureString(_text, font, new Gdi.SizeF(maxW, fontPx * 2), format);
        }
        int tw = Math.Min(maxW, (int)Math.Ceiling(size.Width) + 1), th = (int)Math.Ceiling(font.GetHeight()) + 1;
        int bw = tw + 2 * pad, bh = th + 2 * pad;

        // 흰 글자만 먼저 그린다(힌팅 켠 흑백 부드럽게).
        byte[] glyph;
        using (var bmp = new Gdi.Bitmap(bw, bh, Gdi.Imaging.PixelFormat.Format32bppPArgb))
        {
            using (var g = Gdi.Graphics.FromImage(bmp))
            {
                g.Clear(Gdi.Color.Transparent);
                g.TextRenderingHint = Gdi.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(_text, font, Gdi.Brushes.White, new Gdi.RectangleF(pad, pad, tw, th), format);
            }
            var data = bmp.LockBits(new Gdi.Rectangle(0, 0, bw, bh), Gdi.Imaging.ImageLockMode.ReadOnly, Gdi.Imaging.PixelFormat.Format32bppPArgb);
            glyph = new byte[bw * bh * 4];
            for (int y = 0; y < bh; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, glyph, y * bw * 4, bw * 4);
            bmp.UnlockBits(data);
        }

        // 그림자: 글자 모양(알파)을 오른쪽 아래로 비켜 흐리고, 그 위에 흰 글자를 얹는다.
        var alpha = new float[bw * bh];
        for (int y = 0; y < bh; y++)
        for (int x = 0; x < bw; x++)
        {
            int sx = x - drop, sy = y - drop;
            if (sx >= 0 && sy >= 0) alpha[y * bw + x] = glyph[(sy * bw + sx) * 4 + 3] / 255f;
        }
        var shadow = Blur(alpha, bw, bh, blur);
        var dst = new byte[bw * bh * 4];
        for (int i = 0; i < bw * bh; i++)
        {
            // 흐리면 옅어지니 조금 키워서 짙게(바탕화면 아이콘 이름 정도).
            float sa = Math.Min(1f, shadow[i] * 1.6f) * 0.9f;
            float ga = glyph[i * 4 + 3] / 255f;
            float a = ga + sa * (1 - ga);
            byte white = glyph[i * 4]; // 흰 글자는 알파가 곱해진 색이라 색 값 = 알파
            dst[i * 4] = dst[i * 4 + 1] = dst[i * 4 + 2] = white; // 그림자는 검정이라 색에 더할 것이 없다
            dst[i * 4 + 3] = (byte)Math.Round(a * 255);
        }
        // 가운데 맞춤. 창 원점이 정수 픽셀이니 정수 픽셀로 놓아 구운 픽셀이 화면 픽셀에 그대로 찍히게 한다.
        _baked = new Baked(bw, bh, dst, (int)Math.Round((w - bw) / 2.0), top - pad);
    }

    /// <summary>구운 그림을 창 크기의 알파 비트맵에 올려 창에 반영한다.</summary>
    private void Compose()
    {
        if (_disposed) return;
        var (x, y, w, h, _, _) = _placed;
        if (w <= 0 || h <= 0) return;
        if (_baked == null)
        {
            // 이름이 없으면 완전히 투명한 1×1.
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return;
        }

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
            var canvas = new byte[w * h * 4];
            var b = _baked;
            for (int row = 0; row < b.Height; row++)
            {
                int dy = b.Top + row;
                if (dy < 0 || dy >= h) continue;
                for (int col = 0; col < b.Width; col++)
                {
                    int dx = b.Left + col;
                    if (dx < 0 || dx >= w) continue;
                    Buffer.BlockCopy(b.Pixels, (row * b.Width + col) * 4, canvas, (dy * w + dx) * 4, 4);
                }
            }
            Marshal.Copy(canvas, 0, bits, canvas.Length);

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

    /// <summary>가우스 흐림(가로·세로 한 번씩).</summary>
    private static float[] Blur(float[] src, int w, int h, double sigma)
    {
        int r = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[r * 2 + 1];
        float sum = 0;
        for (int i = -r; i <= r; i++) sum += kernel[i + r] = (float)Math.Exp(-(i * i) / (2 * sigma * sigma));
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;

        var tmp = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float v = 0;
            for (int i = -r; i <= r; i++)
            {
                int xx = x + i;
                if (xx >= 0 && xx < w) v += src[y * w + xx] * kernel[i + r];
            }
            tmp[y * w + x] = v;
        }
        var dst = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float v = 0;
            for (int i = -r; i <= r; i++)
            {
                int yy = y + i;
                if (yy >= 0 && yy < h) v += tmp[yy * w + x] * kernel[i + r];
            }
            dst[y * w + x] = v;
        }
        return dst;
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
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref Native.POINT pptDst, ref SIZE size, IntPtr src, ref Native.POINT pptSrc, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref Native.BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
