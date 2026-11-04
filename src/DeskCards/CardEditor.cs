using System;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DeskCards;

/// <summary>입력을 받을 때 커서 모양을 바꿀 수 있는 Grid.</summary>
internal sealed class CursorGrid : Grid
{
    public void SetCursor(InputSystemCursorShape? shape) =>
        ProtectedCursor = shape is { } s ? InputSystemCursor.Create(s) : null;
}

/// <summary>
/// 편집 모드에서 카드를 옮기고 크기를 바꾸는 입력 처리.
/// 카드 위를 덮는 투명한 막(shield)을 끌면 옮기고, 오른쪽 아래 모서리 손잡이(grip)를 끌면 비율을 유지한 채 크기를 바꾼다.
/// 포인터를 잡고(capture) 마우스 이동량으로 창 위치를 직접 정하므로, 끄는 내내 스마트 가이드·간격 검사가 우리 손 안에 있다.
/// 평소(편집 아님)에는 막과 손잡이가 숨어 있어 카드 내용이 입력을 받는다.
/// </summary>
internal sealed class CardEditor
{
    private enum Mode { None, Move, Resize }

    private readonly CardWindow _card;
    private readonly CursorGrid _shield, _grip;

    private Mode _mode;
    private bool _dragging; // 끌기 문턱을 넘었는지(그 전에는 누르기=고르기일 뿐이다)
    private Point _pressPoint;
    private Native.POINT _cursorStart;
    private Native.RECT _windowStart; // 이름 줄을 뺀 창 자리
    private System.Collections.Generic.IReadOnlyList<Native.RECT> _others = Array.Empty<Native.RECT>();
    private bool _triedFlush; // 간격을 두는 중에 옆 카드에 딱 붙이려고 했는지
    private double _zoomStart;

