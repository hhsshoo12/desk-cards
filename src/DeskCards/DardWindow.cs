using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DeskCards;

/// <summary>
/// .dard 카드 한 장. 폴더 카드와 같은 틀(카드 판, 이름 줄)에 HTML을 담는다.
/// 카드는 비율만 정하고 크기는 앱이 정한다. 크기를 바꾸면 페이지를 그림처럼 늘리지 않고 확대 비율로 다시 그려 선명하게 둔다.
/// </summary>
internal sealed class DardWindow : DeskCard
{
    private const double Corner = 8;

    private readonly Border _frame;
    private readonly Grid _box;
    private readonly DardView _view;
    private readonly TextBlock _label;
    private readonly RowDefinition _labelRow;

    public DardWindow(DardRuntime runtime, DardCardInfo info, GroupManager mgr) : base(mgr)
    {
        Runtime = runtime;
        Info = info;

        _frame = new Border
        {
            CornerRadius = new CornerRadius(Corner),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.22 },
        };
        _frame.SetResourceReference(Border.BackgroundProperty, "CardBg");
        _frame.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        _view = new DardView(runtime, info.Id, settings: false, DardPackage.SizeFor(info.RatioW, info.RatioH, DardPackage.CardArea))
        {
            Margin = new Thickness(1), // 카드 판 테두리가 보이게
        };
        _box = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _box.Children.Add(_frame);
        _box.Children.Add(_view);

        _label = new TextBlock
        {
            Text = info.Name,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.9, Color = Colors.Black },
        };
        _labelRow = new RowDefinition { Height = new GridLength(LabelH) };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(_labelRow);
        Grid.SetRow(_label, 1);
        content.Children.Add(_box);
        content.Children.Add(_label);
        Layout.Children.Add(content);

        // 카드 위 우클릭은 언제나 카드 메뉴(페이지의 우클릭은 받지 않는다).
        PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
        PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            Menus.ForDard(this, Mgr).ShowAtCursor();
        };
    }

    public DardRuntime Runtime { get; }
    public DardCardInfo Info { get; }
    public override string Key => Runtime.KeyFor(Info.Id);
    public override string CardName => Info.Name;
    protected override Border Frame => _frame;

    /// <summary>확대 1일 때 카드 판 크기(DIP): 넓이는 기본 폴더 카드(2×2 칸)와 같고 비율은 카드가 정한다.</summary>
    private Size ContentBase()
    {
        double side = 2 * Mgr.CellSize;
        return DardPackage.SizeFor(Info.RatioW, Info.RatioH, side * side);
    }

    /// <summary>새 카드를 놓을 자리를 찾을 때 쓰는 기준 크기.</summary>
    public static Size BaseSizeFor(DardCardInfo info, double cell)
    {
        var c = DardPackage.SizeFor(info.RatioW, info.RatioH, 4 * cell * cell);
        return new Size(c.Width + 2 * Inset, TopPad + c.Height + LabelH);
    }

    protected override Size BaseSize() => BaseSizeFor(Info, Mgr.CellSize);

    protected override void ApplyScale(double t)
    {
        // 웹 화면은 변환으로 키우면 흐려지므로, 판 크기를 직접 정하고 페이지 확대 비율을 바꾼다.
        Layout.LayoutTransform = Transform.Identity;
        var c = ContentBase();
        double w = c.Width * t, h = c.Height * t;
        _box.Width = w;
        _box.Height = h;
        double r = Corner * t;
        _frame.CornerRadius = new CornerRadius(r);
        _view.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, w - 2), Math.Max(0, h - 2)), Math.Max(0, r - 1), Math.Max(0, r - 1));
        _view.SetZoom((w - 2) / _view.Viewport.Width);
        _labelRow.Height = new GridLength(LabelH * t);
        _label.FontSize = 13 * t;
        _label.Margin = new Thickness(4 * t, 5 * t, 4 * t, 0);
    }

    /// <summary>페이지를 다시 불러온다(우클릭 메뉴).</summary>
    public void Reload() => _view.Reload();

    protected override void OnClosed(EventArgs e)
    {
        _view.Close();
        Runtime.RemoveWindow(this);
        base.OnClosed(e);
    }
}
