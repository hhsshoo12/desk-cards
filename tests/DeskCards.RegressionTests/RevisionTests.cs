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
                await page.Eval("localStorage.setItem('survives','yes')");
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
                Check(await restored.Eval("localStorage.getItem('survives')") == "\"yes\"");
                Check(restored.Core.BrowserProcessId != pid);
            }
            finally { DardStorage.BrowserLost -= OnLost; AppPaths.WebDataDir = original; }
        });
    }
}
