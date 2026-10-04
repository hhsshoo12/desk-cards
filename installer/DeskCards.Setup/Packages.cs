using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace DeskCards.Setup;

internal static class Packages
{
    public const long ExpandedLimit = 1024L * 1024 * 1024;

    public static string Extract(string zip, string stage, Version tagVersion, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(zip);
        long size = 0;
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate every name and declared size before creating the first extracted file.
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('/', '\\');
            string dest = Path.GetFullPath(Path.Combine(stage, name));
            if (Path.IsPathRooted(name) || name.Contains(':') || name.Split('\\').Any(p => p == ".." || p == "." || p.EndsWith(" ") || p.EndsWith("."))
                || !SetupEnvironment.Within(dest, stage) || !names.Add(dest)
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new SetupFailure("압축 파일에 안전하지 않은 경로가 있어요.");
            size = checked(size + entry.Length);
            if (size > ExpandedLimit) throw new SetupFailure("압축을 푼 파일이 허용 크기를 넘었어요.");
        }
        Directory.CreateDirectory(stage);
        long written = 0;
        byte[] buffer = new byte[81920];
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string dest = Path.GetFullPath(Path.Combine(stage, entry.FullName.Replace('/', '\\')));
            if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) { Directory.CreateDirectory(dest); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var input = entry.Open();
            using var output = new FileStream(dest, FileMode.CreateNew);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                written = checked(written + read);
                if (written > ExpandedLimit) throw new SetupFailure("압축을 푼 파일이 허용 크기를 넘었어요.");
                output.Write(buffer, 0, read);
            }
        }
        string exe = Path.Combine(stage, "DeskCards.exe"), versionFile = Path.Combine(stage, "version.txt");
        if (!File.Exists(exe) || !File.Exists(versionFile)) throw new SetupFailure("압축 파일에 필요한 파일이 없어요.");
        string version = File.ReadAllText(versionFile).Trim();
        if (!Version.TryParse(version, out var parsed) || parsed != tagVersion)
            throw new SetupFailure("앱 파일의 버전이 릴리스 버전과 달라요.");
        return version;
    }
}
