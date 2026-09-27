using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskCards;

internal sealed record AppRelease(Version Version, string ZipUrl, string HashUrl);

/// <summary>
/// 앱 업데이트 파일 다루기. 새 버전은 설치 폴더의 update\&lt;버전&gt;\에 받아 풀어 두고,
/// 앱이 켜질 때(또는 [앱 재시작]) 실행 중인 DeskCards.exe를 .old로 비키고 그 자리에 옮긴다.
/// (Windows는 실행 중인 exe를 지울 수는 없어도 이름은 바꿀 수 있다.)
/// </summary>
internal static class UpdatePackage
{
    public const string ReleasesUrl = "https://api.github.com/repos/hhsshoo12/desk-cards/releases?per_page=100";
    public const string ZipName = "DeskCards-win-x64.zip";
    public const string HashName = ZipName + ".sha256";
    public const string ExeName = "DeskCards.exe";
    public const long DownloadLimit = 300L * 1024 * 1024;
    private const long ExpandedLimit = 1024L * 1024 * 1024;

    public static string StageRoot(string installDir) => Path.Combine(installDir, "update");
    public static string StageDir(string installDir, Version v) => Path.Combine(StageRoot(installDir), v.ToString(3));
    public static string OldExe(string installDir) => Path.Combine(installDir, ExeName + ".old");

