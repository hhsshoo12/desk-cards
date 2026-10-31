using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

// 분리된 테스트 데스크톱에는 Explorer가 없다. 카드 소유자 계약만 제공하는 테스트 창이다.
internal sealed class IsolatedDesktopShell : IDisposable
{
    private readonly IntPtr _window;
    private readonly WindowProc _proc = DefWindowProc;
    public IsolatedDesktopShell()
    {
        if (Environment.GetEnvironmentVariable("DESKCARDS_ISOLATED_DESKTOP") != "1") return;
        var cls = new WindowClass { Procedure = _proc, Instance = GetModuleHandle(null), Name = "Progman" };
        if (RegisterClass(ref cls) == 0) throw new Win32Exception();
        _window = CreateWindowEx(0, "Progman", "DeskCards test desktop", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, cls.Instance, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new Win32Exception();
    }
    public void Dispose() { if (_window != IntPtr.Zero) DestroyWindow(_window); }
    private delegate IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public WindowProc Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? Menu; public string Name;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr owner, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
