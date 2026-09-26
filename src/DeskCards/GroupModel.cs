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
        var list = new List<ShellEntry>();
        try
        {
            foreach (var p in Directory.EnumerateFileSystemEntries(Folder))
            {
                var attr = File.GetAttributes(p);
                if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                list.Add(new ShellEntry(p));
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
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            _watcher = null;
        }
    }

    private void Bump() => _debounce.Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Stop();
    }
}
