using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskCards;

/// <summary>
/// 카드 폴더의 requirements.txt를 앱에 품은 pip으로 카드 폴더의 lib에 설치한다.
/// 컴파일러가 없으므로 미리 빌드된 wheel만 받는다. requirements.txt 내용(SHA-256)이 바뀐 때만 다시 설치한다.
/// </summary>
internal static class CardPackages
{
    public const string RequirementsFile = "requirements.txt";
    public const string LibFolder = "lib";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    private static string Marker(string folder) => Path.Combine(folder, LibFolder, ".requirements-sha256");

    private static string? RequirementsHash(string folder)
    {
        string file = Path.Combine(folder, RequirementsFile);
        return File.Exists(file) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant() : null;
    }

    public static bool NeedsInstall(string folder) =>
        RequirementsHash(folder) is { } hash && (!File.Exists(Marker(folder)) || File.ReadAllText(Marker(folder)).Trim() != hash);

    public static async Task InstallAsync(string python, string pipWheel, string folder, CancellationToken cancel)
    {
        string? hash = RequirementsHash(folder);
        if (hash == null) return;
        string lib = Path.Combine(folder, LibFolder);
        Directory.CreateDirectory(lib);
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in new[]
        {
            Path.Combine(pipWheel, "pip"), "install", "--target", lib, "-r", Path.Combine(folder, RequirementsFile),
            "--only-binary=:all:", "--upgrade", "--no-input", "--disable-pip-version-check", "--no-warn-script-location",
        }) start.ArgumentList.Add(arg);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        var output = new StringBuilder();
        void Add(string? line)
        {
            if (line == null) return;
            lock (output) { output.AppendLine(line); if (output.Length > 8192) output.Remove(0, output.Length - 8192); }
        }
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Add(e.Data);
        process.ErrorDataReceived += (_, e) => Add(e.Data);
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("패키지 설치기(pip)를 실행하지 못했어요: " + ex.Message, ex); }
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Limit);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancel.ThrowIfCancellationRequested();
            throw new IOException("패키지 설치가 5분 안에 끝나지 않았어요.");
        }
        if (process.ExitCode != 0)
        {
            string tail; lock (output) tail = output.ToString().Trim();
            throw new IOException("requirements.txt의 패키지를 설치하지 못했어요. 미리 빌드된(wheel) 패키지만 받을 수 있고 인터넷이 필요해요.\n" + tail);
        }
        File.WriteAllText(Marker(folder), hash);
    }
}
