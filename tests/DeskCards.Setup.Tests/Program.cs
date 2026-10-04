using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskCards.Setup;
using Microsoft.Win32;

internal static class Program
{
    private static int _passed, _failed;
    private static DeskCards.Testing.TestSession? _session;
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply(true);
        if (args.Contains("--preview"))
        {
            if (args.Contains("--dark")) Theme.Apply(false);
            using var preview = new Fixture();
            var window = preview.Window(); window.Closed += (_, _) => app.Shutdown(); app.Run(window); return 0;
        }
        using var session = DeskCards.Testing.TestSession.Start("설치기 회귀 테스트");
        _session = session;
        Test("own tile and legacy executable match exactly", f =>
        {
            Check(StartTraces.OnlyOurTiles(Tiles("W~" + f.Env.AppExe.ToUpperInvariant()), f.Env.InstallDir));
            Check(StartTraces.IsOurTile("W~" + Path.Combine(f.Env.InstallDir, "DeskFolders.exe"), f.Env.InstallDir));
        });
        Test("similar directory is preserved", f => Check(!StartTraces.OnlyOurTiles(Tiles("W~" + f.Env.InstallDir + " Other\\DeskCards.exe"), f.Env.InstallDir)));
        Test("other executable in installation folder is preserved", f => Check(!StartTraces.OnlyOurTiles(Tiles("W~" + f.Env.InstallDir + "\\Other.exe"), f.Env.InstallDir)));
        Test("mixed backup is preserved", f => Check(!StartTraces.OnlyOurTiles(Tiles("W~" + f.Env.AppExe, "W~C:\\Windows\\notepad.exe"), f.Env.InstallDir)));
        Test("empty target never matches", f => Check(!StartTraces.OnlyOurTiles(Tiles("something"), "")));
        Test("unreadable and unknown tile backups are preserved", f =>
        {
            foreach (object value in new object[] { "broken", "[]", "{}", "[null]", "123", new byte[] { 1 } })
                Check(!StartTraces.OnlyOurTiles(value, f.Env.InstallDir));
            string single = new JavaScriptSerializer().Serialize(new { tileId = "W~" + f.Env.AppExe });
            Check(StartTraces.OnlyOurTiles(Encoding.Unicode.GetBytes(single + "\0"), f.Env.InstallDir));
        });
        Test("report 06: setup update synchronizes version metadata", f =>
        {
            f.Existing(); File.WriteAllText(Path.Combine(f.Env.InstallDir, "version.txt"), "0.1.0");
            f.Install(); Check(File.ReadAllText(Path.Combine(f.Env.InstallDir, "version.txt")).Trim() == "0.2.0");
        });
        Test("report 07: uninstall removes owned metadata and empty directory", f =>
        {
            f.Install(); f.Engine.Uninstall(false, _ => { }); Check(!Directory.Exists(f.Env.InstallDir));
        });
        Test("report 06: failed setup restores executable and version together", f =>
        {
            f.Existing(); string version = Path.Combine(f.Env.InstallDir, "version.txt"); File.WriteAllText(version, "0.1.0");
            f.Shortcuts.Fail = true; Throws<SetupFailure>(() => f.Install());
            Check(File.ReadAllText(version) == "0.1.0" && File.ReadAllText(f.Env.AppExe) == "old app");
        });
        Test("report 07: uninstall preserves unrelated files", f =>
        {
            f.Install(); string keep = Path.Combine(f.Env.InstallDir, "my-notes.txt"); File.WriteAllText(keep, "mine");
            f.Engine.Uninstall(false, _ => { }); Check(File.ReadAllText(keep) == "mine");
            Check(!File.Exists(Path.Combine(f.Env.InstallDir, "version.txt")));
        });
        Test("BOM version is accepted", f => { f.Package(bom: true); f.Install(); Check(f.Registry.InstalledVersion == "0.2.0"); });
        Test("stop timeout is reported with two five-second waits", f =>
        {
            f.Processes.Exits = false;
            var error = Throws<SetupFailure>(() => AppStopper.Stop(f.Processes, f.Env.AppExe));
            Check(error.Message == "Desk Cards를 끄지 못했어요" && f.Processes.Killed == 1 && f.Processes.Waits.SequenceEqual(new[] { 5000, 5000 }));
        });
        Test("successful graceful exit avoids forced termination", f => { AppStopper.Stop(f.Processes, f.Env.AppExe); Check(f.Processes.Requested == 1 && f.Processes.Killed == 0); });
        Test("package read failure leaves running app and installation intact", f =>
        {
            f.Existing(); f.Payload.CopyError = new IOException("resource read failed");
            Throws<SetupFailure>(() => f.Install()); f.Unchanged();
        });
        Test("missing uninstaller in package is rejected before stopping app", f =>
        {
            f.Existing(); f.Package(missingUninstaller: true); Throws<SetupFailure>(() => f.Install()); f.Unchanged();
        });
        Test("installer without a package cannot install", f =>
        {
            var engine = new SetupEngine(f.Env, null, f.Processes, f.Shortcuts);
            Throws<SetupFailure>(() => engine.Install(new InstallOptions(), _ => { }, () => { }, CancellationToken.None));
            Check(f.Processes.Requested == 0);
            var window = new MainWindow(f.Env, engine, false); window.Show(); Pump();
            try { Check(!window.NextButton.IsEnabled); } finally { window.Close(); }
        });
        Test("uninstaller build always starts in uninstall mode", f =>
        {
            Check(SetupApplication.UninstallMode(new string[0], uninstallerBuild: true));
            Check(!SetupApplication.UninstallMode(new string[0], uninstallerBuild: false));
            Check(SetupApplication.UninstallMode(new[] { "/UNINSTALL" }, uninstallerBuild: false));
        });
        Test("missing package does not stop app", f => { f.Existing(); File.Delete(f.Zip); Throws<SetupFailure>(() => f.Install()); f.Unchanged(); });
        Test("uninstall failure keeps registration", f =>
        {
            f.Existing();
            using var locked = new FileStream(f.Env.AppExe, FileMode.Open, FileAccess.Read, FileShare.Read);
            Throws<SetupFailure>(() => f.Engine.Uninstall(false, _ => { }));
            Check(f.Registry.InstalledVersion == "0.1.0");
        });
        Test("locked uninstaller also keeps registration", f =>
        {
            f.Existing(); File.WriteAllText(f.Env.Uninstaller, "uninstaller");
            using var locked = new FileStream(f.Env.Uninstaller, FileMode.Open, FileAccess.Read, FileShare.Read);
            Throws<SetupFailure>(() => f.Engine.Uninstall(false, _ => { })); Check(f.Registry.InstalledVersion == "0.1.0");
        });
        Test("cleanup command uses literal escaped paths", f =>
        {
            string path = Path.Combine(f.Root, "percent% & quote'", "uninstall.exe");
            string command = Encoding.Unicode.GetString(Convert.FromBase64String(Cleanup.EncodedCommand(path)));
            Check(command.Contains("-LiteralPath $cleanupFile") && command.Contains(path.Replace("'", "''")) && command.Contains("-lt 30") && command.Contains("-Seconds 2"));
        });
        foreach (string entry in new[] { "..\\escape.exe", "../escape.exe", "C:\\outside.exe", "/outside.exe", "folder/../../outside", "a:stream", "folder./evil" })
            Test("unsafe zip path rejected: " + entry, f => { f.Existing(); f.Package(extra: entry); Throws<SetupFailure>(() => f.Install()); f.Unchanged(); });
        Test("missing root executable is rejected", f => { f.Existing(); f.Package(missingExe: true); Throws<SetupFailure>(() => f.Install()); f.Unchanged(); });
        Test("version mismatch leaves installation intact", f => { f.Existing(); f.Package(version: "0.3.0"); Throws<SetupFailure>(() => f.Install()); f.Unchanged(); });
        Test("failure after replacement restores old executable", f =>
        {
            f.Existing(); f.Shortcuts.Fail = true;
            Throws<SetupFailure>(() => f.Install());
            Check(File.ReadAllText(f.Env.AppExe) == "old app" && !File.Exists(f.Env.AppExe + ".old"));
        });
        Test("failed first install removes newly copied files", f =>
        {
            f.Shortcuts.Fail = true; Throws<SetupFailure>(() => f.Install()); Check(!Directory.Exists(f.Env.InstallDir));
        });
        Test("update preserves unrelated installation files", f =>
        {
            f.Existing(); string other = Path.Combine(f.Env.InstallDir, "user.txt"); File.WriteAllText(other, "keep");
            File.WriteAllText(Path.Combine(f.Env.InstallDir, "DeskFolders.exe"), "legacy");
            f.Install(); Check(File.ReadAllText(other) == "keep" && File.ReadAllText(f.Env.AppExe) == "new app");
            Check(!File.Exists(f.Env.AppExe + ".old") && !File.Exists(Path.Combine(f.Env.InstallDir, "DeskFolders.exe")));
        });
        Test("update defaults reflect shortcuts and disabled startup", f =>
        {
            f.Existing(); Directory.CreateDirectory(f.Env.DesktopDir); File.WriteAllText(f.Env.DesktopLink, "link");
            f.Registry.SetAutoStart(true); using (var key = f.Registry.Create(Registration.ApprovalKey)) key.SetValue("DeskCards", new byte[] { 3, 0 }, RegistryValueKind.Binary);
            var options = f.Registry.ReadOptions(); Check(!options.StartMenu && options.Desktop && !options.AutoStart);
        });
        Test("new install defaults", f => { var options = f.Registry.ReadOptions(); Check(options.StartMenu && !options.Desktop && options.AutoStart); });
        Test("startup approval missing, even, odd, and absent Run", f =>
        {
            using (var run = f.Registry.Create(Registration.RunKey)) run.SetValue("DeskCards", "app");
            Check(f.Registry.AutoStartEnabled);
            using (var approved = f.Registry.Create(Registration.ApprovalKey)) approved.SetValue("DeskCards", new byte[] { 2 }, RegistryValueKind.Binary);
            Check(f.Registry.AutoStartEnabled);
            using (var approved = f.Registry.Create(Registration.ApprovalKey)) approved.SetValue("DeskCards", new byte[] { 3 }, RegistryValueKind.Binary);
            Check(!f.Registry.AutoStartEnabled);
            using (var approved = f.Registry.Create(Registration.ApprovalKey)) approved.SetValue("DeskCards", new byte[] { 2 }, RegistryValueKind.Binary);
            using (var run = f.Registry.Create(Registration.RunKey)) run.DeleteValue("DeskCards");
            Check(!f.Registry.AutoStartEnabled);
            f.Registry.SetAutoStart(true); using (var key = f.Registry.Open(Registration.ApprovalKey)) Check(((byte[])key!.GetValue("DeskCards")).SequenceEqual(DeskCards.StartupState.EnabledBytes()));
            f.Registry.SetAutoStart(false); Check(!f.Registry.AutoStartEnabled);
        });
        Test("uninstall preserves group and unchecked settings", f =>
        {
            f.Existing(); string group = Path.Combine(f.Root, "groups"); Directory.CreateDirectory(group); File.WriteAllText(Path.Combine(group, "keep.lnk"), "group");
            Directory.CreateDirectory(f.Env.ConfigDir); File.WriteAllText(Path.Combine(f.Env.ConfigDir, "config.json"), "settings");
            f.Engine.Uninstall(false, _ => { });
            Check(File.Exists(Path.Combine(group, "keep.lnk")) && File.Exists(Path.Combine(f.Env.ConfigDir, "config.json")));
            Check(f.Registry.InstalledVersion == null);
        });
        Test("checked removal only removes sandbox settings", f =>
        {
            f.Existing(); Directory.CreateDirectory(f.Env.ConfigDir); Directory.CreateDirectory(f.Env.OldConfigDir);
            f.Engine.Uninstall(true, _ => { }); Check(!Directory.Exists(f.Env.ConfigDir) && !Directory.Exists(f.Env.OldConfigDir));
        });
        Test("uninstall removes app self-update leftovers", f =>
        {
            f.Existing(); File.WriteAllText(f.Env.AppExe + ".old", "previous");
            string staged = Path.Combine(f.Env.InstallDir, "update", "0.3.0"); Directory.CreateDirectory(staged); File.WriteAllText(Path.Combine(staged, "DeskCards.exe"), "staged");
            f.Engine.Uninstall(false, _ => { }); Check(!Directory.Exists(f.Env.InstallDir));
        });
        Test("leftover old executable does not block update", f =>
        {
            f.Existing(); File.WriteAllText(f.Env.AppExe + ".old", "previous");
            f.Install(); Check(File.ReadAllText(f.Env.AppExe) == "new app" && !File.Exists(f.Env.AppExe + ".old"));
        });
        Test("uninstall preserves other files and containing folder", f =>
        {
            f.Existing(); string file = Path.Combine(f.Env.InstallDir, "other.txt"); File.WriteAllText(file, "keep");
            f.Engine.Uninstall(false, _ => { }); Check(File.ReadAllText(file) == "keep");
        });
        Test("downgrade rejected before unpacking or app termination", f =>
        {
            f.Existing("0.3.0"); Throws<SetupFailure>(() => f.Install()); f.Unchanged(); Check(f.Payload.CopyCount == 0);
        });
        Test("version comparison is numeric and malformed versions differ", f =>
        {
            Check(EmbeddedPackage.CompareInstalled("0.10.0", new Version(0, 9, 0)) > 0);
            Check(EmbeddedPackage.CompareInstalled("0.2.0", new Version(0, 2, 0)) == 0);
            Check(EmbeddedPackage.CompareInstalled("invalid", new Version(0, 2, 0)) == null);
        });
        Test("cancel while unpacking cleans temp without stopping app", f =>
        {
            f.Existing(); using var cancel = new CancellationTokenSource(); f.Payload.OnCopy = () => cancel.Cancel();
            Throws<OperationCanceledException>(() => f.Install(cancel.Token)); f.Unchanged(); Check(!Directory.GetDirectories(f.Env.TempDir).Any());
        });
        Test("cancel at commit boundary does not stop or replace app", f =>
        {
            f.Existing(); using var cancel = new CancellationTokenSource();
            Throws<OperationCanceledException>(() => f.Engine.Install(new InstallOptions(), _ => { }, () => cancel.Cancel(), cancel.Token));
            f.Unchanged();
        });
        Test("test environment rejects escaping file paths", f =>
        {
            Throws<ArgumentException>(() => SetupEnvironment.ForTests(f.Root, f.Env.RegistryRoot, Path.GetTempPath()));
            Throws<ArgumentException>(() => SetupEnvironment.ForTests(f.Root, @"Software\Microsoft\Windows\CurrentVersion"));
        });
        Test("registration types, uninstall command and quiet key removal", f =>
        {
            using (var key = f.Registry.Create(Registration.UninstallKey)) key.SetValue("QuietUninstallString", "old");
            f.Install(); using var registration = f.Registry.Open(Registration.UninstallKey);
            Check(registration!.GetValueKind("NoModify") == RegistryValueKind.DWord && (int)registration.GetValue("NoModify") == 1);
            Check((string)registration.GetValue("UninstallString") == "\"" + f.Env.Uninstaller + "\" /uninstall");
            Check(registration.GetValue("QuietUninstallString") == null && (string)registration.GetValue("DisplayVersion") == "0.2.0");
            Check(File.ReadAllText(f.Env.Uninstaller) == "uninstaller");
        });
        Test("start trace cleanup only deletes our complete backup", f =>
        {
            using (var key = f.Registry.Create(Registration.BackupKey))
            {
                key.SetValue("ListOfEventDrivenBackedUpTiles_ours", Encoding.Unicode.GetBytes(Tiles("W~" + f.Env.AppExe)), RegistryValueKind.Binary);
                key.SetValue("ListOfEventDrivenBackedUpTiles_mixed", Encoding.Unicode.GetBytes(Tiles("W~" + f.Env.AppExe, "other")), RegistryValueKind.Binary);
                key.SetValue("ListOfEventDrivenBackedUpTiles_broken", new byte[] { 123 }, RegistryValueKind.Binary);
            }
            f.Registry.RemoveStartTraces(); using var remaining = f.Registry.Open(Registration.BackupKey);
            Check(remaining!.GetValue("ListOfEventDrivenBackedUpTiles_ours") == null && remaining.GetValueNames().Length == 2);
        });
        Test("real ShellLink has target and AppUserModelID", f =>
        {
            new ShellShortcuts().Create(f.Env.MenuLink, f.Env.AppExe, f.Env.InstallDir);
            var shortcut = ShellShortcuts.Inspect(f.Env.MenuLink);
            Check(shortcut.Item1.Equals(f.Env.AppExe, StringComparison.OrdinalIgnoreCase) && shortcut.Item2 == SetupEnvironment.AppId);
        });
        Test("wizard pages render and selection survives navigation", f =>
        {
            var window = f.Window(); window.Show(); Pump();
            Check(window.Heading.Text.Contains("바탕화면") && window.NextButton.IsEnabled);
            Click(window.NextButton); Check(window.PageContent.Children.OfType<CheckBox>().Count() == 3);
            window.PageContent.Children.OfType<CheckBox>().ElementAt(1).IsChecked = true;
            Click(window.NextButton); Check(window.PageContent.Children.OfType<TextBlock>().Any(t => t.Text.Contains("바탕화면 바로가기: 켬")));
            Click(window.BackButton); Check(window.PageContent.Children.OfType<CheckBox>().ElementAt(1).IsChecked == true);
            Capture(window, Path.Combine(f.Root, "wizard.png")); window.Close();
        });
        Console.WriteLine($"Passed: {_passed}; Failed: {_failed}");
        session.Dispose();
        app.Shutdown(); return _failed == 0 ? 0 : 1;
    }

    private static void Capture(Window window, string file)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(file); png.Save(output);
    }
    private static void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static string Tiles(params string[] ids) => new JavaScriptSerializer().Serialize(ids.Select(id => new { tileId = id }).ToArray());
    internal static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    internal static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T ex) { return ex; } throw new Exception("Expected " + typeof(T).Name); }
    private static void Test(string name, Action<Fixture> test)
    {
        if (Environment.GetEnvironmentVariable("DESKCARDS_TEST_FILTER") is { Length: > 0 } filter && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) return;
        try { using var fixture = new Fixture(); test(fixture); Console.WriteLine("PASS " + name); _passed++; _session?.Complete(true); }
        catch (Exception ex) { Console.WriteLine("FAIL " + name + "\n" + ex); _failed++; _session?.Complete(false); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        public SetupEnvironment Env { get; }
        public Registration Registry { get; }
        public LocalPackage Payload { get; }
        public FakeProcesses Processes { get; } = new FakeProcesses();
        public FakeShortcuts Shortcuts { get; } = new FakeShortcuts();
        public SetupEngine Engine { get; }
        public string Zip => Path.Combine(Root, "payload.zip");
        public Fixture()
        {
            string id = Guid.NewGuid().ToString("N"); Root = Path.Combine(Path.GetTempPath(), "DeskCards-Setup-Tests-" + id);
            Env = SetupEnvironment.ForTests(Root, @"Software\DeskCards-Setup-Tests\" + id);
            Directory.CreateDirectory(Root); Payload = new LocalPackage(Zip); Registry = new Registration(Env);
            Engine = new SetupEngine(Env, Payload, Processes, Shortcuts);
            Package();
        }
        public void Package(string version = "0.2.0", bool bom = false, string? extra = null, bool missingExe = false, bool missingUninstaller = false)
        {
            File.Delete(Zip); using var archive = ZipFile.Open(Zip, ZipArchiveMode.Create);
            if (!missingExe) { using var writer = new StreamWriter(archive.CreateEntry("DeskCards.exe").Open()); writer.Write("new app"); }
            if (!missingUninstaller) { using var writer = new StreamWriter(archive.CreateEntry("uninstall.exe").Open()); writer.Write("uninstaller"); }
            using (var writer = new StreamWriter(archive.CreateEntry("version.txt").Open(), new UTF8Encoding(bom))) writer.Write(version);
            if (extra != null) { using var writer = new StreamWriter(archive.CreateEntry(extra).Open()); writer.Write("unsafe"); }
        }
        public void Existing(string version = "0.1.0")
        { Directory.CreateDirectory(Env.InstallDir); File.WriteAllText(Env.AppExe, "old app"); Registry.Register(version); }
        public void Install(CancellationToken token = default) => Engine.Install(new InstallOptions(), _ => { }, () => { }, token);
        public void Unchanged() { Check(File.ReadAllText(Env.AppExe) == "old app" && Processes.Requested == 0); }
        public MainWindow Window() => new MainWindow(Env, Engine, false);
        public void Dispose()
        {
            // Revalidate before recursive deletion. Neither cleanup can address production paths.
            SetupEnvironment.ForTests(Root, Env.RegistryRoot);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Env.RegistryRoot, false);
            SetupEnvironment.DeleteTree(Root, Path.GetTempPath());
        }
    }
    /// <summary>설치기 리소스 대신 테스트 폴더의 zip을 쓰는 패키지.</summary>
    private sealed class LocalPackage : IAppPackage
    {
        private readonly string _zip;
        public Exception? CopyError;
        public Action? OnCopy;
        public int CopyCount;
        public LocalPackage(string zip) { _zip = zip; }
        public Version Version { get; } = new Version(0, 2, 0);
        public void CopyTo(string path, CancellationToken token)
        {
            CopyCount++; OnCopy?.Invoke(); token.ThrowIfCancellationRequested();
            if (CopyError != null) throw CopyError;
            File.Copy(_zip, path);
        }
    }
    private sealed class FakeProcesses : IAppProcesses
    {
        public bool Exits = true;
        public int Requested, Killed;
        public readonly List<int> Waits = new List<int>();
        public void RequestQuit() { Requested++; }
        public bool WaitForExit(string exe, int milliseconds) { Waits.Add(milliseconds); return Exits; }
        public void Kill(string exe) { Killed++; }
    }
    private sealed class FakeShortcuts : IShortcuts
    {
        public bool Fail;
        public void Create(string path, string exe, string directory)
        { if (Fail) throw new IOException("Injected shortcut failure"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, exe); }
    }
}
