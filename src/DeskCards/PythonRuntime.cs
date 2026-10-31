using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace DeskCards;

/// <summary>
/// 카드의 main.py를 돌리는 Python. 앱 안에 품은 embeddable 배포본과 pip wheel을 처음 쓸 때 한 번 풀어서 쓴다.
/// 품지 않은 빌드(개발·테스트)는 PATH의 python을 쓰고 pip은 쓰지 않는다.
/// </summary>
internal static class PythonRuntime
{
    public const string ResourceName = "DeskCards.python-embed.zip";
    public const string PipResourceName = "DeskCards.pip.whl";
    private static readonly object Gate = new();
    private static string? _executable, _pip;

    /// <summary>풀어 둔 python.exe의 경로. 앱에 품은 Python이 없으면 "python".</summary>
    public static string Executable() { Ensure(); return _executable!; }

    /// <summary>풀어 둔 pip wheel의 경로. 품지 않았으면 null.</summary>
    public static string? PipWheel() { Ensure(); return _pip; }

    private static void Ensure()
    {
        lock (Gate)
        {
            if (_executable != null && (_executable == "python" || File.Exists(_executable))) return;
            var assembly = Assembly.GetExecutingAssembly();
            using var python = assembly.GetManifestResourceStream(ResourceName);
            if (python == null) { _executable = "python"; _pip = null; return; }
            using var pip = assembly.GetManifestResourceStream(PipResourceName);
            string version = assembly.GetName().Version?.ToString() ?? "0";
            string root = Path.Combine(AppPaths.LocalDataDir, "python");
            _executable = Extract(python, root, version, pip);
            string wheel = Path.Combine(root, version, "pip.whl");
            _pip = File.Exists(wheel) ? wheel : null;
        }
    }

    /// <summary>zip을 root\version에 풀고(pip wheel이 있으면 pip.whl로 함께 둔다) python.exe 경로를 돌려준다. 이미 풀려 있으면 다시 풀지 않고, 다른 버전 폴더는 지운다.</summary>
    internal static string Extract(Stream zip, string root, string version, Stream? pip = null)
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
                if (pip != null)
                {
                    using var wheel = File.Create(Path.Combine(staging, "pip.whl"));
                    pip.CopyTo(wheel);
                }
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
