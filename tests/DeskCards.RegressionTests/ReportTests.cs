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
