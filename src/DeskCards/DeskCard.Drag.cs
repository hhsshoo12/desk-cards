using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskCards;

internal abstract partial class DeskCard
{
    // ----- 입력 -----
    // 평소 클릭·끌기·우클릭은 카드 내용이 한다. 카드 자체는 움직이지 않는다.
    // 편집 모드: 누르기 = 고르기, 아무 데나 끌기 = 자유 이동(안내선), 오른쪽 아래 모서리 끌기 = 크기 조절.

    private Point _downPos;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editing) return;
        Mgr.Select(this);
        _pending = !IsWithin<Thumb>(e.OriginalSource as DependencyObject);
        _downPos = e.GetPosition(this);
        if (_pending) e.Handled = true; // 편집 중에는 카드 내용(아이콘·HTML)이 누르기를 받지 않는다.
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pending || !_editing || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downPos;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pending = false;
        try { DragMove(); } catch (InvalidOperationException) { }
        Mgr.SavePosition(this);
    }

    private void OnUp(object sender, MouseButtonEventArgs e) => _pending = false;

    private static bool IsWithin<T>(DependencyObject? d) where T : DependencyObject
    {
        for (; d != null; d = GetParent(d))
            if (d is T) return true;
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject d) =>
        d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    // ----- 편집 모드 -----

    /// <summary>편집 모드로. 켜고 끄는 건 GroupManager.BeginEditMode/EndEditMode가 모든 카드에 한꺼번에 한다.</summary>
    public void BeginEdit()
    {
        _editing = true;
        _grip.Visibility = Visibility.Visible;
        Cursor = Cursors.SizeAll;
        UpdateBorder();
        OnEditChanged(true);
        RaiseForEdit();
    }

    /// <summary>편집 모드가 켜지고 꺼질 때. 누르기를 창이 직접 받지 못하는 내용(웹 화면)은 여기서 멈춰 둔다.</summary>
    protected virtual void OnEditChanged(bool editing) { }

    /// <summary>편집 중에는 어두운 막(EditDim) 위로 올라온다. 편집이 끝나면 다시 바탕화면 층으로 내려간다.</summary>
    public void RaiseForEdit()
    {
        if (_editing) Hwnd.SetZOrder(Handle, Hwnd.Topmost);
    }

    public void EndEdit()
    {
        if (!_editing) return;
        _editing = _selected = _invalid = false;
        _grip.Visibility = Visibility.Collapsed;
        Cursor = null;
        UpdateBorder();
        OnEditChanged(false);
        // HWND_BOTTOM은 맨 위(topmost) 상태도 함께 푼다.
        SendToBottom();
        Mgr.SavePosition(this);
    }

    /// <summary>편집 막대의 작업 대상으로 골랐는지. 고른 카드는 테두리가 굵어진다.</summary>
    public void SetSelected(bool on)
    {
        _selected = on && _editing;
        UpdateBorder();
    }

    /// <summary>화살표 키로 조금씩 옮긴다(물리 픽셀). 작업 영역 밖으로는 나가지 않는다.</summary>
    public void Nudge(int dx, int dy)
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top + LabelPx;
        var wa = DesktopGrid.WorkAreaAt(r.Left + w / 2, r.Top + h / 2);
        int x = Math.Max(wa.Left, Math.Min(r.Left + dx, wa.Right - w));
        int y = Math.Max(wa.Top, Math.Min(r.Top + dy, wa.Bottom - h));
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Mgr.SavePosition(this);
        Mgr.RefreshPlacement();
    }

    private double _gripZoom;

    private void OnGripStart()
    {
        BeginMoveTracking(Handle);
        _gripZoom = _appliedScale / ScaleFactor;
    }

    /// <summary>끌기·크기 조절 시작점(마우스·창 위치)과 안내선 기준이 될 다른 카드 위치를 기억한다.</summary>
    private void BeginMoveTracking(IntPtr hwnd)
    {
        Native.GetCursorPos(out _moveCursorStart);
        _moveWindowStart = Footprint;
        _moveOthers = Mgr.CardRects(except: this);
        _triedFlush = false;
        BalloonTip.CloseCurrent(); // 앞 안내는 이전 자리 이야기라 새로 옮기기 시작하면 치운다
    }

    private void OnGripDelta()
    {
        // 비율 고정: 가로·세로 늘어난 비율의 평균만큼 카드 전체를 키우거나 줄인다.
        // 손잡이가 모서리와 함께 움직여 증분이 흔들리므로, 잡은 순간부터의 전체 마우스 이동량으로 계산한다.
        Native.GetCursorPos(out var cur);
        var r0 = _moveWindowStart;
        double w0 = r0.Right - r0.Left, h0 = r0.Bottom - r0.Top;
        double g = ((w0 + cur.X - _moveCursorStart.X) / w0 + (h0 + cur.Y - _moveCursorStart.Y) / h0) / 2;

        // 다른 카드·화면 가운데와 끝선이 맞으면 붙고 안내선을 보여 준다.
        var wa = DesktopGrid.WorkAreaAt(r0.Left + 1, r0.Top + 1);
        var (sg, lines) = SmartGuides.SnapScale(r0, g, _moveOthers, wa, Native.MonitorScaleOf(Handle));
        SmartGuides.Show(wa, lines);
        SetZoomClamped(_gripZoom * sg);
        SetInvalid(Placement() != SmartGuides.Placement.Ok);
    }

    private void OnGripDone()
    {
        SmartGuides.Hide();
        // 겹치거나 간격보다 가까워지는 크기는 저장하지 않고 잡기 전 크기로 되돌린다.
        var p = Placement();
        if (p != SmartGuides.Placement.Ok)
        {
            _layout.Zoom = _gripZoom;
            LayoutFor();
        }
        CommitLayout();
        Mgr.RefreshPlacement();
        if (p != SmartGuides.Placement.Ok) Warn(p);
    }

    /// <summary>지금 자리를 지금 설정으로 검사한다.</summary>
    private SmartGuides.Placement Placement()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return SmartGuides.Placement.Ok;
        return SmartGuides.Check(Footprint, Mgr.CardRects(except: this), SmartGuides.GapPx(Native.MonitorScaleOf(hwnd)));
    }

    /// <summary>편집 중이면 지금 자리가 안 되는 자리인지 검사해 빨간 테두리로 표시한다.</summary>
    public void CheckPlacement() => SetInvalid(_editing && Placement() != SmartGuides.Placement.Ok);

    private void SetInvalid(bool on)
    {
        on &= _editing;
        if (_invalid == on) return;
        _invalid = on;
        UpdateBorder();
    }

    /// <summary>안 되는 자리에 놓았을 때 이유와 실험 설정을 알려 준다.</summary>
    /// <remarks>편집 모드마다 종류별로 처음 한 번만 말풍선을 띄운다. 그 뒤로는 빨간 테두리와 되돌리기로 충분하다.</remarks>
    private void Warn(SmartGuides.Placement p) => Dispatcher.BeginInvoke(() =>
    {
        bool overlap = p == SmartGuides.Placement.Overlap;
        string id = overlap ? "placement.overlap" : "placement.flush";
        if (!_editing || Mgr.TipHidden(id)) return;
        BalloonTip.Show(Footprint, overlap
            ? new BalloonTip.Tip("카드는 겹칠 수 없어요",
                "빈자리에 놓아 주세요. 겹쳐 두려면 실험 설정에서 '겹치기 · 레이어'를 켜 주세요.",
                "실험 설정 열기", () => SettingsWindow.OpenGeneral(Mgr), () => Mgr.HideTip(id))
            : new BalloonTip.Tip("카드를 조금 띄워 뒀어요",
                "카드끼리는 딱 붙일 수 없어요. 붙여 두려면 실험 설정에서 '완전히 붙이기'를 켜 주세요.",
                "실험 설정 열기", () => SettingsWindow.OpenGeneral(Mgr), () => Mgr.HideTip(id)));
    }, DispatcherPriority.Background);

    /// <summary>바뀐 모양을 적용·저장하고, 화면 밖으로 나갔으면 당겨서 위치도 저장한다.</summary>
    protected void CommitLayout()
    {
        FitToScreen();
        Mgr.SaveLayout(this, _layout);
        Mgr.SavePosition(this);
    }

    /// <summary>확대 비율을 바꾸되, 최소·최대와 작업 영역(오른쪽·아래 끝) 안으로 제한한다.</summary>
    private void SetZoomClamped(double zoom)
    {
        var wa = WorkAreaDip();
        var p = ActualPosition;
        var size = BaseSize();
        double k = ScaleFactor;
        double maxZoom = Math.Min((wa.Right - p.X) / (size.Width * k), (wa.Bottom - p.Y) / ((size.Height + LabelBelow) * k));
        _layout.Zoom = Math.Clamp(zoom, CardLayout.MinZoom, Math.Max(CardLayout.MinZoom, Math.Min(CardLayout.MaxZoom, maxZoom)));
        LayoutFor();
    }

    /// <summary>
    /// 설정에 보여 주는 카드 크기(%). 기준 크기 대비 지금 보이는 크기로, Windows 배율을 따라가는 중이면
    /// 125% 배율에서 기본 카드는 125%가 된다.
    /// </summary>
    public int SizePercent => (int)Math.Round(_appliedScale * 100);

    /// <summary>카드 크기를 %로 정한다. 화면에 들어가지 않으면 들어가는 만큼만 키운다. 실제 적용된 값을 돌려준다.</summary>
    public int SetSizePercent(int percent)
    {
        SetZoomClamped(percent / 100.0 / ScaleFactor);
        CommitLayout();
        return SizePercent;
    }

    private Rect WorkAreaDip()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var p = ActualPosition;
        var wa = DesktopGrid.WorkAreaAt((int)(p.X * dpi.DpiScaleX) + 1, (int)(p.Y * dpi.DpiScaleY) + 1);
        return new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY, wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
    }

    private void MoveWithGuides(IntPtr hwnd, IntPtr lParam)
    {
        // 자유 이동 + 스마트 가이드: 다른 카드·화면 가운데와 줄이 맞으면 살짝 붙고 안내선을 보여 준다.
        // 시스템이 주는 제안 위치는 직전(이미 붙은) 위치 기준이라 한번 붙으면 빠져나오지 못한다.
        // 그래서 끌기 시작점부터의 전체 마우스 이동량으로 직접 계산한다.
        var r = Marshal.PtrToStructure<Native.RECT>(lParam);
        Native.GetCursorPos(out var cur);
        int w = r.Right - r.Left, h = r.Bottom - r.Top, lh = LabelPx;
        int fx = _moveWindowStart.Left + (cur.X - _moveCursorStart.X);
        int fy = _moveWindowStart.Top + (cur.Y - _moveCursorStart.Y);
        var wa = DesktopGrid.WorkAreaAt(fx + w / 2, fy + h / 2);
        double scale = Native.MonitorScaleOf(hwnd);
        var (x, y, lines) = SmartGuides.Snap(fx, fy, w, h + lh, _moveOthers, wa, scale);
        // 간격을 두는 중인데 옆 카드에 딱 붙이려고(간격 절반보다 가까이, 살짝 겹치는 데까지) 끌었는지.
        int gap = SmartGuides.GapPx(scale);
        if (gap > 0)
        {
            var raw = new Native.RECT { Left = fx, Top = fy, Right = fx + w, Bottom = fy + h + lh };
            int slack = (int)Math.Round(8 * scale);
            var (sx, sy) = SmartGuides.Separation(raw, _moveOthers);
            bool Near(int? d) => d is { } v && v > -slack && v < gap / 2;
            _triedFlush |= Near(sx) || Near(sy);
        }
        // 지금 설정에서 안 되는 자리면 끄는 동안 빨간 테두리.
        var placed = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h + lh };
        SetInvalid(SmartGuides.Check(placed, _moveOthers, gap) != SmartGuides.Placement.Ok);
        SmartGuides.Show(wa, lines);
        Marshal.StructureToPtr(new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }, lParam, false);
    }

    /// <summary>
    /// 옮기기를 마쳤을 때 지금 설정으로 자리를 검사한다.
    /// 겹쳤으면(겹치기 허용이 꺼져 있을 때) 저장하지 않고 끌기 전 자리로 돌려놓는다.
    /// 나란한 카드와 간격보다 가까우면(완전히 붙이기가 꺼져 있을 때) 간격만큼 떼어 놓는다.
    /// 어느 쪽이든, 또는 딱 붙이려 했던 것이면 이유와 실험 설정을 알려 준다.
    /// </summary>
    private void Settle(IntPtr hwnd)
    {
        var others = Mgr.CardRects(except: this);
        var me = Footprint;
        int gap = SmartGuides.GapPx(Native.MonitorScaleOf(hwnd));
        var p = SmartGuides.Check(me, others, gap);
        int x = me.Left, y = me.Top;
        if (p == SmartGuides.Placement.Overlap)
        {
            x = _moveWindowStart.Left;
            y = _moveWindowStart.Top;
        }
        else if (p == SmartGuides.Placement.TooClose)
        {
            var (dx, dy) = SmartGuides.PushApart(me, others, gap);
            int w = me.Right - me.Left, h = me.Bottom - me.Top;
            var wa = DesktopGrid.WorkAreaAt(me.Left + w / 2, me.Top + h / 2);
            x = Math.Max(wa.Left, Math.Min(me.Left + dx, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(me.Top + dy, wa.Bottom - h));
        }
        if (x != me.Left || y != me.Top)
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (p == SmartGuides.Placement.Ok && _triedFlush) p = SmartGuides.Placement.TooClose;
        _triedFlush = false;
        Dispatcher.BeginInvoke(() => Mgr.RefreshPlacement(), DispatcherPriority.Background);
        if (p != SmartGuides.Placement.Ok) Warn(p);
    }
}
