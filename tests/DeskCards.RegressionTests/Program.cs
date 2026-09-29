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
        Test("hover expand delay is clamped to 0-1 s in 0.1 s steps", () =>
        {
            string path = Path.Combine(root, "hover.json");
            File.WriteAllText(path, """{"HoverExpandDelay":-50}""");
            var low = Config.Load(path);
            File.WriteAllText(path, """{"HoverExpandDelay":99999}""");
            var high = Config.Load(path);
            Check(low.HoverExpandDelay == 0 && high.HoverExpandDelay == 1000 && low.HoverExpand && Config.NormalizeHoverDelay(449) == 400);
        });
        Test("card bar settings are normalized", () =>
        {
            string path = Path.Combine(root, "bar.json");
            File.WriteAllText(path, """{"BarEdge":9,"BarKeys":[164,16,160,0,999,68],"BarSize":80,"BarDelay":2345,"ShowGuides":false}""");
            var cfg = Config.Load(path);
            Check(cfg.BarEdge == ScreenEdge.Right && string.Join(",", cfg.BarKeys) == "18,16,68" && KeyCombo.Text(cfg.BarKeys) == "Alt + Shift + D"
                && string.Join(",", KeyCombo.Clean(new[] { KeyCombo.Ctrl })) == "17" && KeyCombo.Clean(new[] { 0x41 }).Single() == KeyCombo.Shift
                && !KeyCombo.IsValid(new[] { KeyCombo.Ctrl, 0x41, 0x42 }) && KeyCombo.ToHotkey(cfg.BarKeys) == (0x5u, 0x44u) && cfg.BarSize == 33
                && cfg.BarDelay == 2000 && !cfg.ShowGuides && cfg.BarEnabled);
        });
        Test("card bar edge is chosen per display", () =>
        {
            string path = Path.Combine(root, "bar-edges.json");
            File.WriteAllText(path, """{"BarEdge":0,"BarEdges":{"D2":null,"D3":7,"D4":1}}""");
            var cfg = Config.Load(path);
            var mgr = new GroupManager(Path.Combine(root, "bar-edge-groups"), cfg);
            try
            {
                bool loaded = mgr.BarEdgeFor("D2") == null && mgr.BarEdgeFor("D4") == ScreenEdge.Top
                    && mgr.BarEdgeFor("D3") == ScreenEdge.Left && mgr.BarEdgeFor("D9") == ScreenEdge.Left;
                mgr.SetBarEdge("D9", ScreenEdge.Bottom);
                Check(loaded && mgr.BarEdgeFor("D9") == ScreenEdge.Bottom && mgr.BarEdgeFor("D7") == ScreenEdge.Bottom
                    && Config.Load(path).BarEdges["D9"] == ScreenEdge.Bottom);
            }
            finally { mgr.Shutdown(); }
        });
        Test("card bar zone grows from the edge inside the work area", () =>
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;
            var b = screen.Bounds;
            var touch = EdgeBar.ZoneRect(screen, ScreenEdge.Right, 0);
            var wide = EdgeBar.ZoneRect(screen, ScreenEdge.Right, 25);
            Check(touch.Width == 1 && touch.Right == b.Right && wide.Right == b.Right
                && Math.Abs(wide.Width - (1 + b.Width * 0.025)) <= 1 && wide.Top == screen.WorkingArea.Top && wide.Bottom == screen.WorkingArea.Bottom);
        });
        Test("zone preview slides out, holds 1.5 s, then hides", () =>
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;
            BarPreview.Zone(screen, ScreenEdge.Right, 25);
            Pump(400);
            var band = app.Windows.OfType<ZoneBand>().Single();
            var fill = Visuals<Border>(band).Single(b => b.Background is SolidColorBrush { Color.A: 0x80 });
            double target = EdgeBar.ZoneRect(screen, ScreenEdge.Right, 25).Width / VisualTreeHelper.GetDpi(band).DpiScaleX;
            bool shown = band.IsVisible && Math.Abs(fill.ActualWidth - target) < 1.5;
            BarPreview.Zone(screen, ScreenEdge.Right, 10); // 바꾸면 유지 시간이 다시 시작된다
            Pump(1200);
            bool held = band.IsVisible;
            Pump(900);
            Check(shown && held && !band.IsVisible);
            band.Close();
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
        Test("saved item order comes first, new items follow by name", () =>
        {
            var files = new[] { "c.lnk", "A.lnk", "b.lnk", "d.lnk", "gone.lnk" }.Take(4);
            var arranged = GroupModel.Arrange(files, f => f, f => f, new[] { "d.lnk", "gone.lnk", "B.LNK" });
            Check(string.Join(",", arranged) == "d.lnk,b.lnk,A.lnk,c.lnk"
                && string.Join(",", GroupModel.Arrange(files, f => f, f => f, null)) == "A.lnk,b.lnk,c.lnk,d.lnk");
        });
        Test("item order is saved and follows group rename", () =>
        {
            var cfg = Config.Load(Path.Combine(root, "order.json"));
            var mgr = new GroupManager(Path.Combine(root, "order-groups"), cfg);
            try
            {
                mgr.Start();
                var card = mgr.Cards.Single();
                File.WriteAllText(Path.Combine(card.Group.Folder, "x.txt"), "");
                File.WriteAllText(Path.Combine(card.Group.Folder, "y.txt"), "");
                card.Group.Reload();
                mgr.SetOrder(card.Group, card.Group.Items.AsEnumerable().Reverse());
                bool reversed = card.Group.Items[0].Name == "y";
                Check(mgr.RenameGroup(card, "순서"));
                var renamed = Config.Load(Path.Combine(root, "order.json"));
                Check(reversed && renamed.Orders.TryGetValue("순서", out var order) && order[0] == "y.txt");
            }
            finally { mgr.Shutdown(); }
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
        Test("card bar keeps its own free layout and bar-only groups stay off the desktop", () =>
        {
            string groups = Path.Combine(root, "bar-groups");
            Directory.CreateDirectory(Path.Combine(groups, "DESK"));
            string cfgPath = Path.Combine(root, "bar.json");
            var cfg = Config.Load(cfgPath);
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                Pump(100);
                Check(mgr.BarItems.Count == 0); // 처음엔 빈 바
                var desk = mgr.Cards.Single();
                Check(mgr.CardsNotInBar().Single() == desk);

                // 바에만 있는 그룹: 카드는 있지만 바탕화면에는 뜨지 않고, 편집·배치 대상에서도 빠진다.
                var only = mgr.NewBarGroup()!;
                Pump(100);
                Check(mgr.IsBarOnly(only.Group.Name) && !only.IsVisible && !mgr.AllCards.Contains(only));
                Check(!mgr.CardsNotInBar().Contains(only));

                mgr.SetBarItems(new[]
                {
                    new BarItem { Group = "DESK", X = 0.1, Y = 0.2, W = 0.8 },
                    new BarItem { Group = only.Group.Name, X = 0.1, Y = 2, W = 0.5 },
                });
                var saved = Config.Load(cfgPath);
                Check(saved.BarItems.Count == 2 && saved.BarItems[0].Y == 0.2 && saved.BarOnlyGroups.Single() == only.Group.Name);

                // 이름을 바꾸면 바 배치와 바 전용 표시도 따라간다.
                Check(mgr.RenameGroup(only, "ONLY"));
                Check(mgr.BarItems.Any(i => i.Group == "ONLY") && mgr.IsBarOnly("ONLY"));

                // 바는 저장된 자리에 그대로 놓는다(바 두께 단위). 넣은 카드는 빈자리에, 다른 카드와 겹치지 않게.
                var screen = System.Windows.Forms.Screen.PrimaryScreen!;
                var bar = new BarWindow(mgr, screen, ScreenEdge.Right);
                try
                {
                    bar.Open();
                    Pump(500);
                    Check(!bar.IsEditing);
                    // 오른쪽 위 작은 설정 버튼: 평소엔 보이고 편집 중엔 숨는다.
                    Border SettingsButton() => Visuals<Border>(bar).Single(b => b.ToolTip as string == "카드 바 설정");
                    Check(SettingsButton().IsVisible);
                    if (Environment.GetEnvironmentVariable("DESKCARDS_SNAPSHOT_DIR") is { Length: > 0 } dir)
                        Snapshot(bar, Path.Combine(dir, "card-bar.png"));
                    bar.BeginEdit();
                    Pump(100);
                    Check(bar.IsEditing && app.Windows.OfType<EditBar>().Count() == 1 && !SettingsButton().IsVisible);
                    bar.EndEdit();
                    Pump(100);
                    Check(!bar.IsEditing && !app.Windows.OfType<EditBar>().Any() && SettingsButton().IsVisible);
                }
                finally { bar.Close(); }

                // 바탕화면으로 꺼내면 바 전용 표시가 풀리고 바탕화면에 뜬다.
                mgr.ShowOnDesktop(mgr.GroupCard("ONLY")!);
                Pump(100);
                Check(!mgr.IsBarOnly("ONLY") && mgr.GroupCard("ONLY")!.IsVisible && mgr.AllCards.Contains(mgr.GroupCard("ONLY")!));

                // 그룹을 지우면 바에서도 빠진다.
                Check(mgr.DeleteGroup(mgr.GroupCard("DESK")!));
                Check(mgr.BarItems.All(i => i.Group != "DESK"));
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
                Test("card bar settings keep descriptions readable", () =>
                {
                    var go = typeof(SettingsWindow).GetMethod("Go", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var barPage = Enum.Parse(go.GetParameters()[0].ParameterType, "Bar");
                    go.Invoke(settings, new[] { barPage });
                    foreach (double width in new[] { settings.MinWidth, 760.0, 1100.0 })
                    {
                        settings.Width = width;
                        settings.UpdateLayout();
                        Pump(50);
                        settings.UpdateLayout();
                        if (Environment.GetEnvironmentVariable("DESKCARDS_SNAPSHOT_DIR") is { Length: > 0 } dir)
                            Snapshot(settings, Path.Combine(dir, $"bar-{width:0}.png"));
                        var descriptions = Visuals<TextBlock>(settings).Where(t => t.FontSize == 12 && t.Text.Length > 20).ToList();
                        // 버튼이 창 오른쪽 밖으로 잘려 나가지 않는다(선택 버튼이 많으면 다음 줄로 넘어간다).
                        var content = (FrameworkElement)settings.Content;
                        bool inside = Visuals<Button>(settings).Where(b => b.IsVisible)
                            .All(b => b.TranslatePoint(new Point(b.ActualWidth, 0), content).X <= content.ActualWidth - 12);
                        // 짧아서 한 줄에 다 들어가는 설명은 원래 폭이 좁으니 괜찮다. 여러 줄로 꺾였는데 좁으면 문제.
                        bool Readable(TextBlock t) => t.ActualWidth >= 150 || t.ActualHeight <= 20;
                        var narrow = descriptions.Where(t => !Readable(t)).Select(t => $"{t.Text[..10]}={t.ActualWidth:0}");
                        var clipped = Visuals<Button>(settings).Where(b => b.IsVisible && b.TranslatePoint(new Point(b.ActualWidth, 0), content).X > content.ActualWidth - 12).Select(b => b.Content);
                        if (!(descriptions.Count > 0 && descriptions.All(Readable) && inside))
                            throw new Exception($"width {width}: narrow [{string.Join(", ", narrow)}] clipped [{string.Join(", ", clipped)}]");
                    }
                });
                Test("settings page keeps its scroll position when a value change redraws it", () =>
                {
                    var go = typeof(SettingsWindow).GetMethod("Go", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    go.Invoke(settings, new[] { Enum.Parse(go.GetParameters()[0].ParameterType, "Bar") });
                    settings.Width = 760;
                    settings.Height = 460;
                    settings.UpdateLayout();
                    Pump(400);
                    settings.PageScroller.ScrollToEnd();
                    Pump(100);
                    double before = settings.PageScroller.VerticalOffset;
                    mgr.BarDelay = mgr.BarDelay == 500 ? 600 : 500; // 값이 바뀌면 페이지를 다시 그린다
                    Pump(200);
                    double after = settings.PageScroller.VerticalOffset;
                    if (!(before > 0 && Math.Abs(after - before) < 1)) throw new Exception($"offset {before:0} -> {after:0}");
                });
                Test("key combo subpage opens and returns with escape", () =>
                {
                    var go = typeof(SettingsWindow).GetMethod("Go", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var kind = go.GetParameters()[0].ParameterType;
                    go.Invoke(settings, new[] { Enum.Parse(kind, "BarKeys") });
                    settings.Width = 900;
                    settings.UpdateLayout();
                    Pump(50);
                    if (Environment.GetEnvironmentVariable("DESKCARDS_SNAPSHOT_DIR") is { Length: > 0 } dir)
                        Snapshot(settings, Path.Combine(dir, "bar-keys.png"));
                    bool editor = Visuals<KeyComboEditor>(settings).Count() == 1;
                    go.Invoke(settings, new[] { Enum.Parse(kind, "Bar") });
                    Check(editor && !Visuals<KeyComboEditor>(settings).Any());
                });
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
        DwmCardTests(root);
        DardTests(root, app);
        UpdateTests(root);
        Console.WriteLine($"Failures: {_failed}");
        app.Shutdown();
        return _failed == 0 ? 0 : 1;
    }
    // ----- .dard 카드: 임시 루트와 임시 브라우저 데이터만 쓴다 -----

    private const string ClockManifest = """
        { "dard": 1, "id": "com.test.clock", "name": "시계", "version": "1.0.0",
          "cards": [ { "id": "main", "ratio": [2, 1] }, { "id": "mini", "name": "작은 시계", "ratio": [1, 1] } ] }
        """;

    private static byte[] Dard(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
        return ms.ToArray();
    }

    private static void DardTests(string root, Application app)
    {
        AppPaths.WebDataDir = Path.Combine(root, "webview2");
        Test(".dard manifest is read with cards, ratios and default settings ratio", () =>
        {
            var pkg = DardPackage.Parse(Dard(("manifest.json", ClockManifest), ("card.html", "<p>hi"), ("settings.html", "<p>s")), "x.dard");
            Check(pkg.Id == "com.test.clock" && pkg.Cards.Count == 2 && pkg.Cards[1].Name == "작은 시계" && pkg.Cards[0].Name == "시계");
            Check(pkg.Cards[0].RatioW == 2 && pkg.SettingsRatio == (3, 4) && pkg.Host == "com.test.clock.card.desk" && pkg.Permissions.Count == 0);
            var size = DardPackage.SizeFor(2, 1, DardPackage.CardArea);
            Check(Math.Abs(size.Width * size.Height - DardPackage.CardArea) < 0.01 && Math.Abs(size.Width / size.Height - 2) < 0.001);
        });
        Test(".dard rejects extra files, bad ratios, unknown permissions and zip bombs", () =>
        {
            bool Rejected(byte[] bytes)
            {
                try { DardPackage.Parse(bytes, "x.dard"); return false; }
                catch (DardException) { return true; }
            }
            Check(Rejected(Dard(("manifest.json", ClockManifest), ("card.html", ""), ("run.exe", "MZ"))));
            Check(Rejected(Dard(("manifest.json", ClockManifest), ("card.html", ""), ("sub/card.html", ""))));
            Check(Rejected(Dard(("manifest.json", ClockManifest.Replace("[2, 1]", "[9, 1]")), ("card.html", ""))));
            Check(Rejected(Dard(("manifest.json", ClockManifest.Replace("\"version\": \"1.0.0\",", "\"version\": \"1.0.0\", \"permissions\": { \"shell\": true },")), ("card.html", ""))));
            Check(Rejected(Dard(("manifest.json", ClockManifest.Replace("com.test.clock", "Com.Test")), ("card.html", ""))));
            Check(Rejected(Dard(("manifest.json", ClockManifest))));
            Check(Rejected(Dard(("manifest.json", ClockManifest), ("card.html", new string('a', 33 * 1024 * 1024)))));
            Check(Rejected(new byte[] { 1, 2, 3 }));
            var perms = DardPackage.Parse(Dard(("manifest.json", ClockManifest.Replace("\"version\": \"1.0.0\",",
                "\"version\": \"1.0.0\", \"permissions\": { \"system\": [\"cpu\"], \"internet\": [\"api.example.com\"] },")), ("card.html", "")), "x.dard");
            Check(perms.Permissions.Count == 2);
        });
        Test("approved .dard cards load, follow file changes and unload when removed", () =>
        {
            string groups = Path.Combine(root, "dard-groups");
            Directory.CreateDirectory(Path.Combine(groups, "그룹"));
            string file = Path.Combine(groups, "시계.dard");
            File.WriteAllBytes(file, Dard(("manifest.json", ClockManifest), ("card.html", "<p>1"), ("settings.html", "<p>s")));
            var cfg = Config.Load(Path.Combine(root, "dard.json"));
            cfg.Dards["com.test.clock"] = new DardApproval { Hash = DardPackage.Load(file).Hash, Allowed = true };
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                var cards = mgr.AllCards.OfType<DardWindow>().ToList();
                Check(mgr.Dards.Count == 1 && cards.Count == 2 && mgr.Cards.Count == 1);
                Check(cards.Select(c => c.Key).OrderBy(k => k).SequenceEqual(new[] { "dard:com.test.clock/main", "dard:com.test.clock/mini" }));
                Check(cfg.Positions.ContainsKey("dard:com.test.clock/main"));
                var main = cards.Single(c => c.Info.Id == "main");
                Check(Math.Abs(main.Width / main.Height - 2) < 0.1); // 둥근 창은 여백·이름 줄 없이 비율 그대로
                Test(".dard 카드도 공통 아크릴 틀을 쓰고 활성화는 허용한다", () => CheckDwmCard(main, activatable: true));
                Test(".dard 웹 화면은 편집 중 캡처로 바뀌고 끝나면 같은 확대 비율로 돌아온다", () => CheckDardEditing(main));

                mgr.SetDardSettings(main.Key, System.Text.Json.Nodes.JsonNode.Parse("""{"hour24":false}"""));
                Check((bool?)mgr.GetDardSettings(main.Key)?["hour24"] == false);
                Check(Config.Load(Path.Combine(root, "dard.json")).DardSettings.ContainsKey(main.Key));

                main.Runtime.OpenSettings("main");
                // 설정 창은 포커스를 잃으면 스스로 닫힌다. 테스트 중에 다른 창을 쓰면 여기서 실패할 수 있다.
                Pump(100);
                Check(app.Windows.OfType<DardSettingsWindow>().Count() == 1);

                // 권한이 같은 새 버전은 다시 묻지 않고 바꿔 띄운다(설정 창은 닫힌다).
                File.WriteAllBytes(file, Dard(("manifest.json", ClockManifest), ("card.html", "<p>2"), ("settings.html", "<p>s")));
                Pump(900);
                var reloaded = mgr.AllCards.OfType<DardWindow>().ToList();
                Check(reloaded.Count == 2 && !reloaded.Contains(main) && cfg.Dards["com.test.clock"].Hash == DardPackage.Load(file).Hash);
                Check(!app.Windows.OfType<DardSettingsWindow>().Any());

                File.Delete(file);
                Pump(900);
                Check(mgr.Dards.Count == 0 && !mgr.AllCards.OfType<DardWindow>().Any() && mgr.Cards.Count == 1);
            }
            finally { mgr.Shutdown(); }
            Check(!app.Windows.OfType<DardWindow>().Any());
        });
        Test("declined .dard is not loaded", () =>
        {
            string groups = Path.Combine(root, "dard-declined");
            Directory.CreateDirectory(Path.Combine(groups, "그룹"));
            string file = Path.Combine(groups, "시계.dard");
            File.WriteAllBytes(file, Dard(("manifest.json", ClockManifest), ("card.html", "<p>1")));
            var cfg = Config.Load(Path.Combine(root, "dard-declined.json"));
            cfg.Dards["com.test.clock"] = new DardApproval { Hash = DardPackage.Load(file).Hash, Allowed = false };
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                Check(mgr.Dards.Count == 0 && !mgr.AllCards.OfType<DardWindow>().Any());
            }
            finally { mgr.Shutdown(); }
        });
    }

    // ----- 앱 자체 업데이트: 임시 설치 폴더와 가짜 네트워크만 쓴다 -----

    private const string ReleasesJson = """
        [
          {"tag_name":"installer-v0.9.0","draft":false,"prerelease":false,"assets":[]},
          {"tag_name":"app-v0.10.0","draft":true,"prerelease":false,"assets":[{"name":"DeskCards-win-x64.zip","browser_download_url":"https://x/a"},{"name":"DeskCards-win-x64.zip.sha256","browser_download_url":"https://x/b"}]},
          {"tag_name":"app-v0.3.0","draft":false,"prerelease":true,"assets":[{"name":"DeskCards-win-x64.zip","browser_download_url":"https://x/a"},{"name":"DeskCards-win-x64.zip.sha256","browser_download_url":"https://x/b"}]},
          {"tag_name":"app-v0.2.9","draft":false,"prerelease":false,"assets":[{"name":"DeskCards-win-x64.zip","browser_download_url":"https://x/a"}]},
          {"tag_name":"app-v0.2.10","draft":false,"prerelease":false,"assets":[{"name":"DeskCards-win-x64.zip","browser_download_url":"https://x/zip"},{"name":"DeskCards-win-x64.zip.sha256","browser_download_url":"https://x/hash"}]},
          {"tag_name":"app-v0.2.1","draft":false,"prerelease":false,"assets":[{"name":"DeskCards-win-x64.zip","browser_download_url":"https://x/zip"},{"name":"DeskCards-win-x64.zip.sha256","browser_download_url":"https://x/hash"}]}
        ]
        """;

    private sealed class FakeUpdate
    {
        public string Install = "", Zip = "", Hash = "";
        public string? Relaunched;
        public Version? Registered;
        public UpdateHost Host(bool installed = true) => new()
        {
            InstallDir = Install,
            Installed = installed,
            ReadText = (url, _) => Task.FromResult(url == UpdatePackage.ReleasesUrl ? ReleasesJson : Hash),
            Download = (_, path, progress, _) => { File.Copy(Zip, path); progress.Report(1); return Task.CompletedTask; },
            SetInstalledVersion = v => Registered = v,
            Relaunch = exe => Relaunched = exe,
            LogPath = Path.Combine(Install, "..", "update.log"),
        };
    }

    private static FakeUpdate UpdateSandbox(string root, string name, string version = "0.2.10", string? extra = null, bool badHash = false)
    {
        string dir = Path.Combine(root, name);
        var f = new FakeUpdate { Install = Path.Combine(dir, "install"), Zip = Path.Combine(dir, "pkg.zip") };
        Directory.CreateDirectory(f.Install);
        File.WriteAllText(Path.Combine(f.Install, "DeskCards.exe"), "old app");
        using (var zip = System.IO.Compression.ZipFile.Open(f.Zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("DeskCards.exe").Open())) w.Write("new app");
            using (var w = new StreamWriter(zip.CreateEntry("version.txt").Open())) w.Write(version);
            if (extra != null) using (var w = new StreamWriter(zip.CreateEntry(extra).Open())) w.Write("x");
        }
        using var s = File.OpenRead(f.Zip);
        f.Hash = (badHash ? new string('0', 64) : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s))) + "  DeskCards-win-x64.zip";
        return f;
    }

    private static Updater Downloaded(FakeUpdate f, Config cfg, bool installed = true)
    {
        // UI 스레드 없이 돌려서 이어지는 작업이 멈추지 않게 한다.
        return Task.Run(async () =>
        {
            var u = new Updater(cfg, f.Host(installed), new Version(0, 2, 1));
            await u.CheckAsync();
            await u.DownloadAsync();
            return u;
        }).GetAwaiter().GetResult();
    }

    private static void UpdateTests(string root)
    {
        Test("update picks highest published app release", () =>
            Check(UpdatePackage.Latest(ReleasesJson)?.Version == new Version(0, 2, 10)));
        Test("update downloads, verifies and stages the new version", () =>
        {
            var f = UpdateSandbox(root, "upd-ok");
            var cfg = Config.Load(Path.Combine(root, "upd-ok", "config.json"));
            var u = Downloaded(f, cfg);
            Check(u.State == UpdateState.Ready && cfg.PendingUpdate == "0.2.10");
            Check(File.ReadAllText(Path.Combine(f.Install, "update", "0.2.10", "DeskCards.exe")) == "new app");
            Check(File.ReadAllText(Path.Combine(f.Install, "DeskCards.exe")) == "old app");
            Check(Config.Load(Path.Combine(root, "upd-ok", "config.json")).PendingUpdate == "0.2.10");
        });
        foreach (var (name, sandbox) in new (string, Func<FakeUpdate>)[]
        {
            ("bad hash", () => UpdateSandbox(root, "upd-hash", badHash: true)),
            ("version mismatch", () => UpdateSandbox(root, "upd-ver", version: "0.2.9")),
            ("unsafe path", () => UpdateSandbox(root, "upd-slip", extra: "../evil.txt")),
        })
            Test($"update rejects {name} and leaves nothing staged", () =>
            {
                var f = sandbox();
                var cfg = Config.Load(Path.Combine(f.Install, "..", "config.json"));
                var u = Downloaded(f, cfg);
                Check(u.State == UpdateState.Failed && cfg.PendingUpdate == null);
                Check(!Directory.Exists(Path.Combine(f.Install, "update")) && !File.Exists(Path.Combine(f.Install, "..", "evil.txt")));
                Check(File.ReadAllText(Path.Combine(f.Install, "DeskCards.exe")) == "old app");
            });
        Test("pending update swaps executables and relaunches", () =>
        {
            var f = UpdateSandbox(root, "upd-apply");
            var cfg = Config.Load(Path.Combine(root, "upd-apply", "config.json"));
            Downloaded(f, cfg);
            Check(Updater.ApplyPending(cfg, f.Host(), new Version(0, 2, 1)));
            string exe = Path.Combine(f.Install, "DeskCards.exe");
            Check(File.ReadAllText(exe) == "new app" && File.ReadAllText(exe + ".old") == "old app");
            Check(f.Relaunched == exe && f.Registered == new Version(0, 2, 10) && cfg.PendingUpdate == null);
            UpdatePackage.Cleanup(f.Install);
            Check(!File.Exists(exe + ".old") && !Directory.Exists(Path.Combine(f.Install, "update")));
        });
        Test("pending update not newer than running version is discarded", () =>
        {
            var f = UpdateSandbox(root, "upd-stale");
            var cfg = Config.Load(Path.Combine(root, "upd-stale", "config.json"));
            Downloaded(f, cfg);
            Check(!Updater.ApplyPending(cfg, f.Host(), new Version(0, 2, 10)));
            Check(cfg.PendingUpdate == null && f.Relaunched == null && !Directory.Exists(Path.Combine(f.Install, "update")));
        });
        Test("development build checks but never downloads or applies", () =>
        {
            var f = UpdateSandbox(root, "upd-dev");
            var cfg = Config.Load(Path.Combine(root, "upd-dev", "config.json"));
            var u = Downloaded(f, cfg, installed: false);
            Check(u.State == UpdateState.Available && !u.CanUpdate && cfg.PendingUpdate == null);
            cfg.PendingUpdate = "0.2.10";
            Check(!Updater.ApplyPending(cfg, f.Host(installed: false), new Version(0, 2, 1)) && f.Relaunched == null);
            Check(File.ReadAllText(Path.Combine(f.Install, "DeskCards.exe")) == "old app");
        });
    }

    private static void Check(bool condition, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) { if (!condition) throw new Exception($"Assertion failed (line {line})"); }
    private static void Snapshot(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Shapes.Rectangle { Width = root.ActualWidth, Height = root.ActualHeight, Fill = System.Windows.Media.Brushes.White };
        bg.Measure(new Size(root.ActualWidth, root.ActualHeight));
        bg.Arrange(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(bg);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

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
