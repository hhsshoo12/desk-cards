using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using DeskCards.Expanded;
using Microsoft.UI.Dispatching;

namespace DeskCards;

/// <summary>
/// 카드 바 여는 조건을 지켜본다: 조합키를 누른 채 정한 가장자리에 마우스를 대고 있으면
/// 커서 둘레의 게이지가 12시부터 시계방향으로 차오르고, 한 바퀴가 되는 순간 바가 나온다.
/// 작업 표시줄이 있는 가장자리와 전체 화면 앱 위에서는 열지 않는다.
/// 조합키는 Windows 단축키로 등록해 둬서, 누르는 동안 앞의 앱에 키가 가지 않는다.
/// 바 창과 게이지는 WPF 라이브러리(DeskCards.Expanded)가 그리고, 여기서는 조건과 시간을 본다.
/// </summary>
internal static class EdgeBar
{
    private const int HotkeyId = 1, WM_HOTKEY = 0x0312;
    private const double Gap = 8;

    private static MessageWindow? _hotkeyWindow;
    private static string _registeredFor = "";
    private static bool _suspended;

    /// <summary>조합키 등록 결과. 카드 바가 꺼져 있으면 null, 다른 곳에서 이미 쓰는 조합이면 false.</summary>
    public static bool? HotkeyRegistered { get; private set; }
    private static GroupManager? _mgr;
    private static DispatcherQueueTimer? _timer;
    private static bool _gaugeShown;
    private static OpenBar? _bar;
    private static readonly Stopwatch _dwell = new();
    private static bool _armed = true;

    /// <summary>떠 있는 카드 바 하나.</summary>
    private sealed class OpenBar : IBarHost
    {
        public BarView View = null!;
        public Rectangle Final, Bounds;
        public ScreenEdge Edge;
        public bool Gone, Closing;

        public void Closed() => Ui.Post(() => { Gone = true; SyncPolling(); });
    }

    public static void Start(GroupManager mgr)
    {
        _mgr = mgr;
        _hotkeyWindow = new MessageWindow(OnHotkeyMessage);
        ApplyHotkey();
        mgr.Changed += ApplyHotkey;
        _timer = Ui.Queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick += (_, _) => Tick();
        SyncPolling();
    }

    public static void Stop()
    {
        _timer?.Stop();
        if (_mgr != null) _mgr.Changed -= ApplyHotkey;
        Unregister();
        _hotkeyWindow?.Dispose();
        _hotkeyWindow = null;
        Gauge.Close();
        _bar?.View.CloseNow();
        _bar = null;
        _mgr = null;
        _timer = null;
    }

    // ----- 조합키 등록 -----

