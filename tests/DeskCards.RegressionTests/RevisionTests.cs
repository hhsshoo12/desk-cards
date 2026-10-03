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
