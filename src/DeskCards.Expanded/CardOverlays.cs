using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DeskCards.Expanded;

/// <summary>
/// 웹 카드 한 장의 임시 창(overlay) 하나를 관리한다. 바탕화면 카드와 카드 바 안 카드가 똑같이 쓴다.
/// </summary>
internal sealed class CardOverlays
{
    private readonly string _id, _folder, _browserDir;
    private readonly Func<bool> _light;
    private readonly Func<(int X, int Y, double Scale)> _origin;
    private readonly Action<string> _toPage;
    private readonly Action<string> _toHost;
    private static int _open;
    /// <summary>임시 창이 하나라도 열려 있는지(다른 스레드에서 읽어도 된다).</summary>
    public static bool AnyOpen => System.Threading.Volatile.Read(ref _open) > 0;

    private CardOverlayWindow? _current;
    private CardOverlayWindow? Current
    {
        get => _current;
        set
        {
            if (_current != null && value == null) System.Threading.Interlocked.Decrement(ref _open);
            else if (_current == null && value != null) System.Threading.Interlocked.Increment(ref _open);
            _current = value;
        }
    }

    /// <param name="origin">카드 왼쪽 위의 화면 자리(물리 픽셀)와 배율.</param>
    /// <param name="toPage">카드 화면으로 보낼 JSON(이벤트).</param>
    /// <param name="toHost">임시 창이 deskcard.send로 보낸 메시지(JSON)를 받을 곳.</param>
    public CardOverlays(string id, string folder, string browserDir, Func<bool> light, Func<(int X, int Y, double Scale)> origin, Action<string> toPage, Action<string> toHost)
    {
        (_id, _folder, _browserDir, _light, _origin, _toPage, _toHost) = (id, folder, browserDir, light, origin, toPage, toHost);
    }

    /// <summary>카드 화면이 요청한 "ovl" 메시지를 처리한다.</summary>
    public void Handle(JsonElement root)
    {
        switch (root.GetProperty("a").GetString())
        {
            case "open": Open(root); break;
            case "close": Close(); break;
            case "send": Current?.Post(WebCardWindow.Evt("overlay", root.GetProperty("d"))); break;
        }
    }

    /// <summary>위치(x,y)는 카드 왼쪽 위 기준 CSS 픽셀이고 카드 밖(음수·큰 값)도 된다.</summary>
    private void Open(JsonElement m)
    {
        var (ox, oy, k) = _origin();
        double Num(string key, double fallback) => m.TryGetProperty(key, out var v) && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : fallback;
        string? url = m.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(url) || url.Contains("://") || url.Contains("..")) return; // 카드 폴더 안의 상대 주소만
        int w = (int)Math.Round(Math.Clamp(Num("w", 320), 40, 4000) * k), h = (int)Math.Round(Math.Clamp(Num("h", 240), 40, 4000) * k);
        int x = ox + (int)Math.Round(Num("x", 0) * k), y = oy + (int)Math.Round(Num("y", 0) * k);
        var wa = WorkAreaOf(new PixelRect(x, y, x + w, y + h));
        x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
        y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
        Close();
        var o = new CardOverlayWindow(this, _id + ".deskcard", _folder, url, _browserDir, _light(), new PixelRect(x, y, x + Math.Min(w, wa.Width), y + Math.Min(h, wa.Height)));
        Current = o;
        o.Present();
    }

    public void Close()
    {
        var o = Current;
        if (o == null) return;
        Current = null;
        o.Dismiss();
        _toPage(WebCardWindow.Evt("overlayclose", true));
    }

    /// <summary>창이 닫힐 때(카드가 없어질 때) 알림 없이 정리한다.</summary>
    public void Dispose()
    {
        var o = Current;
        Current = null;
        o?.Dismiss();
    }

    /// <summary>임시 창이 저절로 사라졌다(다른 곳을 눌렀다 등).</summary>
    internal void Dismissed(CardOverlayWindow o)
    {
        if (Current != o) return;
        Current = null;
        o.Dismiss();
        _toPage(WebCardWindow.Evt("overlayclose", true));
    }

    /// <summary>임시 창 화면이 보낸 요청.</summary>
    internal void FromOverlay(CardOverlayWindow from, string? t, JsonElement root)
    {
        if (Current != from) return;
        switch (t)
        {
            case "msg": _toHost(root.GetProperty("d").GetRawText()); break;
            case "ovl":
                switch (root.GetProperty("a").GetString())
                {
                    case "send": _toPage(WebCardWindow.Evt("overlay", root.GetProperty("d"))); break;
                    case "close": Close(); break;
                }
                break;
        }
    }

    private static PixelRect WorkAreaOf(PixelRect r)
    {
        var rc = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        var mon = MonitorFromRect(ref rc, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(mon, ref info) ? new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom) : r;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
