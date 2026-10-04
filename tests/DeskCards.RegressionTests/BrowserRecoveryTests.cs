using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DeskCards;

internal static partial class Program
{
    private static void BrowserRecoveryTests(string root)
    {
        Test("browser recovery policy: rolling minute, fourth exit and persistent stop", () =>
        {
            var policy = new BrowserRecoveryPolicy();
            foreach (long now in new long[] { 0, 10_000, 20_000 }) { policy.RecordExit(now); Check(!policy.Blocked); }
            policy.RecordExit(59_999); Check(policy.Blocked);
            policy.RecordExit(120_000); Check(policy.Blocked); // 시간이 지나도 저절로 재시도하지 않는다.
            var spaced = new BrowserRecoveryPolicy();
            foreach (long now in new long[] { 0, 10_000, 20_000, 60_000 }) { spaced.RecordExit(now); Check(!spaced.Blocked); }
            spaced.RecordExit(60_001); Check(spaced.Blocked);
            Check(!new BrowserRecoveryPolicy().Blocked); // 새 앱 세션은 새 정책으로 시작한다.
        });

        RecoveryTest("browser recovery hidden: replacements share budget, duplicate exits ignored and reset isolated", async () =>
        {
            string original = AppPaths.WebDataDir;
            string profile = Path.Combine(root, "browser-recovery-budget");
            AppPaths.WebDataDir = profile;
            int lost = 0;
            void OnLost(string path) { if (path == DardStorage.ProfileDir(false)) lost++; }
            DardStorage.BrowserLost += OnLost;
            try
            {
                var pkg = RecoveryPackage("com.test.recoverybudget");
                var clock = Stopwatch.StartNew();
                for (int i = 1; i <= 4; i++)
                {
                    await DardStorage.PrepareAsync(pkg);
                    var env = await DardStorage.Environment(false);
                    var page = await HiddenPage.Open(env, pkg.Origins[0]);
                    Check(env.UserDataFolder.StartsWith(profile, StringComparison.OrdinalIgnoreCase));
                    using (var process = Process.GetProcessById((int)page.Core.BrowserProcessId)) process.Kill();
                    for (int n = 0; n < 600 && lost < i; n++) await Task.Delay(25);
                    try { page.Dispose(); } catch (System.Runtime.InteropServices.COMException) { }
                    Check(lost == i);
                    typeof(DardStorage).GetMethod("InvalidateBrowser", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, new object[] { DardStorage.ProfileDir(false), env, true });
                    Check(lost == i); // 같은 환경의 늦은 중복 통지는 집계하지 않는다.
                    Check(DardStorage.BrowserRecoveryBlocked(DardStorage.ProfileDir(false)) == (i == 4));
                }
                Check(clock.Elapsed < TimeSpan.FromMinutes(1));
                bool refused = false;
                try { await DardStorage.Environment(false); }
                catch (InvalidOperationException ex) { refused = ex.Message == DardStorage.BrowserRecoveryError; }
                Check(refused);
                using var online = await HiddenPage.Open(await DardStorage.Environment(true), "https://unaffected.card.desk");
                DardStorage.ResetBrowserRecovery(true);
                Check(DardStorage.BrowserRecoveryBlocked(DardStorage.ProfileDir(false)));
                DardStorage.ResetBrowserRecovery(false);
                await DardStorage.PrepareAsync(pkg);
                using var restored = await HiddenPage.Open(await DardStorage.Environment(false), pkg.Origins[0]);
                Check(await restored.Eval("document.body.textContent") == "\"test\"");
            }
            finally
            {
                DardStorage.BrowserLost -= OnLost;
                DardStorage.ResetBrowserRecovery(false);
                AppPaths.WebDataDir = original;
            }
        });

        Test("browser recovery UI: fourth crash shows errors on all cards until manual reload", () =>
        {
            string original = AppPaths.WebDataDir;
            string profile = Path.Combine(root, "browser-stop-cards");
            string groups = Path.Combine(root, "browser-stop-groups");
            Directory.CreateDirectory(groups);
            string path = Path.Combine(groups, "stop.dard");
            File.WriteAllBytes(path, Dard(("manifest.json", ClockManifest), ("card.html", "<p>recovered")));
            var pkg = DardPackage.Load(path);
            var cfg = Config.Load(Path.Combine(root, "browser-stop.json"));
            cfg.Dards[pkg.Id] = new DardApproval { Allowed = true, Hash = pkg.Hash };
            AppPaths.WebDataDir = profile;
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                var clock = Stopwatch.StartNew();
                for (int i = 1; i <= 4; i++)
                {
                    var before = mgr.AllCards.OfType<DardWindow>().ToList();
                    Check(before.Count == 2);
                    var first = WebOf(before[0]);
                    Check(WebOf(before[1]).CoreWebView2.BrowserProcessId == first.CoreWebView2.BrowserProcessId);
                    Check(first.CoreWebView2.Environment.UserDataFolder.StartsWith(profile, StringComparison.OrdinalIgnoreCase));
                    using (var process = Process.GetProcessById((int)first.CoreWebView2.BrowserProcessId)) process.Kill();
                    if (i < 4)
                    {
                        WaitUntilLong(() => !mgr.AllCards.OfType<DardWindow>().Any(before.Contains), 15000);
                        Check(!mgr.AllCards.OfType<DardWindow>().Any(before.Contains));
                    }
                    else
                    {
                        WaitUntilLong(() => before.All(HasBrowserError), 15000);
                        Check(before.All(HasBrowserError));
                        Pump(500);
                        Check(mgr.AllCards.OfType<DardWindow>().SequenceEqual(before));
                        Check(clock.Elapsed < TimeSpan.FromMinutes(1));
                        before[0].Reload(); // 한 카드의 메뉴로 같은 환경 전체를 복구한다.
                        var after = mgr.AllCards.OfType<DardWindow>().ToList();
                        Check(after.Count == 2 && !after.Any(before.Contains));
                        foreach (var card in after) Check(Eval(WebOf(card), "document.body.textContent.trim()") == "\"recovered\"");
                        Check(!DardStorage.BrowserRecoveryBlocked(DardStorage.ProfileDir(false)));
                        using (var process = Process.GetProcessById((int)WebOf(after[0]).CoreWebView2.BrowserProcessId)) process.Kill();
                        WaitUntilLong(() => !mgr.AllCards.OfType<DardWindow>().Any(after.Contains), 15000);
                        Check(!mgr.AllCards.OfType<DardWindow>().Any(after.Contains));
                        foreach (var card in mgr.AllCards.OfType<DardWindow>())
                            Check(Eval(WebOf(card), "document.body.textContent.trim()") == "\"recovered\"");
                        Check(!DardStorage.BrowserRecoveryBlocked(DardStorage.ProfileDir(false)));
                    }
                }
            }
            finally { mgr.Shutdown(); DardStorage.ResetBrowserRecovery(false); AppPaths.WebDataDir = original; }
        });

