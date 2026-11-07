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
using DeskCards;

internal static partial class Program
{
    private static void ReportTests(string root)
    {
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
