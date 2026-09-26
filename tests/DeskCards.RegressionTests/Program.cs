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
using DeskCards;

internal static class Program
{
    private static int _failed;
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply();
        // Keep the sandbox as diagnostic evidence. Never touch the user's groups/config.
        string root = Path.Combine(Path.GetTempPath(), "DeskCards-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("Sandbox: " + root);
        Test("null position entries are discarded", () =>
        {
            string path = Path.Combine(root, "null-position.json");
            File.WriteAllText(path, """{"Positions":{"bad":null,"good":[12,34]},"ScaleVersion":2}""");
            var cfg = Config.Load(path);
            Check(!cfg.Positions.ContainsKey("bad") && cfg.Positions["good"][0] == 12);
        });
        Test("null layouts do not crash scale migration", () =>
        {
            string path = Path.Combine(root, "null-layout.json");
            File.WriteAllText(path, """{"Layouts":{"bad":null,"good":{"Cols":3}},"ScaleVersion":0}""");
            var cfg = Config.Load(path);
            Check(!cfg.Layouts.ContainsKey("bad") && cfg.Layouts["good"].Cols == 3);
        });
        Test("case-colliding keys preserve unrelated settings", () =>
        {
            string path = Path.Combine(root, "case.json");
            File.WriteAllText(path, """{"Positions":{"Test":[1,2],"test":[3,4]},"ShowGuides":false}""");
            var cfg = Config.Load(path);
            Check(!cfg.ShowGuides && cfg.Positions.Count == 1);
        });
        Test("non-finite zoom is normalized", () => Check(double.IsFinite(new CardLayout { Zoom = double.NaN }.Normalized().Zoom)));
        Test("saved backup recovers interrupted configuration", () =>
        {
            string path = Path.Combine(root, "atomic.json");
            var cfg = Config.Load(path);
            cfg.ShowGuides = false;
            cfg.Save();
            cfg.Save();
            File.WriteAllText(path, "{interrupted");
            Check(!Config.Load(path).ShowGuides);
        });
        Test("ancestor directory drop is rejected", () =>
        {
            string folder = Path.Combine(root, "drop-target");
            Directory.CreateDirectory(folder);
            Check(!FileOps.CanAccept(new DataObject(DataFormats.FileDrop, new[] { root }), folder));
        });
        Test("invalid Windows group names are rejected", () =>
        {
            foreach (string name in new[] { "CON", "con.txt", "LPT1", "COM¹", "..", "hello.", "hello ", "a/b", " " })
                Check(!FileOps.IsValidGroupName(name));
            Check(FileOps.IsValidGroupName("작업 2026") && FileOps.IsValidGroupName("COM10"));
        });
        Test("same-folder move does not rename the file", () =>
        {
            string file = Path.Combine(root, "keep.txt");
            File.WriteAllText(file, "original");
            Check(FileOps.MoveTo(file, root));
            Check(File.ReadAllText(file) == "original" && !File.Exists(Path.Combine(root, "keep (2).txt")));
        });
        Test("drop moves group files without overwriting collisions", () =>
        {
            string groups = Path.Combine(root, "move-groups");
            string source = Path.Combine(groups, "one"), target = Path.Combine(groups, "two");
            Directory.CreateDirectory(source); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "file.txt"), "moved");
            File.WriteAllText(Path.Combine(target, "file.txt"), "existing");
            FileOps.AddToGroup(new[] { Path.Combine(source, "file.txt") }, target, groups);
            Check(!File.Exists(Path.Combine(source, "file.txt")));
            Check(File.ReadAllText(Path.Combine(target, "file.txt")) == "existing");
            Check(File.ReadAllText(Path.Combine(target, "file (2).txt")) == "moved");
        });
        Test("ordinary file drop creates a working shortcut and preserves source", () =>
        {
            string file = Path.Combine(root, "shortcut-source.txt"), target = Path.Combine(root, "shortcut-target");
            File.WriteAllText(file, "preserved"); Directory.CreateDirectory(target);
            FileOps.AddToGroup(new[] { file }, target, target);
            Check(File.ReadAllText(file) == "preserved" && new FileInfo(Path.Combine(target, "shortcut-source.lnk")).Length > 0);
        });
        Test("queued watcher callback cannot revive a disposed group", () =>
        {
            var group = new GroupModel(root);
            int changes = 0;
            group.Changed += () => changes++;
            typeof(GroupModel).GetMethod("Bump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(group, null);
            group.Dispose();
            Pump(400);
            Check(changes == 0);
        });
        Test("grid expansion stays inside monitor work area", () =>
        {
            var cfg = Config.Load(Path.Combine(root, "screen.json"));
            cfg.CellSize = 72; cfg.ScaleVersion = 2; cfg.DefaultZoom = 4; cfg.FixedScale = 1;
            var mgr = new GroupManager(Path.Combine(root, "groups"), cfg);
            try
            {
                mgr.Start();
                var card = mgr.Cards.Single();
                card.SetGrid(8, 8);
                card.UpdateLayout();
                var handle = new WindowInteropHelper(card).Handle;
                Native.GetWindowRect(handle, out var r);
                var wa = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
                Check(r.Left >= wa.Left && r.Top >= wa.Top && r.Right <= wa.Right + 1 && r.Bottom <= wa.Bottom + 1);
            }
            finally { mgr.Shutdown(); }
        });
        Test("live rename preserves layout, identity, and file watching", () =>
        {
            string groups = Path.Combine(root, "rename-groups");
            Directory.CreateDirectory(Path.Combine(groups, "before"));
            var cfg = Config.Load(Path.Combine(root, "rename.json"));
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                var card = mgr.Cards.Single();
                card.SetGrid(3, 2);
                Directory.Move(card.Group.Folder, Path.Combine(groups, "after"));
                Pump(800);
                Check(mgr.Cards.Single() == card && card.Group.Name == "after" && card.CurrentLayout.Cols == 3);
                Check(cfg.Layouts.ContainsKey("after") && !cfg.Layouts.ContainsKey("before"));
                File.WriteAllText(Path.Combine(card.Group.Folder, "new.txt"), "new");
                Pump(700);
                Check(card.Group.Items.Count == 1);
                Directory.CreateDirectory(Path.Combine(groups, "AFTER.~tmp"));
                Check(mgr.RenameGroup(card, "AFTER"));
                Pump(500);
                Check(card.Group.Name == "AFTER" && Directory.Exists(Path.Combine(groups, "AFTER.~tmp")));
            }
            finally { mgr.Shutdown(); }
        });
        Test("settings, edit bar, and expanded window lifecycle", () =>
        {
            var cfg = Config.Load(Path.Combine(root, "windows.json"));
            var mgr = new GroupManager(Path.Combine(root, "window-groups"), cfg);
            try
            {
                mgr.Start();
                var card = mgr.Cards.Single();
                SettingsWindow.Open(mgr, card);
                Pump(100);
                Check(app.Windows.OfType<SettingsWindow>().Count() == 1);
                var settings = app.Windows.OfType<SettingsWindow>().Single();
                settings.Width = settings.MinWidth;
                settings.UpdateLayout();
                Test("settings labels remain readable at minimum window width", () =>
                    Check(Visuals<TextBlock>(settings).Single(t => t.Text == "이름").ActualWidth >= 60));
                mgr.BeginEditMode(card);
                Check(mgr.Editing && mgr.Selected == card && card.IsEditing);
                card.SetGrid(4, 2);
                mgr.EndEditMode();
                ExpandedWindow.Open(card, mgr, editTitle: true);
                Pump(100);
                Check(app.Windows.OfType<ExpandedWindow>().Count() == 1);
                ExpandedWindow.CloseFor(card);
                foreach (var window in app.Windows.OfType<SettingsWindow>().ToArray()) window.Close();
                Pump(100);
                Check(!app.Windows.OfType<ExpandedWindow>().Any() && !app.Windows.OfType<EditBar>().Any());
            }
            finally { mgr.Shutdown(); }
        });
        Console.WriteLine($"Failures: {_failed}");
        app.Shutdown();
        return _failed == 0 ? 0 : 1;
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Visuals<T>(child)) yield return descendant;
        }
    }
    private static void Test(string name, Action action)
    {
        try { action(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
}
