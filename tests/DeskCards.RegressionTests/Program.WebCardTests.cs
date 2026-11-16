using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using DeskCards;
using DeskCards.Expanded;

internal static partial class Program
{
    private static void WebCardTests(string root)
    {
        string work = Path.Combine(root, "webcard");
        Directory.CreateDirectory(work);

        string MakeCard(string name, string json, params string[] files)
        {
            string dir = Path.Combine(work, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), json);
            foreach (string f in files) File.WriteAllText(Path.Combine(dir, f), "x");
            return dir;
        }

        Test("web card: manifest reads html card and background exe widget", () =>
        {
            var a = WebCardManifest.Load(MakeCard("a", "{\"id\":\"com.test.a\",\"name\":\"A\",\"size\":[320,180],\"html\":\"card.html\"}", "card.html"));
            Check(a is { Id: "com.test.a", Width: 320, Height: 180, Html: "card.html", Exe: null, Background: false });
            var b = WebCardManifest.Load(MakeCard("b", "{\"id\":\"b\",\"name\":\"B\",\"exe\":\"w.exe\",\"mode\":\"background\"}", "w.exe"));
            Check(b is { Html: null, Exe: "w.exe", Background: true, Width: 300, Height: 200 });
        });

        Test("web card: manifest rejects bad id, missing files, escaping paths and empty cards", () =>
        {
            void Bad(string json, params string[] files)
            {
                try { WebCardManifest.Load(MakeCard("bad" + Guid.NewGuid().ToString("N")[..6], json, files)); }
                catch (WebCardException) { return; }
                throw new Exception("Assertion failed: expected manifest error for " + json);
            }
            Bad("{\"id\":\"Bad Id\",\"name\":\"x\",\"html\":\"c.html\"}", "c.html");
            Bad("{\"id\":\"x\",\"name\":\"x\",\"html\":\"missing.html\"}");
            Bad("{\"id\":\"x\",\"name\":\"x\",\"html\":\"..\\\\evil.html\"}");
            Bad("{\"id\":\"x\",\"name\":\"x\"}");
            Bad("{\"id\":\"x\",\"name\":\"x\",\"html\":\"c.html\",\"mode\":\"weird\"}", "c.html");
        });

        Test("web card: store imports a .dard zip, lists it, replaces on update and removes", () =>
        {
            string src = MakeCard("pack", "{\"id\":\"com.test.zip\",\"name\":\"Zip\",\"html\":\"c.html\"}", "c.html");
            string dard = Path.Combine(work, "zip.dard");
            ZipFile.CreateFromDirectory(src, dard);
            var store = new WebCardStore(Path.Combine(work, "store"));
            var first = store.Import(dard);
            Check(File.Exists(Path.Combine(first.Directory, "c.html")));
            Check(store.List().Count == 1);
            File.WriteAllText(Path.Combine(src, "c.html"), "new");
            File.Delete(dard);
            ZipFile.CreateFromDirectory(src, dard);
            store.Import(dard);
            Check(File.ReadAllText(Path.Combine(first.Directory, "c.html")) == "new");
            Check(store.List().Count == 1);
            store.Remove("com.test.zip");
            Check(store.List().Count == 0);
        });

        Test("web card: store imports a folder and a zip wrapped in one folder", () =>
        {
            string src = MakeCard("wrapped", "{\"id\":\"com.test.wrap\",\"name\":\"W\",\"html\":\"c.html\"}", "c.html");
            string outer = Path.Combine(work, "outer");
            Directory.CreateDirectory(outer);
            Directory.Move(src, Path.Combine(outer, "inner"));
            string dard = Path.Combine(work, "wrapped.dard");
            ZipFile.CreateFromDirectory(outer, dard);
            var store = new WebCardStore(Path.Combine(work, "store2"));
            store.Import(dard);
            string folder = MakeCard("folder", "{\"id\":\"com.test.folder\",\"name\":\"F\",\"html\":\"c.html\"}", "c.html");
            store.Import(folder);
            Check(store.List().Count == 2);
        });

        Test("web card: process relays stdout lines, accepts stdin lines and stops with the app", () =>
        {
            // PowerShell로 표준 입력의 줄을 그대로 표준 출력으로 돌려준다.
            string findstr = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var lines = new System.Collections.Concurrent.BlockingCollection<string>();
            using var process = new WebCardProcess(findstr, work, "com.test.echo", Path.Combine(work, "data"), "-NoProfile -Command \"while(($l=[Console]::In.ReadLine()) -ne $null){[Console]::Out.WriteLine($l);[Console]::Out.Flush()}\"");
            process.Message += lines.Add;
            process.Start();
            process.Send("{\"hello\":1}");
            Check(lines.TryTake(out var line, 15000) && line == "{\"hello\":1}");
            Check(!process.HasExited);
            process.Dispose();
            Check(process.HasExited);
        });