    /// <summary>GitHub 릴리스 목록에서 가장 높은 app-vX.Y.Z(초안·시험판 제외, 파일 둘 다 있는 것). 없으면 null.</summary>
    public static AppRelease? Latest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        AppRelease? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) continue;
            var m = Regex.Match(tag.GetString()!, @"^app-v(\d+)\.(\d+)\.(\d+)$", RegexOptions.CultureInvariant);
            if (!m.Success || !Version.TryParse($"{m.Groups[1]}.{m.Groups[2]}.{m.Groups[3]}", out var v)) continue;
            if (best != null && v <= best.Version) continue;
            string? zip = null, hash = null;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (url == null || !url.StartsWith("https://", StringComparison.Ordinal)) continue;
                    if (name == ZipName) zip = url;
                    else if (name == HashName) hash = url;
                }
            if (zip != null && hash != null) best = new AppRelease(v, zip, hash);
        }
        return best;
    }

    /// <summary>zip의 SHA-256이 .sha256 파일 내용(첫 단어)과 같은지.</summary>
    public static void Verify(string zip, string hashText)
    {
        string expected = hashText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        using var stream = File.OpenRead(zip);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (expected.Length != 64 || !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("받은 파일이 손상됐어요.");
    }

    /// <summary>
    /// dest에 푼다. 모든 경로가 dest 안이어야 하고, DeskCards.exe와 version.txt가 있어야 하며,
    /// version.txt가 릴리스 버전과 같아야 한다.
    /// </summary>
    public static void Extract(string zip, string dest, Version expected)
    {
        using var archive = ZipFile.OpenRead(zip);
        string root = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("압축 파일에 안전하지 않은 경로가 있어요.");
            total += entry.Length;
            if (total > ExpandedLimit) throw new InvalidDataException("압축을 푼 크기가 너무 커요.");
        }
        Directory.CreateDirectory(dest);
        foreach (var entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
        string versionFile = Path.Combine(dest, "version.txt");
        if (!File.Exists(Path.Combine(dest, ExeName)) || !File.Exists(versionFile))
            throw new InvalidDataException("압축 파일에 필요한 파일이 없어요.");
        if (!Version.TryParse(File.ReadAllText(versionFile).Trim().TrimStart('﻿'), out var v) || v != expected)
            throw new InvalidDataException("앱 파일의 버전이 릴리스 버전과 달라요.");
    }

    /// <summary>
    /// 받아 둔 버전으로 바꿔 끼운다. DeskCards.exe → DeskCards.exe.old, update\&lt;버전&gt;\DeskCards.exe → DeskCards.exe.
    /// 받아 둔 파일이 없으면 false. 옮기다 실패하면 원래 exe를 되돌리고 예외를 던진다.
    /// </summary>
    public static bool Apply(string installDir, Version version)
    {
        string staged = Path.Combine(StageDir(installDir, version), ExeName);
        if (!File.Exists(staged)) return false;
        string exe = Path.Combine(installDir, ExeName), old = OldExe(installDir);
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(staged, exe); }
        catch
        {
            File.Move(old, exe);
            throw;
        }
        return true;
    }

    /// <summary>바꿔 끼운 뒤 남은 .old와 update 폴더를 지운다. 못 지우면 다음에 다시 시도한다.</summary>
    public static void Cleanup(string installDir)
    {
        try { File.Delete(OldExe(installDir)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        DeleteStage(installDir);
    }

    public static void DeleteStage(string installDir)
    {
        try
        {
            string stage = StageRoot(installDir);
            if (Directory.Exists(stage) && (File.GetAttributes(stage) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(stage, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Preparing, Ready, Failed }

/// <summary>설치 위치·등록·네트워크처럼 바깥과 닿는 부분. 테스트에서는 임시 폴더와 가짜로 바꾼다.</summary>
internal sealed class UpdateHost
{
    public required string InstallDir { get; init; }
    /// <summary>설치기로 설치된 위치에서 실행 중인지. 개발 빌드는 확인만 하고 업데이트하지 않는다.</summary>
    public required bool Installed { get; init; }
    public required Func<string, CancellationToken, Task<string>> ReadText { get; init; }
    public required Func<string, string, IProgress<double>, CancellationToken, Task> Download { get; init; }
    /// <summary>설치된 앱 목록(제거 등록)의 버전을 바꾼다.</summary>
    public required Action<Version> SetInstalledVersion { get; init; }
    /// <summary>새 exe를 띄우고 이 프로세스를 끝낸다.</summary>
    public required Action<string> Relaunch { get; init; }
    public required string LogPath { get; init; }

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskCards";
    private static readonly HttpClient Http = CreateClient();

    public static UpdateHost Production(Action<string> relaunch)
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        return new UpdateHost
        {
            InstallDir = dir,
            Installed = IsInstalledAt(dir),
            ReadText = (url, token) => Http.GetStringAsync(url, token),
            Download = DownloadAsync,
            SetInstalledVersion = v =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
                key?.SetValue("DisplayVersion", v.ToString(3));
            },
            Relaunch = relaunch,
            LogPath = Path.Combine(AppPaths.ConfigDir, "update.log"),
        };
    }

    private static bool IsInstalledAt(string dir)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("InstallLocation") is string location && dir.Length > 0 &&
                string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DeskCards/" + Updater.RunningVersion.ToString(3));
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static async Task DownloadAsync(string url, string path, IProgress<double> progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        long? length = response.Content.Headers.ContentLength;
        if (length > UpdatePackage.DownloadLimit) throw new InvalidDataException("파일이 너무 커요.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920];
        long received = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            received += n;
            if (received > UpdatePackage.DownloadLimit) throw new InvalidDataException("파일이 너무 커요.");
            await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token);
            if (length > 0) progress.Report((double)received / length.Value);
        }
        if (length.HasValue && received != length.Value) throw new IOException("다운로드가 도중에 끊겼어요.");
    }
}

/// <summary>
/// 업데이트 확인·받기·적용의 상태. 설정 창의 정보 페이지가 이 상태를 그대로 보여 준다.
/// 자동 업데이트가 켜져 있으면 켜질 때와 몇 시간마다 확인해서 새 버전을 미리 받아 두고,
/// 다음에 앱이 켜질 때(보통 PC를 다시 켤 때) 새 버전으로 바꾼다.
/// </summary>
internal sealed class Updater
{
    private static readonly TimeSpan AutoInterval = TimeSpan.FromHours(6);

    private readonly Config _cfg;
    private readonly UpdateHost _host;
    private AppRelease? _release;
    private DispatcherTimer? _timer;
    private DateTime _lastCheck = DateTime.MinValue;

    public Updater(Config cfg, UpdateHost host, Version? current = null)
    {
        _cfg = cfg;
        _host = host;
        Current = current ?? RunningVersion;
        // 받아 두고 아직 바꾸지 못한 버전이 있으면 바로 [앱 재시작]을 보여 준다.
        if (PendingVersion(cfg) is { } pending && pending > Current &&
            File.Exists(Path.Combine(UpdatePackage.StageDir(host.InstallDir, pending), UpdatePackage.ExeName)))
        {
            Latest = pending;
            State = UpdateState.Ready;
        }
    }

    public static Updater? Instance { get; set; }

    public static Version RunningVersion
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public Version Current { get; }
    public Version? Latest { get; private set; }
    public UpdateState State { get; private set; }
    /// <summary>받는 중일 때 0~1.</summary>
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public bool CanUpdate => _host.Installed;

    /// <summary>상태가 바뀔 때(UI 스레드).</summary>
    public event Action? Changed;

    public bool AutoUpdate
    {
        get => _cfg.AutoUpdate;
        set
        {
            if (_cfg.AutoUpdate == value) return;
            _cfg.AutoUpdate = value;
            _cfg.Save();
            if (value) _ = AutoAsync();
        }
    }

    private void Set(UpdateState state, string? error = null)
    {
        State = state;
        Error = error;
        Changed?.Invoke();
    }

    /// <summary>켜진 뒤 잠시 있다가, 그 뒤로는 몇 시간마다 자동 업데이트를 돌린다.</summary>
    public void StartAuto()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = AutoInterval;
            _ = AutoAsync();
        };
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    private async Task AutoAsync()
    {
        if (!_cfg.AutoUpdate || !CanUpdate) return;
        if (State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Preparing or UpdateState.Ready) return;
        await CheckAsync();
        if (State == UpdateState.Available) await DownloadAsync();
    }

    /// <summary>정보 페이지를 열 때. 최근에 확인했으면 다시 묻지 않는다.</summary>
    public void CheckIfStale()
    {
        if (State is UpdateState.Idle or UpdateState.Failed ||
            (State is UpdateState.UpToDate or UpdateState.Available && DateTime.UtcNow - _lastCheck > TimeSpan.FromMinutes(10)))
            _ = CheckAsync();
    }

    public async Task CheckAsync()
    {
        if (State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Preparing or UpdateState.Ready) return;
        Set(UpdateState.Checking);
        try
        {
            string json = await _host.ReadText(UpdatePackage.ReleasesUrl, CancellationToken.None);
            _release = UpdatePackage.Latest(json);
            _lastCheck = DateTime.UtcNow;
            Latest = _release?.Version;
            Set(_release != null && _release.Version > Current ? UpdateState.Available : UpdateState.UpToDate);
        }
        catch (Exception ex)
        {
            Log("확인 실패", ex);
            Set(UpdateState.Failed, "업데이트를 확인하지 못했어요");
        }
    }

    /// <summary>새 버전을 받아 update\&lt;버전&gt;\에 풀고, 다음 실행 때 쓸 버전으로 적어 둔다.</summary>
    public async Task DownloadAsync()
    {
        if (State != UpdateState.Available || _release is not { } release || !CanUpdate) return;
        string dir = UpdatePackage.StageDir(_host.InstallDir, release.Version);
        string zip = Path.Combine(UpdatePackage.StageRoot(_host.InstallDir), UpdatePackage.ZipName);
        Progress = 0;
        Set(UpdateState.Downloading);
        try
        {
            UpdatePackage.DeleteStage(_host.InstallDir);
            Directory.CreateDirectory(UpdatePackage.StageRoot(_host.InstallDir));
            var progress = new Progress<double>(p => { Progress = p; Changed?.Invoke(); });
            await _host.Download(release.ZipUrl, zip, progress, CancellationToken.None);
            string hash = await _host.ReadText(release.HashUrl, CancellationToken.None);
            Set(UpdateState.Preparing);
            await Task.Run(() =>
            {
                UpdatePackage.Verify(zip, hash);
                UpdatePackage.Extract(zip, dir, release.Version);
                File.Delete(zip);
            });
            _cfg.PendingUpdate = release.Version.ToString(3);
            _cfg.Save();
            Set(UpdateState.Ready);
        }
        catch (Exception ex)
        {
            Log("받기 실패", ex);
            UpdatePackage.DeleteStage(_host.InstallDir);
            Set(UpdateState.Failed, "업데이트를 받지 못했어요");
        }
    }

    /// <summary>[앱 재시작]: 받아 둔 버전으로 바꿔 끼우고 새 버전을 띄운다.</summary>
    public void Restart()
    {
        if (State != UpdateState.Ready) return;
        try
        {
            if (ApplyPending(_cfg, _host, Current)) return;
            Set(UpdateState.Failed, "받아 둔 업데이트가 없어요");
        }
        catch (Exception ex)
        {
            Log("적용 실패", ex);
            Set(UpdateState.Failed, "업데이트를 적용하지 못했어요");
        }
    }

    /// <summary>
    /// 앱이 켜질 때와 [앱 재시작] 때. 적어 둔 버전이 지금보다 높고 받아 둔 파일이 있으면 바꿔 끼우고
    /// 새 exe를 띄운다(true). 지금 버전 이하(설치기로 이미 올린 경우 등)면 적어 둔 것과 받아 둔 파일을 치운다.
    /// </summary>
    public static bool ApplyPending(Config cfg, UpdateHost host, Version current)
    {
        if (PendingVersion(cfg) is not { } pending) return false;
        if (!host.Installed || pending <= current)
        {
            cfg.PendingUpdate = null;
            cfg.Save();
            UpdatePackage.DeleteStage(host.InstallDir);
            return false;
        }
        if (!UpdatePackage.Apply(host.InstallDir, pending))
        {
            cfg.PendingUpdate = null;
            cfg.Save();
            return false;
        }
        cfg.PendingUpdate = null;
        cfg.Save();
        try { host.SetInstalledVersion(pending); } catch { /* 목록 표시만 늦어진다 */ }
        host.Relaunch(Path.Combine(host.InstallDir, UpdatePackage.ExeName));
        return true;
    }

    private static Version? PendingVersion(Config cfg) =>
        Version.TryParse(cfg.PendingUpdate, out var v) ? v : null;

    private void Log(string what, Exception ex)
    {
        try
        {
            File.AppendAllText(_host.LogPath,
                $"{DateTime.Now:o} [{what}]{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch { }
        Debug.WriteLine(ex);
    }
}
