using System.IO;
using System.Linq;
using DeskCards.Shared;

internal static partial class Program
{
    private static void FolderSwapTests(string root)
    {
        static void Put(string dir, string rel, string text)
        {
            string path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        Test("folder update swaps every file, drops removed ones and keeps unrelated files", () =>
        {
            string install = Path.Combine(root, "swap-install"), stage = Path.Combine(root, "swap-stage");
            Put(install, "DeskCards.exe", "old"); Put(install, "a.dll", "old a"); Put(install, "gone.dll", "old gone"); Put(install, "mine.txt", "user");
            AppFiles.Write(install, new[] { "DeskCards.exe", "a.dll", "gone.dll" });
            Put(stage, "DeskCards.exe", "new"); Put(stage, "a.dll", "new a"); Put(stage, "sub\\b.dll", "new b"); Put(stage, "uninstall.exe", "u");

            AppFiles.Swap.Apply(install, stage).Commit();

            Check(File.ReadAllText(Path.Combine(install, "DeskCards.exe")) == "new" && File.ReadAllText(Path.Combine(install, "sub", "b.dll")) == "new b");
            Check(!File.Exists(Path.Combine(install, "gone.dll")) && !Directory.GetFiles(install, "*.old").Any());
            Check(File.ReadAllText(Path.Combine(install, "mine.txt")) == "user" && !File.Exists(Path.Combine(install, "uninstall.exe")));
            Check(string.Join(",", AppFiles.Read(install)) == "a.dll,DeskCards.exe,sub\\b.dll".Replace("a.dll,DeskCards.exe", "a.dll,DeskCards.exe"));
        });
        Test("leftover .old files are removed later and stay listed until then", () =>
        {
            string install = Path.Combine(root, "swap-leftover");
            Put(install, "DeskCards.exe", "new"); Put(install, "DeskCards.exe.old", "old"); Put(install, "gone.dll.old", "old");
            AppFiles.Write(install, new[] { "DeskCards.exe" }, new[] { "gone.dll" });
            Check(AppFiles.Leftovers(install).Count == 1);
            AppFiles.CleanLeftovers(install);
            Check(!Directory.GetFiles(install, "*.old").Any() && AppFiles.Leftovers(install).Count == 0 && AppFiles.Read(install).Count == 1);
        });
        Test("failed folder update puts the old files back", () =>
        {
            string install = Path.Combine(root, "swap-fail"), stage = Path.Combine(root, "swap-fail-stage");
            Put(install, "DeskCards.exe", "old"); Put(install, "a.dll", "old a");
            AppFiles.Write(install, new[] { "DeskCards.exe", "a.dll" });
            Put(stage, "DeskCards.exe", "new"); Put(stage, "a.dll", "new a");
            Put(stage, "x\\c.dll", "c");
            Put(install, "x", "a file where a folder must go"); // 새 폴더를 만들 수 없게 막는다
            bool failed = false;
            try { AppFiles.Swap.Apply(install, stage); } catch (IOException) { failed = true; }
            Check(failed && File.ReadAllText(Path.Combine(install, "DeskCards.exe")) == "old" && File.ReadAllText(Path.Combine(install, "a.dll")) == "old a");
            Check(!Directory.GetFiles(install, "*.old").Any());
        });
        Test("uninstall removes only listed files and empty folders", () =>
        {
            string install = Path.Combine(root, "swap-remove");
            Put(install, "DeskCards.exe", "x"); Put(install, "sub\\b.dll", "x"); Put(install, "mine.txt", "user");
            AppFiles.Write(install, new[] { "DeskCards.exe", "sub\\b.dll" });
            AppFiles.Remove(install);
            Check(!File.Exists(Path.Combine(install, "DeskCards.exe")) && !Directory.Exists(Path.Combine(install, "sub")) && File.Exists(Path.Combine(install, "mine.txt")));
        });
    }
}
