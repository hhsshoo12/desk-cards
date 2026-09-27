using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DeskCards;

/// <summary>
/// 카드 한 장의 모양과 조작: 아이콘 칸(넘치면 마지막 칸이 더보기), 이름, 클릭 실행·끌어 꺼내기·우클릭 메뉴.
/// 바탕화면 카드(CardWindow)와 카드 바(EdgeBar)가 같이 쓴다. 크기는 기준 단위(DIP)로 그리고, 확대는 쓰는 쪽이 한다.
/// </summary>
internal sealed class CardView : Grid
{
    public const double LabelH = 28;
    private const string OverflowTag = "overflow";

    private readonly GroupManager _mgr;
    private readonly UniformGrid _cells = new() { Rows = 2, Columns = 2 };
    private readonly TextBlock _placeholder, _label;
    private readonly DispatcherTimer _hoverTimer = new();
    private CardLayout _layout = new();
    private double _iconSize = 40;
    private Point _downPos;
    private object? _downTarget;
    private bool _pending;

    /// <param name="onDesktop">바탕화면 위(이름을 흰 글씨 + 그림자로)인지, 카드 바 위(테마 글씨색)인지.</param>
    public CardView(GroupModel group, GroupManager mgr, bool onDesktop)
    {
        Group = group;
        _mgr = mgr;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(LabelH) });

        Card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.22 },
        };
        Card.SetResourceReference(Border.BackgroundProperty, "CardBg");
        Card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
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
        if (onDesktop)
        {
            _label.Foreground = Brushes.White;
            _label.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.9, Color = Colors.Black };
        }
        else _label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        SetRow(_label, 1);
        Children.Add(_label);

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (!Editing() && Mouse.LeftButton != MouseButtonState.Pressed && IsVisible) Expand?.Invoke(true);
        };
        PreviewMouseLeftButtonDown += OnDown;
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += OnUp;
        MouseRightButtonUp += OnRightUp;
        Group.Changed += Rebuild;
        Unloaded += (_, _) => _hoverTimer.Stop();
    }

    public GroupModel Group { get; }

    /// <summary>카드 배경 판. 드롭 대상·편집 중 강조 테두리는 쓰는 쪽이 여기에 칠한다.</summary>
    public Border Card { get; }

    /// <summary>펼치기(true = 더보기 칸에 올려 두어서 연 경우).</summary>
    public Action<bool>? Expand { get; set; }

    /// <summary>아이콘이 아닌 곳을 우클릭했을 때 띄울 메뉴.</summary>
    public Func<FluentMenu>? CardMenu { get; set; }

    /// <summary>편집 모드처럼 클릭·끌기를 쓰는 쪽이 가져가는 동안 true.</summary>
    public Func<bool> Editing { get; set; } = () => false;

    /// <summary>그룹을 다 쓰고 나면(창을 닫을 때) 부른다.</summary>
    public void Detach()
    {
        Group.Changed -= Rebuild;
        _hoverTimer.Stop();
    }

    /// <summary>
    /// 칸 수와 칸 하나의 기준 크기로 모양을 정한다. 이 뷰의 크기는 (칸 수 × cell) + 이름 줄이다.
    /// 아이콘 크기가 바뀌었을 때만 다시 그린다.
    /// </summary>
    public void Apply(CardLayout layout, double cell)
    {
        Width = layout.Cols * cell;
        Height = layout.Rows * cell + LabelH;
        double pad = cell * 0.17;
        _cells.Margin = new Thickness(pad);
        double inner = (cell * layout.Cols - 2 * pad) / layout.Cols - 6;
        double icon = Math.Clamp(Math.Round(inner * 0.74), 16, 128);
        bool changed = Math.Abs(icon - _iconSize) > 0.5 || layout.Cols != _layout.Cols || layout.Rows != _layout.Rows;
        _layout = new CardLayout { Cols = layout.Cols, Rows = layout.Rows, Zoom = layout.Zoom };
        _iconSize = icon;
        if (changed || _label.Text != Group.Name) Rebuild();
    }

    public void Rebuild()
    {
        _label.Text = Group.Name;
        _cells.Children.Clear();
        _cells.Columns = _layout.Cols;
        _cells.Rows = _layout.Rows;
        var items = Group.Items;
        _placeholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 칸이 모자라면 마지막 칸을 '더보기' 묶음으로 쓴다.
        int slots = _layout.Cols * _layout.Rows;
        bool overflow = items.Count > slots;
        int direct = overflow ? slots - 1 : items.Count;
        for (int i = 0; i < direct; i++)
        {
            var img = new Image { Source = items[i].Icon, Width = _iconSize, Height = _iconSize };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            _cells.Children.Add(MakeCell(img, items[i], items[i].Name));
        }

        if (overflow)
        {
            double mini = _iconSize * 0.48;
            var grid = new UniformGrid { Rows = 2, Columns = 2, Width = mini * 2 + 8, Height = mini * 2 + 8 };
            foreach (var e in items.Skip(direct).Take(4))
            {
                var img = new Image { Source = e.Icon, Width = mini, Height = mini, Margin = new Thickness(2) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                grid.Children.Add(img);
            }
            var more = MakeCell(grid, OverflowTag, null);
            more.MouseEnter += (_, _) => StartHoverExpand();
            more.MouseLeave += (_, _) => _hoverTimer.Stop();
            _cells.Children.Add(more);
        }
    }

    private Border MakeCell(UIElement content, object tag, string? tip)
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
        b.MouseEnter += (_, _) => { if (!Editing()) b.SetResourceReference(Border.BackgroundProperty, "HoverBg"); };
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }

    /// <summary>더보기 칸에 마우스를 잠시 올려 두면 펼친다(일반 설정). 펼친 창은 그 밖으로 나가면 바로 접힌다.</summary>
    private void StartHoverExpand()
    {
        _hoverTimer.Stop();
        if (Editing() || !_mgr.HoverExpand || Mouse.LeftButton == MouseButtonState.Pressed) return;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(_mgr.HoverExpandDelay);
        _hoverTimer.Start();
    }

    // ----- 입력 -----
    // 아이콘 클릭 = 실행, 더보기 칸 클릭 = 펼치기(빈 칸·이름은 아무 일 없음), 아이콘 끌기 = 밖으로 꺼내기.

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (Editing()) return;
        _downPos = e.GetPosition(this);
        _downTarget = FindTag(e.OriginalSource as DependencyObject);
        _pending = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pending || Editing() || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downPos;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pending = false;
        if (_downTarget is ShellEntry entry) FileOps.DragOut(this, entry.Path);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pending || Editing()) return;
        _pending = false;
        if (_downTarget is ShellEntry entry) FileOps.Launch(entry.Path);
        else if (_downTarget as string == OverflowTag) Expand?.Invoke(false);
    }

    // 아이콘 위 우클릭 = 항목 메뉴(펼친 창과 같음), 그 밖 = 카드 메뉴.
    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!Editing() && FindTag(e.OriginalSource as DependencyObject) is ShellEntry entry)
            Menus.ForEntry(entry).ShowAtCursor();
        else
            CardMenu?.Invoke().ShowAtCursor();
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
