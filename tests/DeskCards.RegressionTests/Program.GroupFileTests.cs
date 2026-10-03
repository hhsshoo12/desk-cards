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
    private static void GroupFileTests(string root)
    {
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
    }
}
