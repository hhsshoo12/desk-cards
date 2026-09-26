using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace DeskCards;

internal static class FileOps
{
    private static readonly string[] ShortcutExts = { ".lnk", ".url", ".appref-ms" };

    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    private static string PublicDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    public static void Launch(string path, bool admin = false)
    {
        try
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            if (admin) psi.Verb = "runas";
            string? dir = Path.GetDirectoryName(path);
            if (dir != null) psi.WorkingDirectory = dir;
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC 취소 등
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Desk Cards", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    public static void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch { }
    }

    /// <summary>드래그된 파일 목록이 이 폴더에 넣을 만한 것인지(전부 이미 들어있는 게 아닌지).</summary>
    public static bool CanAccept(IDataObject data, string folder)
    {
        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return false;
        return paths.Any(p => CanAddPath(p, folder));
    }

    private static bool CanAddPath(string path, string folder)
    {
        try
        {
            return (File.Exists(path) || Directory.Exists(path)) &&
                !SameDir(Path.GetDirectoryName(path), folder) && !SameDir(path, folder) &&
                !(Directory.Exists(path) && IsUnder(folder, path));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool IsValidGroupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        string stem = name.Split('.')[0].TrimEnd();
        return !System.Text.RegularExpressions.Regex.IsMatch(stem,
            @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 그룹 폴더로 항목을 넣는다. 바로가기류, 바탕화면 항목, 다른 그룹의 항목은 이동하고
    /// 그 밖의 파일/폴더(예: Program Files의 exe)는 원본을 건드리지 않고 바로가기를 만든다.
    /// </summary>
    public static void AddToGroup(IEnumerable<string> paths, string folder, string groupsRoot)
    {
        foreach (var p in paths)
        {
            try
            {
                if (!CanAddPath(p, folder)) continue;
                bool isDir = Directory.Exists(p);
                if (!isDir && !File.Exists(p)) continue;
                // 그룹 폴더 자신이나 루트를 넣는 건 막는다.
                if (isDir && (IsUnder(folder, p) || SameDir(p, groupsRoot))) continue;

                string ext = Path.GetExtension(p);
                bool move = ShortcutExts.Contains(ext, StringComparer.OrdinalIgnoreCase)
                            || IsUnder(p, UserDesktop) || IsUnder(p, PublicDesktop) || IsUnder(p, groupsRoot);

                if (move)
                {
                    string dest = Unique(folder, Path.GetFileName(p));
                    try
                    {
                        MovePath(p, dest);
                    }
                    catch (Exception) when (!isDir)
                    {
                        // 공용 바탕화면처럼 권한이 없으면 복사만 한다.
                        File.Copy(p, dest);
                    }
                    catch (Exception)
                    {
                        CreateShortcut(Unique(folder, Path.GetFileName(p) + ".lnk"), p);
                    }
                }
                else
                {
                    string baseName = isDir ? Path.GetFileName(p) : Path.GetFileNameWithoutExtension(p);
                    CreateShortcut(Unique(folder, baseName + ".lnk"), p);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Path.GetFileName(p)}: {ex.Message}", "Desk Cards", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    public static bool MoveTo(string path, string folder)
    {
        try
        {
            if (SameDir(Path.GetDirectoryName(path), folder)) return true;
            string dest = Unique(folder, Path.GetFileName(path));
            MovePath(path, dest);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Desk Cards", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private static void MovePath(string source, string destination)
    {
        if (Directory.Exists(source))
            Microsoft.VisualBasic.FileIO.FileSystem.MoveDirectory(source, destination);
        else File.Move(source, destination);
    }

    public static void Recycle(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch { }
    }

    public static void DragOut(DependencyObject source, string path)
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        try
        {
            DragDrop.DoDragDrop(source, data, DragDropEffects.Move | DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch { }
    }

    public static string Unique(string folder, string name)
    {
        string dest = Path.Combine(folder, name);
        if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            dest = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
        }
    }

    private static void CreateShortcut(string lnkPath, string target)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell 없음");
        dynamic shell = Activator.CreateInstance(type)!;
        object? shortcut = null;
        try
        {
            shortcut = shell.CreateShortcut(lnkPath);
            dynamic lnk = shortcut;
            lnk.TargetPath = target;
            lnk.WorkingDirectory = Directory.Exists(target) ? target : (Path.GetDirectoryName(target) ?? "");
            lnk.Save();
        }
        finally
        {
            if (shortcut != null) Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);
        }
    }

    private static bool SameDir(string? a, string? b) =>
        a != null && b != null &&
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        string p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        string d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        return p.StartsWith(d, StringComparison.OrdinalIgnoreCase) && p.Length > d.Length;
    }
}
