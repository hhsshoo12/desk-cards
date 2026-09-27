using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace DeskCards.Setup;

internal sealed class InstallOptions
{
    public bool StartMenu { get; set; } = true;
    public bool Desktop { get; set; }
    public bool AutoStart { get; set; } = true;
}

internal sealed class Registration
{
    public const string UninstallKey = @"Uninstall\DeskCards";
    public const string RunKey = "Run";
    public const string ApprovalKey = @"Explorer\StartupApproved\Run";
    public const string TilesKey = @"Start\TileProperties";
    public const string BackupKey = "AppListBackup";
    private readonly SetupEnvironment _env;
    public Registration(SetupEnvironment env) { _env = env; }
    public RegistryKey? Open(string relative, bool writable = false) => Registry.CurrentUser.OpenSubKey(_env.RegistryRoot + "\\" + relative, writable);
    public RegistryKey Create(string relative) => Registry.CurrentUser.CreateSubKey(_env.RegistryRoot + "\\" + relative);
    public void Delete(string relative) => Registry.CurrentUser.DeleteSubKeyTree(_env.RegistryRoot + "\\" + relative, false);
    public string? InstalledLocation { get { using var key = Open(UninstallKey); return key?.GetValue("InstallLocation") as string; } }
    public string? InstalledVersion { get { using var key = Open(UninstallKey); return key?.GetValue("DisplayVersion") as string; } }
    public bool IsUpdate => InstalledLocation is string path && File.Exists(Path.Combine(path, "DeskCards.exe"));

    public bool AutoStartEnabled
    {
        get
        {
            using var run = Open(RunKey);
            using var approval = Open(ApprovalKey);
            return StartupState.Enabled(run?.GetValue("DeskCards"), approval?.GetValue("DeskCards"));
        }
    }

    public void SetAutoStart(bool enabled)
    {
        using var run = Create(RunKey);
        using var approval = Create(ApprovalKey);
        if (enabled)
        {
            run.SetValue("DeskCards", "\"" + _env.AppExe + "\"", RegistryValueKind.String);
            approval.SetValue("DeskCards", StartupState.EnabledBytes(), RegistryValueKind.Binary);
        }
        else { run.DeleteValue("DeskCards", false); approval.DeleteValue("DeskCards", false); }
    }

    public InstallOptions ReadOptions() => IsUpdate ? new InstallOptions
    {
        StartMenu = File.Exists(_env.MenuLink), Desktop = File.Exists(_env.DesktopLink), AutoStart = AutoStartEnabled
    } : new InstallOptions();

    public void Register(string version)
    {
        using var key = Create(UninstallKey);
        var values = new Dictionary<string, string>
        {
            ["DisplayName"] = "Desk Cards", ["DisplayVersion"] = version, ["Publisher"] = "hhsshoo12",
            ["URLInfoAbout"] = SetupEnvironment.Repository, ["InstallLocation"] = _env.InstallDir,
            ["DisplayIcon"] = "\"" + _env.AppExe + "\",0", ["UninstallString"] = "\"" + _env.Uninstaller + "\" /uninstall",
            ["InstallDate"] = DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)
        };
        foreach (var value in values) key.SetValue(value.Key, value.Value, RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, (DirectorySize(_env.InstallDir) + 1023) / 1024), RegistryValueKind.DWord);
        key.DeleteValue("QuietUninstallString", false);
    }

    private static long DirectorySize(string path)
    {
        long size = Directory.GetFiles(path).Sum(f => new FileInfo(f).Length);
        foreach (string dir in Directory.GetDirectories(path))
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) size += DirectorySize(dir);
        return size;
    }

    public void RemoveOldRun()
    {
        using var run = Open(RunKey, true);
        run?.DeleteValue("DeskFolders", false);
    }

    public void RemoveStartup()
    {
        foreach (string path in new[] { RunKey, ApprovalKey })
        {
            using var key = Open(path, true);
            key?.DeleteValue("DeskCards", false);
            key?.DeleteValue("DeskFolders", false);
        }
    }

    public void RemoveStartTraces()
    {
        using (var tiles = Open(TilesKey, true))
        {
            if (tiles != null)
                foreach (string name in tiles.GetSubKeyNames())
                    if (name.StartsWith("W~", StringComparison.OrdinalIgnoreCase) && StartTraces.IsOurTile(name, _env.InstallDir)) tiles.DeleteSubKeyTree(name, false);
        }
        using var backup = Open(BackupKey, true);
        if (backup != null)
            foreach (string name in backup.GetValueNames())
                if (name.StartsWith("ListOfEventDrivenBackedUpTiles", StringComparison.Ordinal)
                    && StartTraces.OnlyOurTiles(backup.GetValue(name), _env.InstallDir)) backup.DeleteValue(name, false);
    }
}

internal static class StartTraces
{
    public static bool IsOurTile(object? value, string target)
    {
        if (!(value is string id) || string.IsNullOrWhiteSpace(target)) return false;
        // TileProperties key names require W~; backup tile IDs also accept the legacy raw path.
        if (id.StartsWith("W~", StringComparison.OrdinalIgnoreCase)) id = id.Substring(2);
        string prefix = target.TrimEnd('\\') + "\\";
        return id.Equals(prefix + "DeskCards.exe", StringComparison.OrdinalIgnoreCase)
            || id.Equals(prefix + "DeskFolders.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static bool OnlyOurTiles(object? value, string target)
    {
        try
        {
            if (value is byte[] bytes) value = new UnicodeEncoding(false, false, true).GetString(bytes).TrimEnd('\0');
            if (!(value is string json)) return false;
            object parsed = new JavaScriptSerializer().DeserializeObject(json);
            object[] items = parsed is Dictionary<string, object> ? new[] { parsed } : parsed as object[] ?? Array.Empty<object>();
            return items.Length > 0 && items.All(item => item is Dictionary<string, object> fields
                && fields.TryGetValue("tileId", out var id) && IsOurTile(id, target));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { return false; }
    }
}
