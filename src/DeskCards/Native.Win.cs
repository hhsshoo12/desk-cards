using System;
using System.Runtime.InteropServices;

namespace DeskCards;

internal static partial class Native
{
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_NCDESTROY = 0x0082;
    public const int HTCAPTION = 2;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    public delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] public static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] public static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>창 메시지를 먼저 받아 보는 하위 클래스. 돌려주는 개체를 창이 닫힐 때까지 붙들어 둬야 한다.</summary>
    public sealed class Subclass : IDisposable
    {
        public delegate bool Handler(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);

        private readonly IntPtr _hwnd;
        private readonly Handler _handler;
        private readonly SubclassProc _proc;

        public Subclass(IntPtr hwnd, Handler handler)
        {
            _hwnd = hwnd;
            _handler = handler;
            _proc = Proc;
            SetWindowSubclass(hwnd, _proc, UIntPtr.Zero, UIntPtr.Zero);
        }

        private IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
        {
            if (_handler(hwnd, msg, wParam, lParam, out var result)) return result;
            return DefSubclassProc(hwnd, msg, wParam, lParam);
        }

        public void Dispose() => RemoveWindowSubclass(_hwnd, _proc, UIntPtr.Zero);
    }

    // ----- 트레이 아이콘 -----

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    public const int WM_USER = 0x0400, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bits, byte[]? data);

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }
}
