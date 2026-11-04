using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace DeskCards;

/// <summary>UI 스레드로 일을 넘기고, 앱이 끝나는 중인지 알려 준다.</summary>
internal static class Ui
{
    private static DispatcherQueue? _queue;

    public static DispatcherQueue Queue => _queue ??= DispatcherQueue.GetForCurrentThread();

    /// <summary>앱 시작 때 UI 스레드에서 한 번 부른다.</summary>
    public static void Init() => _queue = DispatcherQueue.GetForCurrentThread();

    /// <summary>앱이 끝나기 시작했는지. 끝나는 중에는 새 UI 일을 넘기지 않는다.</summary>
    public static bool ShuttingDown { get; set; }

    public static bool CheckAccess => Queue.HasThreadAccess;

    public static void Post(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal)
    {
        if (ShuttingDown) return;
        Queue.TryEnqueue(priority, () => { if (!ShuttingDown) action(); });
    }
}

/// <summary>모니터 목록. 작업 영역·경계·주 모니터를 물리 픽셀로 돌려준다.</summary>
internal static class Screens
{
    public sealed record Monitor(IntPtr Handle, Rectangle Bounds, Rectangle WorkArea, bool Primary, string DeviceName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Native.RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool EnumProc(IntPtr monitor, IntPtr dc, ref Native.RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Native.POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private static Rectangle ToRect(Native.RECT r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    private static Monitor? Read(IntPtr handle)
    {
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info)) return null;
        return new Monitor(handle, ToRect(info.rcMonitor), ToRect(info.rcWork), (info.dwFlags & 1) != 0, info.szDevice);
    }

    public static System.Collections.Generic.List<Monitor> All()
    {
        var list = new System.Collections.Generic.List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref Native.RECT r, IntPtr d) =>
        {
            if (Read(m) is { } monitor) list.Add(monitor);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static Monitor FromPoint(int x, int y) =>
        Read(MonitorFromPoint(new Native.POINT { X = x, Y = y }, 2)) ?? Fallback();

    public static Monitor FromHandle(IntPtr hwnd) =>
        Read(MonitorFromWindow(hwnd, 2)) ?? Fallback();

    public static Monitor Primary => Read(MonitorFromPoint(new Native.POINT(), 1)) ?? Fallback();

    private static Monitor Fallback() => new(IntPtr.Zero, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true, "");
}
