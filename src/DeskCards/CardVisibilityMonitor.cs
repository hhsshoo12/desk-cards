using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskCards;

internal sealed class CardVisibilityMonitor : IDisposable
{
    private readonly Window _window;
    private readonly bool _background;
    private readonly Action<bool> _changed;
    private readonly DispatcherTimer _timer;
    private readonly WinEvent _callback;
    private readonly IntPtr _hook;
    private bool _locked, _power, _dirty = true, _suspended, _disposed;
    private long? _hiddenSince;
    public CardVisibilityMonitor(Window window, bool background, Action<bool> changed)
    {
        _window = window; _background = background; _changed = changed;
        _callback = (_, _, _, _, _, _, _) => _dirty = true;
        _hook = SetWinEventHook(3, 0x800B, IntPtr.Zero, _callback, 0, 0, 0);
        SystemEvents.SessionSwitch += OnSession;
        SystemEvents.PowerModeChanged += OnPower;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Evaluate(); _timer.Start();
        window.IsVisibleChanged += OnVisibility;
    }
    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs e) => _dirty = true;
    private void OnSession(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
        _dirty = true;
    }
    private void OnPower(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _power = true;
        else if (e.Mode == PowerModes.Resume) _power = false;
        _dirty = true;
    }
    private void Evaluate()
    {
        if (_disposed || (!_dirty && _hiddenSince == null && _hook != IntPtr.Zero)) return;
        _dirty = false;
        bool hidden = _power || (!_background && (_locked || !_window.IsVisible || Covered()));
        if (!hidden) _hiddenSince = null;
        else _hiddenSince ??= Environment.TickCount64;
        bool suspend = _hiddenSince is long start && Environment.TickCount64 - start >= 5000;
        if (suspend == _suspended) return;
        _suspended = suspend; _changed(suspend);
    }
    private bool Covered()
    {
        var area = System.Windows.Forms.Screen.FromHandle(Hwnd.Of(_window)).WorkingArea;
        bool covered = false;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId || !IsZoomed(hwnd) && !IsForegroundFullscreen(hwnd, area)) return true;
            if (Native.GetWindowRect(hwnd, out var rect) && rect.Left <= area.Left && rect.Top <= area.Top && rect.Right >= area.Right && rect.Bottom >= area.Bottom)
            { covered = true; return false; }
            return true;
        }, IntPtr.Zero);
        return covered;
    }
    private static bool IsForegroundFullscreen(IntPtr hwnd, System.Drawing.Rectangle area) =>
        hwnd == Native.GetForegroundWindow() && Native.GetWindowRect(hwnd, out var r) && r.Left <= area.Left && r.Top <= area.Top && r.Right >= area.Right && r.Bottom >= area.Bottom;
    public void Dispose()
    {
        _disposed = true; _timer.Stop();
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        SystemEvents.SessionSwitch -= OnSession; SystemEvents.PowerModeChanged -= OnPower;
        _window.IsVisibleChanged -= OnVisibility;
    }
    private delegate void WinEvent(IntPtr hook, uint kind, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
}
