using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace DeskFolders;

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
        catch (Win32Exception)
        {
            // UAC 취소 등
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DeskFolders", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        return paths.Any(p => !SameDir(Path.GetDirectoryName(p), folder) && !SameDir(p, folder));
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
                if (SameDir(Path.GetDirectoryName(p), folder) || SameDir(p, folder)) continue;
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
                        if (isDir) Directory.Move(p, dest); else File.Move(p, dest);
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
                MessageBox.Show($"{Path.GetFileName(p)}: {ex.Message}", "DeskFolders", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    public static void MoveTo(string path, string folder)
    {
        try
        {
            string dest = Unique(folder, Path.GetFileName(path));
            if (Directory.Exists(path)) Directory.Move(path, dest); else File.Move(path, dest);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DeskFolders", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = target;
        lnk.WorkingDirectory = Directory.Exists(target) ? target : (Path.GetDirectoryName(target) ?? "");
        lnk.Save();
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
