using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskCards;

internal sealed class ShellEntry : INotifyPropertyChanged
{
    public ShellEntry(string path, CancellationToken token, Func<string, ImageSource?>? loader = null)
    {
        Path = path;
        string ext = System.IO.Path.GetExtension(path);
        Name = Directory.Exists(path) || ext.Length == 0
            ? System.IO.Path.GetFileName(path)
            : System.IO.Path.GetFileNameWithoutExtension(path);
        if (Name.Length == 0) Name = System.IO.Path.GetFileName(path);
        _ = LoadIconAsync(Dispatcher.CurrentDispatcher, token, loader);
    }

    public string Path { get; }
    public string Name { get; }
    public ImageSource? Icon { get; private set; } = ShellIcons.Placeholder;
    public event PropertyChangedEventHandler? PropertyChanged;

    private async Task LoadIconAsync(Dispatcher dispatcher, CancellationToken token, Func<string, ImageSource?>? loader)
    {
        var icon = await ShellIcons.GetAsync(Path, token, loader).ConfigureAwait(false);
        if (token.IsCancellationRequested || dispatcher.HasShutdownStarted) return;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || icon == null) return;
                Icon = icon;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            });
        }
        catch (TaskCanceledException) { }
    }
}

/// <summary>그룹 하나 = 실제 폴더 하나. 폴더 변경을 감시해 항목 목록을 갱신한다.</summary>
internal sealed class GroupModel : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce;
    private bool _disposed;
    private CancellationTokenSource? _icons;
    private readonly Func<string, ImageSource?>? _iconLoader;

    public GroupModel(string folder, IReadOnlyList<string>? order = null, Func<string, ImageSource?>? iconLoader = null)
    {
        Folder = folder;
        _order = order;
        _iconLoader = iconLoader;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Reload(); };
        Reload();
        Watch();
    }

    public string Folder { get; private set; }
    public string Name => Path.GetFileName(Folder);
    public List<ShellEntry> Items { get; private set; } = new();
    private IReadOnlyList<string>? _order;

    /// <summary>사용자가 정한 순서(파일 이름 목록). 없으면 이름 순.</summary>
    public IReadOnlyList<string>? Order
    {
        get => _order;
        set
        {
            _order = value;
            Items = Arrange(Items, e => System.IO.Path.GetFileName(e.Path), e => e.Name, _order);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// 정한 순서(파일 이름)대로 놓고, 순서에 없는 항목(새로 넣은 것 등)은 그 뒤에 이름 순으로 붙인다.
    /// 순서에만 있고 폴더에 없는 이름은 건너뛴다.
    /// </summary>
    public static List<T> Arrange<T>(IEnumerable<T> items, Func<T, string> file, Func<T, string> name, IReadOnlyList<string>? order)
    {
        var byName = items.OrderBy(name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (order is not { Count: > 0 }) return byName;
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        // OrderBy는 안정 정렬이라 순서에 없는 항목끼리는 이름 순이 유지된다.
        return byName.OrderBy(e => rank.TryGetValue(file(e), out int r) ? r : int.MaxValue).ToList();
    }
    public event Action? Changed;

    public void Reload()
    {
        if (_disposed) return;
        _icons?.Cancel();
        _icons?.Dispose();
        _icons = new CancellationTokenSource();
        var list = new List<ShellEntry>();
        try
        {
            foreach (var p in Directory.EnumerateFileSystemEntries(Folder))
            {
                try
                {
                    var attr = File.GetAttributes(p);
                    if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    list.Add(new ShellEntry(p, _icons.Token, _iconLoader));
                }
                catch (IOException) { } // 한 항목이 사라져도 나머지는 표시한다.
                catch (UnauthorizedAccessException) { }
            }
        }
        catch
        {
            // 폴더가 사라지는 중일 수 있다. 관리자가 정리한다.
        }
        Items = Arrange(list, e => System.IO.Path.GetFileName(e.Path), e => e.Name, _order);
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
        if (_disposed) return;
        _disposed = true;
        _icons?.Cancel();
        _icons?.Dispose();
        _watcher?.Dispose();
        _debounce.Stop();
    }
}
