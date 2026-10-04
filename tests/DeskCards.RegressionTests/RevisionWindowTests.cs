using System;
using System.IO;
using System.Linq;
using System.Windows;
using DeskCards;

internal static partial class Program
{
    private static void RevisionWindowTests(string root)
    {
        Test("revision UI B1: physical position survives HWND recreation and config reload", () =>
        {
            string folder = Path.Combine(root, "position-restart");
            Directory.CreateDirectory(Path.Combine(folder, "position"));
            string configPath = Path.Combine(root, "position-restart.json");
            var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var expected = new Point(area.Left + 80, area.Top + 80);
            var cfg = Config.Load(configPath);
            cfg.PositionsPx["position"] = new[] { expected.X, expected.Y };
            cfg.Save();
            for (int run = 0; run < 2; run++)
            {
                var mgr = new GroupManager(folder, Config.Load(configPath));
                try
                {
                    mgr.Start(); Pump(100);
                    Check(mgr.Cards.Single().PhysicalPosition == expected);
                    Check(Config.Load(configPath).PositionsPx["position"].SequenceEqual(new[] { expected.X, expected.Y }));
                }
                finally { mgr.Shutdown(); }
            }
        });
        Test("revision UI A1: one browser crash recreates every affected card once", () =>
        {
            string original = AppPaths.WebDataDir;
            string profile = Path.Combine(root, "cards-browser-crash");
            string groups = Path.Combine(root, "cards-browser-crash-groups");
            Directory.CreateDirectory(groups);
            string path = Path.Combine(groups, "crash.dard");
            File.WriteAllBytes(path, Dard(("manifest.json", ClockManifest), ("card.html", "<p>recovered")));
            var pkg = DardPackage.Load(path);
            var cfg = Config.Load(Path.Combine(root, "cards-browser-crash.json"));
            cfg.Dards[pkg.Id] = new DardApproval { Allowed = true, Hash = pkg.Hash };
            AppPaths.WebDataDir = profile;
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                var before = mgr.AllCards.OfType<DardWindow>().ToList();
                Check(before.Count == 2);
                var first = WebOf(before[0]);
                uint pid = first.CoreWebView2.BrowserProcessId;
                Check(WebOf(before[1]).CoreWebView2.BrowserProcessId == pid);
                Check(first.CoreWebView2.Environment.UserDataFolder.StartsWith(profile, StringComparison.OrdinalIgnoreCase));
                using (var process = System.Diagnostics.Process.GetProcessById((int)pid)) process.Kill();
                WaitUntilLong(() => mgr.AllCards.OfType<DardWindow>().Count() == 2 && !mgr.AllCards.OfType<DardWindow>().Any(before.Contains), 15000);
                var after = mgr.AllCards.OfType<DardWindow>().ToList();
                Check(after.Count == 2 && !after.Any(before.Contains));
                foreach (var card in after)
                {
                    var web = WebOf(card);
                    Check(web.CoreWebView2.BrowserProcessId != pid);
                    Check(Eval(web, "document.body.textContent.trim()") == "\"recovered\"");
                }
                Pump(500);
                Check(mgr.AllCards.OfType<DardWindow>().SequenceEqual(after));
            }
            finally { mgr.Shutdown(); AppPaths.WebDataDir = original; }
        });
    }
}
