using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskCards.Expanded;

/// <summary>
/// 카드 바 안의 카드 한 장: 아이콘 칸(넘치면 마지막 칸이 더보기), 이름, 클릭 실행·끌어 꺼내기·우클릭 메뉴.
/// 옛 WPF CardView(카드 바용)를 옮긴 것이다. 크기는 기준 단위(DIP)로 그리고, 확대는 쓰는 쪽이 한다.
/// </summary>
internal sealed class BarCardView : Grid
{
    public const double LabelH = DesktopCards.LabelHeight;
    private const string OverflowTag = "overflow";

    private readonly ICardViewHost _host;
    private readonly bool _desktop;
    private bool _dirty = true;
    private readonly UniformGrid _cells = new() { Rows = 2, Columns = 2 };
    private readonly TextBlock _placeholder, _label;
    private readonly DispatcherTimer _hoverTimer = new();
    private double _iconSize = 40;
    private Point _downPos;
    private object? _downTarget;
    private bool _pending;
    private BarCardData _data;
    private bool _hoverExpand;

    /// <param name="desktop">바탕화면 카드면 Windows가 그리는 창 배경 위에 칸만 그린다(판·그림자·이름 줄 없음, 크기는 창을 따른다).</param>
    public BarCardView(ICardViewHost host, BarCardData data, bool desktop = false)
    {
        _host = host;
        _data = data;
        _desktop = desktop;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(desktop ? 0 : LabelH) });

        Card = new Border
        {
            CornerRadius = new CornerRadius(desktop ? 4 : 8),
            BorderThickness = new Thickness(desktop ? 0 : 1),
        };
        if (desktop) Card.Background = Brushes.Transparent;
        else
        {
            Card.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.22 };
            Card.SetResourceReference(Border.BackgroundProperty, "CardBg");
            Card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        }
        Children.Add(Card);
        Children.Add(_cells);

        _placeholder = new TextBlock
        {
            Text = "여기로 끌어다 놓기",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
        Children.Add(_placeholder);

        _label = new TextBlock
        {
            Margin = new Thickness(4, 5, 4, 0),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        SetRow(_label, 1);
        if (desktop) _label.Visibility = Visibility.Collapsed;
        Children.Add(_label);
        if (desktop) SizeChanged += (_, _) => Relayout();

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (Mouse.LeftButton != MouseButtonState.Pressed && IsVisible) _host.Expand(_data.Group, true, ScreenRect());
        };
        PreviewMouseLeftButtonDown += OnDown;
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += OnUp;
        MouseRightButtonUp += OnRightUp;
        Unloaded += (_, _) => _hoverTimer.Stop();
    }

    public string Group => _data.Group;

    /// <summary>이름 글자 자리에 입력칸을 띄운다. 끝나면 새 이름(그만두면 null)을 알려 준다.</summary>
    public void BeginRename(Action<string?> finish)
    {
        var box = new TextBox
        {
            Text = _data.Name,
            FontSize = 13,
            MaxLength = 80,
            Margin = new Thickness(4, 3, 4, 0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(2, 1, 2, 1),
            BorderThickness = new Thickness(1),
        };
        box.SetResourceReference(Control.ForegroundProperty, "Fg");
        box.SetResourceReference(Control.BackgroundProperty, "CardBg");
        box.SetResourceReference(Control.BorderBrushProperty, "Accent");
        SetRow(box, 1);
        _label.Visibility = Visibility.Hidden;
        Children.Add(box);
        bool done = false;
        void End(bool ok)
        {
            if (done) return;
            done = true;
            Children.Remove(box);
            _label.Visibility = Visibility.Visible;
            finish(ok ? box.Text.Trim() : null);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { End(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { End(false); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => End(true);
        box.Loaded += (_, _) => { Keyboard.Focus(box); box.SelectAll(); };
    }

    /// <summary>바탕화면 카드: 파일을 끌어다 놓을 대상일 때의 강조 테두리.</summary>
    public void SetDrop(bool on)
    {
        Card.BorderThickness = new Thickness(on ? 3 : 0);
        if (on) Card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        else Card.ClearValue(Border.BorderBrushProperty);
    }
    /// <summary>카드 배경 판. 드롭 대상 강조 테두리는 쓰는 쪽이 여기에 칠한다.</summary>
    public Border Card { get; }

    /// <summary>이 카드가 화면에 있는 자리(물리 픽셀).</summary>
    public PixelRect ScreenRect()
    {
        var tl = PointToScreen(new Point(0, 0));
        var br = PointToScreen(new Point(ActualWidth, ActualHeight));
        return new PixelRect((int)Math.Round(tl.X), (int)Math.Round(tl.Y), (int)Math.Round(br.X), (int)Math.Round(br.Y));
    }

    /// <summary>칸 수와 칸 하나의 기준 크기로 모양을 정하고 항목을 다시 그린다. 이 뷰의 크기는 (칸 수 × cell) + 이름 줄이다.</summary>
    public void Apply(BarCardData data, bool hoverExpand, int hoverDelayMs)
    {
        _data = data;
        _hoverExpand = hoverExpand;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, hoverDelayMs));
        _dirty = true;
        if (_desktop) { Relayout(); return; }
        double cell = data.Cell;
        Width = data.Cols * cell;
        Height = data.Rows * cell + LabelH;
        Layout(cell);
        Rebuild();
    }

    private void Layout(double cell)
    {
        double pad = cell * 0.17;
        _cells.Margin = new Thickness(pad);
        double inner = (cell * _data.Cols - 2 * pad) / _data.Cols - 6;
        _iconSize = Math.Clamp(Math.Round(inner * 0.74), 16, 128);
    }

    /// <summary>바탕화면 카드: 창 크기에서 칸 크기를 정한다. 내용이나 아이콘 크기가 바뀔 때만 다시 만든다.</summary>
    private void Relayout()
    {
        if (ActualWidth <= 0 || _data.Cols <= 0) return;
        double old = _iconSize;
        Layout(ActualWidth / _data.Cols);
        if (_dirty || old != _iconSize) Rebuild();
    }

    // 크기를 끌어 바꾸는 동안 칸을 다시 만들어도 아이콘 그림은 다시 만들지 않는다.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TileData, BitmapSource> IconCache = new();

    private static BitmapSource? IconOf(TileData d)
    {
        if (IconCache.TryGetValue(d, out var cached)) return cached;
        var made = MakeIcon(d);
        if (made != null) IconCache.Add(d, made);
        return made;
    }

    private static BitmapSource? MakeIcon(TileData d)
    {
        if (d.Icon is not { Length: > 0 } px || d.IconWidth <= 0 || d.IconHeight <= 0 || px.Length < d.IconWidth * d.IconHeight * 4) return null;
        var bmp = BitmapSource.Create(d.IconWidth, d.IconHeight, 96, 96, PixelFormats.Pbgra32, null, px, d.IconWidth * 4);
        bmp.Freeze();
        return bmp;
    }

    private void Rebuild()
    {
        _dirty = false;
        _label.Text = _data.Name;
        _cells.Children.Clear();
        _cells.Columns = _data.Cols;
        _cells.Rows = _data.Rows;
        var items = _data.Items;
        _placeholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 칸이 모자라면 마지막 칸을 '더보기' 묶음으로 쓴다.
        int slots = _data.Cols * _data.Rows;
        bool overflow = items.Count > slots;
        int direct = overflow ? slots - 1 : items.Count;
        for (int i = 0; i < direct; i++)
        {
            var img = new Image { Source = IconOf(items[i]), Width = _iconSize, Height = _iconSize };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            _cells.Children.Add(MakeCell(img, items[i], items[i].Name));
        }

        if (overflow)
        {
            double mini = _iconSize * 0.48;
            var grid = new UniformGrid { Rows = 2, Columns = 2, Width = mini * 2 + 8, Height = mini * 2 + 8 };
            foreach (var e in items.Skip(direct).Take(4))
            {
                var img = new Image { Source = IconOf(e), Width = mini, Height = mini, Margin = new Thickness(2) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                grid.Children.Add(img);
            }
            var more = MakeCell(grid, OverflowTag, null);
            more.MouseEnter += (_, _) => StartHoverExpand();
            more.MouseLeave += (_, _) => _hoverTimer.Stop();
            _cells.Children.Add(more);
        }
    }

    private static Border MakeCell(UIElement content, object tag, string? tip)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(3),
            Background = Brushes.Transparent,
            Tag = tag,
            Child = content,
            ToolTip = tip,
        };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }

    /// <summary>더보기 칸에 마우스를 잠시 올려 두면 펼친다(일반 설정). 펼친 창은 그 밖으로 나가면 바로 접힌다.</summary>
    private void StartHoverExpand()
    {
        _hoverTimer.Stop();
        if (!_hoverExpand || Mouse.LeftButton == MouseButtonState.Pressed) return;
        _hoverTimer.Start();
    }

    // ----- 입력 -----
    // 아이콘 클릭 = 실행, 더보기 칸 클릭 = 펼치기(빈 칸·이름은 아무 일 없음), 아이콘 끌기 = 밖으로 꺼내기.

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _downPos = e.GetPosition(this);
        _downTarget = FindTag(e.OriginalSource as DependencyObject);
        _pending = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pending || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downPos;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pending = false;
        if (_downTarget is TileData entry)
        {
            try { DragDrop.DoDragDrop(this, new DataObject(DataFormats.FileDrop, new[] { entry.Path }), DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link); }
            catch { }
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pending) return;
        _pending = false;
        if (_downTarget is TileData entry) _host.Launch(entry.Path);
        else if (_downTarget as string == OverflowTag) _host.Expand(_data.Group, false, ScreenRect());
    }

    // 아이콘 위 우클릭 = 항목 메뉴(펼친 창과 같음), 그 밖 = 카드 메뉴.
    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (FindTag(e.OriginalSource as DependencyObject) is TileData entry) _host.ItemMenu(entry.Path);
        else _host.CardMenu(_data.Group);
    }

    private object? FindTag(DependencyObject? d)
    {
        while (d != null && d != this)
        {
            if (d is Border { Tag: not null } b) return b.Tag;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
