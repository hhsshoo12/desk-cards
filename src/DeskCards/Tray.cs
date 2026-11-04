using System;
using System.Runtime.InteropServices;
using DrawingNs = System.Drawing;

namespace DeskCards;

/// <summary>알림 영역 아이콘. 오른쪽 클릭 = 메뉴, 더블클릭 = 설정. 메시지를 받을 보이지 않는 창을 따로 둔다.</summary>
internal sealed class Tray : IDisposable
{
    private const uint CallbackMessage = Native.WM_USER + 1;
    private readonly IntPtr _hwnd;
    private readonly Native.Subclass _subclass;
    private readonly DrawingNs.Icon _icon;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private Native.NOTIFYICONDATA _data;

    public Tray(string tip, Action onMenu, Action onOpen)
    {
        _hwnd = CreateWindowEx(0, "Static", "Desk Cards Tray", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _icon = MakeIcon();
        _data = new Native.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon.Handle,
            szTip = tip,
        };
        _subclass = new Native.Subclass(_hwnd, (IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result) =>
        {
            result = IntPtr.Zero;
            if (msg == _taskbarCreated) { Native.Shell_NotifyIcon(Native.NIM_ADD, ref _data); return true; }
            if (msg != CallbackMessage) return false;
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case Native.WM_RBUTTONUP: onMenu(); break;
                case Native.WM_LBUTTONDBLCLK: onOpen(); break;
            }
            return true;
        });
        Native.Shell_NotifyIcon(Native.NIM_ADD, ref _data);
    }

    public void Dispose()
    {
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref _data);
        _subclass.Dispose();
        Native.DestroyWindow(_hwnd);
        _icon.Dispose();
    }

    /// <summary>2×2 타일 모양 트레이 아이콘을 그린다.</summary>
    private static DrawingNs.Icon MakeIcon()
    {
        using var bmp = new DrawingNs.Bitmap(32, 32);
        using (var g = DrawingNs.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = DrawingNs.Drawing2D.SmoothingMode.AntiAlias;
            var colors = new[] { "#4CC2FF", "#FFB900", "#6CCB5F", "#FF6F61" };
            for (int i = 0; i < 4; i++)
            {
                int x = 3 + (i % 2) * 14, y = 3 + (i / 2) * 14;
                using var br = new DrawingNs.SolidBrush(DrawingNs.ColorTranslator.FromHtml(colors[i]));
                using var path = new DrawingNs.Drawing2D.GraphicsPath();
                const int r = 4, s = 12;
                path.AddArc(x, y, r * 2, r * 2, 180, 90);
                path.AddArc(x + s - r * 2, y, r * 2, r * 2, 270, 90);
                path.AddArc(x + s - r * 2, y + s - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(x, y + s - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(br, path);
            }
        }
        IntPtr handle = bmp.GetHicon();
        try
        {
            using var borrowed = DrawingNs.Icon.FromHandle(handle);
            return (DrawingNs.Icon)borrowed.Clone();
        }
        finally { Native.DestroyIcon(handle); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
}