        RecoveryTest("browser recovery hidden: normal browser shutdowns do not spend crash budget", async () =>
        {
            string original = AppPaths.WebDataDir;
            AppPaths.WebDataDir = Path.Combine(root, "browser-normal-exits");
            int lost = 0;
            void OnLost(string path) { if (path == DardStorage.ProfileDir(false)) lost++; }
            DardStorage.BrowserLost += OnLost;
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    var env = await DardStorage.Environment(false);
                    var exited = new TaskCompletionSource<bool>();
                    env.BrowserProcessExited += (_, e) => exited.TrySetResult(e.BrowserProcessExitKind == Microsoft.Web.WebView2.Core.CoreWebView2BrowserProcessExitKind.Normal);
                    using (await HiddenPage.Open(env, "https://normal-exit.card.desk")) { }
                    Check(await exited.Task.WaitAsync(TimeSpan.FromSeconds(20)));
                    Check(lost == 0 && !DardStorage.BrowserRecoveryBlocked(DardStorage.ProfileDir(false)));
                }
            }
            finally { DardStorage.BrowserLost -= OnLost; AppPaths.WebDataDir = original; }
        });
    }

    private static bool HasBrowserError(DardWindow card) => Visuals<TextBlock>(card)
        .Any(t => t.Visibility == Visibility.Visible && t.Text == DardStorage.BrowserRecoveryError);
}
