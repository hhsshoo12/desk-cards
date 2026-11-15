using System;
using System.Collections.Generic;
using DeskCards.Expanded;

namespace DeskCards;

/// <summary>
/// 편집 상태(편집 중·고름·안 되는 자리)와 끌기·크기 조절의 앱 쪽 규칙.
/// 끄는 동작 자체(옮기기·크기 조절·강조 테두리)는 창 라이브러리가 하고, 여기서는 안내선에 맞추고 놓은 자리를 검사한다.
/// 창 스레드에서 불리는 Snap·SnapScale은 앱이 아는 카드 자리만 보고 계산하며 앱 UI 스레드가 계산을 끝낼 때까지 창 스레드가 기다린다(최대 3초).
/// </summary>
internal sealed partial class CardWindow
{
    private bool _editing, _selected, _invalid;

    // 끌기 한 번 동안의 상태
    private PixelRect? _dragStart;
    private IReadOnlyList<Native.RECT> _others = Array.Empty<Native.RECT>();
    private bool _triedFlush; // 간격을 두는 중에 옆 카드에 딱 붙이려고 했는지
    private double _zoomStart, _scaleStart, _dragDpi;

    public bool IsEditing => _editing;

    /// <summary>편집 모드로. 켜고 끄는 건 GroupManager.BeginEditMode/EndEditMode가 모든 카드에 한꺼번에 한다.</summary>
    public void BeginEdit()
    {
        _editing = true;
        _h.SetEditing(true);
        RaiseForEdit();
    }

    public void EndEdit()
    {
        if (!_editing) return;
        _editing = _selected = _invalid = false;
        _h.SetEditing(false);
        Mgr.SavePosition(this);
    }

    /// <summary>편집 중에는 어두운 막(EditDim) 위로 올라온다. 편집이 끝나면 다시 바탕화면 층으로 내려간다.</summary>
    public void RaiseForEdit()
    {
        if (!_editing || _closed) return;
        _h.SetEditing(true);
        // 창 스레드의 올리기가 끝난 뒤에 안내 풍선을 올려야 풍선이 카드에 덮이지 않는다.
        DesktopCards.After(() => Ui.Post(BalloonTip.Raise));
    }

    /// <summary>편집 막대의 작업 대상으로 골랐는지. 고른 카드는 테두리가 굵어진다.</summary>
    public void SetSelected(bool on)
    {
        _selected = on && _editing;
        UpdateHighlight();
    }

    /// <summary>지금 자리가 안 되는 자리일 때 빨간 테두리(편집 중에만).</summary>
    internal void SetInvalid(bool on)
    {
        on &= _editing;
        if (_invalid == on) return;
        _invalid = on;
        UpdateHighlight();
    }

    private void UpdateHighlight()
    {
        if (!_closed) _h.SetHighlight(_selected, _invalid);
    }

    /// <summary>편집 중이면 지금 자리를 지금 설정으로 검사해 표시한다.</summary>
    public void CheckPlacement() => SetInvalid(_editing && Placement() != SmartGuides.Placement.Ok);

    /// <summary>지금 자리를 지금 설정으로 검사한다.</summary>
    private SmartGuides.Placement Placement() =>
        SmartGuides.Check(Footprint, Mgr.CardRects(except: this), SmartGuides.GapPx(Native.MonitorScaleOf(Handle)));

    // ----- 끌기 (창 스레드에서 불린다) -----

    private void BeginDrag()
    {
        _dragStart = _rect;
        _others = Mgr.CardRects(except: this);
        _triedFlush = false;
        _zoomStart = _layout.Zoom;
        _scaleStart = _appliedScale;
        _dragDpi = DpiScale;
        Ui.Post(BalloonTip.CloseCurrent); // 앞 안내는 이전 자리 이야기라 새로 옮기기 시작하면 치운다
    }

