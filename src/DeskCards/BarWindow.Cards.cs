using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskCards;

/// <summary>
/// 카드 바 안의 카드: 사용자가 놓은 자리에 그대로 둔다(자율 배치). 위치·크기는 바 두께를 1로 친 값으로 저장해서
/// 바 두께나 모니터가 바뀌어도 같은 모양으로 늘고 준다. 비어 있으면 "클릭하여 나만의 카드 바 만들기"만 보인다.
///
/// 카드 바 편집: 바가 나온 채로 멈추고, 카드를 끌어 옮기거나(바탕화면과 같은 안내선·붙이기) 오른쪽 아래 모서리로
/// 크기를 바꾼다. 겹치면 빨간 테두리가 되고 놓으면 제자리로, 너무 붙이면 간격만큼 띄운다.
/// </summary>
internal sealed partial class BarWindow
{
    private const double GripSize = 14;

    private sealed class Entry
    {
        public required BarItem Item;
        public required CardWindow Card;
        public required CardView View;
        public required Grid Box;
        public required Border Scaled, Frame, Grip, Shield;
        /// <summary>카드 높이 ÷ 너비.</summary>
        public double Aspect;
        public bool Invalid;
    }

    private readonly Canvas _canvas = new();
    private readonly List<Entry> _entries = new();
    private readonly Border _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
    private bool _editing, _saving;
    private Entry? _selected;

    // 끌기·크기 조절 중인 상태
    private Entry? _dragging;
    private bool _resizing;
    private Point _dragStart;
    private BarItem? _dragFrom;

    /// <summary>편집이 시작되거나 끝나거나 고른 카드가 바뀔 때(편집 막대가 이름·버튼을 맞춘다).</summary>
    public event Action? EditStateChanged;

    public bool IsEditing => _editing;

    public string? SelectedName => _selected?.Card.Group.Name;

    private bool Side => _edge is ScreenEdge.Left or ScreenEdge.Right;

    /// <summary>바 두께(안쪽, DIP). 저장된 위치·크기의 단위.</summary>
    private double U => Math.Max(1, (Side ? _final.Width : _final.Height) / _scale - 2 * Pad);

    private Size Inner => new(Math.Max(1, _final.Width / _scale - 2 * Pad), Math.Max(1, _final.Height / _scale - 2 * Pad));

