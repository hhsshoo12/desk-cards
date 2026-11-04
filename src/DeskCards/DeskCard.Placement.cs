using System;
using Windows.Foundation;

namespace DeskCards;

internal abstract partial class DeskCard
{
    // ----- 크기 / 위치 -----

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

    /// <summary>확대 비율에 곱할 값. 따라가기를 끈 동안 Windows 배율이 바뀌어도 실제 크기를 유지한다.</summary>
    private double ScaleFactor => Mgr.ZoomFactor(DpiScale);

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

    /// <summary>저장된 모양(칸 수·확대 비율)을 불러와 적용한다.</summary>
    public void ApplySize()
    {
        _layout = Mgr.GetLayout(Key);
        LayoutFor();
    }

    protected void LayoutFor()
    {
        var size = BaseSize();
        if (FreeResize && _layout.Width > 0 && _layout.Height > 0) size = new Size(_layout.Width, _layout.Height);
        // 비율 고정 확대: 카드 확대 비율(배율 보정 포함)을 내용 전체(아이콘·글자·모서리)에 똑같이 건다.
        double t = ScaleFactor * _layout.Zoom;
        var wa = Screens.FromHandle(Handle).WorkArea;
        double dpi = DpiScale;
        t = Math.Min(t, Math.Min(wa.Width / (size.Width * dpi), wa.Height / ((size.Height + LabelBelow) * dpi)));
        _appliedScale = t;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(size.Width * t * dpi), (int)Math.Round(size.Height * t * dpi)));
        ApplyScale(t);
        FollowLabel();
    }

    /// <summary>
    /// 확대 비율 t를 내용에 건다. WinUI에는 레이아웃 변환이 없어서, 쓰는 쪽이 칸·아이콘 크기를 t배로 직접 정한다.
    /// </summary>
    protected virtual void ApplyScale(double t) { }

    protected virtual bool FreeResize => false;

    /// <summary>크기를 다시 적용하고, 작업 영역 밖으로 나갔으면 안쪽으로 당긴다.</summary>
    public void FitToScreen()
    {
        LayoutFor();
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        double dpi = DpiScale;
        int w = (int)Math.Round(Width * dpi), h = (int)Math.Round(FootprintHeight * dpi);
        var wa = DesktopGrid.WorkAreaAt(r.Left + w / 2, r.Top + h / 2);
        int x = Math.Max(wa.Left, Math.Min(r.Left, wa.Right - w));
        int y = Math.Max(wa.Top, Math.Min(r.Top, wa.Bottom - h));
        if (x != r.Left || y != r.Top)
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>화면 안으로 다시 맞추고 위치를 저장한다(배율·해상도·작업 표시줄이 바뀌었을 때).</summary>
    public void Refit()
    {
        FitToScreen();
        Mgr.SavePosition(this);
    }

    /// <summary>실제 창 위치(DIP). 설정 저장에는 PhysicalPosition을 쓴다.</summary>
    public Point ActualPosition
    {
        get
        {
            var hwnd = Handle;
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return _restorePosition ?? default;
            double dpi = DpiScale;
            return new Point(r.Left / dpi, r.Top / dpi);
        }
    }

    internal static Point StoragePoint(Native.RECT rect) => new(rect.Left, rect.Top);

    internal Point PhysicalPosition => Native.GetWindowRect(Handle, out var rect) ? StoragePoint(rect) : _restorePosition ?? default;

    internal void RestorePosition(Point point)
    {
        _restorePosition = point;
        if (Handle != IntPtr.Zero) PixelPlacement.Move(this, point);
    }
}
