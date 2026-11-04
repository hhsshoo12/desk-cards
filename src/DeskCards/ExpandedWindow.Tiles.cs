using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace DeskCards;

/// <summary>펼친 창의 항목 칸·페이지(위아래 3줄 단위)·꺼내기·순서 바꾸기·끌어 넣기.</summary>
internal sealed partial class ExpandedWindow
{
    private Canvas _items = null!;
    private ScrollViewer _scroller = null!;
    private StackPanel _dots = null!;
    private TextBlock _empty = null!;
    private int _page, _pages = 1, _wheelAcc;

    // 항목 끌기
    private ShellEntry? _downEntry;
    private Point _downPos;
    private bool _pending;
    private ShellEntry? _dragEntry; // 창 안에서 순서를 바꾸는 중인 항목
    private bool _dragMoved, _dropped;
    private DateTime _lastFlip;

    private void BuildBody()
    {
        var gear = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 14, 0),
        };
        ToolTipService.SetToolTip(gear, "설정");
        gear.Click += (_, _) => OpenSettings();

        _items = new Canvas { VerticalAlignment = VerticalAlignment.Top };
        _scroller = new ScrollViewer
        {
            Content = _items,
            Margin = new Thickness(SidePad, 0, SidePad, BottomPad),
            VerticalAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled, // 페이지는 휠·점·키로만 넘긴다
            IsTabStop = false,
        };
        Grid.SetRow(_scroller, 1);

        _dots = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, BottomPad) };
        Grid.SetRow(_dots, 1);

        _empty = new TextBlock
        {
            Text = "바로가기나 파일을 여기로 끌어다 놓으세요",
            Foreground = Theme.Brush("SubFg"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, BottomPad),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(_empty, 1);

        var body = new Grid { AllowDrop = true };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleH) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        foreach (UIElement e in new UIElement[] { _title, gear, _scroller, _dots, _empty }) body.Children.Add(e);
        body.DragEnter += OnDragOver;
        body.DragOver += OnDragOver;
        body.DragLeave += OnDragLeave;
        body.Drop += OnDrop;

        var root = (UserControl)Content;
        root.Content = body;
        root.Width = WindowW;
    }

    private void Rebuild()
    {
        if (_title.FocusState == FocusState.Unfocused) _title.Text = Group.Name;
        _items.Children.Clear();
        var items = Group.Items;
        for (int i = 0; i < items.Count; i++)
        {
            var tile = MakeTile(items[i]);
            Canvas.SetLeft(tile, i % Columns * TileW);
            Canvas.SetTop(tile, i / Columns * TileH);
            _items.Children.Add(tile);
        }
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 한 번에 4×3까지 보이고, 넘치면 3줄 단위 페이지로 위아래 스크롤한다.
        int rows = Math.Max(1, (int)Math.Ceiling(items.Count / (double)Columns));
        _pages = Math.Max(1, (int)Math.Ceiling(rows / (double)RowsPerPage));
        double viewH = Math.Min(rows, RowsPerPage) * TileH;
        _items.Width = Columns * TileW;
        _items.Height = _pages > 1 ? _pages * PageH : rows * TileH; // 마지막 페이지도 딱 맞게 멈추도록
        _scroller.Height = viewH;
        ((FrameworkElement)Content).Height = TitleH + viewH + BottomPad;

        _page = Math.Min(_page, _pages - 1);
        _scroller.ChangeView(null, _page * PageH, null, disableAnimation: true);
        BuildDots();
        if (Handle != IntPtr.Zero && AppWindow.IsVisible) PlaceNearCard();
    }

    private void BuildDots()
    {
        _dots.Children.Clear();
        _dots.Visibility = _pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _pages; i++)
        {
            int index = i;
            var dot = new Ellipse
            {
                Width = i == _page ? 6 : 5,
                Height = i == _page ? 6 : 5,
                Opacity = i == _page ? 0.85 : 0.35,
                Fill = Theme.Brush("Fg"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 점이 작아서 누르기 쉽도록 투명한 여백을 준다.
            var hit = new Grid { Background = new SolidColorBrush(Colors.Transparent), Width = 14, Height = 14, Children = { dot } };
            hit.Tapped += (_, e) => { GoToPage(index); e.Handled = true; };
            _dots.Children.Add(hit);
        }
    }

    private void GoToPage(int page)
    {
        page = Math.Clamp(page, 0, _pages - 1);
        if (page == _page) return;
        _page = page;
        BuildDots();
        _scroller.ChangeView(null, _page * PageH, null, disableAnimation: !Motion.Enabled);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_pages <= 1) return;
        // 터치패드는 작은 값이 여러 번 오므로 한 칸(120)이 모일 때마다 한 페이지씩 넘긴다.
        _wheelAcc += e.GetCurrentPoint((UIElement)Content).Properties.MouseWheelDelta;
        if (Math.Abs(_wheelAcc) < 120) return;
        int dir = _wheelAcc > 0 ? -1 : 1;
        _wheelAcc = 0;
        GoToPage(_page + dir);
    }

    // ----- 칸 -----

    private Border MakeTile(ShellEntry entry)
    {
        var img = new Image { Source = entry.Icon, Width = 40, Height = 40, Margin = new Thickness(0, 14, 0, 0) };
        entry.PropertyChanged += (_, _) => img.Source = entry.Icon; // 아이콘은 셸에서 나중에 온다
        var text = new TextBlock
        {
            Text = entry.Name,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxHeight = 38,
            Margin = new Thickness(6, 8, 6, 0),
            Foreground = Theme.Brush("Fg"),
        };
        var tile = new Border
        {
            Width = TileW - 4,
            Height = TileH - 4,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            Tag = entry,
            Child = new StackPanel { Children = { img, text } },
        };
        tile.PointerEntered += (_, _) => tile.Background = Theme.Brush("HoverBg");
        tile.PointerExited += (_, _) => tile.Background = new SolidColorBrush(Colors.Transparent);
        tile.PointerPressed += (_, e) =>
        {
            var p = e.GetCurrentPoint(tile);
            if (!p.Properties.IsLeftButtonPressed) return;
            _downEntry = entry;
            _downPos = p.Position;
            _pending = true;
        };
        tile.PointerMoved += (_, e) => OnTileMoved(tile, e);
        tile.PointerReleased += (_, _) => OnTileReleased();
        tile.RightTapped += (_, e) =>
        {
            e.Handled = true;
            Menus.ForEntry(entry, launched: SafeClose).ShowAtCursor();
        };
        return tile;
    }

    private void OnTileReleased()
    {
        if (!_pending || _downEntry == null) return;
        _pending = false;
        FileOps.Launch(_downEntry.Path);
        SafeClose();
    }

    private async void OnTileMoved(UIElement tile, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(tile);
        if (!_pending || _downEntry == null || !point.Properties.IsLeftButtonPressed) return;
        if (Math.Abs(point.Position.X - _downPos.X) < Native.GetSystemMetrics(68) &&
            Math.Abs(point.Position.Y - _downPos.Y) < Native.GetSystemMetrics(69)) return;
        _pending = false;
        _busy = true;
        // 창 안에서 놓으면 순서 바꾸기, 창 밖(바탕화면·탐색기·다른 카드)에 놓으면 꺼내기.
        _dragEntry = _downEntry;
        _dragMoved = _dropped = false;
        try { await FileOps.DragOut(tile, _downEntry.Path, point); }
        finally
        {
            _dragEntry = null;
            _busy = false;
        }
        if (_dropped && _dragMoved)
            _mgr.SetOrder(Group, _items.Children.OfType<Border>().Select(b => b.Tag).OfType<ShellEntry>());
        else if (_dragMoved) Rebuild();
    }

    // ----- 끌어 넣기·순서 바꾸기 -----
    // 끄는 동안 칸이 마우스 아래로 바로 옮겨 가서 놓았을 때의 모습을 미리 보여 준다.

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool hasItems = e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = hasItems ? DataPackageOperation.Move : DataPackageOperation.None;
        if (_dragEntry != null) PreviewReorder(e);
        e.Handled = true;
    }

    private void PreviewReorder(DragEventArgs e)
    {
        // 위·아래 끝에 대고 있으면 페이지를 넘긴다(너무 빨리 넘어가지 않게 0.6초에 한 번).
        double y = e.GetPosition(_scroller).Y;
        if ((y < 18 || y > _scroller.ActualHeight - 18) && DateTime.Now - _lastFlip > TimeSpan.FromMilliseconds(600))
        {
            int before = _page;
            GoToPage(_page + (y < 18 ? -1 : 1));
            if (_page != before) _lastFlip = DateTime.Now;
        }

        var p = e.GetPosition(_items);
        var tiles = _items.Children.OfType<Border>().ToList();
        int col = Math.Clamp((int)(p.X / TileW), 0, Columns - 1);
        int row = Math.Max(0, (int)(p.Y / TileH));
        int target = Math.Min(row * Columns + col, tiles.Count - 1);
        var tile = tiles.FirstOrDefault(b => b.Tag == _dragEntry);
        if (tile == null) return;
        int current = tiles.IndexOf(tile);
        if (target == current) return;
        tiles.RemoveAt(current);
        tiles.Insert(target, tile);
        for (int i = 0; i < tiles.Count; i++)
        {
            Canvas.SetLeft(tiles[i], i % Columns * TileW);
            Canvas.SetTop(tiles[i], i / Columns * TileH);
        }
        // 목록 순서도 같이 맞춘다(놓을 때 이 순서를 저장한다).
        foreach (var t in tiles) { _items.Children.Remove(t); _items.Children.Add(t); }
        _dragMoved = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // 창 밖으로 나가면 미리 보기를 되돌린다(자식 요소 사이를 지날 때도 이 이벤트가 오므로 위치로 판단).
        if (_dragEntry == null || !_dragMoved) return;
        var p = e.GetPosition((UIElement)Content);
        var root = (FrameworkElement)Content;
        if (p.X >= 0 && p.Y >= 0 && p.X < root.ActualWidth && p.Y < root.ActualHeight) return;
        _dragMoved = false;
        Rebuild();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_dragEntry != null)
        {
            _dropped = true;
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
            return;
        }
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var paths = new List<string>();
                foreach (var item in await e.DataView.GetStorageItemsAsync())
                    if (!string.IsNullOrEmpty(item.Path)) paths.Add(item.Path);
                FileOps.AddToGroup(paths, Group.Folder, _mgr.Root);
            }
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally { deferral.Complete(); }
        e.Handled = true;
    }
}
