using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskCards.Expanded;

internal sealed partial class BarWindow
{
    private sealed class Entry
    {
        public BarCardView View = null!;
        public Grid Box = null!;
        public Border Frame = null!, Shield = null!, Grip = null!;
        public ScaleTransform Scale = null!;
        public BarCardData Data = null!;
        public double Aspect;
    }

    private readonly List<Entry> _entries = new();
    private bool _editing;
    private string? _selected;
    private Entry? _active;   // 지금 끌고 있는 카드
    private BarContent? _deferred;
    private static readonly Brush Invalid = MakeBrush(Color.FromRgb(0xE8, 0x3A, 0x3A));

    private static Brush MakeBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>바 안 카드를 새 내용으로 다시 늘어놓는다.</summary>
    public void SetContent(BarContent content)
    {
        if (_closing && !IsVisible) return;
        if (_active != null) { _deferred = content; return; }
        _canvas.Children.Clear();
        _entries.Clear();
        double u = U;
        foreach (var data in content.Cards)
        {
            var view = new BarCardView(_host, data);
            view.Apply(data, content.HoverExpand, content.HoverDelayMs);
            double w = data.W * u;
            double aspect = view.Height / Math.Max(1, view.Width);
            double k = w / Math.Max(1, view.Width);
            var scale = new ScaleTransform(k, k);
            var scaled = new Border { Child = view, LayoutTransform = scale, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var frame = new Border
            {
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(-4),
                IsHitTestVisible = false,
                Visibility = Visibility.Hidden,
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
            var box = new Grid { Width = w, Height = w * aspect };
            box.Children.Add(scaled);
            box.Children.Add(frame);
            var entry = new Entry { View = view, Box = box, Frame = frame, Scale = scale, Data = data, Aspect = aspect };

            // 편집 중에는 카드 위를 덮개가 가려 눌러도 실행되지 않고, 끌어 옮기고 모서리로 크기를 바꾼다.
            var shield = new Border { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Visibility = Visibility.Collapsed };
            var grip = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -5, -5), Cursor = Cursors.SizeNWSE,
                Background = Brushes.White, BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed,
            };
            grip.SetResourceReference(Border.BorderBrushProperty, "Accent");
            entry.Shield = shield;
            entry.Grip = grip;
            box.Children.Add(shield);
            box.Children.Add(grip);
            WireEdit(entry);

            // 바탕화면 카드처럼 파일·아이콘을 끌어다 놓으면 그 그룹에 넣는다. 놓을 수 있는 동안 파란 테두리.
            box.AllowDrop = true;
            void Over(object? _, DragEventArgs a)
            {
                bool ok = !_editing && a.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths &&
                    paths.Any(p => !string.Equals(System.IO.Path.GetDirectoryName(p), data.Folder.TrimEnd((char)92), StringComparison.OrdinalIgnoreCase));
                a.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
                frame.Visibility = ok ? Visibility.Visible : (_editing && _selected == data.Group ? Visibility.Visible : Visibility.Hidden);
                a.Handled = true;
            }
            box.DragEnter += Over;
            box.DragOver += Over;
            box.DragLeave += (_, _) => ShowFrame(entry);
            box.Drop += (_, a) =>
            {
                ShowFrame(entry);
                if (!_editing && a.Data.GetData(DataFormats.FileDrop) is string[] paths) _host.AddToGroup(data.Group, paths);
                // 이동은 앱이 직접 하므로 원본 쪽에서 삭제하지 않도록 None을 돌려준다.
                a.Effects = DragDropEffects.None;
                a.Handled = true;
            };
            Canvas.SetLeft(box, data.X * u);
            Canvas.SetTop(box, data.Y * u);
            _canvas.Children.Add(box);
            _entries.Add(entry);
        }
        if (_selected != null && _entries.All(e => e.Data.Group != _selected)) Select(null);
        ApplyEditState();
        UpdateEmpty();
    }

    private void UpdateEmpty()
    {
        if (_entries.Count > 0 || _editing) { _empty.Visibility = Visibility.Collapsed; return; }
        _empty.Visibility = Visibility.Visible;
        var text = new TextBlock
        {
            Text = "클릭하여 나만의 카드 바 만들기",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = Math.Max(80, Inner.Width - 16),
            Foreground = (Brush)Resources["Fg"],
        };
        _empty.Cursor = Cursors.Hand;
        _empty.MouseEnter += (_, _) => text.TextDecorations = TextDecorations.Underline;
        _empty.MouseLeave += (_, _) => text.TextDecorations = null;
        _empty.Child = text;
    }

    // ----- 편집 -----

    public bool Editing => _editing;

    public void BeginEdit()
    {
        _editing = true;
        _float?.SetEditing(true);
        ApplyEditState();
        UpdateEmpty();
        Activate();
    }

