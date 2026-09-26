using System;
using System.Runtime.InteropServices;

namespace DeskFolders;

/// <summary>
/// 바탕화면 아이콘 격자(탐색기의 SysListView32 칸 간격)를 읽어 카드 위치를 칸에 맞춘다.
/// 모든 좌표는 물리 픽셀(화면 좌표)이다.
/// </summary>
internal static class DesktopGrid
{
    private const int LVM_GETITEMSPACING = 0x1000 + 51;

    private static int _cx, _cy, _ox, _oy;
    private static long _cachedAt;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 내용 영역(x, y, w, h)이 차지할 칸들(가로·세로 올림) 한가운데 오도록
    /// 가장 가까운 칸 위치로 옮긴 내용 영역의 왼쪽 위를 돌려준다.
    /// </summary>
    public static (int X, int Y) Snap(int x, int y, int w, int h)
    {
        if (!TryGet(out int ox, out int oy, out int cx, out int cy)) return (x, y);
        int cols = Math.Max(1, (int)Math.Ceiling(w / (double)cx));
        int rows = Math.Max(1, (int)Math.Ceiling(h / (double)cy));
        int padX = (cols * cx - w) / 2, padY = (rows * cy - h) / 2;
        int fx = ox + (int)Math.Round((x - padX - ox) / (double)cx) * cx;
        int fy = oy + (int)Math.Round((y - padY - oy) / (double)cy) * cy;
        return (fx + padX, fy + padY);
    }

    private static bool TryGet(out int ox, out int oy, out int cx, out int cy)
    {
        // 끄는 동안 WM_MOVING마다 불리므로 잠깐 캐시한다.
        long now = Environment.TickCount64;
        if (_cx > 0 && now - _cachedAt < 1000)
        {
            (ox, oy, cx, cy) = (_ox, _oy, _cx, _cy);
            return true;
        }

        ox = oy = cx = cy = 0;
        var lv = FindDesktopListView();
        if (lv == IntPtr.Zero) return false;
        long s = SendMessage(lv, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero).ToInt64();
        cx = (int)(s & 0xFFFF);
        cy = (int)((s >> 16) & 0xFFFF);
        if (cx < 16 || cy < 16 || !Native.GetWindowRect(lv, out var r)) return false;
        ox = r.Left;
        oy = r.Top;

        (_ox, _oy, _cx, _cy, _cachedAt) = (ox, oy, cx, cy, now);
        return true;
    }

    private static IntPtr FindDesktopListView()
    {
        // 보통 Progman 아래에 있고, 배경 슬라이드쇼 등을 쓰면 WorkerW 아래로 옮겨진다.
        var defView = FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        var worker = IntPtr.Zero;
        while (defView == IntPtr.Zero && (worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            defView = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
        return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }
}
