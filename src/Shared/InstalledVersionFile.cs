using System;
using System.IO;
using System.Text;

namespace DeskCards.Shared;

/// <summary>설치기와 앱 업데이트가 함께 쓰는 버전 표식. 실패 시 기존 파일은 보존한다.</summary>
internal static class InstalledVersionFile
{
    public const string Name = "version.txt";

    public static void Write(string directory, byte[] contents)
    {
        string path = Path.Combine(directory, Name);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(contents, 0, contents.Length); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static byte[] Read(string stage, Version expected)
    {
        byte[] contents = File.ReadAllBytes(Path.Combine(stage, Name));
        if (!Version.TryParse(Encoding.UTF8.GetString(contents).Trim().TrimStart('\uFEFF'), out var actual) || actual != expected)
            throw new InvalidDataException("앱 파일의 버전이 릴리스 버전과 달라요.");
        return contents;
    }
}
