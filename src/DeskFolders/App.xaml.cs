using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DeskFolders;

public partial class App : Application
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "DeskFolders";

    private Mutex? _mutex;
    private GroupManager? _mgr;
    private Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, "DeskFolders.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        Theme.Apply();
        SystemEvents.UserPreferenceChanged += (_, a) =>
        {
            if (a.Category == UserPreferenceCategory.General) Dispatcher.BeginInvoke(Theme.Apply);
        };

        _mgr = new GroupManager();
        _mgr.Start();
        CreateTray();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("새 그룹", null, (_, _) => _mgr!.NewGroup());
        menu.Items.Add("그룹 폴더 열기", null, (_, _) => FileOps.OpenFolder(_mgr!.Root));
        var startup = new Forms.ToolStripMenuItem("Windows 시작 시 실행") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Quit());

        _tray = new Forms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "DeskFolders",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => FileOps.OpenFolder(_mgr!.Root);
    }

    private void Quit()
    {
        _mgr?.Shutdown();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunName) != null;
    }

    private static void SetStartup(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunName, $"\"{Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName}\"");
        else key.DeleteValue(RunName, false);
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
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
