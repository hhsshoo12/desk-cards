using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskCards;

/// <summary>
/// 카드 아래에 떠 있는 이름 줄. 바탕화면 아이콘 이름처럼 판 없이 글자만 보인다.
/// Windows가 그리는 카드 창(아크릴·모서리·그림자)은 한 창 안에서 일부만 투명하게 할 수 없어서 따로 띄운다.
/// 카드 창이 소유자라 z 순서는 카드를 따라가고, 위치·크기는 카드가 옮길 때마다 Place로 맞춘다.
/// 클릭은 아래로 그대로 통과한다.
/// </summary>
internal sealed class CardLabel : Window
{
    private const double BaseFont = 13;
    private readonly TextBlock _text;

    public CardLabel(IntPtr owner)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Width = Height = 1;
        Title = "Desk Cards Label";
        _text = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic"),
            FontSize = BaseFont,
            Foreground = Brushes.White, // 바탕화면 아이콘 이름과 같은 흰 글자. 그림자는 두지 않는다.
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        TextOptions.SetTextRenderingMode(_text, TextRenderingMode.Grayscale);
        Content = _text;
        new WindowInteropHelper(this).Owner = owner;
        SourceInitialized += (_, _) => Hwnd.MakeClickThrough(Hwnd.Of(this));
    }

    public string Text
    {
        get => _text.Text;
        set => _text.Text = value;
    }

    /// <summary>물리 픽셀 위치·크기로 옮기고, 카드 확대 비율 t에 맞춰 글자 크기를 정한다.</summary>
    public void Place(int x, int y, int w, int h, double t)
    {
        _text.FontSize = BaseFont * t;
        _text.Margin = new Thickness(4 * t, 5 * t, 4 * t, 0);
        var hwnd = Hwnd.Of(this);
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }
}
