using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskCards;

internal sealed class ShellEntry
{
    public ShellEntry(string path)
    {
        Path = path;
        string ext = System.IO.Path.GetExtension(path);
        Name = Directory.Exists(path) || ext.Length == 0
            ? System.IO.Path.GetFileName(path)
            : System.IO.Path.GetFileNameWithoutExtension(path);
        Icon = ShellIcons.Get(path);
    }

    public string Path { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
}

/// <summary>그룹 하나 = 실제 폴더 하나. 폴더 변경을 감시해 항목 목록을 갱신한다.</summary>
internal sealed class GroupModel : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce;
    private bool _disposed;

    public GroupModel(string folder)
    {
        Folder = folder;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Reload(); };
        Reload();
        Watch();
    }

    public string Folder { get; private set; }
    public string Name => Path.GetFileName(Folder);
    public List<ShellEntry> Items { get; private set; } = new();
    public event Action? Changed;

    public void Reload()
    {
        if (_disposed) return;
        var list = new List<ShellEntry>();
        try
        {
            foreach (var p in Directory.EnumerateFileSystemEntries(Folder))
            {
                try
                {
                    var attr = File.GetAttributes(p);
                    if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    list.Add(new ShellEntry(p));
                }
                catch (IOException) { } // 한 항목이 사라져도 나머지는 표시한다.
                catch (UnauthorizedAccessException) { }
            }
        }
        catch
        {
            // 폴더가 사라지는 중일 수 있다. 관리자가 정리한다.
        }
        Items = list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Changed?.Invoke();
    }

    public void MovedTo(string newFolder)
    {
        Folder = newFolder;
        Watch();
        Reload();
    }

    private void Watch()
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(Folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Attributes,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler h = (_, _) => Bump();
            _watcher.Created += h;
            _watcher.Deleted += h;
            _watcher.Changed += h;
            _watcher.Renamed += (_, _) => Bump();
            _watcher.Error += (_, _) => _debounce.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                Watch();
                Reload();
            });
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcher = null;
        }
    }

    private void Bump() => _debounce.Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        _debounce.Stop();
        _debounce.Start();
    });

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _debounce.Stop();
    }
}
