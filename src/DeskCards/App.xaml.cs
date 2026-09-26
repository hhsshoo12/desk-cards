using System;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DeskCards;

public partial class App : Application
{
    private Mutex? _mutex;
    private GroupManager? _mgr;
    private Forms.NotifyIcon? _tray;
    private EventWaitHandle? _showSettings;
    private readonly ManualResetEvent _stopListener = new(false);
    private Thread? _listener;
    private Drawing.Icon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, "DeskCards.SingleInstance", out bool created);
        _showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, "DeskCards.ShowSettings");
        if (!created)
        {
            // 이미 실행 중이면(시작 메뉴 바로가기를 다시 누르는 등) 그쪽에 설정 창을 띄우라고 알리고 끝낸다.
            _showSettings.Set();
            Shutdown();
            return;
        }

        // 예전 이름(DeskFolders)의 그룹·설정 폴더를 옮긴다. 설정을 읽기 전에 해야 한다.
        AppPaths.Migrate();

        Theme.Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _mgr = new GroupManager();
        _mgr.Start();
        CreateTray();

        _listener = new Thread(() =>
        {
            while (WaitHandle.WaitAny(new WaitHandle[] { _stopListener, _showSettings }) == 1)
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(() => { if (_mgr != null) SettingsWindow.Open(_mgr); });
            }
        }) { IsBackground = true };
        _listener.Start();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(Theme.Apply);
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        var settings = new Forms.ToolStripMenuItem("설정", null, (_, _) => SettingsWindow.Open(_mgr!));
        settings.Font = new Drawing.Font(settings.Font, Drawing.FontStyle.Bold);
        menu.Items.Add(settings);
        menu.Items.Add("카드 편집 (이동 · 크기 · 삭제)", null, (_, _) => _mgr!.BeginEditMode());
        menu.Items.Add("새 그룹", null, (_, _) => _mgr!.NewGroup());
        menu.Items.Add("그룹 폴더 열기", null, (_, _) => FileOps.OpenFolder(_mgr!.Root));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Quit());

        _trayIcon = MakeTrayIcon();
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Desk Cards",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => SettingsWindow.Open(_mgr!);
    }

    private void Quit()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _stopListener.Set();
        _listener?.Join();
        _showSettings?.Dispose();
        _stopListener.Dispose();
        _mgr?.Shutdown();
        _mgr = null;
        _tray?.ContextMenuStrip?.Dispose();
        _tray?.Dispose();
        _trayIcon?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>2×2 타일 모양 트레이 아이콘을 그린다.</summary>
    private static Drawing.Icon MakeTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var colors = new[] { "#4CC2FF", "#FFB900", "#6CCB5F", "#FF6F61" };
            for (int i = 0; i < 4; i++)
            {
                int x = 3 + (i % 2) * 14, y = 3 + (i / 2) * 14;
                using var br = new Drawing.SolidBrush(Drawing.ColorTranslator.FromHtml(colors[i]));
                using var path = new Drawing.Drawing2D.GraphicsPath();
                const int r = 4, s = 12;
                path.AddArc(x, y, r * 2, r * 2, 180, 90);
                path.AddArc(x + s - r * 2, y, r * 2, r * 2, 270, 90);
                path.AddArc(x + s - r * 2, y + s - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(x, y + s - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                g.FillPath(br, path);
            }
        }
        IntPtr handle = bmp.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)borrowed.Clone();
        }
        finally { Native.DestroyIcon(handle); }
    }
}
