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
    private static int _failed;
    private static DeskCards.Testing.TestSession? _session;
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        GroupManager.QuietDardIssues = true; // 경고 확인 창은 그 기능을 보는 테스트에서만 띄운다
        Theme.Apply();
        using var session = DeskCards.Testing.TestSession.Start("앱 회귀 테스트");
        _session = session;
        // Keep the sandbox as diagnostic evidence. Never touch the user's groups/config.
        string root = Path.Combine(Path.GetTempPath(), "DeskCards-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppPaths.WebDataDir = Path.Combine(root, "webview2");
        Console.WriteLine("Sandbox: " + root);
        TestSessionTests();
        ReportTests(root);
        RevisionTests(root);
        RevisionWindowTests(root);
        ConfigurationTests(root);
        BarSettingsTests(root, app);
        ConfigurationRecoveryTests(root);
        GroupFileTests(root);
        CardWindowTests(root, app);
        SettingsLifecycleTests(root, app);
        DwmCardTests(root);
        DardTests(root, app);
        UpdateTests(root);
        Console.WriteLine($"Failures: {_failed}");
        session.Dispose();
        app.Shutdown();
        return _failed == 0 ? 0 : 1;
    }
    private static void Test(string name, Action action)
    {
        // 일부만 돌릴 때: DESKCARDS_TEST_FILTER에 테스트 이름 일부를 넣는다.
        if (Environment.GetEnvironmentVariable("DESKCARDS_TEST_FILTER") is { Length: > 0 } filter && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
        try { action(); Console.WriteLine("PASS " + name); _session?.Complete(true); }
        catch (Exception ex)
        {
            _failed++;
            _session?.Complete(false);
            // 확인(Check) 말고 다른 예외면 어디서 났는지도 적는다.
            string where = ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("RegressionTests") && l.Contains(":line"))?.Trim() ?? "";
            Console.WriteLine("FAIL " + name + ": " + ex.Message + (ex.Message.StartsWith("Assertion") ? "" : " " + where));
        }
    }
}
