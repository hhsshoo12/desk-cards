using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DeskCards;

/// <summary>
/// 카드 한 장의 모양과 조작: 아이콘 칸(넘치면 마지막 칸이 더보기), 이름, 클릭 실행·끌어 꺼내기·우클릭 메뉴.
/// 바탕화면 카드(CardWindow)와 카드 바(EdgeBar)가 같이 쓴다. 크기는 DIP로 그리고, 확대는 쓰는 쪽이 칸 크기에 곱해서 넘긴다.
/// </summary>
internal sealed class CardView : Grid
{
    public const double LabelH = 28;
    private const string OverflowTag = "overflow";

    private readonly GroupManager _mgr;
    private readonly Grid _cells = new();
    private readonly TextBlock _placeholder, _label;
    private readonly DispatcherTimer _hoverTimer = new();
    private readonly double _labelH;
    private CardLayout _layout = new();
    private double _iconSize = 40;
    private Point _downPos;
    private PointerPoint? _downPoint;
    private object? _downTarget;
    private bool _pending;

    /// <param name="onDesktop">
    /// 바탕화면의 아크릴 창 안인지(이름은 창 밖에 CardLabel로 따로 띄운다), 카드 바의 판·그림자 위인지.
    /// </param>
    public CardView(GroupModel group, GroupManager mgr, bool onDesktop)
    {
        Group = group;
        _mgr = mgr;
        _labelH = onDesktop ? 0 : LabelH;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(_labelH) });

        Card = new Border { CornerRadius = new CornerRadius(8) };
        if (onDesktop)
        {
            // 창 전체의 강조 테두리. 아이콘 칸의 크기·여백은 바꾸지 않는다.
            Card.IsHitTestVisible = false;
        }
        else
        {
            Card.BorderThickness = new Thickness(1);
            Card.Background = Theme.Brush("CardBg");
            Card.BorderBrush = Theme.Brush("CardBorder");
            Card.Shadow = new ThemeShadow();
            Card.Translation = new System.Numerics.Vector3(0, 0, 16);
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
            Foreground = Theme.Brush("SubFg"),
        };
        Children.Add(_placeholder);

        _label = new TextBlock
        {
            Margin = new Thickness(4, 5, 4, 0),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Theme.Brush("Fg"),
        };
        SetRow(_label, 1);
        if (onDesktop) _label.Visibility = Visibility.Collapsed;
        Children.Add(_label);

        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (!Editing() && !LeftDown() && IsLoaded) Expand?.Invoke(true);
        };
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnDown), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnMove), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnUp), true);
        RightTapped += OnRightTapped;
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

    private static bool LeftDown() => (Native.GetAsyncKeyState(0x01) & 0x8000) != 0;

    /// <summary>그룹을 다 쓰고 나면(창을 닫을 때) 부른다.</summary>
    public void Detach()
    {
        Group.Changed -= Rebuild;
        _hoverTimer.Stop();
    }

    /// <summary>
    /// 칸 수와 칸 하나의 크기(DIP, 확대 반영)로 모양을 정한다. 이 뷰의 크기는 (칸 수 × cell) + 이름 줄(카드 바만)이다.
    /// 아이콘 크기가 바뀌었을 때만 다시 그린다.
    /// </summary>
    public void Apply(CardLayout layout, double cell)
    {
        Width = layout.Cols * cell;
        Height = layout.Rows * cell + _labelH;
        double pad = cell * 0.17;
        _cells.Margin = new Thickness(pad);
        double inner = (cell * layout.Cols - 2 * pad) / layout.Cols - 6;
        double icon = Math.Clamp(Math.Round(inner * 0.74), 16, 256);
        bool changed = Math.Abs(icon - _iconSize) > 0.5 || layout.Cols != _layout.Cols || layout.Rows != _layout.Rows;
        _layout = new CardLayout { Cols = layout.Cols, Rows = layout.Rows, Zoom = layout.Zoom };
        _iconSize = icon;
        if (changed || _label.Text != Group.Name) Rebuild();
    }

    private static Image IconImage(ShellEntry e, double size, Thickness margin)
    {
        var img = new Image { Source = e.Icon, Width = size, Height = size, Margin = margin };
        // 아이콘은 셸에서 나중에 오므로, 도착하면 그림만 바꾼다.
        e.PropertyChanged += (_, _) => img.Source = e.Icon;
        return img;
    }

    public void Rebuild()
    {
        _label.Text = Group.Name;
        _cells.Children.Clear();
        _cells.RowDefinitions.Clear();
        _cells.ColumnDefinitions.Clear();
        for (int r = 0; r < _layout.Rows; r++) _cells.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < _layout.Cols; c++) _cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var items = Group.Items;
        _placeholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 칸이 모자라면 마지막 칸을 '더보기' 묶음으로 쓴다.
        int slots = _layout.Cols * _layout.Rows;
        bool overflow = items.Count > slots;
        int direct = overflow ? slots - 1 : items.Count;
        void Place(Border cell, int index)
        {
            SetRow(cell, index / _layout.Cols);
            SetColumn(cell, index % _layout.Cols);
            _cells.Children.Add(cell);
        }
        for (int i = 0; i < direct; i++)
            Place(MakeCell(IconImage(items[i], _iconSize, new Thickness(0)), items[i], items[i].Name), i);

        if (overflow)
        {
            double mini = _iconSize * 0.48;
            var grid = new Grid { Width = mini * 2 + 8, Height = mini * 2 + 8 };
            for (int i = 0; i < 2; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition());
            }
            int n = 0;
            foreach (var e in items.Skip(direct).Take(4))
            {
                var img = IconImage(e, mini, new Thickness(2));
                SetRow(img, n / 2);
                SetColumn(img, n % 2);
                grid.Children.Add(img);
                n++;
            }
            var more = MakeCell(grid, OverflowTag, null);
            more.PointerEntered += (_, _) => StartHoverExpand();
            more.PointerExited += (_, _) => _hoverTimer.Stop();
            Place(more, direct);
        }
    }

    private Border MakeCell(UIElement content, object tag, string? tip)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(3),
            Background = new SolidColorBrush(Colors.Transparent),
            Tag = tag,
            Child = content,
        };
        if (tip != null) ToolTipService.SetToolTip(b, tip);
        b.PointerEntered += (_, _) => { if (!Editing()) b.Background = Theme.Brush("HoverBg"); };
        b.PointerExited += (_, _) => b.Background = new SolidColorBrush(Colors.Transparent);
        return b;
    }

    /// <summary>더보기 칸에 마우스를 잠시 올려 두면 펼친다(일반 설정). 펼친 창은 그 밖으로 나가면 바로 접힌다.</summary>
    private void StartHoverExpand()
    {
        _hoverTimer.Stop();
        if (Editing() || !_mgr.HoverExpand || LeftDown()) return;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(_mgr.HoverExpandDelay);
        _hoverTimer.Start();
    }

    // ----- 입력 -----
    // 아이콘 클릭 = 실행, 더보기 칸 클릭 = 펼치기(빈 칸·이름은 아무 일 없음), 아이콘 끌기 = 밖으로 꺼내기.

    private void OnDown(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (Editing() || !point.Properties.IsLeftButtonPressed) return;
        _downPos = point.Position;
        _downPoint = point;
        _downTarget = FindTag(e.OriginalSource as DependencyObject);
        _pending = true;
    }

    private void OnMove(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!_pending || Editing() || !point.Properties.IsLeftButtonPressed) return;
        if (Math.Abs(point.Position.X - _downPos.X) < Native.GetSystemMetrics(68) &&
            Math.Abs(point.Position.Y - _downPos.Y) < Native.GetSystemMetrics(69)) return;
        _pending = false;
        if (_downTarget is ShellEntry entry && _downPoint != null) _ = FileOps.DragOut(this, entry.Path, _downPoint);
    }

    private void OnUp(object sender, PointerRoutedEventArgs e)
    {
        if (!_pending || Editing()) return;
        _pending = false;
        if (_downTarget is ShellEntry entry) FileOps.Launch(entry.Path);
        else if (_downTarget as string == OverflowTag) Expand?.Invoke(false);
    }

    // 아이콘 위 우클릭 = 항목 메뉴(펼친 창과 같음), 그 밖 = 카드 메뉴.
    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
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
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
