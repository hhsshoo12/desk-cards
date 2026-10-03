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

internal static partial class Program
{
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
}
