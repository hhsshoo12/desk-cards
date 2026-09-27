using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace DeskCards;

/// <summary>
/// 파일의 탐색기 우클릭 메뉴("추가 옵션 표시"를 눌렀을 때 나오는 메뉴)를 그대로 띄운다.
/// 설치된 프로그램이 넣은 항목(7-Zip, 연결 프로그램, 보내기 등)까지 모두 들어 있고, 누르면 Windows가 실행한다.
/// </summary>
internal static class ShellMenu
{
    private const uint CMF_NORMAL = 0x0, CMF_EXTENDEDVERBS = 0x100;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const uint GCS_VERBW = 0x4;
    private const int CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000;
    private const int CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const int FirstCmd = 1, LastCmd = 0x7FFF;
    private const int WM_INITMENUPOPUP = 0x0117, WM_DRAWITEM = 0x002B, WM_MEASUREITEM = 0x002C, WM_MENUCHAR = 0x0120;

    /// <param name="extended">Shift를 누른 채 열면 탐색기처럼 숨은 항목(경로로 복사 등)까지 보인다.</param>
    public static void Show(string path, int x, int y, bool extended = false)
    {
        IntPtr pidl = IntPtr.Zero, menu = IntPtr.Zero;
        IShellFolder? parent = null;
        IContextMenu? cm = null;
        IContextMenu3? cm3 = null;
        using var owner = new HwndSource(new HwndSourceParameters("DeskCards.ShellMenu") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0) return;
            var iidFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidl, ref iidFolder, out var folderObj, out IntPtr child) != 0) return;
            parent = (IShellFolder)folderObj;
            var iidMenu = typeof(IContextMenu).GUID;
            var children = new[] { child };
            if (parent.GetUIObjectOf(owner.Handle, 1, children, ref iidMenu, IntPtr.Zero, out var menuObj) != 0) return;
            cm = (IContextMenu)menuObj;
            cm3 = menuObj as IContextMenu3; // 보내기·연결 프로그램 같은 하위 메뉴는 열 때 채워지므로 메시지를 넘겨 줘야 한다.

            menu = CreatePopupMenu();
            if (cm.QueryContextMenu(menu, 0, FirstCmd, LastCmd, extended ? CMF_EXTENDEDVERBS : CMF_NORMAL) < 0) return;

            owner.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (cm3 != null && msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
                {
                    if (cm3.HandleMenuMsg2((uint)msg, w, l, out IntPtr result) == 0)
                    {
                        handled = true;
                        return result;
                    }
                }
                return IntPtr.Zero;
            });

            MatchTheme();
            // 메뉴 창의 주인이 앞에 있어야 바깥을 눌렀을 때 메뉴가 닫힌다.
            SetForegroundWindow(owner.Handle);
            int cmd = (int)TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, x, y, owner.Handle, IntPtr.Zero);
            if (cmd < FirstCmd) return;

            int offset = cmd - FirstCmd;
            // 이름 바꾸기는 탐색기 창 안에서만 되는 항목이라, 탐색기에서 그 파일을 골라 둔다(F2로 바로 바꿀 수 있다).
            if (Verb(cm, offset) is "rename")
            {
                FileOps.Reveal(path);
                return;
            }
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
                    | (Native.GetAsyncKeyState(0x10) < 0 ? CMIC_MASK_SHIFT_DOWN : 0)
                    | (Native.GetAsyncKeyState(0x11) < 0 ? CMIC_MASK_CONTROL_DOWN : 0),
                hwnd = owner.Handle,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                nShow = 1,
                ptInvoke = new Native.POINT { X = x, Y = y },
            };
            cm.InvokeCommand(ref info);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            // 메뉴를 만들 수 없는 파일(지워졌거나 접근할 수 없음)은 아무것도 띄우지 않는다.
        }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (cm != null) Marshal.ReleaseComObject(cm);
            if (parent != null) Marshal.ReleaseComObject(parent);
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>커서 자리에 띄운다.</summary>
    public static void ShowAtCursor(string path, bool extended = false)
    {
        Native.GetCursorPos(out var pt);
        Show(path, pt.X, pt.Y, extended);
    }

    /// <summary>
    /// 옛 메뉴도 Windows 앱 테마(밝게·어둡게)를 따르게 한다. 탐색기가 쓰는 uxtheme 내부 함수라
    /// 없는 Windows에서는 그냥 밝은 메뉴로 뜬다.
    /// </summary>
    private static void MatchTheme()
    {
        try
        {
            SetPreferredAppMode(Theme.IsLight ? 3 : 2); // 2 = 어둡게 강제, 3 = 밝게 강제
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    private static string? Verb(IContextMenu cm, int offset)
    {
        var sb = new StringBuilder(256);
        try
        {
            return cm.GetCommandString((IntPtr)offset, GCS_VERBW, IntPtr.Zero, sb, sb.Capacity) == 0 ? sb.ToString() : null;
        }
        catch (COMException) { return null; }
    }

    // ----- Win32 · COM -----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv, out IntPtr pidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        public string? lpParametersW;
        public string? lpDirectoryW;
        public string? lpTitleW;
        public Native.POINT ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName();
        void EnumObjects();
        void BindToObject();
        void BindToStorage();
        void CompareIDs();
        void CreateViewObject();
        void GetAttributesOf();
        [PreserveSig]
        int GetUIObjectOf(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, int first, int last, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(IntPtr cmd, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, int first, int last, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(IntPtr cmd, uint type, IntPtr reserved, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }
}
