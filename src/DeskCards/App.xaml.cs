using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace DeskCards;

public partial class App : Application
{
    private Mutex? _mutex;
    private GroupManager? _mgr;
    private Tray? _tray;
    private UISettings? _uiSettings;
    private EventWaitHandle? _showSettings;
    private EventWaitHandle? _quitRequest;
    private readonly ManualResetEvent _stopListener = new(false);
    private Thread? _listener;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        UnhandledException += (_, ev) => Log(ev.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log(ev.ExceptionObject as Exception);
        try { Launch(); }
        catch (Exception ex) { Log(ex); throw; }
    }

    private static void Log(Exception? ex)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "deskcards-crash.log"), $"{DateTime.Now:s} {ex}\r\n\r\n"); }
        catch { }
    }

    private void Launch()
    {
        Ui.Init();
        string[] args = Environment.GetCommandLineArgs()[1..];
        Native.SetCurrentProcessExplicitAppUserModelID("hhsshoo12.DeskCards");
        // 업데이트로 다시 켜졌으면 옛 버전이 끝날 때까지 기다린다(그쪽이 아직 단일 실행 잠금을 쥐고 있다).
        WaitForUpdatedFrom(args);
        _mutex = new Mutex(true, "DeskCards.SingleInstance", out bool created);
        _showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "DeskCards.ShowSettings");
        _quitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "DeskCards.Quit");
        if (!created)
        {
            // 이미 실행 중이면(시작 메뉴 바로가기를 다시 누르는 등) 그쪽에 설정 창을 띄우라고 알리고 끝낸다.
            _showSettings.Set();
            Exit();
            return;
        }

        // 예전 이름(DeskFolders)의 그룹·설정 폴더를 옮긴다. 설정을 읽기 전에 해야 한다.
        AppPaths.Migrate();

        // 받아 둔 새 버전이 있으면 바꿔 끼우고 새 버전으로 다시 켠다.
        var cfg = Config.Load();
        var host = UpdateHost.Production(RelaunchInto);
        try
        {
            if (Updater.ApplyPending(cfg, host, Updater.RunningVersion)) return;
        }
        catch
        {
            // 바꿔 끼우지 못하면 지금 버전으로 켠다. 설정의 [앱 재시작]으로 다시 시도할 수 있다.
        }
        if (host.Installed && cfg.PendingUpdate == null) UpdatePackage.Cleanup(host.InstallDir, Updater.RunningVersion);

        Theme.Apply();
        _uiSettings = new UISettings();
        _uiSettings.ColorValuesChanged += (_, _) => Ui.Post(Theme.Apply);

        _mgr = new GroupManager(config: cfg);
        _mgr.Start();
        EdgeBar.Start(_mgr);
        _tray = new Tray("Desk Cards", () => Menus.ForTray(_mgr!, Quit).ShowAtCursor(), () => SettingsWindow.Open(_mgr!));
        Updater.Instance = new Updater(cfg, host);
        Updater.Instance.StartAuto();

        _listener = new Thread(() =>
        {
            while (true)
            {
                int signal = WaitHandle.WaitAny(new WaitHandle[] { _stopListener, _showSettings, _quitRequest });
                if (signal == 0 || Ui.ShuttingDown) return;
                if (signal == 2) { Ui.Post(Quit); return; }
                Ui.Post(() => { if (_mgr != null) SettingsWindow.Open(_mgr); });
            }
        }) { IsBackground = true };
        _listener.Start();
    }

    private void Quit()
    {
        Ui.ShuttingDown = true;
        Updater.Instance?.Stop();
        EdgeBar.Stop();
        _stopListener.Set();
        _listener?.Join();
        _showSettings?.Dispose();
        _quitRequest?.Dispose();
        _stopListener.Dispose();
        _mgr?.Shutdown();
        _mgr = null;
        _tray?.Dispose();
        _mutex?.Dispose();
        Exit();
    }

    /// <summary>새 버전 exe를 띄우고 이 프로세스는 끝낸다. 새 버전은 이 프로세스가 끝나길 기다렸다가 켜진다.</summary>
    private void RelaunchInto(string exe)
    {
        using var current = Process.GetCurrentProcess();
        Process.Start(new ProcessStartInfo(exe, $"--updated-from {Environment.ProcessId} --updated-start {current.StartTime.ToUniversalTime().Ticks}")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe),
        })?.Dispose();
        Quit();
    }

    private static void WaitForUpdatedFrom(string[] args)
    {
        int i = Array.IndexOf(args, "--updated-from");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid) || pid == Environment.ProcessId) return;
        try
        {
            using var old = Process.GetProcessById(pid);
            // 예전 버전은 PID만 보낸다. 최소한 실행 파일이 같은 앱인지 확인한다(.old로 바뀐 경우 포함).
            string? path = old.MainModule?.FileName;
            string? currentPath = Environment.ProcessPath;
            if (path == null || currentPath == null ||
                (!path.Equals(currentPath, StringComparison.OrdinalIgnoreCase) && !path.Equals(currentPath + ".old", StringComparison.OrdinalIgnoreCase))) return;
            int start = Array.IndexOf(args, "--updated-start");
            if (start >= 0 && (start + 1 >= args.Length || !long.TryParse(args[start + 1], out long ticks) || old.StartTime.ToUniversalTime().Ticks != ticks)) return;
            old.WaitForExit(15000);
        }
        catch (ArgumentException) { } // 이미 끝났다
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { } // 다른 사용자/권한의 프로세스는 기다리지 않는다.
    }
}
