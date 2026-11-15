using System;
using DeskCards.Expanded;
using Windows.Foundation;

namespace DeskCards;

/// <summary>크기·배율·위치. 화면 위의 길이는 모두 물리 픽셀이고, 카드 안쪽 크기만 DIP(배율 적용 전 단위)다.</summary>
internal sealed partial class CardWindow
{
    private CardLayout _layout = new();
    private double _appliedScale = 1;

    public CardLayout CurrentLayout => _layout;

    /// <summary>이 카드가 있는 모니터의 지금 배율(125% = 1.25).</summary>
    public double DpiScale
    {
        get
        {
            var hwnd = Handle;
            uint dpi = hwnd == IntPtr.Zero ? 0 : Native.GetDpiForWindow(hwnd);
            return dpi == 0 ? Native.PrimaryScale() : dpi / 96.0;
        }
    }

    /// <summary>
    /// 미리보기 칸 하나의 기준 크기(DIP)를 바탕화면 아이콘 간격으로 잰다. 기본 2×2 카드의 가로가 아이콘 2칸이 된다.
    /// 처음 한 번만 재서 설정에 고정한다(배율을 바꾼 직후에는 아이콘 간격이 늦게 바뀌어 값이 흔들린다).
    /// </summary>
    public static double MeasureCellSize(double dpiScale)
    {
        double w = 152;
        if (DesktopGrid.TryGetIconSpacing(out int cx)) w = DesktopGrid.CardCols * cx / dpiScale;
        return w / 2;
    }

    /// <summary>확대 비율을 적용하기 전의 카드 창 크기(DIP). 이름은 창 밖 아래에 따로 띄운다.</summary>
    public static Size BaseSize(CardLayout layout, double cell) => new(layout.Cols * cell, layout.Rows * cell);

    // ----- 이름 줄 -----

    /// <summary>지금 크기의 이름 줄 높이(물리 픽셀). 이름 줄 자체는 창 라이브러리가 그린다.</summary>
    private int LabelPx => DesktopCards.LabelPixels(_appliedScale, DpiScale);

    /// <summary>
    /// 카드와 그 아래 이름 줄을 합친 자리(물리 픽셀). 안내선·화면 맞춤·빈 자리 찾기는 이 자리로 한다.
    /// 그래야 붙여 놓은 카드가 위 카드의 이름을 덮지 않는다.
    /// </summary>
    public Native.RECT Footprint => _closed ? default
        : new Native.RECT { Left = _rect.Left, Top = _rect.Top, Right = _rect.Right, Bottom = _rect.Bottom + LabelPx };

    // ----- 크기 -----

    /// <summary>저장된 모양(칸 수·확대 비율)을 불러와 적용한다.</summary>
    public void ApplySize()
    {
        _layout = Mgr.GetLayout(Key);
        LayoutFor();
    }

    /// <summary>미리보기 칸 수를 바꾼다(카드 비율이 따라 바뀐다).</summary>
    public void SetGrid(int cols, int rows)
    {
        _layout.Cols = Math.Clamp(cols, CardLayout.MinCells, CardLayout.MaxCells);
        _layout.Rows = Math.Clamp(rows, CardLayout.MinCells, CardLayout.MaxCells);
        CommitLayout();
    }

    /// <summary>
    /// 카드 확대 비율(배율 보정 포함)에 맞춘 카드 크기(물리 픽셀). 작업 영역보다 커지지 않게 누른다.
    /// 내용(아이콘·글자)은 창 크기를 따라 그려지므로 크기만 정하면 된다.
    /// </summary>
    private (int W, int H) SizeFor(double dpi, double workW, double workH)
    {
        var size = BaseSize(_layout, Mgr.CellSize);
        double t = Mgr.ZoomFactor(dpi) * _layout.Zoom;
        t = Math.Min(t, Math.Min(workW / (size.Width * dpi), workH / ((size.Height + LabelH) * dpi)));
        _appliedScale = t;
        return ((int)Math.Round(size.Width * t * dpi), (int)Math.Round(size.Height * t * dpi));
    }

    private void LayoutFor()
    {
        var wa = Screens.FromHandle(Handle).WorkArea;
        var (w, h) = SizeFor(DpiScale, wa.Width, wa.Height);
        Place(new PixelRect(_rect.Left, _rect.Top, _rect.Left + w, _rect.Top + h));
        PushData();
    }