        Test("web card: process reports its exit code when it ends by itself", () =>
        {
            string ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var codes = new System.Collections.Concurrent.BlockingCollection<int>();
            using var process = new WebCardProcess(ps, work, "com.test.exit", Path.Combine(work, "data"), "-NoProfile -Command \"exit 7\"");
            process.Exited += codes.Add;
            process.Start();
            Check(codes.TryTake(out int code, 15000) && code == 7);
        });

        Test("web card: bar key round-trips and never collides with a folder name", () =>
        {
            string key = WebCardManager.BarKey("com.test.a");
            Check(key == "web:com.test.a" && WebCardManager.IsBarKey(key) && WebCardManager.BarId(key) == "com.test.a");
            Check(!WebCardManager.IsBarKey("새 그룹"));
            Check(Path.GetInvalidFileNameChars().Contains(':')); // 폴더 이름에는 ':'를 쓸 수 없으므로 그룹 이름과 겹치지 않는다
        });

        Test("desktop card: focus is not restored into the page when not editing (activation ping-pong)", () =>
        {
            // 되살리면 WebView2의 늦은 MoveFocus가 맨 아래로 내린 카드를 다시 활성화해 Progman과 핑퐁하며 깜박였다.
            Check(DesktopCardRules.SwallowSetFocus(editing: false));
            Check(!DesktopCardRules.SwallowSetFocus(editing: true));
        });

        Test("desktop card: child HWND focus guard and Z-order rules", () =>
        {
            Check(DesktopCardRules.ShouldSuppressChildFocusActivation(editing: false, hasChildHwnd: true));
            Check(!DesktopCardRules.ShouldSuppressChildFocusActivation(editing: false, hasChildHwnd: false));
            Check(!DesktopCardRules.ShouldSuppressChildFocusActivation(editing: true, hasChildHwnd: true));

            Check((DesktopCardRules.RestackBottomFlags & DesktopCardRules.SwpNoOwnerZOrder) == 0);
            Check((DesktopCardRules.TopmostFlags & DesktopCardRules.SwpNoOwnerZOrder) != 0);

            Check((DesktopCardRules.FilterPosChangingFlags(0, movingZ: false, editing: false) & DesktopCardRules.SwpNoZOrder) != 0);
            Check((DesktopCardRules.FilterPosChangingFlags(0, movingZ: true, editing: false) & DesktopCardRules.SwpNoZOrder) == 0);
            Check((DesktopCardRules.FilterPosChangingFlags(0, movingZ: false, editing: true) & DesktopCardRules.SwpNoZOrder) == 0);
        });

