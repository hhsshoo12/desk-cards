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
        Test("revision A10: enumerated directory attributes preserve dotted folder names", () =>
        {
            string folder = Path.Combine(root, "entry-attributes"); Directory.CreateDirectory(Path.Combine(folder, "folder.ext"));
            File.WriteAllText(Path.Combine(folder, "file.ext"), "test");
            using var group = new GroupModel(folder, iconLoader: _ => null);
            Check(group.Items.Any(e => e.Name == "folder.ext") && group.Items.Any(e => e.Name == "file"));
            using var cancelled = new System.Threading.CancellationTokenSource(); cancelled.Cancel();
            var gone = new ShellEntry(Path.Combine(folder, "gone.ext"), cancelled.Token, isDirectory: true);
            Check(gone.Name == "gone.ext"); // 存在確認に依存しない列挙時点の属性
        });
        RecoveryTest("revision A8: nested template scripts run when cloned and foreign scripts remain blocked", async () =>
        {
            const string html = """
                <template id="outer"><template id="inner"><script>window.nested='한글';</script></template></template>
                <script>document.body.append(document.getElementById('outer').content.cloneNode(true));document.body.append(document.getElementById('inner').content.cloneNode(true));</script>
                """;
            using var page = await HiddenPage.Open(await DardStorage.Environment(false), "https://template.card.desk", "<body>" + html,
                "Content-Security-Policy: " + DardView.ContentPolicy(System.Text.Encoding.UTF8.GetBytes("<body>" + html), false));
            Check(await page.Eval("window.nested") == "\"한글\"");
            Check(await page.Eval("const s=document.createElement('script');s.textContent='window.foreign=1';document.body.append(s);!window.foreign") == "true");
        });
        Test("revision A9: malformed and unsupported archives report a package error", () =>
        {
            byte[] unsupported = Dard(("manifest.json", ClockManifest), ("card.html", "<p>test"));
            for (int n = 0; n + 12 < unsupported.Length; n++)
            {
                uint sig = BitConverter.ToUInt32(unsupported, n);
                int offset = sig == 0x04034b50 ? 8 : sig == 0x02014b50 ? 10 : -1;
                if (offset >= 0) { unsupported[n + offset] = 99; unsupported[n + offset + 1] = 0; }
            }
            foreach (byte[] bytes in new[] { new byte[] { 0, 1, 2 }, unsupported, unsupported.Take(40).ToArray() })
            {
                bool caught = false;
                try { DardPackage.Parse(bytes, "broken.dard"); } catch (DardException ex) { caught = ex.Message.Contains("손상"); }
                Check(caught);
            }
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
        Test("revision A4: failed large response copy immediately deletes its temporary file", () =>
        {
            string temp = Path.Combine(root, "response-temp"); Directory.CreateDirectory(temp);
            using var broken = new BrokenReadStream();
            bool failed = false;
            try { using var unused = DardStorage.CopyResponse(broken, 65L * 1024 * 1024, temp); }
            catch (IOException) { failed = true; }
            Check(failed && Directory.GetFiles(temp).Length == 0);
            using (var input = new MemoryStream(new byte[] { 1, 2, 3 }))
            using (var copied = DardStorage.CopyResponse(input, 65L * 1024 * 1024, temp))
                Check(copied.ReadByte() == 1 && Directory.GetFiles(temp).Length == 1);
            Check(Directory.GetFiles(temp).Length == 0);
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
                File.WriteAllText(Path.Combine(folder, "check.txt"), "test");
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
        RecoveryTest("revision A1: isolated browser crash invalidates environment and keeper once", async () =>
        {
            string original = AppPaths.WebDataDir;
            string profile = Path.Combine(root, "browser-crash");
            int lost = 0;
            void OnLost(string path) { if (path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)) lost++; }
            DardStorage.BrowserLost += OnLost;
            try
            {
                AppPaths.WebDataDir = profile;
                var pkg = RecoveryPackage("com.test.browsercrash");
                await DardStorage.PrepareAsync(pkg);
                var env = await DardStorage.Environment(false);
                var page = await HiddenPage.Open(env, pkg.Origins[0]);
                await page.Async("const r=await navigator.storage.getDirectory();const h=await r.getFileHandle('saved',{create:true});const w=await h.createWritable();await w.write('yes');await w.close();return true;");
                uint pid = page.Core.BrowserProcessId;
                Check(env.UserDataFolder.StartsWith(profile, StringComparison.OrdinalIgnoreCase));
                using (var browser = System.Diagnostics.Process.GetProcessById((int)pid)) browser.Kill();
                for (int n = 0; n < 400 && lost == 0; n++) await Task.Delay(25);
                try { page.Dispose(); } catch (System.Runtime.InteropServices.COMException) { }
                Check(lost == 1);
                var replacement = await DardStorage.Environment(false);
                Check(!ReferenceEquals(env, replacement));
                await DardStorage.PrepareAsync(pkg); // 죽은 keeper를 재사용하면 실패한다.
                using var restored = await HiddenPage.Open(replacement, pkg.Origins[0]);
                Check(await restored.Async("const r=await navigator.storage.getDirectory();return await (await (await r.getFileHandle('saved')).getFile()).text();") == "\"yes\"");
                Check(restored.Core.BrowserProcessId != pid);
            }
            finally { DardStorage.BrowserLost -= OnLost; AppPaths.WebDataDir = original; }
        });
    }
    private sealed class BrokenReadStream : MemoryStream
    {
        public override void CopyTo(Stream destination, int bufferSize) { destination.WriteByte(1); throw new IOException("read failed"); }
    }
}
