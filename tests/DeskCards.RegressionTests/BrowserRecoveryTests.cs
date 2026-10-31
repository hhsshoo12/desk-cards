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

        Test("browser recovery registry: profile budgets and reset are isolated", () =>
        {
            var registry = new BrowserRecoveryRegistry();
            string first = Path.Combine(root, "profile-a");
            string second = Path.Combine(root, "profile-b");
            foreach (long now in new long[] { 0, 10_000, 20_000, 30_000 }) registry.RecordExit(first, now);
            Check(registry.IsBlocked(first) && !registry.IsBlocked(second));
            registry.RecordExit(second, 30_000);
            registry.Reset(first);
            Check(!registry.IsBlocked(first) && !registry.IsBlocked(second));
            foreach (long now in new long[] { 31_000, 32_000, 33_000 }) registry.RecordExit(second, now);
            Check(registry.IsBlocked(second) && !registry.IsBlocked(first));
        });

        RecoveryTest("browser recovery hidden: replacements share budget, duplicate exits ignored and reset isolated", async () =>
        {
            string original = AppPaths.WebDataDir;
            string profile = Path.Combine(root, "browser-recovery-budget");
            AppPaths.WebDataDir = profile;
            int lost = 0;
            void OnLost(string path) { if (path == BrowserEnvironment.ProfileDir()) lost++; }
            BrowserEnvironment.BrowserLost += OnLost;
            try
            {
                var pkg = RecoveryPackage("com.test.recoverybudget");
                var clock = Stopwatch.StartNew();
                for (int i = 1; i <= 4; i++)
                {
                    var env = await BrowserEnvironment.Environment();
                    var page = await HiddenPage.Open(env, pkg.Origins[0]);
                    Check(env.UserDataFolder.StartsWith(profile, StringComparison.OrdinalIgnoreCase));
                    using (var process = Process.GetProcessById((int)page.Core.BrowserProcessId)) process.Kill();
                    for (int n = 0; n < 600 && lost < i; n++) await Task.Delay(25);
                    try { page.Dispose(); } catch (System.Runtime.InteropServices.COMException) { }
                    Check(lost == i);
                    typeof(BrowserEnvironment).GetMethod("InvalidateBrowser", BindingFlags.NonPublic | BindingFlags.Static)!
                        .Invoke(null, new object[] { BrowserEnvironment.ProfileDir(), env, true });
                    Check(lost == i); // 같은 환경의 늦은 중복 통지는 집계하지 않는다.
                    Check(BrowserEnvironment.BrowserRecoveryBlocked(BrowserEnvironment.ProfileDir()) == (i == 4));
                }
                Check(clock.Elapsed < TimeSpan.FromMinutes(1));
                bool refused = false;
                try { await BrowserEnvironment.Environment(); }
                catch (InvalidOperationException ex) { refused = ex.Message == BrowserEnvironment.BrowserRecoveryError; }
                Check(refused);
                using var online = await HiddenPage.Open(await BrowserEnvironment.Environment(Path.Combine(profile, "separate")), "https://unaffected.card.desk");
                BrowserEnvironment.ResetBrowserRecovery(Path.Combine(profile, "separate"));
                Check(BrowserEnvironment.BrowserRecoveryBlocked(BrowserEnvironment.ProfileDir()));
                BrowserEnvironment.ResetBrowserRecovery();
                using var restored = await HiddenPage.Open(await BrowserEnvironment.Environment(), pkg.Origins[0]);
                Check(await restored.Eval("document.body.textContent") == "\"test\"");
            }
            finally
            {
                BrowserEnvironment.BrowserLost -= OnLost;
                BrowserEnvironment.ResetBrowserRecovery();
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
            var pkg = RecoveryPackage("com.test.recovery");
            var cfg = Config.Load(Path.Combine(root, "browser-stop.json"));
            AppPaths.WebDataDir = profile;
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                AttachRecoveryCards(mgr, pkg);
                var clock = Stopwatch.StartNew();
                for (int i = 1; i <= 4; i++)
                {
                    var before = mgr.AllCards.OfType<WebCardWindow>().ToList();
                    Check(before.Count == 2);
                    var first = WebOf(before[0]);
                    Check(WebOf(before[1]).CoreWebView2.BrowserProcessId == first.CoreWebView2.BrowserProcessId);
                    Check(first.CoreWebView2.Environment.UserDataFolder.StartsWith(profile, StringComparison.OrdinalIgnoreCase));
                    using (var process = Process.GetProcessById((int)first.CoreWebView2.BrowserProcessId)) process.Kill();
                    if (i < 4)
                    {
                        WaitUntilLong(() => !mgr.AllCards.OfType<WebCardWindow>().Any(before.Contains), 15000);
                        Check(!mgr.AllCards.OfType<WebCardWindow>().Any(before.Contains));
                    }
                    else
                    {
                        WaitUntilLong(() => before.All(HasBrowserError), 15000);
                        Check(before.All(HasBrowserError));
                        Pump(500);
                        Check(mgr.AllCards.OfType<WebCardWindow>().SequenceEqual(before));
                        Check(clock.Elapsed < TimeSpan.FromMinutes(1));
                        before[0].Reload(); // 한 카드의 메뉴로 같은 환경 전체를 복구한다.
                        var after = mgr.AllCards.OfType<WebCardWindow>().ToList();
                        Check(after.Count == 2 && !after.Any(before.Contains));
                        foreach (var card in after) Check(Eval(WebOf(card), "document.body.textContent.trim()") == "\"recovered\"");
                        Check(!BrowserEnvironment.BrowserRecoveryBlocked(BrowserEnvironment.ProfileDir()));
                        using (var process = Process.GetProcessById((int)WebOf(after[0]).CoreWebView2.BrowserProcessId)) process.Kill();
                        WaitUntilLong(() => !mgr.AllCards.OfType<WebCardWindow>().Any(after.Contains), 15000);
                        Check(!mgr.AllCards.OfType<WebCardWindow>().Any(after.Contains));
                        foreach (var card in mgr.AllCards.OfType<WebCardWindow>())
                            Check(Eval(WebOf(card), "document.body.textContent.trim()") == "\"recovered\"");
                        Check(!BrowserEnvironment.BrowserRecoveryBlocked(BrowserEnvironment.ProfileDir()));
                    }
                }
            }
            finally { mgr.Shutdown(); BrowserEnvironment.ResetBrowserRecovery(); AppPaths.WebDataDir = original; }
        });

        RecoveryTest("browser recovery hidden: normal browser shutdowns do not spend crash budget", async () =>
        {
            string original = AppPaths.WebDataDir;
            AppPaths.WebDataDir = Path.Combine(root, "browser-normal-exits");
            int lost = 0;
            void OnLost(string path) { if (path == BrowserEnvironment.ProfileDir()) lost++; }
            BrowserEnvironment.BrowserLost += OnLost;
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    var env = await BrowserEnvironment.Environment();
                    var exited = new TaskCompletionSource<bool>();
                    env.BrowserProcessExited += (_, e) => exited.TrySetResult(e.BrowserProcessExitKind == Microsoft.Web.WebView2.Core.CoreWebView2BrowserProcessExitKind.Normal);
                    using (await HiddenPage.Open(env, "https://normal-exit.card.desk")) { }
                    Check(await exited.Task.WaitAsync(TimeSpan.FromSeconds(20)));
                    Check(lost == 0 && !BrowserEnvironment.BrowserRecoveryBlocked(BrowserEnvironment.ProfileDir()));
                }
            }
            finally { BrowserEnvironment.BrowserLost -= OnLost; AppPaths.WebDataDir = original; }
        });
    }

    private static bool HasBrowserError(WebCardWindow card) => Visuals<TextBlock>(card)
        .Any(t => t.Visibility == Visibility.Visible && t.Text == BrowserEnvironment.BrowserRecoveryError);
}