    /// <summary>설정(켜기·조합)에 맞게 단축키를 등록하거나 푼다. 바뀐 게 없으면 그대로 둔다.</summary>
    private static void ApplyHotkey()
    {
        SyncPolling();
        if (_mgr == null || _hotkeyWindow == null) return;
        string want = _mgr.BarEnabled && !_suspended ? string.Join(",", _mgr.BarKeys) : "";
        if (want == _registeredFor) return;
        Unregister();
        _registeredFor = want;
        if (want.Length == 0) { HotkeyRegistered = _mgr.BarEnabled ? HotkeyRegistered : null; return; }
        // 보조키만 있는 조합은 등록할 수 없고 그럴 필요도 없다(앱에 아무 일도 일으키지 않는다).
        if (!KeyCombo.NeedsHotkey(_mgr.BarKeys)) { HotkeyRegistered = true; _registeredFor = ""; return; }
        var (mods, key) = KeyCombo.ToHotkey(_mgr.BarKeys);
        const uint MOD_NOREPEAT = 0x4000;
        HotkeyRegistered = RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, mods | MOD_NOREPEAT, key);
    }

    private static void SyncPolling()
    {
        if (_timer == null) return;
        // 이미 열린 바는 닫힘 감지가 끝날 때까지 폴링한다. 비활성화 중 게이지는 즉시 치운다.
        if (_mgr is { IsShuttingDown: false } && (_mgr.BarEnabled || _bar is { Gone: false })) _timer.Start();
        else { Reset(); _armed = true; _timer.Stop(); }
        if (_mgr is { BarEnabled: false }) Reset();
    }

    private static void Unregister()
    {
        if (_hotkeyWindow != null && _registeredFor.Length > 0) UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        _registeredFor = "";
    }

    /// <summary>이 조합을 지금 단축키로 등록할 수 있는지(Windows나 다른 앱이 쓰고 있지 않은지) 잠깐 등록해 보고 푼다.</summary>
    public static bool IsHotkeyFree(IEnumerable<int> keys)
    {
        if (_hotkeyWindow == null) return true;
        const int ProbeId = 2;
        var (mods, key) = KeyCombo.ToHotkey(keys);
        if (!RegisterHotKey(_hotkeyWindow.Handle, ProbeId, mods | 0x4000, key)) return false;
        UnregisterHotKey(_hotkeyWindow.Handle, ProbeId);
        return true;
    }

    /// <summary>설정에서 새 조합을 누르는 동안에는 지금 조합을 풀어 둔다(안 그러면 그 키가 설정 창에 오지 않는다).</summary>
    public static void SuspendHotkey(bool on)
    {
        _suspended = on;
        ApplyHotkey();
    }

    private static void OnHotkeyMessage(int msg, IntPtr wParam)
    {
        // 조합을 누른 순간 빈 키를 한 번 눌러, 나중에 Alt·Win을 뗄 때 앱 메뉴·시작 메뉴가 열리지 않게 한다.
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId && _mgr != null) KeyCombo.SuppressRelease(_mgr.BarKeys);
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // ----- 가장자리 지켜보기 -----

    private static void Tick()
    {
        if (_mgr == null || _mgr.IsShuttingDown) return;
        if (_bar != null)
        {
            if (_bar.Gone) { _bar = null; SyncPolling(); }
            else { CheckLeave(_bar); return; }
        }

        if (!Native.GetCursorPos(out var pt) || !AtEdge(pt, out var screen, out var edge))
        {
            Reset();
            _armed = true; // 한 번 조건에서 벗어나야 다시 열 수 있다(닫히자마자 또 열리지 않게).
            return;
        }
        if (!_armed) return;

        if (!_dwell.IsRunning) _dwell.Restart();
        double progress = _mgr.BarDelay <= 0 ? 1 : _dwell.ElapsedMilliseconds / (double)_mgr.BarDelay;
        if (progress >= 1)
        {
            Reset();
            _armed = false;
            // 보조키만 있는 조합은 WM_HOTKEY가 오지 않으니, 여기서 막아야 Alt·Win을 뗄 때 메뉴가 열리지 않는다.
            KeyCombo.SuppressRelease(_mgr.BarKeys);
            OpenBarOn(screen, edge);
            return;
        }
        ShowGauge(pt, progress);
        _timer!.Interval = TimeSpan.FromMilliseconds(15); // 게이지가 도는 동안은 부드럽게
    }

    private static void Reset()
    {
        _dwell.Reset();
        if (_gaugeShown) { _gaugeShown = false; Gauge.Hide(); }
        if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(40);
    }

    private static void ShowGauge(Native.POINT pt, double progress)
    {
        double s = Native.MonitorScaleAt(pt.X, pt.Y);
        int px = (int)Math.Round(44 * s);
        var b = Screens.FromPoint(pt.X, pt.Y).Bounds;
        int x = Math.Clamp(pt.X - px / 2, b.Left, b.Right - px);
        int y = Math.Clamp(pt.Y - px / 2, b.Top, b.Bottom - px);
        _gaugeShown = true;
        Gauge.Update(x, y, progress, ExpandedWindow.Hex("Accent"));
    }

    // ----- 바 열기·닫기 -----

    private static void OpenBarOn(Screens.Monitor screen, ScreenEdge edge)
    {
        var mgr = _mgr!;
        double scale = ScaleOf(screen);
        var bar = new OpenBar { Edge = edge, Bounds = screen.Bounds, Final = FinalRect(screen, edge, mgr.BarSize) };
        bar.View = BarView.Open(bar, new BarOptions(ExpandedWindow.CurrentPalette(), (int)edge, ToPx(bar.Final), ToPx(bar.Bounds), scale));
        _bar = bar;
    }

    private static PixelRect ToPx(Rectangle r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private static double ScaleOf(Screens.Monitor screen)
    {
        var wa = screen.WorkArea;
        return Native.MonitorScaleAt(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
    }

    /// <summary>다 나왔을 때 바의 자리(물리 픽셀): 작업 영역 안, 가장자리에서 틈(8)만큼 떨어져, 두께는 작업 영역의 sizePercent%.</summary>
    public static Rectangle FinalRect(Screens.Monitor screen, ScreenEdge edge, int sizePercent)
    {
        var wa = screen.WorkArea;
        int gap = (int)Math.Round(Gap * ScaleOf(screen));
        bool side = edge is ScreenEdge.Left or ScreenEdge.Right;
        int thick = (int)Math.Round((side ? wa.Width : wa.Height) * sizePercent / 100.0);
        return edge switch
        {
            ScreenEdge.Left => new(wa.Left + gap, wa.Top + gap, thick, wa.Height - 2 * gap),
            ScreenEdge.Right => new(wa.Right - gap - thick, wa.Top + gap, thick, wa.Height - 2 * gap),
            ScreenEdge.Top => new(wa.Left + gap, wa.Top + gap, wa.Width - 2 * gap, thick),
            _ => new(wa.Left + gap, wa.Bottom - gap - thick, wa.Width - 2 * gap, thick),
        };
    }

    /// <summary>
    /// 마우스가 바(와 가장자리 사이 틈) 밖으로 나가면 들어간다.
    /// 펼친 창·우클릭 메뉴가 떠 있거나 항목을 끌어내는 중에는 기다린다.
    /// </summary>
    private static void CheckLeave(OpenBar bar)
    {
        if (bar.Closing || FileOps.Dragging || FluentMenu.IsOpen || ExpandedWindow.IsOpen) return;
        if (!Native.GetCursorPos(out var pt)) return;
        var r = bar.Final;
        var b = bar.Bounds;
        int l = r.Left, t = r.Top, rr = r.Right, bb = r.Bottom;
        switch (bar.Edge)
        {
            case ScreenEdge.Left: l = b.Left; break;
            case ScreenEdge.Right: rr = b.Right; break;
            case ScreenEdge.Top: t = b.Top; break;
            default: bb = b.Bottom; break;
        }
        if (pt.X >= l && pt.X < rr && pt.Y >= t && pt.Y < bb) return;
        bar.Closing = true;
        bar.View.BeginClose();
    }

    /// <summary>바를 열 조건(설정 켜짐, 조합키, 정한 가장자리, 작업 표시줄 쪽 아님, 전체 화면 아님, 버튼 안 누름)이 맞는지.</summary>
    private static bool AtEdge(Native.POINT pt, out Screens.Monitor screen, out ScreenEdge edge)
    {
        screen = Screens.FromPoint(pt.X, pt.Y);
        var mgr = _mgr!;
        edge = default;
        // 디스플레이마다 여는 가장자리가 다르다(열지 않는 디스플레이도 있다).
        if (mgr.BarEdgeFor(screen.DeviceName) is not { } chosen) return false;
        edge = chosen;
        if (!mgr.BarEnabled || _suspended || mgr.Editing || !KeyCombo.IsDown(mgr.BarKeys)) return false;
        if (Native.GetAsyncKeyState(0x01) < 0 || Native.GetAsyncKeyState(0x02) < 0) return false; // 마우스 왼쪽·오른쪽 버튼
        if (edge == Native.TaskbarEdgeOn(screen) || ExpandedWindow.IsOpen || FluentMenu.IsOpen) return false;
        return ZoneRect(screen, edge, mgr.BarZone).Contains(pt.X, pt.Y) && !Native.IsFullScreenBusy();
    }

    /// <summary>
    /// 마우스를 대면 게이지가 도는 띠(물리 픽셀). 가장자리에 딱 붙은 1픽셀부터, 인식 영역(zone, 0.1% 단위)만큼 안쪽까지.
    /// 가장자리를 따라서는 작업 영역 안(작업 표시줄 옆은 빼고)만.
    /// </summary>
    public static Rectangle ZoneRect(Screens.Monitor screen, ScreenEdge edge, int zone)
    {
        var b = screen.Bounds;
        var wa = screen.WorkArea;
        bool side = edge is ScreenEdge.Left or ScreenEdge.Right;
        int depth = 1 + (int)Math.Round((side ? b.Width : b.Height) * zone / 1000.0);
        return edge switch
        {
            ScreenEdge.Left => new(b.Left, wa.Top, depth, wa.Height),
            ScreenEdge.Right => new(b.Right - depth, wa.Top, depth, wa.Height),
            ScreenEdge.Top => new(wa.Left, b.Top, wa.Width, depth),
            _ => new(wa.Left, b.Bottom - depth, wa.Width, depth),
        };
    }

    /// <summary>WM_HOTKEY만 받는 보이지 않는 메시지 전용 창.</summary>
    private sealed class MessageWindow : IDisposable
    {
        private delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        private readonly WndProc _proc;
        private readonly Action<int, IntPtr> _onMessage;
        public IntPtr Handle { get; }

        public MessageWindow(Action<int, IntPtr> onMessage)
        {
            _onMessage = onMessage;
            _proc = Proc;
            var cls = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), lpszClassName = "DeskCards.Hotkey", hInstance = GetModuleHandle(null) };
            RegisterClass(ref cls);
            Handle = CreateWindowEx(0, cls.lpszClassName, "", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        }

        private IntPtr Proc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            _onMessage(msg, wParam);
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        public void Dispose() { if (Handle != IntPtr.Zero) DestroyWindow(Handle); }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS cls);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    }
}