    public void EndEdit()
    {
        _editing = false;
        _float?.SetEditing(false);
        _active = null;
        Select(null);
        ApplyEditState();
        UpdateEmpty();
        if (_deferred != null) { var c = _deferred; _deferred = null; SetContent(c); }
    }

    private void ApplyEditState()
    {
        foreach (var e in _entries)
        {
            e.Shield.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
            ShowFrame(e);
        }
    }

    private void ShowFrame(Entry e)
    {
        bool sel = _editing && _selected == e.Data.Group;
        e.Frame.Visibility = sel ? Visibility.Visible : Visibility.Hidden;
        e.Frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
        e.Grip.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Select(string? group)
    {
        if (_selected == group) return;
        _selected = group;
        foreach (var e in _entries) ShowFrame(e);
        _host.SelectionChanged(group);
    }

    private void WireEdit(Entry en)
    {
        Point start = default;
        double x0 = 0, y0 = 0, w0 = 0;
        PixelRect startPx = default;
        bool moved = false;

        en.Shield.MouseLeftButtonDown += (_, a) =>
        {
            Select(en.Data.Group);
            _active = en;
            start = a.GetPosition(_canvas);
            x0 = Canvas.GetLeft(en.Box); y0 = Canvas.GetTop(en.Box);
            moved = false;
            en.Shield.CaptureMouse();
            a.Handled = true;
        };
        en.Shield.MouseMove += (_, a) =>
        {
            if (_active != en || !en.Shield.IsMouseCaptured) return;
            var p = a.GetPosition(_canvas);
            double nx = Math.Clamp(x0 + p.X - start.X, 0, Math.Max(0, _canvas.Width - en.Box.Width));
            double ny = Math.Clamp(y0 + p.Y - start.Y, 0, Math.Max(0, _canvas.Height - en.Box.Height));
            // 안내선: 다른 카드·바 가운데와 줄이 맞으면 붙는다(앱이 계산하고 선을 그린다).
            var snapped = _host.SnapMove(en.Data.Group, PxRect(nx, ny, en.Box.Width, en.Box.Height));
            nx = Math.Clamp(CanvasX(snapped.Left), 0, Math.Max(0, _canvas.Width - en.Box.Width));
            ny = Math.Clamp(CanvasY(snapped.Top), 0, Math.Max(0, _canvas.Height - en.Box.Height));
            if (Math.Abs(nx - Canvas.GetLeft(en.Box)) < 0.01 && Math.Abs(ny - Canvas.GetTop(en.Box)) < 0.01) return;
            moved = true;
            Canvas.SetLeft(en.Box, nx);
            Canvas.SetTop(en.Box, ny);
            MarkInvalid(en);
        };
        en.Shield.MouseLeftButtonUp += (_, a) =>
        {
            if (_active != en) return;
            en.Shield.ReleaseMouseCapture();
            bool bad = !SettlePlacement(en);
            _host.GuidesOff();
            if (bad) { Canvas.SetLeft(en.Box, x0); Canvas.SetTop(en.Box, y0); }
            _active = null;
            ShowFrame(en);
            if (moved && !bad) Commit();
            else if (_deferred != null) { var c = _deferred; _deferred = null; SetContent(c); }
            a.Handled = true;
        };
        en.Shield.MouseRightButtonUp += (_, a) => { a.Handled = true; Select(en.Data.Group); _host.CardMenu(en.Data.Group); };

        en.Grip.MouseLeftButtonDown += (_, a) =>
        {
            _active = en;
            start = a.GetPosition(_canvas);
            w0 = en.Box.Width;
            startPx = PxRect(Canvas.GetLeft(en.Box), Canvas.GetTop(en.Box), en.Box.Width, en.Box.Height);
            x0 = en.Box.Width; // 직전 크기(되돌릴 때)
            y0 = en.Box.Height;
            moved = false;
            en.Grip.CaptureMouse();
            a.Handled = true;
        };
        en.Grip.MouseMove += (_, a) =>
        {
            if (_active != en || !en.Grip.IsMouseCaptured) return;
            var p = a.GetPosition(_canvas);
            double left = Canvas.GetLeft(en.Box), top = Canvas.GetTop(en.Box);
            double maxW = Math.Min(_canvas.Width - left, (_canvas.Height - top) / Math.Max(0.01, en.Aspect));
            double nw = Math.Clamp(w0 + (p.X - start.X), Math.Min(U * 0.25, maxW), Math.Max(U * 0.25, maxW));
            double g = _host.SnapScale(en.Data.Group, startPx, nw / Math.Max(1, w0));
            nw = Math.Clamp(w0 * g, Math.Min(U * 0.25, maxW), Math.Max(U * 0.25, maxW));
            moved = true;
            Resize(en, nw);
            MarkInvalid(en);
        };
        en.Grip.MouseLeftButtonUp += (_, a) =>
        {
            if (_active != en) return;
            en.Grip.ReleaseMouseCapture();
            bool bad = !SettlePlacement(en);
            _host.GuidesOff();
            if (bad) Resize(en, x0);
            _active = null;
            ShowFrame(en);
            if (moved && !bad) Commit();
            else if (_deferred != null) { var c = _deferred; _deferred = null; SetContent(c); }
            a.Handled = true;
        };
    }

    private static void Resize(Entry en, double w)
    {
        en.Box.Width = w;
        en.Box.Height = w * en.Aspect;
        double k = w / Math.Max(1, en.View.Width);
        en.Scale.ScaleX = en.Scale.ScaleY = k;
    }

    // ----- 안내선·자리 검사는 바탕화면 카드와 같은 규칙을 쓰려고 앱(SmartGuides)에 묻는다. 좌표는 화면 물리 픽셀이다. -----

    private PixelRect PxRect(double x, double y, double w, double h)
    {
        double s = _o.Scale;
        int l = (int)Math.Round(_o.Final.Left + (Pad + x) * s), t = (int)Math.Round(_o.Final.Top + (Pad + y) * s);
        return new PixelRect(l, t, l + (int)Math.Round(w * s), t + (int)Math.Round(h * s));
    }

    private PixelRect PxRect(Entry en) => PxRect(Canvas.GetLeft(en.Box), Canvas.GetTop(en.Box), en.Box.Width, en.Box.Height);

    private double CanvasX(int px) => (px - _o.Final.Left) / _o.Scale - Pad;

    private double CanvasY(int px) => (px - _o.Final.Top) / _o.Scale - Pad;

    private bool Overlaps(Entry en) => _host.Placement(en.Data.Group, PxRect(en)) != 0;

    /// <summary>놓은 자리가 괜찮은지. 간격보다 가까우면 간격만큼 밀어 보고, 그래도 안 되면 false.</summary>
    private bool SettlePlacement(Entry en)
    {
        int placement = _host.Placement(en.Data.Group, PxRect(en));
        if (placement == 1)
        {
            var pushed = _host.PushApart(en.Data.Group, PxRect(en));
            double nx = Math.Clamp(CanvasX(pushed.Left), 0, Math.Max(0, _canvas.Width - en.Box.Width));
            double ny = Math.Clamp(CanvasY(pushed.Top), 0, Math.Max(0, _canvas.Height - en.Box.Height));
            Canvas.SetLeft(en.Box, nx);
            Canvas.SetTop(en.Box, ny);
            placement = _host.Placement(en.Data.Group, PxRect(en));
        }
        return placement == 0;
    }

    private void MarkInvalid(Entry en)
    {
        en.Frame.Visibility = Visibility.Visible;
        if (Overlaps(en)) en.Frame.BorderBrush = Invalid;
        else en.Frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
    }

    private void Commit()
    {
        double u = U;
        _host.ItemsEdited(_entries.Select(e => new BarItemData(e.Data.Group, Canvas.GetLeft(e.Box) / u, Canvas.GetTop(e.Box) / u, e.Box.Width / u)).ToList());
    }

    private void Nudge(double dx, double dy)
    {
        var en = _entries.FirstOrDefault(e => e.Data.Group == _selected);
        if (en == null || _active != null) return;
        double ox = Canvas.GetLeft(en.Box), oy = Canvas.GetTop(en.Box);
        double nx = Math.Clamp(ox + dx, 0, Math.Max(0, _canvas.Width - en.Box.Width));
        double ny = Math.Clamp(oy + dy, 0, Math.Max(0, _canvas.Height - en.Box.Height));
        Canvas.SetLeft(en.Box, nx);
        Canvas.SetTop(en.Box, ny);
        if (Overlaps(en)) { Canvas.SetLeft(en.Box, ox); Canvas.SetTop(en.Box, oy); return; }
        Commit();
    }

    private void HandleKey(KeyEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        double step = shift ? 10 : 1;
        if (e.Key == Key.Escape)
        {
            if (_editing) _host.EditEnded(); else BeginClose();
            e.Handled = true;
        }
        else if (!_editing) return;
        else if (e.Key == Key.Delete && _selected != null) { _host.RemoveRequested(_selected); e.Handled = true; }
        else if (e.Key == Key.Left) { Nudge(-step, 0); e.Handled = true; }
        else if (e.Key == Key.Right) { Nudge(step, 0); e.Handled = true; }
        else if (e.Key == Key.Up) { Nudge(0, -step); e.Handled = true; }
        else if (e.Key == Key.Down) { Nudge(0, step); e.Handled = true; }
    }
}