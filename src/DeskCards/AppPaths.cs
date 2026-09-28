using System;
using System.IO;
using Microsoft.Win32;

namespace DeskCards;

/// <summary>
/// 그룹 폴더와 설정 폴더 위치. 예전 이름(DeskFolders)으로 쓰던 폴더가 있으면 새 이름(DeskCards)으로 옮긴다.
/// 옮기지 못하면(다른 프로그램이 폴더를 쓰는 중 등) 잃어버리지 않도록 예전 위치를 그대로 쓴다.
/// </summary>
internal static class AppPaths
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunName = "DeskCards";
    private const string OldName = "DeskFolders";
    private const string NewName = "DeskCards";

    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>그룹 폴더들이 들어 있는 곳. 그룹 하나 = 하위 폴더 하나.</summary>
    public static string GroupsRoot { get; private set; } = Path.Combine(Profile, NewName);

    /// <summary>카드 위치·크기 등 설정(config.json)이 있는 곳.</summary>
    public static string ConfigDir { get; private set; } = Path.Combine(AppData, NewName);

    /// <summary>.dard 화면(WebView2)의 브라우저 데이터. 로밍되지 않는 로컬 앱 데이터에 둔다.</summary>
    public static string WebDataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), NewName, "WebView2");

    /// <summary>앱 시작 때 한 번. 예전 이름의 폴더와 자동 실행 등록을 새 이름으로 옮긴다.</summary>
    public static void Migrate()
    {
        GroupsRoot = MoveOrKeep(Path.Combine(Profile, OldName), Path.Combine(Profile, NewName));
        ConfigDir = MoveOrKeep(Path.Combine(AppData, OldName), Path.Combine(AppData, NewName));
        MigrateRunValue();
    }

    private static string MoveOrKeep(string oldDir, string newDir)
    {
        if (Directory.Exists(newDir) || !Directory.Exists(oldDir)) return newDir;
        try
        {
            Directory.Move(oldDir, newDir);
            return newDir;
        }
        catch
        {
            return oldDir;
        }
    }

    private static void MigrateRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(OldName) == null) return;
            string? exe = Environment.ProcessPath;
            if (exe == null) return;
            if (key.GetValue(RunName) == null) key.SetValue(RunName, $"\"{exe}\"");
            key.DeleteValue(OldName, false);
        }
        catch
        {
            // 자동 실행은 설정에서 다시 켤 수 있다.
        }
    }
}

/// <summary>Windows 시작 시 실행(HKCU Run 값).</summary>
internal static class AutoStart
{
    private const string ApprovalKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(AppPaths.RunKey);
            using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKey);
            return StartupState.Enabled(key?.GetValue(AppPaths.RunName), approval?.GetValue(AppPaths.RunName));
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(AppPaths.RunKey);
            using var approval = Registry.CurrentUser.CreateSubKey(ApprovalKey);
            if (value)
            {
                key.SetValue(AppPaths.RunName, $"\"{Environment.ProcessPath}\"");
                approval.SetValue(AppPaths.RunName, StartupState.EnabledBytes(), RegistryValueKind.Binary);
            }
            else
            {
                key.DeleteValue(AppPaths.RunName, false);
                approval.DeleteValue(AppPaths.RunName, false);
            }
        }
    }
}
