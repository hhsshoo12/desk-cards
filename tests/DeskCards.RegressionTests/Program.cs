using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeskCards;

internal static partial class Program
{
    private static int _failed;
    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var desktopShell = new IsolatedDesktopShell();
        // Keep the sandbox as diagnostic evidence. Never touch the user's groups/config.
        string root = Path.Combine(Path.GetTempPath(), "DeskCards-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("Sandbox: " + root);
        ReportTests(root);
        RevisionTests(root);
        ConfigurationTests(root);
        GroupFileTests(root);
        UpdateTests(root);
        FolderSwapTests(root);
        Console.WriteLine($"Failures: {_failed}");
        return _failed == 0 ? 0 : 1;
    }
    private static void Test(string name, Action action)
    {
        // 일부만 돌릴 때: DESKCARDS_TEST_FILTER에 테스트 이름 일부를 넣는다.
        if (Environment.GetEnvironmentVariable("DESKCARDS_TEST_FILTER") is { Length: > 0 } filter && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception ex)
        {
            _failed++;

            // 확인(Check) 말고 다른 예외면 어디서 났는지도 적는다.
            string where = ex.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("RegressionTests") && l.Contains(":line"))?.Trim() ?? "";
            Console.WriteLine("FAIL " + name + ": " + ex.Message + (ex.Message.StartsWith("Assertion") ? "" : " " + where));
        }
    }
}
