using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace DeskCards;

/// <summary>
/// 카드의 main.py를 돌리는 Python. 앱 안에 품은 embeddable 배포본을 처음 쓸 때 한 번 풀어서 쓴다.
/// 품지 않은 빌드(개발·테스트)는 PATH의 python을 쓴다.
/// </summary>
internal static class PythonRuntime
{
    public const string ResourceName = "DeskCards.python-embed.zip";
    private static readonly object Gate = new();
    private static string? _executable;

    /// <summary>풀어 둔 python.exe의 경로. 앱에 품은 Python이 없으면 "python".</summary>
    public static string Executable()
    {
        lock (Gate)
        {
            if (_executable != null && File.Exists(_executable)) return _executable;
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (resource == null) return _executable = "python";
            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";
            return _executable = Extract(resource, Path.Combine(AppPaths.LocalDataDir, "python"), version);
        }
    }

    /// <summary>zip을 root\version에 풀고 python.exe 경로를 돌려준다. 이미 풀려 있으면 다시 풀지 않고, 다른 버전 폴더는 지운다.</summary>
    internal static string Extract(Stream zip, string root, string version)
    {
        string target = Path.Combine(root, version);
        string exe = Path.Combine(target, "python.exe");
        if (!File.Exists(Path.Combine(target, ".ready")))
        {
            Directory.CreateDirectory(root);
            string staging = Path.Combine(root, ".extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true)) archive.ExtractToDirectory(staging);
                // ._pth가 있으면 스크립트 폴더가 import 경로에 들어가지 않아 main.py 옆의 모듈과 lib 폴더를 못 쓴다. 지우면 보통 Python처럼 동작한다.
                foreach (string pth in Directory.GetFiles(staging, "*._pth")) File.Delete(pth);
                File.WriteAllText(Path.Combine(staging, ".ready"), version);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(staging, target);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
        foreach (string other in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(other);
            if (name == version) continue;
            try { Directory.Delete(other, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return exe;
    }
}
