using System;
using System.Runtime.InteropServices;

namespace DeskFolders;

/// <summary>
/// 바탕화면 아이콘 격자(탐색기의 SysListView32 칸 간격)를 읽는다.
/// 칸은 목록 창의 왼쪽 위(보통 주 모니터 0,0)에서 시작해 간격만큼 반복된다. 모든 값은 물리 픽셀이다.
/// </summary>
internal static class DesktopGrid
{
    /// <summary>카드 하나가 차지하는 칸 수(가로×세로).</summary>
    public const int CardCols = 2, CardRows = 2;

    private const int LVM_GETITEMSPACING = 0x1000 + 51;

    private static int _cx, _cy, _ox, _oy;
    private static long _cachedAt;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 카드 이동 격자. 아이콘 칸이 아니라 모니터 작업 영역(작업 표시줄 제외) 기준이다.
    /// 카드가 움직일 수 있는 폭(작업 영역 − 카드 크기)을 정수 칸으로 나누므로, 첫 칸에서는 왼쪽 위 모서리에,
    /// 마지막 칸에서는 오른쪽 아래 모서리에 카드가 정확히 붙는다. 칸 간격은 카드 크기의 절반 근처로 잡아
    /// 카드끼리 나란히 놓아도 거의 딱 붙는다.
    /// </summary>
    public static (int X, int Y) Snap(int x, int y, int w, int h)
    {
        var wa = WorkAreaAt(x + w / 2, y + h / 2);
        var (nx, ny) = Counts(wa, w, h);
        double stepX = StepOf(wa.Width - w, nx), stepY = StepOf(wa.Height - h, ny);
        int i = stepX > 0 ? Math.Clamp((int)Math.Round((x - wa.Left) / stepX), 0, nx) : 0;
        int j = stepY > 0 ? Math.Clamp((int)Math.Round((y - wa.Top) / stepY), 0, ny) : 0;
        return (wa.Left + (int)Math.Round(i * stepX), wa.Top + (int)Math.Round(j * stepY));
    }

    /// <summary>작업 영역에서 가로/세로로 몇 칸 움직일 수 있는지(0칸 = 한 자리뿐).</summary>
    public static (int Nx, int Ny) Counts(System.Drawing.Rectangle wa, int w, int h) =>
        (CountOf(wa.Width - w, w), CountOf(wa.Height - h, h));

    /// <summary>i번째 열, j번째 행 칸의 왼쪽 위(물리 픽셀).</summary>
    public static (int X, int Y) CellAt(System.Drawing.Rectangle wa, int w, int h, int i, int j)
    {
        var (nx, ny) = Counts(wa, w, h);
        return (wa.Left + (int)Math.Round(i * StepOf(wa.Width - w, nx)),
                wa.Top + (int)Math.Round(j * StepOf(wa.Height - h, ny)));
    }

    public static System.Drawing.Rectangle WorkAreaAt(int x, int y) =>
        System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;

    private static int CountOf(int free, int size) =>
        free <= 0 ? 0 : Math.Max(1, (int)Math.Round(free / (size / 2.0)));

    private static double StepOf(int free, int count) => count == 0 ? 0 : free / (double)count;

    public static bool TryGet(out int ox, out int oy, out int cx, out int cy)
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
