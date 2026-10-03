using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeskCards.Setup;

internal sealed class SetupEngine
{
    private readonly SetupEnvironment _env;
    private readonly IDownloads _downloads;
    private readonly IAppProcesses _processes;
    private readonly IShortcuts _shortcuts;
    private readonly SetupLog _log;
    public Registration Registration { get; }
    public SetupEngine(SetupEnvironment env, IDownloads downloads, IAppProcesses processes, IShortcuts shortcuts)
    {
        _env = env; _downloads = downloads; _processes = processes; _shortcuts = shortcuts;
        _log = new SetupLog(env); Registration = new Registration(env);
    }

    public async Task InstallAsync(AppRelease release, InstallOptions options, string setupExe,
        IProgress<TransferProgress>? progress, Action<string> status, Action disableCancel, CancellationToken token)
    {
        string temp = Path.Combine(_env.TempDir, "DeskCards-Setup-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(temp, "stage"), old = _env.AppExe + ".old";
        string step = "설치 준비";
        bool backedUp = false, replaced = false, createdDirectory = false;
        string versionPath = Path.Combine(_env.InstallDir, Shared.InstalledVersionFile.Name);
        byte[]? oldVersion = null;
        bool versionReplaced = false;
        void Step(string value) { step = value; _log.Write(value); status(value); }
        try
        {
            if (Registration.IsUpdate && ReleaseService.CompareInstalled(Registration.InstalledVersion, release.Version) > 0)
                throw new SetupFailure("설치된 버전(" + Registration.InstalledVersion + ")이 더 새로워요", false);
            Directory.CreateDirectory(temp);
            Step("다운로드");
            string zip = Path.Combine(temp, SetupEnvironment.ZipName), hash = Path.Combine(temp, SetupEnvironment.HashName);
            await _downloads.DownloadAsync(release.ZipUrl, zip, Packages.DownloadLimit, progress, token).ConfigureAwait(false);
            await _downloads.DownloadAsync(release.HashUrl, hash, 65536, null, token).ConfigureAwait(false);
            Step("검증"); Packages.Verify(zip, hash);
            Step("압축 풀기"); string version = Packages.Extract(zip, stage, release.Version, token);
            token.ThrowIfCancellationRequested();
            disableCancel();
            token.ThrowIfCancellationRequested();
            // The UI cannot cancel or close from this point through registration.
            Step("앱 종료"); AppStopper.Stop(_processes, _env.AppExe);
            Step("파일 교체");
            if (!Directory.Exists(_env.InstallDir))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_env.InstallDir)!);
                // TEMP may be on another volume: copy first, with rollback confined to these new files.
                Directory.CreateDirectory(_env.InstallDir); createdDirectory = true;
                CopyDirectory(stage, _env.InstallDir); replaced = true;
            }
            else
            {
                if (File.Exists(versionPath)) oldVersion = File.ReadAllBytes(versionPath);
                // 앱이 스스로 업데이트하고 남긴 .old가 있으면 먼저 치운다.
                DeleteFile(old);
                if (File.Exists(_env.AppExe)) { File.Move(_env.AppExe, old); backedUp = true; }
                replaced = true;
                File.Copy(Path.Combine(stage, "DeskCards.exe"), _env.AppExe);
                Shared.InstalledVersionFile.Write(_env.InstallDir, Shared.InstalledVersionFile.Read(stage, release.Version));
                versionReplaced = true;
            }
            DeleteFile(Path.Combine(_env.InstallDir, "DeskFolders.exe"));
            Step("제거기 복사");
            if (!Path.GetFullPath(setupExe).Equals(_env.Uninstaller, StringComparison.OrdinalIgnoreCase))
            {
                string next = _env.Uninstaller + ".new";
                File.Copy(setupExe, next, true);
                if (File.Exists(_env.Uninstaller)) File.Replace(next, _env.Uninstaller, null);
                else File.Move(next, _env.Uninstaller);
            }
            Step("바로가기");
            SetLink(_env.MenuLink, options.StartMenu); SetLink(_env.DesktopLink, options.Desktop);
            Step("설치된 앱 등록"); Registration.Register(version);
            Step("자동 실행"); Registration.SetAutoStart(options.AutoStart);
            Step("정리"); Registration.RemoveOldRun();
            if (backedUp) File.Delete(old);
            backedUp = false;
            Step("설치 완료");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { _log.Write("설치 취소"); throw; }
        catch (Exception ex)
        {
            _log.Write(step + " 실패", ex);
            try
            {
                if (backedUp) { File.Delete(_env.AppExe); File.Move(old, _env.AppExe); _log.Write("기존 앱 복구"); }
                else if (createdDirectory) { SetupEnvironment.DeleteTree(_env.InstallDir, Path.GetDirectoryName(_env.InstallDir)!); }
                else if (replaced) File.Delete(_env.AppExe);
                if (versionReplaced)
                {
                    if (oldVersion != null) Shared.InstalledVersionFile.Write(_env.InstallDir, oldVersion);
                    else File.Delete(versionPath);
                }
            }
            catch (Exception rollback)
            {
                _log.Write("복구 실패", rollback);
                throw new SetupFailure(step + " 단계에서 실패했고 기존 앱을 복구하지 못했어요. 로그를 확인해 주세요.", false, new AggregateException(ex, rollback));
            }
            throw new SetupFailure(step + ": " + (ex is SetupFailure ? ex.Message : "작업을 완료하지 못했어요. 로그를 확인해 주세요."), false, ex);
        }
        finally
        {
            try { SetupEnvironment.DeleteTree(temp, _env.TempDir); }
            catch (Exception ex) { _log.Write("임시 폴더 정리 실패", ex); }
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string dir in Directory.GetDirectories(from))
        { string target = Path.Combine(to, Path.GetFileName(dir)); Directory.CreateDirectory(target); CopyDirectory(dir, target); }
    }

    private void SetLink(string path, bool enabled)
    {
        if (enabled) _shortcuts.Create(path, _env.AppExe, _env.InstallDir);
        else DeleteFile(path);
    }

    private static void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
    }

    public void Uninstall(bool removeConfig, Action<string> status)
    {
        string step = "제거 준비";
        void Step(string value) { step = value; _log.Write(value); status(value); }
        try
        {
            Step("앱 종료"); AppStopper.Stop(_processes, _env.AppExe);
            Step("바로가기 삭제"); DeleteFile(_env.MenuLink); DeleteFile(_env.DesktopLink);
            Step("자동 실행 해제"); Registration.RemoveStartup();
            Step("앱 파일 삭제");
            foreach (string name in new[] { "DeskCards.exe", "DeskCards.exe.old", "DeskFolders.exe", "uninstall.exe", Shared.InstalledVersionFile.Name }) DeleteFile(Path.Combine(_env.InstallDir, name));
            // 앱 자체 업데이트가 받아 둔 파일(update\)
            SetupEnvironment.DeleteTree(Path.Combine(_env.InstallDir, "update"), _env.InstallDir);
            Step("설치된 앱 등록 삭제"); Registration.Delete(Registration.UninstallKey);
            Step("시작 메뉴 흔적 정리"); Registration.RemoveStartTraces();
            if (Directory.Exists(_env.InstallDir) && Directory.GetFileSystemEntries(_env.InstallDir).Length == 0) Directory.Delete(_env.InstallDir);
            if (removeConfig)
            {
                Step("카드 설정 삭제");
                SetupEnvironment.DeleteTree(_env.ConfigDir, Path.GetDirectoryName(_env.ConfigDir)!);
                SetupEnvironment.DeleteTree(_env.OldConfigDir, Path.GetDirectoryName(_env.OldConfigDir)!);
            }
            Step("제거 완료");
        }
        catch (Exception ex) { _log.Write(step + " 실패", ex); throw new SetupFailure(step + ": 작업을 완료하지 못했어요. 로그를 확인해 주세요.", false, ex); }
    }

    public void FinishUninstall() { _log.Write("시작 메뉴 흔적 다시 정리"); Registration.RemoveStartTraces(); }
}
