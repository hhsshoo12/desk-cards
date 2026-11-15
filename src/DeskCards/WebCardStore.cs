using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace DeskCards;

internal sealed record InstalledWebCard(WebCardManifest Manifest, string Directory);

/// <summary>
/// 가져온 웹 카드(.dard)를 두는 곳. .dard는 확장자만 다른 zip이고, 폴더째로도 가져올 수 있다.
/// 같은 id가 이미 있으면 새것으로 바꾼다(업데이트).
/// </summary>
internal sealed class WebCardStore
{
    public WebCardStore(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }

    /// <summary>.dard(zip) 파일이나 카드 폴더를 가져온다.</summary>
    public InstalledWebCard Import(string source)
    {
        source = Path.GetFullPath(source);
        if (Directory.Exists(source)) return ImportFolder(source);
        if (!File.Exists(source)) throw new WebCardException("가져올 파일을 찾을 수 없어요.");
        string temp = Path.Combine(Path.GetTempPath(), "DeskCards-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            try { ZipFile.ExtractToDirectory(source, temp); }
            catch (InvalidDataException ex) { throw new WebCardException("카드 파일(.dard)을 열 수 없어요: " + ex.Message, ex); }
            string folder = temp;
            if (!File.Exists(Path.Combine(folder, WebCardManifest.FileName)))
            {
                // 폴더 하나로 감싸서 압축한 경우
                var inner = Directory.GetDirectories(folder);
                if (inner.Length == 1 && Directory.GetFiles(folder).Length == 0) folder = inner[0];
            }
            return ImportFolder(folder);
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
    }

    private InstalledWebCard ImportFolder(string source)
    {
        var manifest = WebCardManifest.Load(source);
        Directory.CreateDirectory(Root);
        string destination = Path.Combine(Root, manifest.Id);
        string staging = Path.Combine(Root, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(source, staging);
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(staging, destination);
            return new(manifest, destination);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    /// <summary>가져온 카드 전체(읽을 수 없는 폴더는 건너뛴다).</summary>
    public IReadOnlyList<InstalledWebCard> List()
    {
        var list = new List<InstalledWebCard>();
        if (!Directory.Exists(Root)) return list;
        foreach (string dir in Directory.GetDirectories(Root).Where(d => !Path.GetFileName(d).StartsWith('.')))
        {
            try { list.Add(new(WebCardManifest.Load(dir), dir)); }
            catch (WebCardException) { }
        }
        return list;
    }

    public void Remove(string id)
    {
        string dir = Path.Combine(Root, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}