    private void Place(PixelRect r)
    {
        _rect = r;
        if (!_closed) _h.SetBounds(r.Left, r.Top, r.Width, r.Height);
    }

    /// <summary>바뀐 모양을 적용·저장하고, 화면 밖으로 나갔으면 당겨서 위치도 저장한다.</summary>
    private void CommitLayout()
    {
        FitToScreen();
        Mgr.SaveLayout(this, _layout);
        Mgr.SavePosition(this);
    }

    /// <summary>확대 비율을 바꾸되, 최소·최대와 작업 영역(오른쪽·아래 끝) 안으로 제한한다.</summary>
    private void SetZoomClamped(double zoom)
    {
        var wa = DesktopGrid.WorkAreaAt(_rect.Left + 1, _rect.Top + 1);
        var size = BaseSize(_layout, Mgr.CellSize);
        double k = Mgr.ZoomFactor(DpiScale) * DpiScale;
        double maxZoom = Math.Min((wa.Right - _rect.Left) / (size.Width * k), (wa.Bottom - _rect.Top) / ((size.Height + LabelH) * k));
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
        SetZoomClamped(percent / 100.0 / Mgr.ZoomFactor(DpiScale));
        CommitLayout();
        return SizePercent;
    }

    /// <summary>따라가기를 켜고 끌 때 보이는 크기가 그대로 남도록 확대 비율을 옮겨 담는다.</summary>
    public void RebaseZoom(double factor)
    {
        _layout.Zoom *= factor;
        _layout = _layout.Normalized();
        Mgr.SaveLayout(this, _layout);
    }

    /// <summary>
    /// 모니터 배율이 바뀌었는데 이 창이 그 알림을 못 받아 옛 배율로 그려지고 있는지.
    /// 바탕화면 층에서 배율 알림이 누락된 경우에도 실제 모니터 배율로 확인한다.
    /// </summary>
    public bool IsDpiStale
    {
        get
        {
            var hwnd = Handle;
            if (hwnd == IntPtr.Zero || _editing) return false;
            return Math.Abs(Native.MonitorScaleOf(hwnd) - DpiScale) > 0.01;
        }
    }

    // ----- 위치 -----

    /// <summary>실제 카드 위치(물리 픽셀). 설정 저장에 쓴다.</summary>
    internal Point PhysicalPosition => new(_rect.Left, _rect.Top);

    internal void RestorePosition(Point point)
    {
        MoveTo((int)Math.Round(point.X), (int)Math.Round(point.Y));
    }

    /// <summary>크기와 층은 그대로 두고 물리 픽셀 위치로 옮긴다.</summary>
    internal void MoveTo(int x, int y) => Place(new PixelRect(x, y, x + _rect.Width, y + _rect.Height));

    /// <summary>크기를 다시 적용하고, 작업 영역 밖으로 나갔으면 안쪽으로 당긴다.</summary>
    public void FitToScreen()
    {
        LayoutFor();
        var f = Footprint;
        if (f.Right == f.Left) return;
        int w = f.Right - f.Left, h = f.Bottom - f.Top;
        var wa = DesktopGrid.WorkAreaAt(f.Left + w / 2, f.Top + h / 2);
        int x = Math.Max(wa.Left, Math.Min(f.Left, wa.Right - w));
        int y = Math.Max(wa.Top, Math.Min(f.Top, wa.Bottom - h));
        if (x != f.Left || y != f.Top) MoveTo(x, y);
    }

    /// <summary>화면 안으로 다시 맞추고 위치를 저장한다(배율·해상도·작업 표시줄이 바뀌었을 때).</summary>
    public void Refit()
    {
        FitToScreen();
        Mgr.SavePosition(this);
    }

    /// <summary>화살표 키로 조금씩 옮긴다(물리 픽셀). 작업 영역 밖으로는 나가지 않는다.</summary>
    public void Nudge(int dx, int dy)
    {
        var f = Footprint;
        int w = f.Right - f.Left, h = f.Bottom - f.Top;
        var wa = DesktopGrid.WorkAreaAt(f.Left + w / 2, f.Top + h / 2);
        MoveTo(Math.Max(wa.Left, Math.Min(f.Left + dx, wa.Right - w)), Math.Max(wa.Top, Math.Min(f.Top + dy, wa.Bottom - h)));
        Mgr.SavePosition(this);
        Mgr.RefreshPlacement();
    }
}