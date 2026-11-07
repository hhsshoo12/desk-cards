using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeskCards.Shared;

/// <summary>
/// 설치 폴더에 앱이 둔 파일 목록(files.txt)과, 새 버전으로 통째로 바꿔 끼우는 일.
/// 설치 폴더에 앱과 상관없는 파일이 있을 수 있어서, 지울 때는 이 목록에 있는 파일만 지운다.
/// 실행 중인 파일은 지우지 못해도 이름은 바꿀 수 있으므로, 옛 파일은 .old로 비켜 두었다가 나중에 지운다.
/// </summary>
internal static class AppFiles
{
    public const string ManifestName = "files.txt";
    private const string LeftoverMark = "-";
    private static readonly string[] Reserved = { "uninstall.exe", InstalledVersionFile.Name, ManifestName };

    /// <summary>폴더 안의 앱 파일(상대 경로). 제거기·버전 표식·목록 파일은 따로 다루므로 뺀다.</summary>
    public static List<string> Enumerate(string dir)
    {
        string root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFullPath(f).Substring(root.Length))
            .Where(r => !Reserved.Contains(r, StringComparer.OrdinalIgnoreCase))
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ManifestPath(string installDir) => Path.Combine(installDir, ManifestName);

    private static IEnumerable<string> Lines(string installDir) =>
        File.Exists(ManifestPath(installDir)) ? File.ReadAllLines(ManifestPath(installDir)).Select(l => l.Trim()).Where(l => l.Length > 0) : Enumerable.Empty<string>();

    /// <summary>설치된 앱 파일. 목록이 없는 옛 설치는 DeskCards.exe 하나다.</summary>
    public static List<string> Read(string installDir)
    {
        var list = Lines(installDir).Where(l => !l.StartsWith(LeftoverMark)).ToList();
        return list.Count > 0 ? list : new List<string> { "DeskCards.exe" };
    }

    /// <summary>지우지 못해 남은 .old 파일에 해당하는 원래 파일(상대 경로).</summary>
    public static List<string> Leftovers(string installDir) =>
        Lines(installDir).Where(l => l.StartsWith(LeftoverMark)).Select(l => l.Substring(1)).ToList();

    public static void Write(string installDir, IEnumerable<string> files, IEnumerable<string>? leftovers = null)
    {
        string path = ManifestPath(installDir), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temp, files.Concat((leftovers ?? Enumerable.Empty<string>()).Select(l => LeftoverMark + l)));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>지금 파일들의 .old와 남은 .old를 지운다. 못 지우는 것은 목록에 남긴다.</summary>
    public static void CleanLeftovers(string installDir)
    {
        var files = Read(installDir);
        var remaining = new List<string>();
        foreach (string rel in files.Concat(Leftovers(installDir)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string old = Path.Combine(installDir, rel + ".old");
            if (!TryDelete(old) && File.Exists(old)) remaining.Add(rel);
        }
        if (File.Exists(ManifestPath(installDir))) Write(installDir, files, remaining);
    }

    public static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>목록에 있는 파일(과 .old, 목록 파일)을 지우고 빈 하위 폴더를 치운다. 설치 폴더 자체와 다른 파일은 건드리지 않는다.</summary>
    public static void Remove(string installDir)
    {
        var rels = Read(installDir).Concat(Leftovers(installDir)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string rel in rels)
        {
            File.Delete(Path.Combine(installDir, rel));
            File.Delete(Path.Combine(installDir, rel + ".old"));
        }
        File.Delete(ManifestPath(installDir));
        foreach (string? dir in rels.Select(r => Path.GetDirectoryName(r)).Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d!.Length))
            for (string? d = dir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            {
                string full = Path.Combine(installDir, d);
                if (Directory.Exists(full) && Directory.GetFileSystemEntries(full).Length == 0) Directory.Delete(full);
            }
    }

    /// <summary>stage의 파일로 설치 폴더의 앱 파일을 바꾼다. 되돌리려면 Rollback, 확정하려면 Commit.</summary>
    public sealed class Swap
    {
        private readonly string _dir;
        private readonly List<string> _moved = new(), _created = new();
        public List<string> Files { get; }

        private Swap(string dir, List<string> files) { _dir = dir; Files = files; }

        public static Swap Apply(string installDir, string stage)
        {
            var swap = new Swap(installDir, Enumerate(stage));
            try
            {
                foreach (string rel in Read(installDir).Concat(swap.Files).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string target = Path.Combine(installDir, rel);
                    if (!File.Exists(target)) continue;
                    File.Delete(target + ".old");
                    File.Move(target, target + ".old");
                    swap._moved.Add(rel);
                }
                foreach (string rel in swap.Files)
                {
                    string target = Path.Combine(installDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Path.Combine(stage, rel), target);
                    swap._created.Add(rel);
                }
            }
            catch { swap.Rollback(); throw; }
            return swap;
        }

        public void Rollback()
        {
            foreach (string rel in _created) TryDelete(Path.Combine(_dir, rel));
            foreach (string rel in _moved)
            {
                string target = Path.Combine(_dir, rel);
                File.Delete(target);
                File.Move(target + ".old", target);
            }
            _created.Clear();
            _moved.Clear();
        }

        /// <summary>새 목록을 적고 .old를 지운다. 못 지운 것은 목록에 남겨 다음에 다시 지운다.</summary>
        public void Commit()
        {
            var leftovers = _moved.Where(rel => !TryDelete(Path.Combine(_dir, rel + ".old"))).ToList();
            Write(_dir, Files, leftovers);
        }
    }
}
