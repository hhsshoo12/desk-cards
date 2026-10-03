using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DeskCards;

internal static partial class Program
{
    private static void ReportTests(string root)
    {
        RecoveryTest("report 03: browser rejects SSH and SMTP ports before proxy tunnelling", async () =>
        {
            using var page = await HiddenPage.Open(await DardStorage.Environment(true), "https://report-ports.card.desk", interceptAll: false);
            await page.Core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            var errors = new System.Collections.Generic.List<string>();
            page.Core.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_, e) =>
            {
                using var json = System.Text.Json.JsonDocument.Parse(e.ParameterObjectAsJson);
                errors.Add(json.RootElement.GetProperty("errorText").GetString() ?? "");
            };
            await page.Eval("for(const p of [22,25]) fetch('https://8.8.8.8:'+p+'/',{mode:'no-cors'}).catch(()=>{});");
            for (int i = 0; i < 100 && errors.Count < 2; i++) await Task.Delay(20);
            Console.WriteLine("  browser ports: " + string.Join(", ", errors));
            Check(errors.Count == 2 && errors.All(e => e == "net::ERR_UNSAFE_PORT"));
        });
        Test("report 09: completed icon updates bindings on the UI thread", () =>
        {
            string folder = Path.Combine(root, "report-icon-complete"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, ".env"), "test");
            var icon = new System.Windows.Media.DrawingImage(); icon.Freeze();
            using var group = new GroupModel(folder, iconLoader: _ => icon);
            var entry = group.Items.Single(); Check(entry.Name == ".env");
            var image = new System.Windows.Controls.Image();
            image.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding(nameof(ShellEntry.Icon)) { Source = entry });
            int ui = Environment.CurrentManagedThreadId, notified = 0;
            entry.PropertyChanged += (_, _) => notified = Environment.CurrentManagedThreadId;
            WaitUntil(() => ReferenceEquals(image.Source, icon));
            Check(ReferenceEquals(image.Source, icon) && notified == ui);
        });
        Test("report 10: measure idle polling frequency without claiming battery impact", () =>
        {
            int ticks = 0;
            var timer = EdgeBar.CreatePollingTimer(() => ticks++);
            var watch = Stopwatch.StartNew(); timer.Start();
            try { Pump(1000); } finally { timer.Stop(); }
            Console.WriteLine($"  idle poll: {ticks / watch.Elapsed.TotalSeconds:F1} ticks/sec; power/C-state not measured");
            Check(ticks > 0);
        });
        Test("report 09: slow shell icon extraction cannot block the UI or revive a disposed group", () =>
        {
            string folder = Path.Combine(root, "report-icons"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "shortcut.lnk"), "test");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int ui = Environment.CurrentManagedThreadId, worker = ui;
            var clock = Stopwatch.StartNew();
            using var group = new GroupModel(folder, iconLoader: _ =>
            {
                worker = Environment.CurrentManagedThreadId; entered.Set();
                if (!release.Wait(5000)) throw new TimeoutException();
                return ShellIcons.Placeholder;
            });
            try
            {
                Check(clock.Elapsed < TimeSpan.FromSeconds(1));
                Check(entered.Wait(2000) && worker != ui);
                var old = group.Items.Single(); int changed = 0;
                old.PropertyChanged += (_, _) => changed++;
                group.Dispose(); release.Set(); Pump(100);
                Check(changed == 0);
            }
            finally { release.Set(); }
        });
        Test("report 12: simultaneous card recovery shares one timer", () =>
        {
            var mgr = new GroupManager(Path.Combine(root, "no-recovery-root"), Config.Load(Path.Combine(root, "recovery-timer.json")));
            var schedule = typeof(GroupManager).GetMethod("ScheduleDesktopRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var field = typeof(GroupManager).GetField("_desktopRetry", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                schedule.Invoke(mgr, null); var first = field.GetValue(mgr);
                for (int n = 0; n < 20; n++) schedule.Invoke(mgr, null);
                Check(first != null && ReferenceEquals(first, field.GetValue(mgr)));
                mgr.Shutdown();
                Check(!((System.Windows.Threading.DispatcherTimer)first!).IsEnabled);
                schedule.Invoke(mgr, null); Check(field.GetValue(mgr) == null);
            }
            finally { mgr.Shutdown(); }
        });
        RecoveryTest("report 11: storage I/O failure returns unknown usage", async () =>
        {
            string original = AppPaths.WebDataDir;
            try
            {
                AppPaths.WebDataDir = Path.Combine(root, "unwritable-profile"); File.WriteAllText(AppPaths.WebDataDir, "file blocks directory");
                Check(await DardStorage.UsageAsync(false, "https://failure.card.desk") == null);
            }
            finally { AppPaths.WebDataDir = original; }
        });
        RecoveryTest("report 03: traffic keeps relay alive and EOF ends its peer", async () =>
        {
            using var first = await LocalPair.Create(); using var second = await LocalPair.Create();
            var relay = DardProxy.RelayAsync(first.Inside.GetStream(), second.Inside.GetStream(), TimeSpan.FromSeconds(1));
            try
            {
                for (int n = 0; n < 6; n++)
                {
                    await first.Outside.GetStream().WriteAsync(new byte[] { (byte)n });
                    byte[] received = new byte[1];
                    Check(await second.Outside.GetStream().ReadAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == 1 && received[0] == n);
                    await Task.Delay(250); Check(!relay.IsCompleted);
                }
                first.Outside.Dispose(); await relay.WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { first.Dispose(); second.Dispose(); }
        });
        RecoveryTest("report 03: idle tunnel relay releases both directions", async () =>
        {
            using var first = await LocalPair.Create();
            using var second = await LocalPair.Create();
            var relay = DardProxy.RelayAsync(first.Inside.GetStream(), second.Inside.GetStream(), TimeSpan.FromMilliseconds(150));
            bool ended = await Task.WhenAny(relay, Task.Delay(2000)) == relay;
            first.Dispose(); second.Dispose();
            try { await relay; } catch (IOException) { }
            Check(ended);
        });
        Test("report 04: failed group deletion restores already moved files", () =>
        {
            string folder = Path.Combine(root, "report-group"), target = Path.Combine(root, "report-desktop");
            Directory.CreateDirectory(folder); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(folder, "a.txt"), "a"); File.WriteAllText(Path.Combine(folder, "b.txt"), "b");
            File.WriteAllText(Path.Combine(target, "a.txt"), "desktop");
            string lockedPath = Directory.GetFileSystemEntries(folder)[1];
            using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            bool failed = false;
            try { FileOps.MoveContentsAndDelete(folder, target); } catch (IOException) { failed = true; }
            Check(failed && File.ReadAllText(Path.Combine(folder, "a.txt")) == "a" && File.Exists(lockedPath));
            Check(Directory.GetFiles(target).Length == 1 && File.ReadAllText(Path.Combine(target, "a.txt")) == "desktop");
        });
        Test("report 04: successful deletion preserves colliding files and nested directories", () =>
        {
            string folder = Path.Combine(root, "report-group-ok"), target = Path.Combine(root, "report-desktop-ok");
            Directory.CreateDirectory(Path.Combine(folder, "nested")); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(folder, "nested", "a.txt"), "nested");
            File.WriteAllText(Path.Combine(folder, ".env"), "moved"); File.WriteAllText(Path.Combine(target, ".env"), "existing");
            FileOps.MoveContentsAndDelete(folder, target);
            Check(!Directory.Exists(folder) && File.ReadAllText(Path.Combine(target, ".env (2)")) == "moved");
            Check(File.ReadAllText(Path.Combine(target, ".env")) == "existing" && File.ReadAllText(Path.Combine(target, "nested", "a.txt")) == "nested");
        });
        Test("report 01: malformed bridge messages never escape the host", () =>
        {
            var mgr = new GroupManager(Path.Combine(root, "report-missing"), Config.Load(Path.Combine(root, "report.json")));
            var runtime = new DardRuntime(RecoveryPackage("com.test.messages"), mgr, default);
            var view = new DardView(runtime, "main", false, new Size(100, 100));
            try
            {
                foreach (string json in new[] { "null", "[]", "{", "{\"t\":{}}", "{\"t\":123}",
                    "{\"t\":\"call\",\"id\":1,\"fn\":{}}",
                    "{\"t\":\"call\",\"id\":1,\"fn\":\"cards.post\",\"args\":12345}",
                    "{\"t\":\"call\",\"id\":2,\"fn\":\"openUrl\",\"args\":{}}",
                    "{\"t\":\"call\",\"id\":3,\"fn\":\"cards.post\",\"args\":{\"to\":[]}}",
                    "{\"t\":\"call\",\"id\":4,\"fn\":\"cards.post\",\"args\":{\"to\":\"main\",\"data\":{\"ok\":true}}}" })
                    view.ReceiveMessage(json);
            }
            finally { view.Close(); mgr.Shutdown(); }
        });
        RecoveryTest("report 02: approved inline script runs but dynamic source does not", async () =>
        {
            const string html = """
                <!doctype html><script>
                window.approved = true;
                const s = document.createElement('script');
                s.textContent = 'window.injected = true'; document.head.appendChild(s);
                try { new Function('window.evaluated=true')(); } catch {}
                </script><button onclick="window.handler=true">test</button>
                """;
            using var page = await HiddenPage.Open(await DardStorage.Environment(false), "https://report.card.desk", html,
                "Content-Security-Policy: " + DardView.ContentPolicy(Encoding.UTF8.GetBytes(html), true));
            Check(await page.Eval("window.approved===true && !window.injected && !window.evaluated") == "true");
            Check(await page.Eval("document.querySelector('button').click(); !window.handler") == "true");
        });
        RecoveryTest("report 02: HTML parsing and line endings preserve approved scripts", async () =>
        {
            string html = "<!-- <script>window.fake=true</script> --><textarea><script>window.textarea=true</script></textarea>" +
                "<SCRIPT data-x='>'>\r\nwindow.unicode='한글';\rwindow.lines=true;\n</SCRIPT>" +
                "<script type='module'>window.moduleOK=true;</script>" +
                "<script src='https://untrusted.example/script.js'></script>";
            var bytes = Encoding.UTF8.GetBytes(html);
            using var page = await HiddenPage.Open(await DardStorage.Environment(false), "https://report-parser.card.desk", html,
                "Content-Security-Policy: " + DardView.ContentPolicy(bytes, false));
            Check(await page.Eval("window.unicode==='한글' && window.lines===true && window.moduleOK===true && !window.fake && !window.textarea") == "true");
        });
        Test("report 05: dotfile collisions keep the filename stem", () =>
        {
            string dir = Path.Combine(root, "dotfiles"); Directory.CreateDirectory(dir);
            foreach (string name in new[] { ".gitignore", ".env", "normal.txt", ".env.local" }) File.WriteAllText(Path.Combine(dir, name), "keep");
            Check(Path.GetFileName(FileOps.Unique(dir, ".gitignore")) == ".gitignore (2)");
            Check(Path.GetFileName(FileOps.Unique(dir, ".env")) == ".env (2)");
            Check(Path.GetFileName(FileOps.Unique(dir, "normal.txt")) == "normal (2).txt");
            Check(Path.GetFileName(FileOps.Unique(dir, ".env.local")) == ".env (2).local");
        });
        Test("report 06: in-app update synchronizes version metadata", () =>
        {
            var f = UpdateSandbox(root, "report-version");
            var cfg = Config.Load(Path.Combine(root, "report-version.json"));
            File.WriteAllText(Path.Combine(f.Install, "version.txt"), "0.2.1");
            Downloaded(f, cfg);
            Check(Updater.ApplyPending(cfg, f.Host(), new Version(0, 2, 1)));
            Check(File.ReadAllText(Path.Combine(f.Install, "version.txt")).Trim() == "0.2.10");
        });
        Test("report 06: metadata write failure restores executable and staged update", () =>
        {
            var f = UpdateSandbox(root, "report-version-locked");
            var cfg = Config.Load(Path.Combine(root, "report-version-locked.json"));
            string metadata = Path.Combine(f.Install, "version.txt"); File.WriteAllText(metadata, "0.2.1");
            Downloaded(f, cfg);
            using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { Updater.ApplyPending(cfg, f.Host(), new Version(0, 2, 1)); } catch (IOException) { failed = true; }
                Check(failed && f.Relaunched == null && cfg.PendingUpdate == "0.2.10");
                Check(File.ReadAllText(Path.Combine(f.Install, "DeskCards.exe")) == "old app" && File.ReadAllText(metadata) == "0.2.1");
            }
            Check(Updater.ApplyPending(cfg, f.Host(), new Version(0, 2, 1)));
        });
        Test("report 08: unrelated PID cannot stall update startup", () =>
        {
            var watch = Stopwatch.StartNew();
            typeof(App).GetMethod("WaitForUpdatedFrom", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { new[] { "--updated-from", Environment.ProcessId.ToString() } });
            Check(watch.Elapsed < TimeSpan.FromSeconds(1));
            using var unrelated = Process.GetProcessesByName("explorer").FirstOrDefault();
            if (unrelated != null)
            {
                watch.Restart();
                typeof(App).GetMethod("WaitForUpdatedFrom", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, new object[] { new[] { "--updated-from", unrelated.Id.ToString() } });
                Check(watch.Elapsed < TimeSpan.FromSeconds(1));
            }
        });
    }

    private sealed class LocalPair : IDisposable
    {
        public TcpClient Inside { get; }
        public TcpClient Outside { get; }
        private LocalPair(TcpClient inside, TcpClient outside) { Inside = inside; Outside = outside; }
        public static async Task<LocalPair> Create()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var outside = new TcpClient();
            await outside.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            return new LocalPair(await listener.AcceptTcpClientAsync(), outside);
        }
        public void Dispose() { Inside.Dispose(); Outside.Dispose(); }
    }
}
