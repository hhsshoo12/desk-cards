using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeskCards;

internal static class Program
{
    private static int _failed;
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply();
        // Keep the sandbox as diagnostic evidence. Never touch the user's groups/config.
        string root = Path.Combine(Path.GetTempPath(), "DeskCards-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("Sandbox: " + root);
        Test("null position entries are discarded", () =>
        {
            string path = Path.Combine(root, "null-position.json");
            File.WriteAllText(path, """{"Positions":{"bad":null,"good":[12,34]},"ScaleVersion":2}""");
            var cfg = Config.Load(path);
            Check(!cfg.Positions.ContainsKey("bad") && cfg.Positions["good"][0] == 12);
        });
        Test("null layouts do not crash scale migration", () =>
        {
            string path = Path.Combine(root, "null-layout.json");
            File.WriteAllText(path, """{"Layouts":{"bad":null,"good":{"Cols":3}},"ScaleVersion":0}""");
            var cfg = Config.Load(path);
            Check(!cfg.Layouts.ContainsKey("bad") && cfg.Layouts["good"].Cols == 3);
        });
        Test("case-colliding keys preserve unrelated settings", () =>
        {
            string path = Path.Combine(root, "case.json");
            File.WriteAllText(path, """{"Positions":{"Test":[1,2],"test":[3,4]},"ShowGuides":false}""");
            var cfg = Config.Load(path);
            Check(!cfg.ShowGuides && cfg.Positions.Count == 1);
        });
        Test("hover expand delay is clamped to 0-1 s in 0.1 s steps", () =>
        {
            string path = Path.Combine(root, "hover.json");
            File.WriteAllText(path, """{"HoverExpandDelay":-50}""");
            var low = Config.Load(path);
            File.WriteAllText(path, """{"HoverExpandDelay":99999}""");
            var high = Config.Load(path);
            Check(low.HoverExpandDelay == 0 && high.HoverExpandDelay == 1000 && low.HoverExpand && Config.NormalizeHoverDelay(449) == 400);
        });
        Test("card bar settings are normalized", () =>
        {
            string path = Path.Combine(root, "bar.json");
            File.WriteAllText(path, """{"BarEdge":9,"BarKeys":[164,16,160,0,999,68],"BarSize":80,"BarDelay":2345,"ShowGuides":false}""");
            var cfg = Config.Load(path);
            Check(cfg.BarEdge == ScreenEdge.Right && string.Join(",", cfg.BarKeys) == "18,16,68" && KeyCombo.Text(cfg.BarKeys) == "Alt + Shift + D"
                && string.Join(",", KeyCombo.Clean(new[] { KeyCombo.Ctrl })) == "17,18,68"
                && !KeyCombo.IsValid(new[] { KeyCombo.Ctrl, 0x41, 0x42 }) && KeyCombo.ToHotkey(cfg.BarKeys) == (0x5u, 0x44u) && cfg.BarSize == 33
                && cfg.BarDelay == 2000 && !cfg.ShowGuides && cfg.BarEnabled);
        });
        Test("non-finite zoom is normalized", () => Check(double.IsFinite(new CardLayout { Zoom = double.NaN }.Normalized().Zoom)));
        Test("saved backup recovers interrupted configuration", () =>
        {
            string path = Path.Combine(root, "atomic.json");
            var cfg = Config.Load(path);
            cfg.ShowGuides = false;
            cfg.Save();
            cfg.Save();
            File.WriteAllText(path, "{interrupted");
            Check(!Config.Load(path).ShowGuides);
        });
        Test("ancestor directory drop is rejected", () =>
        {
            string folder = Path.Combine(root, "drop-target");
            Directory.CreateDirectory(folder);
            Check(!FileOps.CanAccept(new DataObject(DataFormats.FileDrop, new[] { root }), folder));
        });
        Test("invalid Windows group names are rejected", () =>
        {
            foreach (string name in new[] { "CON", "con.txt", "LPT1", "COM¹", "..", "hello.", "hello ", "a/b", " " })
                Check(!FileOps.IsValidGroupName(name));
            Check(FileOps.IsValidGroupName("작업 2026") && FileOps.IsValidGroupName("COM10"));
        });
        Test("same-folder move does not rename the file", () =>
        {
            string file = Path.Combine(root, "keep.txt");
            File.WriteAllText(file, "original");
            Check(FileOps.MoveTo(file, root));
            Check(File.ReadAllText(file) == "original" && !File.Exists(Path.Combine(root, "keep (2).txt")));
        });
        Test("drop moves group files without overwriting collisions", () =>
        {
            string groups = Path.Combine(root, "move-groups");
            string source = Path.Combine(groups, "one"), target = Path.Combine(groups, "two");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "file.txt"), "moved");
            File.WriteAllText(Path.Combine(target, "file.txt"), "existing");
            FileOps.AddToGroup(new[] { Path.Combine(source, "file.txt") }, target, groups);
            Check(!File.Exists(Path.Combine(source, "file.txt")));
            Check(File.ReadAllText(Path.Combine(target, "file.txt")) == "existing");
            Check(File.ReadAllText(Path.Combine(target, "file (2).txt")) == "moved");
        });
        Test("ordinary file drop creates a working shortcut and preserves source", () =>
        {
            string file = Path.Combine(root, "shortcut-source.txt"), target = Path.Combine(root, "shortcut-target");
            File.WriteAllText(file, "preserved"); Directory.CreateDirectory(target);
            FileOps.AddToGroup(new[] { file }, target, target);
            Check(File.ReadAllText(file) == "preserved" && new FileInfo(Path.Combine(target, "shortcut-source.lnk")).Length > 0);
        });
        Test("saved item order comes first, new items follow by name", () =>
        {
            var files = new[] { "c.lnk", "A.lnk", "b.lnk", "d.lnk", "gone.lnk" }.Take(4);
            var arranged = GroupModel.Arrange(files, f => f, f => f, new[] { "d.lnk", "gone.lnk", "B.LNK" });
            Check(string.Join(",", arranged) == "d.lnk,b.lnk,A.lnk,c.lnk"
                && string.Join(",", GroupModel.Arrange(files, f => f, f => f, null)) == "A.lnk,b.lnk,c.lnk,d.lnk");
        });
        Test("item order is saved and follows group rename", () =>
        {
            var cfg = Config.Load(Path.Combine(root, "order.json"));
            var mgr = new GroupManager(Path.Combine(root, "order-groups"), cfg);
            try
            {
                mgr.Start();
                var card = mgr.Cards.Single();
                File.WriteAllText(Path.Combine(card.Group.Folder, "x.txt"), "");
                File.WriteAllText(Path.Combine(card.Group.Folder, "y.txt"), "");
                card.Group.Reload();
                mgr.SetOrder(card.Group, card.Group.Items.AsEnumerable().Reverse());
                bool reversed = card.Group.Items[0].Name == "y";
                Check(mgr.RenameGroup(card, "순서"));
                var renamed = Config.Load(Path.Combine(root, "order.json"));
                Check(reversed && renamed.Orders.TryGetValue("순서", out var order) && order[0] == "y.txt");
            }
            finally { mgr.Shutdown(); }
        });
        Test("queued watcher callback cannot revive a disposed group", () =>
        {
            var group = new GroupModel(root);
            int changes = 0;
            group.Changed += () => changes++;
            typeof(GroupModel).GetMethod("Bump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(group, null);
            group.Dispose();
            Pump(400);
            Check(changes == 0);
        });
        Test("grid expansion stays inside monitor work area", () =>
        {
            var cfg = Config.Load(Path.Combine(root, "screen.json"));
            cfg.CellSize = 72; cfg.ScaleVersion = 2; cfg.DefaultZoom = 4; cfg.FixedScale = 1;
            var mgr = new GroupManager(Path.Combine(root, "groups"), cfg);
            try
            {
                mgr.Start();
                var card = mgr.Cards.Single();
                card.SetGrid(8, 8);
                card.UpdateLayout();
                var handle = new WindowInteropHelper(card).Handle;
                Native.GetWindowRect(handle, out var r);
                var wa = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                Check(r.Left >= wa.Left && r.Top >= wa.Top && r.Right <= wa.Right + 1 && r.Bottom <= wa.Bottom + 1);
            }
            finally { mgr.Shutdown(); }
        });
        Test("live rename preserves layout, identity, and file watching", () =>
        {
            string groups = Path.Combine(root, "rename-groups");
            Directory.CreateDirectory(Path.Combine(groups, "before"));
            var cfg = Config.Load(Path.Combine(root, "rename.json"));
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                var card = mgr.Cards.Single();
                card.SetGrid(3, 2);
                Directory.Move(card.Group.Folder, Path.Combine(groups, "after"));
                Pump(800);
                Check(mgr.Cards.Single() == card && card.Group.Name == "after" && card.CurrentLayout.Cols == 3);
                Check(cfg.Layouts.ContainsKey("after") && !cfg.Layouts.ContainsKey("before"));
                File.WriteAllText(Path.Combine(card.Group.Folder, "new.txt"), "new");
                Pump(700);
                Check(card.Group.Items.Count == 1);
                Directory.CreateDirectory(Path.Combine(groups, "AFTER.~tmp"));
                Check(mgr.RenameGroup(card, "AFTER"));
                Pump(500);
                Check(card.Group.Name == "AFTER" && Directory.Exists(Path.Combine(groups, "AFTER.~tmp")));
            }
            finally { mgr.Shutdown(); }
        });
        Test("settings, edit bar, and expanded window lifecycle", () =>
        {
            var cfg = Config.Load(Path.Combine(root, "windows.json"));
            var mgr = new GroupManager(Path.Combine(root, "window-groups"), cfg);
            try
            {
                mgr.Start();
                var card = mgr.Cards.Single();
                SettingsWindow.Open(mgr, card);
                Pump(100);
                Check(app.Windows.OfType<SettingsWindow>().Count() == 1);
                var settings = app.Windows.OfType<SettingsWindow>().Single();
                settings.Width = settings.MinWidth;
                settings.UpdateLayout();
                Test("settings labels remain readable at minimum window width", () =>
                    Check(Visuals<TextBlock>(settings).Single(t => t.Text == "이름").ActualWidth >= 60));
                mgr.BeginEditMode(card);
                Check(mgr.Editing && mgr.Selected == card && card.IsEditing);
                card.SetGrid(4, 2);
                mgr.EndEditMode();
                ExpandedWindow.Open(card, mgr, editTitle: true);
                Pump(100);
                Check(app.Windows.OfType<ExpandedWindow>().Count() == 1);
                ExpandedWindow.CloseFor(card);
                foreach (var window in app.Windows.OfType<SettingsWindow>().ToArray()) window.Close();
                Pump(100);
                Check(!app.Windows.OfType<ExpandedWindow>().Any() && !app.Windows.OfType<EditBar>().Any());
            }
            finally { mgr.Shutdown(); }
        });
        UpdateTests(root);
        Console.WriteLine($"Failures: {_failed}");
        app.Shutdown();
        return _failed == 0 ? 0 : 1;
    }
    // ----- 앱 자체 업데이트: 임시 설치 폴더와 가짜 네트워크만 쓴다 -----

    private const string ReleasesJson = """
        [
          {"tag_name":"installer-v0.9.0","draft":false,"prerelease":false,"assets":[]},
          {"tag_name":"app-v0.10.0","draft":true,"prerelease":false,"assets":[{"name":"DeskCards-win-x64.zip","browser_download_url":"https://x/a"},{"name":"DeskCards-win-x64.zip.sha256","browser_download_url":"https://x/b"}]},
          {"tag_name":"app-v0.3.0","draft":false,"prerelease":true,"assets":[{"name":"DeskCards-win-x64.zip","browser_download_url":"https://x/a"},{"name":"DeskCards-win-x64.zip.sha256","browser_download_url":"https://x/b"}]},
          {"tag_name":"app-v0.2.9","draft":false,"prerelease":false,"assets":[{"name":"DeskCards-win-x64.zip","browser_download_url":"https://x/a"}]},
          {"tag_name":"app-v0.2.10","draft":false,"prerelease":false,"assets":[{"name":"DeskCards-win-x64.zip","browser_download_url":"https://x/zip"},{"name":"DeskCards-win-x64.zip.sha256","browser_download_url":"https://x/hash"}]},
          {"tag_name":"app-v0.2.1","draft":false,"prerelease":false,"assets":[{"name":"DeskCards-win-x64.zip","browser_download_url":"https://x/zip"},{"name":"DeskCards-win-x64.zip.sha256","browser_download_url":"https://x/hash"}]}
        ]
        """;

    private sealed class FakeUpdate
    {
        public string Install = "", Zip = "", Hash = "";
        public string? Relaunched;
        public Version? Registered;
        public UpdateHost Host(bool installed = true) => new()
        {
            InstallDir = Install,
            Installed = installed,
            ReadText = (url, _) => Task.FromResult(url == UpdatePackage.ReleasesUrl ? ReleasesJson : Hash),
            Download = (_, path, progress, _) => { File.Copy(Zip, path); progress.Report(1); return Task.CompletedTask; },
            SetInstalledVersion = v => Registered = v,
            Relaunch = exe => Relaunched = exe,
            LogPath = Path.Combine(Install, "..", "update.log"),
        };
    }

    private static FakeUpdate UpdateSandbox(string root, string name, string version = "0.2.10", string? extra = null, bool badHash = false)
    {
        string dir = Path.Combine(root, name);
        var f = new FakeUpdate { Install = Path.Combine(dir, "install"), Zip = Path.Combine(dir, "pkg.zip") };
        Directory.CreateDirectory(f.Install);
        File.WriteAllText(Path.Combine(f.Install, "DeskCards.exe"), "old app");
        using (var zip = System.IO.Compression.ZipFile.Open(f.Zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("DeskCards.exe").Open())) w.Write("new app");
            using (var w = new StreamWriter(zip.CreateEntry("version.txt").Open())) w.Write(version);
            if (extra != null) using (var w = new StreamWriter(zip.CreateEntry(extra).Open())) w.Write("x");
        }
        using var s = File.OpenRead(f.Zip);
        f.Hash = (badHash ? new string('0', 64) : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s))) + "  DeskCards-win-x64.zip";
        return f;
    }

    private static Updater Downloaded(FakeUpdate f, Config cfg, bool installed = true)
    {
        // UI 스레드 없이 돌려서 이어지는 작업이 멈추지 않게 한다.
        return Task.Run(async () =>
        {
            var u = new Updater(cfg, f.Host(installed), new Version(0, 2, 1));
            await u.CheckAsync();
            await u.DownloadAsync();
            return u;
        }).GetAwaiter().GetResult();
    }

    private static void UpdateTests(string root)
    {
        Test("update picks highest published app release", () =>
            Check(UpdatePackage.Latest(ReleasesJson)?.Version == new Version(0, 2, 10)));
        Test("update downloads, verifies and stages the new version", () =>
        {
            var f = UpdateSandbox(root, "upd-ok");
            var cfg = Config.Load(Path.Combine(root, "upd-ok", "config.json"));
            var u = Downloaded(f, cfg);
            Check(u.State == UpdateState.Ready && cfg.PendingUpdate == "0.2.10");
            Check(File.ReadAllText(Path.Combine(f.Install, "update", "0.2.10", "DeskCards.exe")) == "new app");
            Check(File.ReadAllText(Path.Combine(f.Install, "DeskCards.exe")) == "old app");
            Check(Config.Load(Path.Combine(root, "upd-ok", "config.json")).PendingUpdate == "0.2.10");
        });
        foreach (var (name, sandbox) in new (string, Func<FakeUpdate>)[]
        {
            ("bad hash", () => UpdateSandbox(root, "upd-hash", badHash: true)),
            ("version mismatch", () => UpdateSandbox(root, "upd-ver", version: "0.2.9")),
            ("unsafe path", () => UpdateSandbox(root, "upd-slip", extra: "../evil.txt")),
        })
            Test($"update rejects {name} and leaves nothing staged", () =>
            {
                var f = sandbox();
                var cfg = Config.Load(Path.Combine(f.Install, "..", "config.json"));
                var u = Downloaded(f, cfg);
                Check(u.State == UpdateState.Failed && cfg.PendingUpdate == null);
                Check(!Directory.Exists(Path.Combine(f.Install, "update")) && !File.Exists(Path.Combine(f.Install, "..", "evil.txt")));
                Check(File.ReadAllText(Path.Combine(f.Install, "DeskCards.exe")) == "old app");
            });
        Test("pending update swaps executables and relaunches", () =>
        {
            var f = UpdateSandbox(root, "upd-apply");
            var cfg = Config.Load(Path.Combine(root, "upd-apply", "config.json"));
            Downloaded(f, cfg);
            Check(Updater.ApplyPending(cfg, f.Host(), new Version(0, 2, 1)));
            string exe = Path.Combine(f.Install, "DeskCards.exe");
            Check(File.ReadAllText(exe) == "new app" && File.ReadAllText(exe + ".old") == "old app");
            Check(f.Relaunched == exe && f.Registered == new Version(0, 2, 10) && cfg.PendingUpdate == null);
            UpdatePackage.Cleanup(f.Install);
            Check(!File.Exists(exe + ".old") && !Directory.Exists(Path.Combine(f.Install, "update")));
        });
        Test("pending update not newer than running version is discarded", () =>
        {
            var f = UpdateSandbox(root, "upd-stale");
            var cfg = Config.Load(Path.Combine(root, "upd-stale", "config.json"));
            Downloaded(f, cfg);
            Check(!Updater.ApplyPending(cfg, f.Host(), new Version(0, 2, 10)));
            Check(cfg.PendingUpdate == null && f.Relaunched == null && !Directory.Exists(Path.Combine(f.Install, "update")));
        });
        Test("development build checks but never downloads or applies", () =>
        {
            var f = UpdateSandbox(root, "upd-dev");
            var cfg = Config.Load(Path.Combine(root, "upd-dev", "config.json"));
            var u = Downloaded(f, cfg, installed: false);
            Check(u.State == UpdateState.Available && !u.CanUpdate && cfg.PendingUpdate == null);
            cfg.PendingUpdate = "0.2.10";
            Check(!Updater.ApplyPending(cfg, f.Host(installed: false), new Version(0, 2, 1)) && f.Relaunched == null);
            Check(File.ReadAllText(Path.Combine(f.Install, "DeskCards.exe")) == "old app");
        });
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }
    private static void Test(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
}
