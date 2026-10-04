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
    // ----- 크기 / 위치 -----

    /// <summary>이 카드가 있는 모니터의 지금 배율(125% = 1.25).</summary>
    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

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

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Windows 배율이 바뀌면 크기를 다시 계산하고 화면 안으로 맞춘다.
        Dispatcher.BeginInvoke(() => { if (!_closed) Refit(); }, DispatcherPriority.Background);
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
        // 비율 고정 확대: 카드 확대 비율(배율 보정 포함)을 내용 전체(아이콘·글자·모서리)에 똑같이 건다.
        double t = ScaleFactor * _layout.Zoom;
        var wa = System.Windows.Forms.Screen.FromHandle(Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        t = Math.Min(t, Math.Min(wa.Width / (size.Width * dpi.DpiScaleX), wa.Height / ((size.Height + LabelBelow) * dpi.DpiScaleY)));
        _appliedScale = t;
        Width = size.Width * t;
        Height = size.Height * t;
        ApplyScale(t);
        FollowLabel();
    }

    /// <summary>
    /// 확대 비율 t를 내용에 건다. 기본은 내용 전체를 그림처럼 키운다.
    /// 흐려지면 안 되는 내용(웹 화면)은 쓰는 쪽이 크기를 직접 정한다.
    /// </summary>
    protected virtual void ApplyScale(double t) =>
        Layout.LayoutTransform = t == 1 ? Transform.Identity : new ScaleTransform(t, t);

    /// <summary>크기를 다시 적용하고, 작업 영역 밖으로 나갔으면 안쪽으로 당긴다.</summary>
    public void FitToScreen()
    {
        LayoutFor();
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(Width * dpi.DpiScaleX), h = (int)Math.Round(FootprintHeight * dpi.DpiScaleY);
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

    /// <summary>
    /// 실제 창 위치(DIP). SetWindowPos나 끌기로 옮긴 직후에는 WPF의 Left/Top이 늦게 갱신될 수 있어
    /// DIP를 쓰는 UI 계산용이며, 설정 저장에는 PhysicalPosition을 쓴다.
    /// </summary>
    public Point ActualPosition
    {
        get
        {
            var hwnd = Handle;
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return new Point(Left, Top);
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Point(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY);
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