        Test("desktop card Win32: child SetFocus blocks activation under WS_DISABLED and SendToBottom stays above owner", () =>
        {
            Win32CardProbe.VerifyChildFocusGuardAndBottomOrder();
        });
    }

    private static class Win32CardProbe
    {
        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize, style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string cls, IntPtr inst);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int idx, IntPtr val);
        [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);

        private static bool _movingZ;
        private static int _cardActivateCount;
        private static IntPtr _card1, _card2;

        private static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            if (h == _card1 || h == _card2)
            {
                if (m == 0x0006 && (w.ToInt64() & 0xFFFF) != 0) _cardActivateCount++;
                if (m == 0x0046)
                {
                    var wp = Marshal.PtrToStructure<WINDOWPOS>(l);
                    wp.flags = DesktopCardRules.FilterPosChangingFlags(wp.flags, _movingZ, editing: false);
                    Marshal.StructureToPtr(wp, l, false);
                }
            }
            return DefWindowProc(h, m, w, l);
        }

        private static int ZIndex(IntPtr target)
        {
            int i = 0;
            for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, 2))
            {
                if (h == target) return i;
                i++;
            }
            return -1;
        }

        private static void SendToBottom(params IntPtr[] cards)
        {
            _movingZ = true;
            try
            {
                foreach (var c in cards)
                {
                    if ((GetWindowLongPtr(c, DesktopCardRules.GwlExStyle).ToInt64() & DesktopCardRules.WsExTopmost) != 0)
                        SetWindowPos(c, new IntPtr(-2), 0, 0, 0, 0, DesktopCardRules.TopmostFlags);
                    SetWindowPos(c, new IntPtr(1), 0, 0, 0, 0, DesktopCardRules.RestackBottomFlags);
                }
            }
            finally { _movingZ = false; }
        }

        public static void VerifyChildFocusGuardAndBottomOrder()
        {
            static void check(bool ok, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
            {
                if (!ok) throw new Exception($"Assertion failed (probe line {line})");
            }
            const uint wsPopup = 0x80000000u, wsChild = 0x40000000u, wsVisible = 0x10000000u;
            const uint exCard = 0x80u | 0x08000000u; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            WndProcDelegate del = WndProc;
            string cls = "DeskCardsZProbe_" + Guid.NewGuid().ToString("N")[..8];
            var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = del, lpszClassName = cls };
            check(RegisterClassEx(ref wc) != 0);

            IntPtr owner = IntPtr.Zero, norm = IntPtr.Zero, label = IntPtr.Zero, child = IntPtr.Zero;
            _card1 = _card2 = IntPtr.Zero;
            _cardActivateCount = 0;
            try
            {
                owner = CreateWindowEx(0x80u, cls, "MockOwner", wsPopup, -32000, -32000, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                norm = CreateWindowEx(0x80u, cls, "MockNormal", wsPopup, -32000, -32000, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                _card1 = CreateWindowEx(exCard, cls, "Card1", wsPopup, -32000, -32000, 20, 20, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                _card2 = CreateWindowEx(exCard, cls, "Card2", wsPopup, -32000, -32000, 20, 20, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                label = CreateWindowEx(exCard | 0x20u, cls, "Label1", wsPopup, -32000, -32000, 10, 10, _card1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                child = CreateWindowEx(0, cls, "ChildWeb", wsChild | wsVisible, 0, 0, 10, 10, _card2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                SetWindowLongPtr(_card1, -8, owner);
                SetWindowLongPtr(_card2, -8, owner);
                foreach (var w in new[] { owner, norm, _card1, label, _card2 }) ShowWindow(w, 4);

                // 1) WS_DISABLED가 부모에 걸리면 자식의 SetFocus가 실패하고 부모 활성화도 일어나지 않는다.
                long st = GetWindowLongPtr(_card2, DesktopCardRules.GwlStyle).ToInt64();
                SetWindowLongPtr(_card2, DesktopCardRules.GwlStyle, new IntPtr(st | DesktopCardRules.WsDisabled));
                IntPtr focusResult = SetFocus(child);
                check(focusResult == IntPtr.Zero);
                check(_cardActivateCount == 0);
                check(IsWindowEnabled(child));
                SetWindowLongPtr(_card2, DesktopCardRules.GwlStyle, new IntPtr(st));

                // 2) SendToBottom 후 카드와 이름 라벨 모두 owner 위, 일반 창 아래에 놓인다.
                SendToBottom(_card1, _card2);
                int zNorm = ZIndex(norm), zLabel = ZIndex(label), z1 = ZIndex(_card1), z2 = ZIndex(_card2), zOwner = ZIndex(owner);
                check(zNorm >= 0 && zNorm < z1 && zNorm < z2);
                check(zLabel >= 0 && zLabel < z1);
                check(z1 < zOwner && z2 < zOwner);

                // 3) 편집 모드(TOPMOST)로 올렸다 내려도 owner 위로 유지된다.
                _movingZ = true;
                try
                {
                    SetWindowPos(_card1, new IntPtr(-1), 0, 0, 0, 0, DesktopCardRules.TopmostFlags);
                    SetWindowPos(_card2, new IntPtr(-1), 0, 0, 0, 0, DesktopCardRules.TopmostFlags);
                }
                finally { _movingZ = false; }
                SendToBottom(_card1, _card2);
                zNorm = ZIndex(norm); z1 = ZIndex(_card1); z2 = ZIndex(_card2); zOwner = ZIndex(owner);
                check(zNorm < z1 && zNorm < z2 && z1 < zOwner && z2 < zOwner);

                // 4) 강제 활성화(SetActiveWindow)로 순서가 뒤틀려도 SendToBottom이 모든 카드를 owner 위로 복구한다.
                SetActiveWindow(_card2);
                SendToBottom(_card1, _card2);
                zNorm = ZIndex(norm); z1 = ZIndex(_card1); z2 = ZIndex(_card2); zOwner = ZIndex(owner);
                check(zNorm < z1 && zNorm < z2 && z1 < zOwner && z2 < zOwner);
            }
            finally
            {
                foreach (var w in new[] { child, label, _card2, _card1, norm, owner })
                    if (w != IntPtr.Zero) DestroyWindow(w);
                _card1 = _card2 = IntPtr.Zero;
                UnregisterClass(cls, IntPtr.Zero);
                GC.KeepAlive(del);
            }
        }
    }
}