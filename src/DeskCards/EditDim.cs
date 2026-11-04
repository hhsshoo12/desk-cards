using System.Collections.Generic;

namespace DeskCards;

/// <summary>
/// 편집 모드 동안 화면을 어둡게 덮는 막(Windows 캡처 도구처럼). 모니터마다 하나씩, 작업 표시줄을 뺀 작업 영역만 덮는다.
/// 카드와 편집 막대는 이 막 위에 올라와 밝게 보인다. 막을 누르면(= 바탕화면을 누르면) 고른 카드가 풀린다.
/// </summary>
internal static class EditDim
{
    private static readonly List<LayeredWindow> All = new();

    /// <summary>모든 모니터에 막을 띄운다.</summary>
    public static void ShowAll(GroupManager mgr)
    {
        CloseAll();
        foreach (var screen in Screens.All())
        {
            var a = screen.WorkArea;
            // 눌러도 활성 창이 되지 않아야 편집 막대의 키보드(Esc, 화살표)가 계속 먹는다.
            var dim = new LayeredWindow(topmost: true, clickThrough: false);
            dim.Clicked += () => mgr.Select(null);
            dim.Update(a.Left, a.Top, a.Width, a.Height, LayeredWindow.Solid(a.Width, a.Height, 0, 0, 0, 0x73));
            Native.SetWindowPos(dim.Handle, Hwnd.Topmost, a.Left, a.Top, a.Width, a.Height, Native.SWP_NOACTIVATE);
            dim.Show();
            dim.LockZOrder = true; // 띄운 뒤로는 눌러도 카드·막대 위로 올라오지 않는다.
            All.Add(dim);
        }
    }

    public static void CloseAll()
    {
        foreach (var dim in All) dim.Dispose();
        All.Clear();
    }
}
