using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskCards;

/// <summary>
/// 카드 바 여는 조건을 지켜본다: 조합키를 누른 채 정한 가장자리에 마우스를 대고 있으면
/// 커서 둘레의 게이지가 12시부터 시계방향으로 차오르고, 한 바퀴가 되는 순간 바가 나온다.
/// 작업 표시줄이 있는 가장자리와 전체 화면 앱 위에서는 열지 않는다.
/// 조합키는 Windows 단축키로 등록해 둬서, 누르는 동안 앞의 앱에 키가 가지 않는다.
/// </summary>
internal static class EdgeBar
{
    private const int HotkeyId = 1, WM_HOTKEY = 0x0312;
    private static HwndSource? _hotkeyWindow;
    private static string _registeredFor = "";
    private static bool _suspended;

    /// <summary>조합키 등록 결과. 카드 바가 꺼져 있으면 null, 다른 곳에서 이미 쓰는 조합이면 false.</summary>
    public static bool? HotkeyRegistered { get; private set; }
    private static GroupManager? _mgr;
    private static DispatcherTimer? _timer;
    private static GaugeOverlay? _gauge;
    private static BarWindow? _bar;
    private static readonly Stopwatch _dwell = new();
    private static bool _armed = true;

    public static void Start(GroupManager mgr)
    {
        _mgr = mgr;
        _hotkeyWindow = new HwndSource(new HwndSourceParameters("DeskCards.Hotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        _hotkeyWindow.AddHook(HotkeyHook);
        ApplyHotkey();
        mgr.Changed += ApplyHotkey;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>카드 바 편집: 바를 열고(이미 열려 있으면 그대로) 편집을 시작한다. 주 디스플레이의 바를 먼저 쓴다.</summary>
    public static void OpenForEdit()
    {
        if (_mgr == null) return;
        if (_bar is { IsGone: false } open) { open.BeginEdit(); return; }
        foreach (var screen in Forms.Screen.AllScreens.OrderByDescending(s => s.Primary))
        {
            if (_mgr.BarEdgeFor(screen.DeviceName) is not { } edge) continue;
            Reset();
            _armed = false;
            _bar = new BarWindow(_mgr, screen, edge);
            _bar.Open();
            _bar.BeginEdit();
            return;
        }
    }

    public static void Stop()
    {
        _timer?.Stop();
        if (_mgr != null) _mgr.Changed -= ApplyHotkey;
        Unregister();
        _hotkeyWindow?.Dispose();
        _hotkeyWindow = null;
        _gauge?.Close();
        _bar?.Close();
        _gauge = null;
        _bar = null;
    }

    // ----- 조합키 등록 -----

    /// <summary>설정(켜기·조합)에 맞게 단축키를 등록하거나 푼다. 바뀐 게 없으면 그대로 둔다.</summary>
    private static void ApplyHotkey()
    {
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

    private static IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 조합을 누른 순간 빈 키를 한 번 눌러, 나중에 Alt·Win을 뗄 때 앱 메뉴·시작 메뉴가 열리지 않게 한다.
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId && _mgr != null)
        {
            KeyCombo.SuppressRelease(_mgr.BarKeys);
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // ----- 가장자리 지켜보기 -----

    private static void Tick()
    {
        if (_mgr == null || _mgr.IsShuttingDown) return;
        if (_bar != null)
        {
            if (_bar.IsGone) _bar = null;
            else { _bar.CheckLeave(); return; }
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
            _bar = new BarWindow(_mgr, screen, edge);
            _bar.Open();
            return;
        }
        _gauge ??= new GaugeOverlay();
        _gauge.Update(pt, progress);
        _timer!.Interval = TimeSpan.FromMilliseconds(15); // 게이지가 도는 동안은 부드럽게
    }

    private static void Reset()
    {
        _dwell.Reset();
        _gauge?.Hide();
        if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(40);
    }

    /// <summary>바를 열 조건(설정 켜짐, 조합키, 정한 가장자리, 작업 표시줄 쪽 아님, 전체 화면 아님, 버튼 안 누름)이 맞는지.</summary>
    private static bool AtEdge(Native.POINT pt, out Forms.Screen screen, out ScreenEdge edge)
    {
        screen = Forms.Screen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
        var mgr = _mgr!;
        edge = default;
        // 디스플레이마다 여는 가장자리가 다르다(열지 않는 디스플레이도 있다).
        if (mgr.BarEdgeFor(screen.DeviceName) is not { } chosen) return false;
        edge = chosen;
        if (!mgr.BarEnabled || _suspended || mgr.Editing || !KeyCombo.IsDown(mgr.BarKeys)) return false;
        if (Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed) return false;
        if (edge == Native.TaskbarEdgeOn(screen) || ExpandedWindow.IsOpen || FluentMenu.IsOpen) return false;
        return ZoneRect(screen, edge, mgr.BarZone).Contains(pt.X, pt.Y) && !Native.IsFullScreenBusy();
    }

    /// <summary>
    /// 마우스를 대면 게이지가 도는 띠(물리 픽셀). 가장자리에 딱 붙은 1픽셀부터, 인식 영역(zone, 0.1% 단위)만큼 안쪽까지.
    /// 가장자리를 따라서는 작업 영역 안(작업 표시줄 옆은 빼고)만.
    /// </summary>
    public static System.Drawing.Rectangle ZoneRect(Forms.Screen screen, ScreenEdge edge, int zone)
    {
        var b = screen.Bounds;
        var wa = screen.WorkingArea;
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
}