    public CardEditor(CardWindow card, Grid root)
    {
        _card = card;
        _shield = new CursorGrid { Background = new SolidColorBrush(Colors.Transparent), Visibility = Visibility.Collapsed };
        _grip = new CursorGrid
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Colors.Transparent),
            Visibility = Visibility.Collapsed,
        };
        _grip.Children.Add(GripGlyph());
        _shield.SetCursor(InputSystemCursorShape.SizeAll);
        _grip.SetCursor(InputSystemCursorShape.SizeNorthwestSoutheast);
        root.Children.Add(_shield);
        root.Children.Add(_grip);

        Hook(_shield, Mode.Move);
        Hook(_grip, Mode.Resize);
    }

    /// <summary>편집 모드일 때만 막과 손잡이를 보여 입력을 가로챈다.</summary>
    public bool Enabled
    {
        set
        {
            var v = value ? Visibility.Visible : Visibility.Collapsed;
            _shield.Visibility = v;
            _grip.Visibility = v;
        }
    }

    private void Hook(CursorGrid target, Mode mode)
    {
        target.PointerPressed += (_, e) => Press(target, mode, e);
        target.PointerMoved += (_, e) => Track(target, e);
        target.PointerReleased += (_, e) => Release(target, e);
        target.PointerCaptureLost += (_, _) => Finish();
    }

    // ----- 누르기·끌기·놓기 -----

    private void Press(CursorGrid target, Mode mode, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(target);
        if (!point.Properties.IsLeftButtonPressed) return;
        _card.Mgr.Select(_card);
        _mode = mode;
        _dragging = false;
        _pressPoint = point.Position;
        Native.GetCursorPos(out _cursorStart);
        _windowStart = _card.Footprint;
        _windowStart.Bottom -= LabelPx();
        _others = _card.Mgr.CardRects(except: _card);
        _triedFlush = false;
        _zoomStart = _card.CurrentLayout.Zoom;
        target.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Track(CursorGrid target, PointerRoutedEventArgs e)
    {
        if (_mode == Mode.None) return;
        var point = e.GetCurrentPoint(target);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (!_dragging)
        {
            if (Math.Abs(point.Position.X - _pressPoint.X) < Native.GetSystemMetrics(68) &&
                Math.Abs(point.Position.Y - _pressPoint.Y) < Native.GetSystemMetrics(69)) return;
            _dragging = true;
            BalloonTip.CloseCurrent(); // 앞 안내는 이전 자리 이야기라 새로 옮기기 시작하면 치운다
        }
        Native.GetCursorPos(out var cursor);
        if (_mode == Mode.Move) MoveTo(cursor);
        else ResizeTo(cursor);
        e.Handled = true;
    }

    private void Release(CursorGrid target, PointerRoutedEventArgs e)
    {
        target.ReleasePointerCapture(e.Pointer);
        Finish();
        e.Handled = true;
    }

    private void Finish()
    {
        var mode = _mode;
        _mode = Mode.None;
        if (!_dragging) return;
        _dragging = false;
        SmartGuides.Hide();
        if (mode == Mode.Move) SettleMove();
        else SettleResize();
    }

    // ----- 옮기기 -----

    /// <summary>
    /// 자유 이동 + 스마트 가이드: 다른 카드·화면 가운데와 줄이 맞으면 살짝 붙고 안내선을 보여 준다.
    /// 직전(이미 붙은) 위치가 아니라 누른 자리부터의 전체 마우스 이동량으로 계산해서, 한번 붙어도 빠져나올 수 있다.
    /// </summary>
    private void MoveTo(Native.POINT cursor)
    {
        int w = _windowStart.Right - _windowStart.Left, h = _windowStart.Bottom - _windowStart.Top + LabelPx();
        int fx = _windowStart.Left + cursor.X - _cursorStart.X, fy = _windowStart.Top + cursor.Y - _cursorStart.Y;
        var wa = DesktopGrid.WorkAreaAt(fx + w / 2, fy + h / 2);
        double scale = Scale();
        var (x, y, lines) = SmartGuides.Snap(fx, fy, w, h, _others, wa, scale);

        int gap = SmartGuides.GapPx(scale);
        if (gap > 0)
        {
            // 간격을 두는 중인데 옆 카드에 딱 붙이려고(간격 절반보다 가까이, 살짝 겹치는 데까지) 끌었는지.
            var raw = new Native.RECT { Left = fx, Top = fy, Right = fx + w, Bottom = fy + h };
            int slack = (int)Math.Round(8 * scale);
            var (sx, sy) = SmartGuides.Separation(raw, _others);
            bool Near(int? d) => d is { } v && v > -slack && v < gap / 2;
            _triedFlush |= Near(sx) || Near(sy);
        }
        var placed = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        _card.SetInvalid(SmartGuides.Check(placed, _others, gap) != SmartGuides.Placement.Ok);
        SmartGuides.Show(wa, lines);
        _card.MoveTo(x, y);
    }

    /// <summary>
    /// 옮기기를 마쳤을 때 지금 설정으로 자리를 검사한다.
    /// 겹쳤으면(겹치기 허용이 꺼져 있을 때) 저장하지 않고 끌기 전 자리로 돌려놓는다.
    /// 나란한 카드와 간격보다 가까우면(완전히 붙이기가 꺼져 있을 때) 간격만큼 떼어 놓는다.
    /// 어느 쪽이든, 또는 딱 붙이려 했던 것이면 이유와 실험 설정을 알려 준다.
    /// </summary>
    private void SettleMove()
    {
        var me = _card.Footprint;
        int gap = SmartGuides.GapPx(Scale());
        var p = SmartGuides.Check(me, _others, gap);
        int x = me.Left, y = me.Top;
        if (p == SmartGuides.Placement.Overlap)
        {
            x = _windowStart.Left;
            y = _windowStart.Top;
        }
        else if (p == SmartGuides.Placement.TooClose)
        {
            var (dx, dy) = SmartGuides.PushApart(me, _others, gap);
            int w = me.Right - me.Left, h = me.Bottom - me.Top;
            var wa = DesktopGrid.WorkAreaAt(me.Left + w / 2, me.Top + h / 2);
            x = Math.Max(wa.Left, Math.Min(me.Left + dx, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(me.Top + dy, wa.Bottom - h));
        }
        if (x != me.Left || y != me.Top) _card.MoveTo(x, y);
        if (p == SmartGuides.Placement.Ok && _triedFlush) p = SmartGuides.Placement.TooClose;
        _card.SetInvalid(false);
        _card.Mgr.SavePosition(_card);
        Ui.Post(() => _card.Mgr.RefreshPlacement(), Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
        if (p != SmartGuides.Placement.Ok) Warn(p);
    }

    // ----- 크기 조절 -----

    /// <summary>
    /// 비율 고정: 가로·세로 늘어난 비율의 평균만큼 카드 전체를 키우거나 줄인다.
    /// 손잡이가 모서리와 함께 움직여 증분이 흔들리므로, 잡은 순간부터의 전체 마우스 이동량으로 계산한다.
    /// 다른 카드·화면 가운데와 끝선이 맞으면 붙고 안내선을 보여 준다.
    /// </summary>
    private void ResizeTo(Native.POINT cursor)
    {
        var r0 = _windowStart;
        double w0 = r0.Right - r0.Left, h0 = r0.Bottom - r0.Top;
        double g = ((w0 + cursor.X - _cursorStart.X) / w0 + (h0 + cursor.Y - _cursorStart.Y) / h0) / 2;
        var wa = DesktopGrid.WorkAreaAt(r0.Left + 1, r0.Top + 1);
        var (snapped, lines) = SmartGuides.SnapScale(r0, g, _others, wa, Scale());
        SmartGuides.Show(wa, lines);
        _card.SetZoomFromEditor(_zoomStart * snapped);
        _card.SetInvalid(Placement() != SmartGuides.Placement.Ok);
    }

    /// <summary>겹치거나 간격보다 가까워지는 크기는 저장하지 않고 잡기 전 크기로 되돌린다.</summary>
    private void SettleResize()
    {
        var p = Placement();
        if (p != SmartGuides.Placement.Ok) _card.SetZoomFromEditor(_zoomStart);
        _card.CommitFromEditor();
        _card.SetInvalid(false);
        _card.Mgr.RefreshPlacement();
        if (p != SmartGuides.Placement.Ok) Warn(p);
    }

    // ----- 공통 -----

    /// <summary>지금 자리를 지금 설정으로 검사한다.</summary>
    public SmartGuides.Placement Placement() =>
        SmartGuides.Check(_card.Footprint, _card.Mgr.CardRects(except: _card), SmartGuides.GapPx(Scale()));

    private double Scale() => Native.MonitorScaleOf(Hwnd.Of(_card));

    private int LabelPx() => _card.LabelHeightPx;

    /// <summary>안 되는 자리에 놓았을 때 이유와 실험 설정을 알려 준다. 편집 모드마다 종류별로 처음 한 번만 띄운다.</summary>
    private void Warn(SmartGuides.Placement p) => Ui.Post(() =>
    {
        bool overlap = p == SmartGuides.Placement.Overlap;
        string id = overlap ? "placement.overlap" : "placement.flush";
        if (!_card.IsEditing || _card.Mgr.TipHidden(id)) return;
        var mgr = _card.Mgr;
        BalloonTip.Show(_card.Footprint, overlap
            ? new BalloonTip.Tip("카드는 겹칠 수 없어요",
                "빈자리에 놓아 주세요. 겹쳐 두려면 실험 설정에서 '겹치기 · 레이어'를 켜 주세요.",
                "실험 설정 열기", () => SettingsWindow.OpenGeneral(mgr), () => mgr.HideTip(id))
            : new BalloonTip.Tip("카드를 조금 띄워 뒀어요",
                "카드끼리는 딱 붙일 수 없어요. 붙여 두려면 실험 설정에서 '완전히 붙이기'를 켜 주세요.",
                "실험 설정 열기", () => SettingsWindow.OpenGeneral(mgr), () => mgr.HideTip(id)));
    }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

    private static UIElement GripGlyph()
    {
        var geometry = new PathGeometry();
        void Segment(double x1, double y1, double x2, double y2)
        {
            var figure = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false };
            figure.Segments.Add(new LineSegment { Point = new Point(x2, y2) });
            geometry.Figures.Add(figure);
        }
        Segment(15, 5, 5, 15);
        Segment(15, 10, 10, 15);
        return new Path
        {
            Data = geometry,
            Stroke = Theme.Brush("Accent"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2),
        };
    }
}
