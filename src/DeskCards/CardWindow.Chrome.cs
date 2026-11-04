using System;
using System.Runtime.InteropServices;

namespace DeskCards;

internal sealed partial class CardWindow
{
    private const uint WM_NCCALCSIZE = 0x0083;
    private static uint _taskbarCreatedMsg;
    private Native.Subclass? _hook;
    private bool _movingZ;

    /// <summary>Progman을 소유자로 두면 Win+D(바탕화면 보기) 때도 숨겨지지 않는다.</summary>
    private void AttachToDesktop(IntPtr hwnd)
    {
        Hwnd.SetOwner(hwnd, Native.FindWindow("Progman", null));
        SendToBottom();
    }

    /// <summary>바탕화면 바로 위(맨 아래)로 보낸다. 카드 앞뒤 순서는 앱이 이렇게 직접 정할 때만 바뀐다.</summary>
    private void SendToBottom()
    {
        _movingZ = true;
        try { Hwnd.SetZOrder(Handle, Native.HWND_BOTTOM); }
        finally { _movingZ = false; }
    }

    /// <summary>편집 중에는 어두운 막(EditDim) 위로 올라온다. 편집이 끝나면 다시 바탕화면 층으로 내려간다.</summary>
    public void RaiseForEdit()
    {
        if (_editing) Hwnd.SetZOrder(Handle, Hwnd.Topmost);
    }

    private void InstallMessageHook(IntPtr hwnd)
    {
        if (_taskbarCreatedMsg == 0) _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        _hook = new Native.Subclass(hwnd, OnMessage);
    }

    private bool OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (msg == Native.WM_NCACTIVATE)
        {
            // Windows 11 아크릴 배경은 창이 비활성이면 단색으로 바뀐다. 바탕화면 카드는 거의 늘 비활성이라,
            // 창 틀을 늘 활성 상태로 그리게 해서 아크릴을 유지한다(실제 활성·초점은 그대로).
            result = Native.DefSubclassProc(hwnd, msg, new IntPtr(1), lParam);
            return true;
        }
        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            // 창 전체가 내용 영역이다(눈에 안 보이는 크기 조절 테두리·제목 줄 자리 없음).
            // 테두리·그림자·둥근 모서리는 DWM이 그대로 그리므로, 창 크기 = 카드 크기가 된다.
            return true;
        }
        if (msg == Native.WM_WINDOWPOSCHANGED) FollowLabel();
        else if (msg == Native.WM_WINDOWPOSCHANGING) KeepDesktopOrder(lParam);
        else if (msg == Native.WM_DPICHANGED)
            // Windows 배율이 바뀌면 크기를 다시 계산하고 화면 안으로 맞춘다.
            Ui.Post(() => { if (!_closed) Refit(); }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0) AttachToDesktop(hwnd);
        return false;
    }

    /// <summary>
    /// 항상 다른 창 뒤(바탕화면 바로 위)에 머문다. 편집 중에만 어두운 막 위로 올라온다.
    /// 누르기(활성화)로 순서가 바뀌면 카드끼리 앞뒤가 뒤바뀌며 그림자가 이쪽저쪽 드리우므로,
    /// 앱이 직접 맨 아래로 보낼 때 말고는 순서를 바꾸지 않는다.
    /// </summary>
    private void KeepDesktopOrder(IntPtr lParam)
    {
        if (_editing) return;
        var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
        if ((wp.flags & Native.SWP_NOZORDER) != 0) return;
        if (_movingZ) wp.hwndInsertAfter = Native.HWND_BOTTOM;
        else wp.flags |= Native.SWP_NOZORDER;
        Marshal.StructureToPtr(wp, lParam, false);
    }
}
