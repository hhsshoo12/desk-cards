using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DeskCards;

internal static partial class Program
{
    private static void RevisionTests(string root)
    {
        Test("revision B1: secondary monitor saved position round trips through a primary-DPI startup", () =>
        {
            // HWND 전 위치 복원은 시스템 DPI를 사용한다. 주 100%, 보조 150%의 계산을 고정해 재현한다.
            var physical = new Native.RECT { Left = 2400, Top = 300, Right = 2700, Bottom = 600 };
            var saved = DeskCard.StoragePoint(physical);
            var restored = saved; // HWND 생성 뒤 SetWindowPos는 물리 좌표를 그대로 받는다.
            Check(restored.X == physical.Left && restored.Y == physical.Top);
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            foreach (int origin in new[] { -2560, 1920 })
            {
                var work = new System.Windows.Rect(origin, -300, 2560, 1440);
                var card = new Native.RECT { Left = origin + 400, Top = 0, Right = origin + 700, Bottom = 300 };
                foreach (bool centered in new[] { true, false })
                {
                    var p = PixelPlacement.Beside(card, work, new System.Windows.Size(400, 300), scale, centered);
                    Check(work.Contains(new System.Windows.Rect(p, new System.Windows.Size(400 * scale, 300 * scale))));
                    if (!centered) Check(p.X == card.Right + 8 * scale);
                }
            }
            string path = Path.Combine(root, "old-dip.json");
            File.WriteAllText(path, "{\"Positions\":{\"old\":[1600,200]}}");
            Check(Config.Load(path).PositionsPx.Count == 0);
        });
        Test("revision A10: enumerated directory attributes preserve dotted folder names", () =>
        {
            string folder = Path.Combine(root, "entry-attributes"); Directory.CreateDirectory(Path.Combine(folder, "folder.ext"));
            File.WriteAllText(Path.Combine(folder, "file.ext"), "test");
            using var group = new GroupModel(folder, iconLoader: _ => null);
            Check(group.Items.Any(e => e.Name == "folder.ext") && group.Items.Any(e => e.Name == "file"));
            using var cancelled = new System.Threading.CancellationTokenSource(); cancelled.Cancel();
            var gone = new ShellEntry(Path.Combine(folder, "gone.ext"), cancelled.Token, isDirectory: true);
            Check(gone.Name == "gone.ext"); // 현재 존재 여부 대신 열거 시점의 속성을 쓴다.
        });
        Test("revision A7: transient replace lock retries and permanent failure logs without losing config", () =>
        {
            string path = Path.Combine(root, "save-retry.json"); var cfg = Config.Load(path); cfg.Save();
            var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = Task.Run(async () => { await Task.Delay(65); locked.Dispose(); });
            cfg.ShowGuides = false;
            Check(cfg.TrySave()); release.GetAwaiter().GetResult();
            Check(!Config.Load(path).ShowGuides);
            using (var permanent = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                cfg.ShowGuides = true; Check(!cfg.TrySave());
                Check(!Config.Load(path).ShowGuides && File.ReadAllText(path + ".log").Contains("설정 저장 실패"));
            }
            Check(!Directory.EnumerateFiles(root, "save-retry.json.*.tmp").Any());
        });
        Test("revision A5: queued settings entry points do nothing after manager shutdown", () =>
        {
            var mgr = new GroupManager(Path.Combine(root, "settings-shutdown"), Config.Load(Path.Combine(root, "settings-shutdown.json")));
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                SettingsWindow.Open(mgr); SettingsWindow.OpenWidget(mgr, "missing.dard");
                SettingsWindow.OpenWidgetCards(mgr); SettingsWindow.OpenBar(mgr); SettingsWindow.OpenGeneral(mgr);
            });
            mgr.Shutdown(); Pump(50);
            Check(!System.Windows.Application.Current.Windows.OfType<SettingsWindow>().Any());
        });
        Test("revision A6: disabled bar stops idle polling and reenabling restarts it", () =>
        {
            var cfg = Config.Load(Path.Combine(root, "bar-poll.json")); cfg.BarEnabled = false;
            var mgr = new GroupManager(Path.Combine(root, "bar-poll"), cfg);
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var timer = EdgeBar.CreatePollingTimer(() => { });
            typeof(EdgeBar).GetField("_mgr", flags)!.SetValue(null, mgr);
            typeof(EdgeBar).GetField("_timer", flags)!.SetValue(null, timer);
            var sync = typeof(EdgeBar).GetMethod("ApplyHotkey", flags)!;
            try
            {
                timer.Start(); sync.Invoke(null, null); Check(!timer.IsEnabled);
                cfg.BarEnabled = true; sync.Invoke(null, null); Check(timer.IsEnabled);
                cfg.BarEnabled = false; sync.Invoke(null, null); Check(!timer.IsEnabled);
            }
            finally { EdgeBar.Stop(); mgr.Shutdown(); }
        });
        Test("revision A2: root watcher is replaced after error and stays stopped after shutdown", () =>
        {
            string folder = Path.Combine(root, "watch-recovery"); Directory.CreateDirectory(folder);
            var mgr = new GroupManager(folder, Config.Load(Path.Combine(root, "watch.json")));
            var field = typeof(GroupManager).GetField("_rootWatcher", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var start = typeof(GroupManager).GetMethod("StartRootWatch", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var error = typeof(GroupManager).GetMethod("OnRootWatchError", BindingFlags.NonPublic | BindingFlags.Instance)!;
            try
            {
                start.Invoke(mgr, null); var before = field.GetValue(mgr);
                error.Invoke(mgr, null); Pump(50);
                var after = (FileSystemWatcher)field.GetValue(mgr)!;
                Check(!ReferenceEquals(before, after) && after.EnableRaisingEvents);
                bool seen = false; after.Created += (_, _) => seen = true;
                Directory.CreateDirectory(Path.Combine(folder, "check-group"));
                WaitUntil(() => seen); Check(seen);
                mgr.Shutdown(); error.Invoke(mgr, null); Pump(50);
                Check(ReferenceEquals(after, field.GetValue(mgr)));
            }
            finally { mgr.Shutdown(); }
        });
        Test("revision A3: double rename failure remains hidden and recovers its data on startup", () =>
        {
            string folder = Path.Combine(root, "rename-recovery"), source = Path.Combine(folder, "docs");
            Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "kept.txt"), "kept");
            int calls = 0;
            try { GroupManager.RenameCaseOnly(source, Path.Combine(folder, "Docs"), (a, b) => { if (++calls > 1) throw new IOException("locked"); Directory.Move(a, b); }); }
            catch (IOException) { }
            var mgr = new GroupManager(folder, Config.Load(Path.Combine(root, "rename.json")));
            try
            {
                var list = typeof(GroupManager).GetMethod("ListGroupFolders", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Check(((System.Collections.Generic.List<string>)list.Invoke(mgr, null)!).Count == 0);
                typeof(GroupManager).GetMethod("RecoverRenamedGroups", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(mgr, null);
                Check(File.ReadAllText(Path.Combine(folder, "복구된 그룹", "kept.txt")) == "kept");
                Check(Directory.GetDirectories(folder).Length == 1);
            }
            finally { mgr.Shutdown(); }
        });
        RecoveryTest("revision A1: isolated browser crash invalidates environment once", async () =>
        {
            string original = AppPaths.WebDataDir;
            string profile = Path.Combine(root, "browser-crash");
            int lost = 0;
            void OnLost(string path) { if (path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)) lost++; }
            BrowserEnvironment.BrowserLost += OnLost;
            try
            {
                AppPaths.WebDataDir = profile;
                var pkg = RecoveryPackage("com.test.browsercrash");
                var env = await BrowserEnvironment.Environment();
                var page = await HiddenPage.Open(env, pkg.Origins[0]);
                await page.Async("const r=await navigator.storage.getDirectory();const h=await r.getFileHandle('saved',{create:true});const w=await h.createWritable();await w.write('yes');await w.close();return true;");
                uint pid = page.Core.BrowserProcessId;
                Check(env.UserDataFolder.StartsWith(profile, StringComparison.OrdinalIgnoreCase));
                using (var browser = System.Diagnostics.Process.GetProcessById((int)pid)) browser.Kill();
                for (int n = 0; n < 400 && lost == 0; n++) await Task.Delay(25);
                try { page.Dispose(); } catch (System.Runtime.InteropServices.COMException) { }
                Check(lost == 1);
                var replacement = await BrowserEnvironment.Environment();
                Check(!ReferenceEquals(env, replacement));
                using var restored = await HiddenPage.Open(replacement, pkg.Origins[0]);
                Check(await restored.Async("const r=await navigator.storage.getDirectory();return await (await (await r.getFileHandle('saved')).getFile()).text();") == "\"yes\"");
                Check(restored.Core.BrowserProcessId != pid);
            }
            finally { BrowserEnvironment.BrowserLost -= OnLost; AppPaths.WebDataDir = original; }
        });
    }
    private sealed class BrokenReadStream : MemoryStream
    {
        public override void CopyTo(Stream destination, int bufferSize) { destination.WriteByte(1); throw new IOException("read failed"); }
    }
}
