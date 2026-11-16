using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace DeskCards.Setup;

/// <summary>앱이 실행에 필요로 하는 런타임(.NET 10 데스크톱, Windows App Runtime)을 확인하고 없으면 설치한다.</summary>
internal interface IRuntimeInstaller
{
    /// <summary>이미 모두 설치돼 있으면 true.</summary>
    bool IsInstalled(string workDir, CancellationToken token);
    /// <summary>설치한다. 실패하면 SetupFailure.</summary>
    void Install(string workDir, CancellationToken token);
}

/// <summary>설치기에 품은 winui3-runtime-installer.exe를 --nogui로 돌린다(종료 코드 0이 성공).</summary>
internal sealed class EmbeddedRuntimeInstaller : IRuntimeInstaller
{
    public const string ResourceName = "RuntimeInstaller.exe";
    private static readonly Assembly Self = typeof(EmbeddedRuntimeInstaller).Assembly;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    private EmbeddedRuntimeInstaller() { }

    public static EmbeddedRuntimeInstaller? Load() => Self.GetManifestResourceInfo(ResourceName) == null ? null : new EmbeddedRuntimeInstaller();

    public bool IsInstalled(string workDir, CancellationToken token) => Run(Extract(workDir), "--nogui --check", token, TimeSpan.FromMinutes(2)) == 0;

    public void Install(string workDir, CancellationToken token)
    {
        int code = Run(Extract(workDir), "--nogui", token, Timeout);
        if (code != 0) throw new SetupFailure("필요한 런타임을 설치하지 못했어요(종료 코드 " + code + "). 인터넷 연결과 winget을 확인해 주세요.", false);
    }

    private static string Extract(string workDir)
    {
        Directory.CreateDirectory(workDir);
        string path = Path.Combine(workDir, "winui3-runtime-installer.exe");
        if (File.Exists(path)) return path;
        using var input = Self.GetManifestResourceStream(ResourceName) ?? throw new SetupFailure("설치기에 런타임 설치 도구가 들어 있지 않아요", false);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        input.CopyTo(output);
        return path;
    }

    private static int Run(string exe, string arguments, CancellationToken token, TimeSpan limit)
    {
        var info = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        using var process = Process.Start(info) ?? throw new SetupFailure("런타임 설치 도구를 실행하지 못했어요", false);
        var clock = Stopwatch.StartNew();
        while (!process.WaitForExit(200))
        {
            if (token.IsCancellationRequested || clock.Elapsed > limit)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                token.ThrowIfCancellationRequested();
                throw new SetupFailure("런타임 설치가 너무 오래 걸려 멈췄어요", false);
            }
        }
        return process.ExitCode;
    }
}
