using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace DeskCards.Setup;

internal static class SetupApplication
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        Theme.Apply();
        string exe = Assembly.GetExecutingAssembly().Location;
        var env = SetupEnvironment.Production();
        bool uninstall = UninstallMode(args);
        try
        {
            string? target = Argument(args, "/target");
            if (uninstall) env = SetupEnvironment.Production(target ?? new Registration(env).InstalledLocation);
            string? tempCopy = null;
            if (uninstall && target != null && Path.GetDirectoryName(exe)!.Equals(Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(exe).StartsWith("DeskCards-uninstall-", StringComparison.Ordinal))
            {
                tempCopy = exe;
                string pidText = Path.GetFileNameWithoutExtension(exe).Substring("DeskCards-uninstall-".Length);
                if (int.TryParse(pidText, out int parentId))
                {
                    try { using var parent = Process.GetProcessById(parentId); parent.WaitForExit(10000); }
                    catch (ArgumentException) { }
                }
            }
            using var mutex = new Mutex(true, "DeskCards.Setup.SingleInstance", out bool created);
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, "DeskCards.Setup.Show");
            if (!created) { show.Set(); return; }
            if (uninstall && exe.Equals(env.Uninstaller, StringComparison.OrdinalIgnoreCase))
            {
                string copy = Path.Combine(env.TempDir, "DeskCards-uninstall-" + Process.GetCurrentProcess().Id + ".exe");
                File.Copy(exe, copy, true);
                Process.Start(new ProcessStartInfo(copy, "/uninstall /target " + Quote(env.InstallDir)) { UseShellExecute = false });
                return;
            }
            var package = EmbeddedPackage.Load();
            if (!uninstall && package == null) throw new InvalidOperationException("설치기에 앱이 들어 있지 않습니다.");
            var engine = new SetupEngine(env, package, new WindowsAppProcesses(), new ShellShortcuts());
            var window = new MainWindow(env, engine, uninstall, tempCopy);
            using var stop = new ManualResetEvent(false);
            var listener = new Thread(() =>
            {
                while (WaitHandle.WaitAny(new WaitHandle[] { stop, show }) == 1)
                {
                    if (app.Dispatcher.HasShutdownStarted) return;
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                        window.Activate(); WindowEffects.SetForegroundWindow(new WindowInteropHelper(window).Handle);
                    }));
                }
            }) { IsBackground = true };
            listener.Start();
            try { app.Run(window); }
            finally { stop.Set(); listener.Join(); }
        }
        catch (Exception ex)
        {
            try { new SetupLog(env).Write("설치기 시작 실패", ex); } catch { }
            ShowStartupFailure(app, env.LogPath);
        }
    }

    /// <summary>제거기 전용 빌드(uninstall.exe)는 인자와 상관없이 늘 제거 모드다.</summary>
    internal static bool UninstallMode(string[] args, bool uninstallerBuild = UninstallerBuild) =>
        uninstallerBuild || args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));

#if UNINSTALLER
    internal const bool UninstallerBuild = true;
#else
    internal const bool UninstallerBuild = false;
#endif

    private static string? Argument(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 == args.Length || args[i + 1].StartsWith("/")) throw new ArgumentException(name + " 값이 없습니다.");
                return args[i + 1];
            }
        return null;
    }
    internal static string Quote(string value) => "\"" + value.TrimEnd('\\').Replace("\"", "\\\"") + "\"";

    private static void ShowStartupFailure(Application app, string log)
    {
        var window = new Window { Title = "Desk Cards", Width = 460, Height = 240, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        window.SetResourceReference(Window.BackgroundProperty, "FlyoutBg"); window.SetResourceReference(Window.ForegroundProperty, "Fg");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "설치기를 시작하지 못했어요", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "원인은 로그 파일에 기록했어요.", Margin = new Thickness(0, 14, 0, 18) });
        var link = new Button { Content = "로그 파일 열기", Height = 32 };
        link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(log) { UseShellExecute = true }); } catch { } };
        panel.Children.Add(link); window.Content = panel;
        window.SourceInitialized += (_, _) => WindowEffects.Apply(new WindowInteropHelper(window).Handle, Theme.IsLight);
        if (!app.Dispatcher.HasShutdownStarted) window.ShowDialog();
    }
}

internal static class WindowEffects
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    public static void Apply(IntPtr hwnd, bool light)
    {
        int dark = light ? 0 : 1, round = 2, mica = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);
        if (Environment.OSVersion.Version.Build >= 22621) DwmSetWindowAttribute(hwnd, 38, ref mica, 4);
    }
}
