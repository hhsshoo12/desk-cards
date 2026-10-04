using System;
using System.IO;
using Microsoft.Win32;

namespace DeskCards.Setup;

internal sealed class SetupEnvironment
{
    public const string AppId = "hhsshoo12.DeskCards";
    public const string Repository = "https://github.com/hhsshoo12/desk-cards";
    public const string ReleasesUrl = "https://api.github.com/repos/hhsshoo12/desk-cards/releases?per_page=100";
    public const string ZipName = "DeskCards-win-x64.zip";
    public const string HashName = ZipName + ".sha256";
    public string InstallDir { get; }
    public string MenuDir { get; }
    public string DesktopDir { get; }
    public string ConfigDir { get; }
    public string OldConfigDir { get; }
    /// <summary>앱의 로컬 데이터(위젯 카드 브라우저 저장소 등). %LOCALAPPDATA%\DeskCards</summary>
    public string LocalDataDir { get; }
    /// <summary>그룹 폴더들의 루트와 예전 이름. 설치기는 맨 위의 .dard(설치한 카드)만 지울 수 있고 그룹(하위 폴더)은 건드리지 않는다.</summary>
    public string GroupsDir { get; }
    public string OldGroupsDir { get; }
    public string TempDir { get; }
    public string RegistryRoot { get; }
    public string? TestRoot { get; }
    public string AppExe => Path.Combine(InstallDir, "DeskCards.exe");
    public string Uninstaller => Path.Combine(InstallDir, "uninstall.exe");
    public string MenuLink => Path.Combine(MenuDir, "Desk Cards.lnk");
    public string DesktopLink => Path.Combine(DesktopDir, "Desk Cards.lnk");
    public string LogPath => Path.Combine(TempDir, "DeskCards-Setup.log");

    private SetupEnvironment(string install, string menu, string desktop, string config, string oldConfig,
        string localData, string groups, string oldGroups, string temp, string registryRoot, string? testRoot)
    {
        InstallDir = Path.GetFullPath(install); MenuDir = Path.GetFullPath(menu);
        DesktopDir = Path.GetFullPath(desktop); ConfigDir = Path.GetFullPath(config);
        OldConfigDir = Path.GetFullPath(oldConfig); LocalDataDir = Path.GetFullPath(localData);
        GroupsDir = Path.GetFullPath(groups); OldGroupsDir = Path.GetFullPath(oldGroups); TempDir = Path.GetFullPath(temp);
        RegistryRoot = registryRoot; TestRoot = testRoot;
        if (testRoot != null)
        {
            string name = Path.GetFileName(testRoot);
            if (!Path.GetDirectoryName(testRoot)!.Equals(Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith("DeskCards-Setup-Tests-", StringComparison.Ordinal)
                || !Guid.TryParse(name.Substring("DeskCards-Setup-Tests-".Length), out var id)
                || !registryRoot.Equals(@"Software\DeskCards-Setup-Tests\" + id.ToString("N"), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("테스트 루트가 격리되지 않았습니다.");
            foreach (string path in new[] { InstallDir, MenuDir, DesktopDir, ConfigDir, OldConfigDir, LocalDataDir, GroupsDir, OldGroupsDir, TempDir })
                if (!Within(path, testRoot)) throw new ArgumentException("테스트 경로가 루트 밖입니다: " + path);
        }
    }

    public static SetupEnvironment ForTests(string root, string registryRoot, string? installOverride = null) =>
        new SetupEnvironment(installOverride ?? Path.Combine(root, "install"), Path.Combine(root, "menu"),
            Path.Combine(root, "desktop"), Path.Combine(root, "config"), Path.Combine(root, "old-config"),
            Path.Combine(root, "local-data"), Path.Combine(root, "groups"), Path.Combine(root, "old-groups"),
            Path.Combine(root, "temp"), registryRoot, Path.GetFullPath(root));

    public static SetupEnvironment Production(string? uninstallTarget = null) => new SetupEnvironment(
        uninstallTarget ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Desk Cards"),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskCards"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskFolders"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCards"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskCards"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskFolders"),
        Path.GetTempPath(), @"Software\Microsoft\Windows\CurrentVersion", null);

    public static bool Within(string path, string root) => Path.GetFullPath(path).StartsWith(
        Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void DeleteTree(string path, string allowedRoot)
    {
        if (!Within(path, allowedRoot)) throw new IOException("삭제 경로가 허용된 폴더 밖입니다.");
        if (!Directory.Exists(path)) return;
        // Never follow junctions while deleting a staging/configuration tree.
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(path); return; }
        foreach (string file in Directory.GetFiles(path)) File.Delete(file);
        foreach (string dir in Directory.GetDirectories(path)) DeleteTree(dir, allowedRoot);
        Directory.Delete(path);
    }
}

internal sealed class SetupLog
{
    public string Path { get; }
    private readonly object _gate = new object();
    public SetupLog(SetupEnvironment env) { Path = env.LogPath; }
    public void Write(string stage, Exception? error = null)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, DateTimeOffset.Now.ToString("o") + " [" + stage + "]" +
                (error == null ? "" : Environment.NewLine + error) + Environment.NewLine);
        }
    }
}

internal sealed class SetupFailure : Exception
{
    public bool Retry { get; }
    public SetupFailure(string message, bool retry = true, Exception? inner = null) : base(message, inner) { Retry = retry; }
}
