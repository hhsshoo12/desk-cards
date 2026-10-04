using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeskCards.Setup;

internal sealed class SetupEngine
{
    private readonly SetupEnvironment _env;
    private readonly IAppProcesses _processes;
    private readonly IShortcuts _shortcuts;
    private readonly SetupLog _log;
    public Registration Registration { get; }
    /// <summary>설치할 앱. 제거기 전용 빌드에서는 null.</summary>
    public IAppPackage? Package { get; }
    public SetupEngine(SetupEnvironment env, IAppPackage? package, IAppProcesses processes, IShortcuts shortcuts)
    {
        _env = env; Package = package; _processes = processes; _shortcuts = shortcuts;
        _log = new SetupLog(env); Registration = new Registration(env);
    }

    public void Install(InstallOptions options, Action<string> status, Action disableCancel, CancellationToken token)
    {
        var package = Package ?? throw new SetupFailure("설치기에 앱이 들어 있지 않아요", false);
        string temp = Path.Combine(_env.TempDir, "DeskCards-Setup-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(temp, "stage"), old = _env.AppExe + ".old";
        string step = "설치 준비";
        var rollback = new InstallRollback();
        void Step(string value) { step = value; _log.Write(value); status(value); }
        try
        {
            if (Registration.IsUpdate && EmbeddedPackage.CompareInstalled(Registration.InstalledVersion, package.Version) > 0)
                throw new SetupFailure("설치된 버전(" + Registration.InstalledVersion + ")이 더 새로워요", false);
            Directory.CreateDirectory(temp);
            string version = Unpack(package, temp, stage, Step, token);
            token.ThrowIfCancellationRequested();
            disableCancel();
            token.ThrowIfCancellationRequested();
            // The UI cannot cancel or close from this point through registration.
            Step("앱 종료"); AppStopper.Stop(_processes, _env.AppExe);
            Step("파일 교체");
            ReplaceApp(stage, old, package.Version, rollback);
            RegisterInstall(stage, options, version, Step);
            Step("정리"); Registration.RemoveOldRun();
            if (rollback.backedUp) File.Delete(old);
            rollback.backedUp = false;
            Step("설치 완료");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { _log.Write("설치 취소"); throw; }
        catch (Exception ex)
        {
            _log.Write(step + " 실패", ex);
            try
            {
                RollbackInstall(old, rollback);
            }
            catch (Exception recoveryError)
            {
                _log.Write("복구 실패", recoveryError);
                throw new SetupFailure(step + " 단계에서 실패했고 기존 앱을 복구하지 못했어요. 로그를 확인해 주세요.", false, new AggregateException(ex, recoveryError));
            }
            throw new SetupFailure(step + ": " + (ex is SetupFailure ? ex.Message : "작업을 완료하지 못했어요. 로그를 확인해 주세요."), false, ex);
        }
        finally
        {
            try { SetupEnvironment.DeleteTree(temp, _env.TempDir); }
            catch (Exception ex) { _log.Write("임시 폴더 정리 실패", ex); }
        }
    }

    private sealed class InstallRollback
    {
        public bool backedUp, replaced, createdDirectory, versionReplaced;
        public byte[]? oldVersion;
    }

    private static string Unpack(IAppPackage package, string temp, string stage, Action<string> Step, CancellationToken token)
    {
        Step("압축 풀기");
        string zip = Path.Combine(temp, SetupEnvironment.ZipName);
        package.CopyTo(zip, token);
        string version = Packages.Extract(zip, stage, package.Version, token);
        // 앱을 끄기 전에 확인한다. 설치 폴더의 제거기는 늘 이 패키지에 든 것으로 둔다.
        if (!File.Exists(Path.Combine(stage, "uninstall.exe"))) throw new SetupFailure("설치기에 제거기가 들어 있지 않아요", false);
        return version;
    }

    private void ReplaceApp(string stage, string old, Version version, InstallRollback rollback)
    {
        if (!Directory.Exists(_env.InstallDir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_env.InstallDir)!);
            // TEMP may be on another volume: copy first, with rollback confined to these new files.
            Directory.CreateDirectory(_env.InstallDir); rollback.createdDirectory = true;
            CopyDirectory(stage, _env.InstallDir); rollback.replaced = true;
        }
        else
        {
            if (File.Exists(Path.Combine(_env.InstallDir, Shared.InstalledVersionFile.Name))) rollback.oldVersion = File.ReadAllBytes(Path.Combine(_env.InstallDir, Shared.InstalledVersionFile.Name));
            // 앱이 스스로 업데이트하고 남긴 .old가 있으면 먼저 치운다.
            DeleteFile(old);
            if (File.Exists(_env.AppExe)) { File.Move(_env.AppExe, old); rollback.backedUp = true; }
            rollback.replaced = true;
            File.Copy(Path.Combine(stage, "DeskCards.exe"), _env.AppExe);
            Shared.InstalledVersionFile.Write(_env.InstallDir, Shared.InstalledVersionFile.Read(stage, version));
            rollback.versionReplaced = true;
        }
        DeleteFile(Path.Combine(_env.InstallDir, "DeskFolders.exe"));
    }

    private void RegisterInstall(string stage, InstallOptions options, string version, Action<string> Step)
    {
        Step("제거기 복사");
        string next = _env.Uninstaller + ".new";
        File.Copy(Path.Combine(stage, "uninstall.exe"), next, true);
        if (File.Exists(_env.Uninstaller)) File.Replace(next, _env.Uninstaller, null);
        else File.Move(next, _env.Uninstaller);
        Step("바로가기");
        SetLink(_env.MenuLink, options.StartMenu); SetLink(_env.DesktopLink, options.Desktop);
        Step("설치된 앱 등록"); Registration.Register(version);
        Step("자동 실행"); Registration.SetAutoStart(options.AutoStart);
    }

    private void RollbackInstall(string old, InstallRollback rollback)
    {
        if (rollback.backedUp) { File.Delete(_env.AppExe); File.Move(old, _env.AppExe); _log.Write("기존 앱 복구"); }
        else if (rollback.createdDirectory) { SetupEnvironment.DeleteTree(_env.InstallDir, Path.GetDirectoryName(_env.InstallDir)!); }
        else if (rollback.replaced) File.Delete(_env.AppExe);
        if (rollback.versionReplaced)
        {
            if (rollback.oldVersion != null) Shared.InstalledVersionFile.Write(_env.InstallDir, rollback.oldVersion);
            else File.Delete(Path.Combine(_env.InstallDir, Shared.InstalledVersionFile.Name));
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
                Step("설정 삭제");
                SetupEnvironment.DeleteTree(_env.ConfigDir, Path.GetDirectoryName(_env.ConfigDir)!);
                SetupEnvironment.DeleteTree(_env.OldConfigDir, Path.GetDirectoryName(_env.OldConfigDir)!);
                Step("카드 데이터 삭제");
                // 앱이 끝난 뒤에도 카드 화면(WebView2) 프로세스가 잠깐 파일을 쥐고 있을 수 있어 몇 초 다시 시도한다.
                Retry(() => SetupEnvironment.DeleteTree(_env.LocalDataDir, Path.GetDirectoryName(_env.LocalDataDir)!));
                Step("설치한 카드 삭제");
                DeleteInstalledCards(_env.GroupsDir); DeleteInstalledCards(_env.OldGroupsDir);
            }
            Step("제거 완료");
        }
        catch (Exception ex) { _log.Write(step + " 실패", ex); throw new SetupFailure(step + ": 작업을 완료하지 못했어요. 로그를 확인해 주세요.", false, ex); }
    }

    /// <summary>그룹 폴더 맨 위의 .dard(설치한 카드)만 지운다. 그룹(하위 폴더)과 다른 파일은 건드리지 않는다.</summary>
    private static void DeleteInstalledCards(string groups)
    {
        if (!Directory.Exists(groups) || (File.GetAttributes(groups) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string file in Directory.GetFiles(groups))
            if (string.Equals(Path.GetExtension(file), ".dard", StringComparison.OrdinalIgnoreCase)) File.Delete(file);
    }

    private static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when (attempt < 20 && (ex is IOException || ex is UnauthorizedAccessException)) { Thread.Sleep(250); }
        }
    }

    public void FinishUninstall() { _log.Write("시작 메뉴 흔적 다시 정리"); Registration.RemoveStartTraces(); }
}
