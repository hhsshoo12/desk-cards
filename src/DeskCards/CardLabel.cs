using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace DeskCards;

/// <summary>
/// 카드 아래에 떠 있는 이름 줄. 바탕화면 아이콘 이름처럼 판 없이 글자만 보인다.
/// Windows가 그리는 카드 창(아크릴·모서리·그림자)은 한 창 안에서 일부만 투명하게 할 수 없어서 따로 띄운다.
/// 카드 창이 소유자라 z 순서는 카드를 따라가고, 위치·크기는 카드가 옮길 때마다 Place로 맞춘다.
/// 클릭은 아래로 그대로 통과한다.
///
/// 글자는 어떤 배경에서도 읽히도록 흰 글자에 검은 테두리와 옅은 그림자를 두고,
/// 가로세로 4배(픽셀당 16표본)로 한 번 그린 뒤 실제 픽셀로 평균 내어 구운 그림을 픽셀에 딱 맞춰 띄운다.
/// 글자·크기·배율이 바뀔 때만 다시 굽는다.
/// </summary>
internal sealed class CardLabel : Window
{
    private const double BaseFont = 13;
    /// <summary>한 방향 표본 수. 4 × 4 = 픽셀당 16표본(SSAA 16x).</summary>
    private const int SS = 4;
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private readonly Canvas _canvas = new();
    private readonly Image _image = new() { Stretch = Stretch.None };
    private string _text = "";
    private (int W, double T, double Dpi) _placed;
    private string? _bakedKey;

    public CardLabel(IntPtr owner)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        Width = Height = 1;
        Title = "Desk Cards Label";
        // 구운 그림을 다시 늘이거나 줄이지 않는다(픽셀 그대로).
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        _canvas.Children.Add(_image);
        Content = _canvas;
        new WindowInteropHelper(this).Owner = owner;
        SourceInitialized += (_, _) => Hwnd.MakeClickThrough(Hwnd.Of(this));
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value ?? "";
            Bake();
        }
    }

    /// <summary>구운 글자 그림(테스트·확인용).</summary>
    public BitmapSource? Baked => _image.Source as BitmapSource;

    /// <summary>물리 픽셀 위치·크기로 옮기고, 카드 확대 비율 t에 맞춰 글자 크기를 정한다.</summary>
    public void Place(int x, int y, int w, int h, double t)
    {
        var hwnd = Hwnd.Of(this);
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _placed = (w, t, VisualTreeHelper.GetDpi(this).DpiScaleX);
        Bake();
    }

    private void Bake()
    {
        var (w, t, dpi) = _placed;
        if (w <= 0 || dpi <= 0) return;
        string key = $"{_text}|{w}|{t:0.####}|{dpi:0.####}";
        if (key == _bakedKey) return;
        _bakedKey = key;

        double k = t * dpi;                                // DIP → 물리 픽셀
        double fontPx = BaseFont * k;
        double outline = Math.Max(1, Math.Round(0.75 * k * 2) / 2); // 글자 밖으로 나가는 테두리 두께(px). 100%에서 1px
        double blur = Math.Max(1.5, 2 * k), drop = Math.Max(1, Math.Round(k));
        int pad = (int)Math.Ceiling(outline + blur + drop);
        int side = (int)Math.Round(4 * k), top = (int)Math.Round(5 * k);

        if (string.IsNullOrEmpty(_text))
        {
            _image.Source = null;
            return;
        }
        var ft = new FormattedText(_text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, fontPx * SS, Brushes.White, 1.0)
        {
            MaxTextWidth = Math.Max(1, (w - 2 * side) * SS),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        int bw = (int)Math.Ceiling(ft.Width / SS) + 2 * pad, bh = (int)Math.Ceiling(ft.Height / SS) + 2 * pad;
        var geometry = ft.BuildGeometry(new Point(pad * SS, pad * SS));
        geometry.Freeze();

        // 옅은 그림자: 테두리까지 포함한 글자 모양을 조금 아래로, 흐리게.
        var shadowPen = new Pen(Brushes.Black, outline * 2 * SS) { LineJoin = PenLineJoin.Round };
        var shadow = new DrawingVisual { Effect = new BlurEffect { Radius = blur * SS, KernelType = KernelType.Gaussian }, Opacity = 0.35 };
        shadow.Offset = new Vector(0, drop * SS);
        using (var dc = shadow.RenderOpen()) dc.DrawGeometry(Brushes.Black, shadowPen, geometry);

        // 글자: 테두리를 먼저 긋고 그 위에 흰 글자를 채운다(테두리가 글자 안쪽을 깎지 않게).
        var text = new DrawingVisual();
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)), outline * 2 * SS) { LineJoin = PenLineJoin.Round };
        using (var dc = text.RenderOpen())
        {
            dc.DrawGeometry(null, pen, geometry);
            dc.DrawGeometry(Brushes.White, null, geometry);
        }
        var root = new ContainerVisual();
        root.Children.Add(shadow);
        root.Children.Add(text);

        var big = new RenderTargetBitmap(bw * SS, bh * SS, 96, 96, PixelFormats.Pbgra32);
        big.Render(root);
        var baked = Downsample(big, bw, bh, 96 * dpi);

        _image.Source = baked;
        // 가운데 맞춤. 창 원점이 정수 픽셀이니 정수 픽셀로 놓아 구운 픽셀이 화면 픽셀에 그대로 찍히게 한다.
        Canvas.SetLeft(_image, Math.Round((w - bw) / 2.0) / dpi);
        Canvas.SetTop(_image, (top - pad) / dpi);
    }

    /// <summary>SS×SS 칸마다 평균 낸다(상자 필터). 알파가 곱해진 색이라 그대로 평균 내면 된다.</summary>
    private static BitmapSource Downsample(BitmapSource big, int w, int h, double dpi)
    {
        int bigStride = w * SS * 4;
        var src = new byte[bigStride * h * SS];
        big.CopyPixels(src, bigStride, 0);
        var dst = new byte[w * h * 4];
        const int n = SS * SS;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int b = 0, g = 0, r = 0, a = 0;
            for (int sy = 0; sy < SS; sy++)
            {
                int row = (y * SS + sy) * bigStride + x * SS * 4;
                for (int sx = 0; sx < SS; sx++)
                {
                    int i = row + sx * 4;
                    b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3];
                }
            }
            int o = (y * w + x) * 4;
            dst[o] = (byte)((b + n / 2) / n);
            dst[o + 1] = (byte)((g + n / 2) / n);
            dst[o + 2] = (byte)((r + n / 2) / n);
            dst[o + 3] = (byte)((a + n / 2) / n);
        }
        var bitmap = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Pbgra32, null, dst, w * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