    private UIElement CreateSurface()
    {
        _canvas.Width = Inner.Width;
        _canvas.Height = Inner.Height;
        _canvas.Background = Brushes.Transparent; // 빈 곳을 누르면 선택을 푼다
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (!_editing || e.OriginalSource != _canvas) return;
            Select(null);
        };
        _empty.MouseLeftButtonUp += (_, e) =>
        {
            if (_editing) return;
            e.Handled = true;
            BeginEdit();
        };
        var grid = new Grid { Margin = new Thickness(Pad) };
        grid.Children.Add(_canvas);
        grid.Children.Add(_empty);
        var root = new Grid();
        root.Children.Add(grid);
        root.Children.Add(_settingsButton = SettingsButton());
        return root;
    }

    private Border? _settingsButton;

    /// <summary>
    /// 바 오른쪽 위의 작은 설정(톱니) 버튼. Windows의 아이콘 버튼처럼 평소엔 바탕 없이 아이콘만, 올리면 옅은 바탕.
    /// 누르면 바를 넣고 설정 › 카드 바를 연다.
    /// </summary>
    private Border SettingsButton()
    {
        var icon = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var hover = new Border { CornerRadius = new CornerRadius(4), Opacity = 0 };
        hover.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        var b = new Border
        {
            Width = 32,
            Height = 32,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Child = new Grid { Children = { hover, icon } },
            ToolTip = "카드 바 설정",
        };
        Panel.SetZIndex(b, 2);
        b.MouseEnter += (_, _) => hover.BeginAnimation(OpacityProperty, Motion.In(null, 1, Motion.Faster));
        b.MouseLeave += (_, _) => { hover.BeginAnimation(OpacityProperty, Motion.In(null, 0, Motion.Faster)); icon.Opacity = 1; };
        b.MouseLeftButtonDown += (_, e) => { e.Handled = true; icon.Opacity = 0.7; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            icon.Opacity = 1;
            BeginClose();
            SettingsWindow.OpenBar(_mgr);
        };
        return b;
    }

    private void OnGroupsChanged()
    {
        // 그룹이 생기거나 없어졌거나 이름이 바뀌었을 때만 다시 늘어놓는다(항목 변화는 각 CardView가 알아서 그린다).
        var want = _mgr.BarItems.Select(i => _mgr.GroupCard(i.Group)).Where(c => c != null).Select(c => c!.Group).ToList();
        if (!want.SequenceEqual(_entries.Select(e => e.Card.Group))) Dispatcher.BeginInvoke(BuildCards);
    }

    private void OnBarChanged()
    {
        if (!_saving) Dispatcher.BeginInvoke(BuildCards);
    }

    private void BuildCards()
    {
        string? selected = _selected?.Card.Group.Name;
        foreach (var e in _entries) e.View.Detach();
        _canvas.Children.Clear();
        _entries.Clear();
        _selected = null;
        double cell = _mgr.CellSize;

        foreach (var saved in _mgr.BarItems)
        {
            if (_mgr.GroupCard(saved.Group) is not { } card) continue;
            var view = new CardView(card.Group, _mgr, onDesktop: false);
            view.Apply(_mgr.GetLayout(card.Group.Name), cell);
            view.Expand = hover => OpenExpanded(card, view, hover);
            view.CardMenu = () => Menus.ForCard(card, _mgr);
            _entries.Add(CreateEntry(saved.Clone(), card, view));
        }
        foreach (var e in _entries)
        {
            _canvas.Children.Add(e.Box);
            Layout(e);
        }
        if (selected != null) Select(_entries.FirstOrDefault(e => e.Card.Group.Name == selected));
        UpdateEmpty();
        EditStateChanged?.Invoke();
    }

    private Entry CreateEntry(BarItem item, CardWindow card, CardView view)
    {
        var scaled = new Border { Child = view, LayoutTransform = new ScaleTransform(), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        // 편집 중에는 카드 위를 덮어서, 누르면 카드 안 항목이 열리는 대신 카드를 끈다.
        var shield = new Border { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Visibility = _editing ? Visibility.Visible : Visibility.Collapsed };
        var frame = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(-4),
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden,
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        var grip = new Border
        {
            Width = GripSize,
            Height = GripSize,
            CornerRadius = new CornerRadius(GripSize / 2),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -GripSize / 2 - 2, -GripSize / 2 - 2),
            Cursor = Cursors.SizeNWSE,
            Visibility = Visibility.Hidden,
        };
        grip.SetResourceReference(Border.BackgroundProperty, "FlyoutBg");
        grip.SetResourceReference(Border.BorderBrushProperty, "Accent");
        var box = new Grid();
        box.Children.Add(scaled);
        box.Children.Add(shield);
        box.Children.Add(frame);
        box.Children.Add(grip);

        var e = new Entry
        {
            Item = item, Card = card, View = view, Box = box, Scaled = scaled, Frame = frame, Grip = grip, Shield = shield,
            Aspect = view.Height / Math.Max(1, view.Width),
        };
        // 바탕화면 카드처럼 파일·아이콘을 끌어다 놓으면 그 그룹에 넣는다. 놓을 수 있는 동안 파란 테두리.
        box.AllowDrop = true;
        void Over(object? _, DragEventArgs a)
        {
            bool ok = FileOps.CanAccept(a.Data, card.Group.Folder);
            a.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            ShowDropTarget(e, ok);
            a.Handled = true;
        }
        box.DragEnter += Over;
        box.DragOver += Over;
        box.DragLeave += (_, _) => ShowDropTarget(e, false);
        box.Drop += (_, a) =>
        {
            ShowDropTarget(e, false);
            if (a.Data.GetData(DataFormats.FileDrop) is string[] paths)
                FileOps.AddToGroup(paths, card.Group.Folder, _mgr.Root);
            // 이동은 우리가 직접 했으므로 원본 쪽에서 삭제하지 않도록 None을 돌려준다.
            a.Effects = DragDropEffects.None;
            a.Handled = true;
        };
        shield.MouseLeftButtonDown += (_, a) => StartDrag(e, a, resize: false, shield);
        grip.MouseLeftButtonDown += (_, a) => StartDrag(e, a, resize: true, grip);
        foreach (var handle in new UIElement[] { shield, grip })
        {
            handle.MouseMove += (_, a) => { if (_dragging == e) DragTo(a.GetPosition(_canvas)); };
            handle.MouseLeftButtonUp += (_, a) => { if (_dragging == e) { a.Handled = true; EndDrag(); } };
            handle.LostMouseCapture += (_, _) => { if (_dragging == e) EndDrag(); };
        }
        return e;
    }

    /// <summary>저장된 값(바 두께 단위)으로 카드 자리·크기를 맞춘다.</summary>
    private void Layout(Entry e)
    {
        double u = U, w = e.Item.W * u;
        double k = w / Math.Max(1, e.View.Width);
        ((ScaleTransform)e.Scaled.LayoutTransform).ScaleX = k;
        ((ScaleTransform)e.Scaled.LayoutTransform).ScaleY = k;
        e.Box.Width = w;
        e.Box.Height = w * e.Aspect;
        Canvas.SetLeft(e.Box, e.Item.X * u);
        Canvas.SetTop(e.Box, e.Item.Y * u);
    }

    private Rect RectOf(BarItem item, double aspect)
    {
        double u = U;
        return new Rect(item.X * u, item.Y * u, item.W * u, item.W * u * aspect);
    }

    private void UpdateEmpty()
    {
        if (_entries.Count > 0) { _empty.Visibility = Visibility.Collapsed; return; }
        _empty.Visibility = Visibility.Visible;
        var text = new TextBlock
        {
            Text = _editing ? "편집 막대의 [+]로 카드를 넣어요" : "클릭하여 나만의 카드 바 만들기",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = Math.Max(80, Inner.Width - 16),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, _editing ? "SubFg" : "Fg");
        if (!_editing)
        {
            _empty.Cursor = Cursors.Hand;
            _empty.MouseEnter += (_, _) => text.TextDecorations = TextDecorations.Underline;
            _empty.MouseLeave += (_, _) => text.TextDecorations = null;
        }
        else _empty.Cursor = null;
        _empty.Child = text;
    }

    // ----- 카드 바 편집 -----

    /// <summary>바를 나온 채로 멈추고 편집 막대를 띄운다.</summary>
    public void BeginEdit()
    {
        if (_editing || _closing) return;
        _editing = true;
        ExpandedWindow.CloseCurrent();
        foreach (var e in _entries) e.Shield.Visibility = Visibility.Visible;
        if (_settingsButton != null) _settingsButton.Visibility = Visibility.Collapsed;
        UpdateEmpty();
        EditBar.OpenForCardBar(_mgr, this);
        EditStateChanged?.Invoke();
    }

    public void EndEdit()
    {
        if (!_editing) return;
        _editing = false;
        if (_dragging != null) EndDrag();
        Select(null);
        foreach (var e in _entries) e.Shield.Visibility = Visibility.Collapsed;
        if (_settingsButton != null) _settingsButton.Visibility = Visibility.Visible;
        UpdateEmpty();
        SmartGuides.Hide();
        EditBar.CloseBar();
        EditStateChanged?.Invoke();
        Activate();
    }

    private void Select(Entry? e)
    {
        if (_selected == e) return;
        _selected = e;
        foreach (var x in _entries)
        {
            bool on = x == e && _editing;
            x.Frame.Visibility = on || x.Invalid ? Visibility.Visible : Visibility.Hidden;
            x.Grip.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            // 고른 카드를 맨 위에
            Panel.SetZIndex(x.Box, x == e ? 1 : 0);
        }
        EditStateChanged?.Invoke();
    }

    private void SetInvalid(Entry e, bool invalid)
    {
        e.Invalid = invalid;
        e.Frame.BorderBrush = invalid ? new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)) : null;
        if (!invalid) e.Frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        e.Frame.Visibility = invalid || (e == _selected && _editing) ? Visibility.Visible : Visibility.Hidden;
    }

    private void ShowDropTarget(Entry e, bool on)
    {
        if (e.Invalid) return;
        e.Frame.Visibility = on || (e == _selected && _editing) ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>[+]: 바탕화면 카드 넣기, 또는 바에만 있는 새 그룹 만들기.</summary>
    public void ShowAddMenu(Point screenPx)
    {
        var menu = new FluentMenu();
        var cards = _mgr.CardsNotInBar().ToList();
        foreach (var c in cards)
        {
            var card = c;
            menu.Item("", card.Group.Name, () => Add(card), image: card.Group.Items.FirstOrDefault()?.Icon);
        }
        if (cards.Count > 0) menu.Separator();
        menu.Item("", "카드 바에만 둘 새 그룹", () =>
        {
            if (_mgr.NewBarGroup() is not { } card) return;
            var e = Add(card);
            if (e != null) OpenExpanded(card, e.Box, hover: false, editTitle: true);
        });
        menu.ShowAt((int)screenPx.X, (int)screenPx.Y);
    }

    /// <summary>카드를 바 안 빈자리(가장자리 쪽부터 차례로)에 넣고 고른다.</summary>
    private Entry? Add(CardWindow card)
    {
        if (_entries.Any(x => x.Card == card)) return null;
        var view = new CardView(card.Group, _mgr, onDesktop: false);
        view.Apply(_mgr.GetLayout(card.Group.Name), _mgr.CellSize);
        view.Expand = hover => OpenExpanded(card, view, hover);
        view.CardMenu = () => Menus.ForCard(card, _mgr);
        double aspect = view.Height / Math.Max(1, view.Width);
        // 바 두께의 80%에 맞춘 크기(가로·세로 중 긴 쪽 기준).
        double w = 0.8 / Math.Max(1, aspect);
        var item = new BarItem { Group = card.Group.Name, W = w };
        (item.X, item.Y) = FreeSpot(w, aspect);
        var e = CreateEntry(item, card, view);
        _entries.Add(e);
        _canvas.Children.Add(e.Box);
        Layout(e);
        UpdateEmpty();
        Select(e);
        Save();
        return e;
    }

    /// <summary>바 길이 방향으로 훑으며 다른 카드와 간격을 두고 들어갈 첫 자리(바 두께 단위).</summary>
    private (double X, double Y) FreeSpot(double w, double aspect)
    {
        double u = U, gap = SmartGuides.GapDip / u;
        double cross = Side ? w : w * aspect;          // 두께 방향 크기
        double along = Side ? w * aspect : w;          // 길이 방향 크기
        double c0 = Math.Max(0, (1 - cross) / 2);      // 두께 방향 가운데
        double length = (Side ? Inner.Height : Inner.Width) / u;
        for (double a = 0; a + along <= length + 1e-6; a += 0.05)
        {
            var r = Side ? new Rect(c0, a, w, w * aspect) : new Rect(a, c0, w, w * aspect);
            bool free = _entries.All(x =>
            {
                var o = new Rect(x.Item.X, x.Item.Y, x.Item.W, x.Item.W * x.Aspect);
                o.Inflate(gap, gap);
                return !o.IntersectsWith(r);
            });
            if (free) return (r.X, r.Y);
        }
        return Side ? (c0, 0) : (0, c0);
    }

    public void RemoveSelected()
    {
        if (!_editing || _selected is not { } e) return;
        _entries.Remove(e);
        _canvas.Children.Remove(e.Box);
        e.View.Detach();
        _selected = null;
        Save();
        // 카드 바에만 있던 그룹은 없어지지 않게 바탕화면으로 꺼낸다.
        if (_mgr.IsBarOnly(e.Card.Group.Name)) _mgr.ShowOnDesktop(e.Card);
        UpdateEmpty();
        EditStateChanged?.Invoke();
    }

    /// <summary>화살표: 고른 카드를 dx, dy(DIP)만큼 옮긴다. 바 밖으로는 나가지 않는다.</summary>
    public void Nudge(double dx, double dy)
    {
        if (!_editing || _selected is not { } e) return;
        var r = RectOf(e.Item, e.Aspect);
        double x = Math.Clamp(r.X + dx, 0, Math.Max(0, Inner.Width - r.Width));
        double y = Math.Clamp(r.Y + dy, 0, Math.Max(0, Inner.Height - r.Height));
        e.Item.X = x / U;
        e.Item.Y = y / U;
        Layout(e);
        SetInvalid(e, Placement(e) != SmartGuides.Placement.Ok);
        Save();
    }

    private void Save()
    {
        _saving = true;
        try { _mgr.SetBarItems(_entries.Select(e => e.Item)); }
        finally { _saving = false; }
    }

    // ----- 끌기 · 크기 조절 (안내선은 바탕화면과 같은 SmartGuides를 바 안쪽 영역 기준 물리 픽셀로 쓴다) -----

    private Point OriginPx => _canvas.PointToScreen(new Point(0, 0));

    private System.Drawing.Rectangle AreaPx
    {
        get
        {
            var o = OriginPx;
            return new((int)Math.Round(o.X), (int)Math.Round(o.Y), (int)Math.Round(Inner.Width * _scale), (int)Math.Round(Inner.Height * _scale));
        }
    }

    private Native.RECT ToPx(Rect r)
    {
        var o = OriginPx;
        return new Native.RECT
        {
            Left = (int)Math.Round(o.X + r.Left * _scale),
            Top = (int)Math.Round(o.Y + r.Top * _scale),
            Right = (int)Math.Round(o.X + r.Right * _scale),
            Bottom = (int)Math.Round(o.Y + r.Bottom * _scale),
        };
    }

    private List<Native.RECT> OthersPx(Entry me) =>
        _entries.Where(x => x != me).Select(x => ToPx(RectOf(x.Item, x.Aspect))).ToList();

    private SmartGuides.Placement Placement(Entry e) =>
        SmartGuides.Check(ToPx(RectOf(e.Item, e.Aspect)), OthersPx(e), SmartGuides.GapPx(_scale));

    private void StartDrag(Entry e, MouseButtonEventArgs a, bool resize, UIElement handle)
    {
        if (!_editing) return;
        a.Handled = true;
        Select(e);
        _dragging = e;
        _resizing = resize;
        _dragStart = a.GetPosition(_canvas);
        _dragFrom = e.Item.Clone();
        handle.CaptureMouse();
    }

    private void DragTo(Point p)
    {
        if (_dragging is not { } e || _dragFrom == null) return;
        var start = RectOf(_dragFrom, e.Aspect);
        var others = OthersPx(e);
        var area = AreaPx;
        List<SmartGuides.Line> lines;
        if (!_resizing)
        {
            var moved = start;
            moved.Offset(p.X - _dragStart.X, p.Y - _dragStart.Y);
            var px = ToPx(moved);
            var (x, y, l) = SmartGuides.Snap(px.Left, px.Top, px.Right - px.Left, px.Bottom - px.Top, others, area, _scale);
            lines = l;
            e.Item.X = (x - area.Left) / _scale / U;
            e.Item.Y = (y - area.Top) / _scale / U;
        }
        else
        {
            // 비율 고정: 가로·세로로 늘린 비율 중 큰 쪽만큼 키운다. 왼쪽 위는 그대로.
            double g = 1 + Math.Max((p.X - _dragStart.X) / start.Width, (p.Y - _dragStart.Y) / start.Height);
            double gMin = 0.25 * U / start.Width;
            double gMax = Math.Min((Inner.Width - start.X) / start.Width, (Inner.Height - start.Y) / start.Height);
            g = Math.Clamp(g, Math.Min(gMin, gMax), gMax);
            var (snapped, l) = SmartGuides.SnapScale(ToPx(start), g, others, area, _scale);
            lines = l;
            e.Item.W = _dragFrom.W * Math.Clamp(snapped, Math.Min(gMin, gMax), gMax);
        }
        Layout(e);
        SetInvalid(e, Placement(e) != SmartGuides.Placement.Ok);
        if (_mgr.ShowGuides) SmartGuides.Show(area, lines);
    }

    private void EndDrag()
    {
        if (_dragging is not { } e) return;
        _dragging = null;
        SmartGuides.Hide();
        Mouse.Capture(null);
        switch (Placement(e))
        {
            case SmartGuides.Placement.Overlap when !SmartGuides.AllowOverlap:
                // 겹친 채 놓으면 원래 자리로.
                if (_dragFrom != null) { e.Item.X = _dragFrom.X; e.Item.Y = _dragFrom.Y; e.Item.W = _dragFrom.W; }
                break;
            case SmartGuides.Placement.TooClose:
                var (dx, dy) = SmartGuides.PushApart(ToPx(RectOf(e.Item, e.Aspect)), OthersPx(e), SmartGuides.GapPx(_scale));
                var r = RectOf(e.Item, e.Aspect);
                e.Item.X = Math.Clamp(r.X + dx / _scale, 0, Math.Max(0, Inner.Width - r.Width)) / U;
                e.Item.Y = Math.Clamp(r.Y + dy / _scale, 0, Math.Max(0, Inner.Height - r.Height)) / U;
                break;
        }
        Layout(e);
        SetInvalid(e, false);
        foreach (var x in _entries) if (x != e) SetInvalid(x, false);
        Save();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_editing) EndEdit(); else BeginClose();
            e.Handled = true;
            return;
        }
        if (!_editing) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Delete: RemoveSelected(); break;
            case Key.Left: Nudge(-step, 0); break;
            case Key.Right: Nudge(step, 0); break;
            case Key.Up: Nudge(0, -step); break;
            case Key.Down: Nudge(0, step); break;
            default: return;
        }
        e.Handled = true;
    }
}
