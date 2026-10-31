using System;
using System.IO;
using System.IO.Compression;

namespace DeskCards;

internal sealed record InstalledCard(CardManifest Manifest, string Directory, string Source, bool Linked);

/// <summary>가져온 카드는 내부 폴더에서 실행하고 연결 카드는 원본 폴더를 사용한다.</summary>
internal sealed class CardStore
{
    public CardStore(string root) => Root = Path.GetFullPath(root);
    public string Root { get; }

    public InstalledCard ImportFolder(string source, bool linked = false)
    {
        source = Path.GetFullPath(source);
        var manifest = CardManifest.Load(source);
        if (linked) return new(manifest, source, source, true);
        Directory.CreateDirectory(Root);
        string destination = Path.Combine(Root, manifest.Id);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"'{manifest.Id}' 카드가 이미 있어요.");
        if (Root.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Root, source, StringComparison.OrdinalIgnoreCase))
            throw new IOException("가져올 폴더 안에 카드 저장소가 있어요. 개별 카드 폴더를 선택해 주세요.");
        string staging = Path.Combine(Root, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(source, staging);
            var copied = CardManifest.Load(staging);
            if (copied != manifest) throw new IOException("가져오는 동안 manifest.json이 바뀌었어요. 다시 시도해 주세요.");
            Directory.Move(staging, destination);
            return new(manifest, destination, source, false);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public InstalledCard ImportArchive(string archive)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "DeskCards-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            ZipFile.ExtractToDirectory(archive, temporary);
            string source = temporary;
            if (!File.Exists(Path.Combine(source, "manifest.json")))
            {
                var folders = Directory.GetDirectories(source);
                if (folders.Length == 1 && Directory.GetFiles(source).Length == 0) source = folders[0];
            }
            var installed = ImportFolder(source);
            return installed with { Source = Path.GetFullPath(archive) };
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(directory));
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget is { } link)
                Directory.CreateSymbolicLink(target, Path.GetFullPath(link, source));
            else CopyDirectory(directory, target);
        }
    }
}