    /// <summary>
    /// 자유 이동 + 스마트 가이드: 다른 카드·화면 가운데와 줄이 맞으면 살짝 붙고 안내선을 보여 준다.
    /// 창이 넘겨 주는 자리는 누른 자리부터의 전체 이동량이라 한번 붙어도 빠져나올 수 있다.
    /// </summary>
    public PixelRect Snap(string id, PixelRect rect) => ExpandedWindow.OnUi(() =>
    {
        if (_closed) return rect;
        if (_dragStart == null) BeginDrag();
        int w = rect.Width, h = rect.Height + LabelPx;
        var wa = DesktopGrid.WorkAreaAt(rect.Left + w / 2, rect.Top + h / 2);
        double scale = Native.MonitorScaleOf(Handle);
        var (x, y, lines) = SmartGuides.Snap(rect.Left, rect.Top, w, h, _others, wa, scale);

        int gap = SmartGuides.GapPx(scale);
        if (gap > 0)
        {
            // 간격을 두는 중인데 옆 카드에 딱 붙이려고(간격 절반보다 가까이, 살짝 겹치는 데까지) 끌었는지.
            var raw = new Native.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Left + w, Bottom = rect.Top + h };
            int slack = (int)Math.Round(8 * scale);
            var (sx, sy) = SmartGuides.Separation(raw, _others);
            bool Near(int? d) => d is { } v && v > -slack && v < gap / 2;
            _triedFlush |= Near(sx) || Near(sy);
        }
        var placed = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        SetInvalid(SmartGuides.Check(placed, _others, gap) != SmartGuides.Placement.Ok);
        SmartGuides.Show(wa, lines);
        return new PixelRect(x, y, x + rect.Width, y + rect.Height);
    }, rect);

    /// <summary>
    /// 비율 고정 크기 조절: 다른 카드·화면 가운데와 끝선이 맞으면 붙고 안내선을 보여 준다.
    /// scale은 크기를 바꾸기 시작할 때 자리를 1로 친 배율이다.
    /// </summary>
    public double SnapScale(string id, PixelRect start, double scale) => ExpandedWindow.OnUi(() =>
    {
        if (_closed) return scale;
        if (_dragStart == null) BeginDrag();
        var r0 = new Native.RECT { Left = start.Left, Top = start.Top, Right = start.Right, Bottom = start.Bottom };
        var wa = DesktopGrid.WorkAreaAt(r0.Left + 1, r0.Top + 1);
        double dpi = Native.MonitorScaleOf(Handle);
        var (snapped, lines) = SmartGuides.SnapScale(r0, scale, _others, wa, dpi);
        SmartGuides.Show(wa, lines);
        var placed = new Native.RECT
        {
            Left = start.Left, Top = start.Top,
            Right = start.Left + (int)Math.Round(start.Width * snapped),
            Bottom = start.Top + (int)Math.Round(start.Height * snapped) + DesktopCards.LabelPixels(_appliedScale * snapped, dpi),
        };
        SetInvalid(SmartGuides.Check(placed, _others, SmartGuides.GapPx(dpi)) != SmartGuides.Placement.Ok);
        return snapped;
    }, scale);

    public void GuidesOff() => Ui.Post(SmartGuides.Hide);

    /// <summary>옮기기·크기 조절을 마쳤다(또는 창이 다른 이유로 옮겨졌다).</summary>
    public void Bounds(int x, int y, int width, int height) => Ui.Post(() =>
    {
        if (_closed) return;
        _rect = new PixelRect(x, y, x + width, y + height);
        if (_dragStart is not { } start) return;
        _dragStart = null;
        SmartGuides.Hide();
        if (Math.Abs(DpiScale - _dragDpi) > 0.01)
        {
            // 배율이 다른 모니터로 옮겨 오는 동안 창 크기가 새 배율로 바뀐 것이라 크기 조절이 아니다.
            SetInvalid(false);
            Refit();
            return;
        }
        if (width != start.Width || height != start.Height) SettleResize(start);
        else SettleMove(start);
    });

    /// <summary>
    /// 옮기기를 마쳤을 때 지금 설정으로 자리를 검사한다.
    /// 겹쳤으면(겹치기 허용이 꺼져 있을 때) 저장하지 않고 끌기 전 자리로 돌려놓는다.
    /// 나란한 카드와 간격보다 가까우면(완전히 붙이기가 꺼져 있을 때) 간격만큼 떼어 놓는다.
    /// 어느 쪽이든, 또는 딱 붙이려 했던 것이면 이유와 실험 설정을 알려 준다.
    /// </summary>
    private void SettleMove(PixelRect start)
    {
        var me = Footprint;
        int gap = SmartGuides.GapPx(Native.MonitorScaleOf(Handle));
        var p = SmartGuides.Check(me, _others, gap);
        int x = me.Left, y = me.Top;
        if (p == SmartGuides.Placement.Overlap)
        {
            x = start.Left;
            y = start.Top;
        }
        else if (p == SmartGuides.Placement.TooClose)
        {
            var (dx, dy) = SmartGuides.PushApart(me, _others, gap);
            int w = me.Right - me.Left, h = me.Bottom - me.Top;
            var wa = DesktopGrid.WorkAreaAt(me.Left + w / 2, me.Top + h / 2);
            x = Math.Max(wa.Left, Math.Min(me.Left + dx, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(me.Top + dy, wa.Bottom - h));
        }
        if (x != me.Left || y != me.Top) MoveTo(x, y);
        if (p == SmartGuides.Placement.Ok && _triedFlush) p = SmartGuides.Placement.TooClose;
        SetInvalid(false);
        Mgr.SavePosition(this);
        Ui.Post(() => Mgr.RefreshPlacement(), Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
        if (p != SmartGuides.Placement.Ok) Warn(p);
    }

    /// <summary>끌어서 바꾼 크기를 확대 비율로 옮겨 담는다. 겹치거나 간격보다 가까워지는 크기는 잡기 전 크기로 되돌린다.</summary>
    private void SettleResize(PixelRect start)
    {
        // 화면에 눌려 있던 크기에서도 줄일 수 있게, 저장된 확대 비율이 아니라 실제로 보이던 배율에 비율을 곱한다.
        SetZoomClamped(_scaleStart * _rect.Width / start.Width / Mgr.ZoomFactor(_dragDpi));
        var p = Placement();
        if (p != SmartGuides.Placement.Ok) SetZoomClamped(_zoomStart);
        CommitLayout();
        SetInvalid(false);
        Mgr.RefreshPlacement();
        if (p != SmartGuides.Placement.Ok) Warn(p);
    }

    /// <summary>안 되는 자리에 놓았을 때 이유와 실험 설정을 알려 준다. 편집 모드마다 종류별로 처음 한 번만 띄운다.</summary>
    private void Warn(SmartGuides.Placement p) => Ui.Post(() =>
    {
        bool overlap = p == SmartGuides.Placement.Overlap;
        string id = overlap ? "placement.overlap" : "placement.flush";
        if (_closed || !_editing || Mgr.TipHidden(id)) return;
        var mgr = Mgr;
        BalloonTip.Show(Footprint, overlap
            ? new BalloonTip.Tip("카드는 겹칠 수 없어요",
                "빈자리에 놓아 주세요. 겹쳐 두려면 실험 설정에서 '겹치기 · 레이어'를 켜 주세요.",
                "실험 설정 열기", () => SettingsWindow.OpenGeneral(mgr), () => mgr.HideTip(id))
            : new BalloonTip.Tip("카드를 조금 띄워 뒀어요",
                "카드끼리는 딱 붙일 수 없어요. 붙여 두려면 실험 설정에서 '완전히 붙이기'를 켜 주세요.",
                "실험 설정 열기", () => SettingsWindow.OpenGeneral(mgr), () => mgr.HideTip(id)));
    }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
}