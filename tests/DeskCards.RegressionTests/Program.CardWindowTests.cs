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
    private static void CardWindowTests(string root, Application app)
    {
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
                    // 바 옆에 떠 있는 설정 버튼: 바가 다 나오면 보이고, 바의 시작 모서리 옆(화면 안쪽)에 있고, 편집 중엔 숨는다.
                    var gear = app.Windows.OfType<BarFloatButton>().Single();
                    Native.GetWindowRect(Hwnd.Of(bar), out var barRect);
                    Check(gear.IsShown && gear.BodyRect.Right < barRect.Left && gear.BodyRect.Top == barRect.Top);
                    if (Environment.GetEnvironmentVariable("DESKCARDS_SNAPSHOT_DIR") is { Length: > 0 } dir)
                        Snapshot(bar, Path.Combine(dir, "card-bar.png"));
                    bar.BeginEdit();
                    Pump(100);
                    Check(bar.IsEditing && app.Windows.OfType<EditBar>().Count() == 1 && !gear.IsShown);
                    bar.EndEdit();
                    Pump(100);
                    Check(!bar.IsEditing && !app.Windows.OfType<EditBar>().Any() && gear.IsShown);
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
    }
}
