using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace DeskCards;

internal abstract partial class DeskCard
{
    // ----- 바탕화면 층에 붙이기 -----

    /// <summary>창이 만들어진 직후 한 번: 도구 창 스타일, 둥근 모서리·아크릴, 바탕화면 층, 메시지 감시.</summary>
    private void InitHandle()
    {
        var hwnd = Handle;
        Hwnd.MakeNoActivateTool(hwnd);
        Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        Theme.Changed += OnThemeChanged;
        UpdateBorder();
        AttachToDesktop(hwnd);
        ApplySize();

        if (_taskbarCreatedMsg == 0) _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        _subclass = new Native.Subclass(hwnd, WndProc);
        base.Closed += (_, _) =>
        {
            OnClosed();
            CardClosed?.Invoke(this, EventArgs.Empty);
        };
    }

    private void AttachToDesktop(IntPtr hwnd)
    {
        // Progman을 소유자로 두면 Win+D(바탕화면 보기) 때도 숨겨지지 않는다.
        Hwnd.SetOwner(hwnd, Native.FindWindow("Progman", null));
        SendToBottom();
    }

    /// <summary>바탕화면 바로 위(맨 아래)로 보낸다. 카드 앞뒤 순서는 앱이 이렇게 직접 정할 때만 바뀐다.</summary>
    private void SendToBottom()
    {
        _ownZ = true;
        try { Hwnd.SetZOrder(Handle, Native.HWND_BOTTOM); }
        finally { _ownZ = false; }
    }

    private bool WndProc(IntPtr hwnd, uint umsg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        int msg = (int)umsg;
        result = IntPtr.Zero;
        if (msg == Native.WM_NCACTIVATE)
        {
            // Windows 11 아크릴 배경은 창이 비활성이면 단색으로 바뀐다. 바탕화면 카드는 거의 늘 비활성이라,
            // 창 틀을 늘 활성 상태로 그리게 해서 아크릴을 유지한다(실제 활성·초점은 그대로).
            result = Native.DefSubclassProc(hwnd, umsg, new IntPtr(1), lParam);
            return true;
        }
        if (msg == Native.WM_WINDOWPOSCHANGED)
        {
            FollowLabel();
        }
        else if (msg == Native.WM_WINDOWPOSCHANGING)
        {
            KeepDesktopOrder(lParam);
        }
        else if (msg == Native.WM_DPICHANGED)
        {
            // Windows 배율이 바뀌면 크기를 다시 계산하고 화면 안으로 맞춘다.
            Ui.Post(() => { if (!_closed) Refit(); }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
        }
        else if (msg == Native.WM_ENTERSIZEMOVE)
        {
            BeginMoveTracking(hwnd);
        }
        else if (msg == Native.WM_MOVING)
        {
            MoveWithGuides(hwnd, lParam);
            result = new IntPtr(1);
            return true;
        }
        else if (msg == Native.WM_EXITSIZEMOVE)
        {
            SmartGuides.Hide();
            Settle(hwnd);
        }
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AttachToDesktop(hwnd);
        }
        return false;
    }

    private void KeepDesktopOrder(IntPtr lParam)
    {
        // 항상 다른 창 뒤(바탕화면 바로 위)에 머문다. 편집 중에만 어두운 막 위로 올라온다.
        // 누르기(활성화)로 순서가 바뀌면 카드끼리 앞뒤가 뒤바뀌며 그림자가 이쪽저쪽 드리우므로,
        // 앱이 직접 맨 아래로 보낼 때 말고는 순서를 바꾸지 않는다.
        var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
        if ((wp.flags & Native.SWP_NOZORDER) == 0 && !_editing)
        {
            if (_ownZ) wp.hwndInsertAfter = Native.HWND_BOTTOM;
            else wp.flags |= Native.SWP_NOZORDER;
            Marshal.StructureToPtr(wp, lParam, false);
        }
    }
}
