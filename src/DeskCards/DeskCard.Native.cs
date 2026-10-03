using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskCards;

internal abstract partial class DeskCard
{
    // ----- 바탕화면 층에 붙이기 -----

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Handle;
        if (_restorePosition is { } position) PixelPlacement.Move(this, position);
        Hwnd.RemoveSysMenu(hwnd);
        if (Activatable) Hwnd.MakeTool(hwnd);
        else Hwnd.MakeNoActivateTool(hwnd);
        Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        Theme.Changed += OnThemeChanged;
        UpdateBorder();
        AttachToDesktop(hwnd);
        ApplySize();

        if (_taskbarCreatedMsg == 0) _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
    }

    private void AttachToDesktop(IntPtr hwnd)
    {
        // Progman을 소유자로 두면 Win+D(바탕화면 보기) 때도 숨겨지지 않는다.
        var progman = Native.FindWindow("Progman", null);
        if (progman != IntPtr.Zero) Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, progman);
        SendToBottom();
    }

    /// <summary>바탕화면 바로 위(맨 아래)로 보낸다. 카드 앞뒤 순서는 앱이 이렇게 직접 정할 때만 바뀐다.</summary>
    private void SendToBottom()
    {
        _ownZ = true;
        try { Hwnd.SetZOrder(Handle, Native.HWND_BOTTOM); }
        finally { _ownZ = false; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_NCACTIVATE)
        {
            // Windows 11 아크릴 배경은 창이 비활성이면 단색으로 바뀐다. 바탕화면 카드는 거의 늘 비활성이라,
            // 창 틀을 늘 활성 상태로 그리게 해서 아크릴을 유지한다(실제 활성·초점은 그대로).
            handled = true;
            return Native.DefWindowProc(hwnd, msg, new IntPtr(1), lParam);
        }
        if (msg == Native.WM_WINDOWPOSCHANGED)
        {
            FollowLabel();
        }
        else if (msg == Native.WM_WINDOWPOSCHANGING)
        {
            KeepDesktopOrder(lParam);
        }
        else if (msg == Native.WM_ENTERSIZEMOVE)
        {
            BeginMoveTracking(hwnd);
        }
        else if (msg == Native.WM_MOVING)
        {
            MoveWithGuides(hwnd, lParam);
            handled = true;
            return new IntPtr(1);
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
        return IntPtr.Zero;
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
