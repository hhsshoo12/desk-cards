using System;
using System.Runtime.InteropServices;

namespace DeskCards;

/// <summary>
/// 바탕화면 아이콘 간격(탐색기의 SysListView32)과, 새 카드 자리를 찾는 작업 영역 격자. 모든 값은 물리 픽셀이다.
/// </summary>
internal static class DesktopGrid
{
    /// <summary>기본 카드의 가로가 바탕화면 아이콘 몇 칸인지.</summary>
    public const int CardCols = 2;

    private const int LVM_GETITEMSPACING = 0x1000 + 51;

    private static int _cx;
    private static long _cachedAt;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // 작업 영역 격자: 카드가 움직일 수 있는 폭(작업 영역 − 카드 크기)을 정수 칸으로 나누므로, 첫 칸에서는
    // 왼쪽 위 모서리에, 마지막 칸에서는 오른쪽 아래 모서리에 카드가 정확히 붙는다. 칸 간격은 카드 크기의 절반 근처다.

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

    /// <summary>바탕화면 아이콘 가로 간격. 읽지 못하면 false.</summary>
    public static bool TryGetIconSpacing(out int cx)
    {
        long now = Environment.TickCount64;
        if (_cx > 0 && now - _cachedAt < 1000)
        {
            cx = _cx;
            return true;
        }

        cx = 0;
        var lv = FindDesktopListView();
        if (lv == IntPtr.Zero) return false;
        long s = SendMessage(lv, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero).ToInt64();
        cx = (int)(s & 0xFFFF);
        int cy = (int)((s >> 16) & 0xFFFF);
        if (cx < 16 || cy < 16) return false;

        (_cx, _cachedAt) = (cx, now);
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
