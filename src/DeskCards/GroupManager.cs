using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskCards;

/// <summary>그룹 루트 폴더의 하위 폴더마다 카드 창을 하나씩 띄우고 동기화한다.</summary>
internal sealed partial class GroupManager
{
    private readonly Config _cfg;
    private readonly Dictionary<string, CardWindow> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _rootWatcher;
    private DispatcherTimer? _rootWatchRetry;
    private DispatcherTimer? _dpiCheck;
    private DispatcherTimer? _desktopRetry;
    private bool _shuttingDown;

    public GroupManager(string? root = null, Config? config = null)
    {
        Root = root ?? AppPaths.GroupsRoot;
        _cfg = config ?? Config.Load();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Reconcile(); };
        DardStorage.BrowserLost += OnBrowserLost;
    }

    public string Root { get; }
    public bool IsShuttingDown => _shuttingDown;

    /// <summary>바탕화면에 떠 있는 카드(카드 바 전용 그룹은 빼고).</summary>
    public IEnumerable<DeskCard> AllCards => _cards.Values.Where(c => !IsBarOnly(c.Group.Name)).Cast<DeskCard>().Concat(_dards.Values.SelectMany(d => d.Windows));

    /// <summary>폴더 카드 목록, 이름 순.</summary>
    public IReadOnlyList<CardWindow> Cards =>
        _cards.Values.OrderBy(c => c.Group.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>그룹이 생기거나 없어지거나, 이름·모양·설정이 바뀌었을 때(설정 창 갱신용).</summary>
    public event Action? Changed;

    /// <summary>편집 모드가 켜지고 꺼지거나 고른 카드가 바뀌었을 때.</summary>
    public event Action? EditChanged;

    private void RaiseChanged() => Changed?.Invoke();

    public void Start()
    {
        MigrateScale();
        SmartGuides.Enabled = _cfg.ShowGuides;
        SmartGuides.Flush = _cfg.FlushSnap;
        Directory.CreateDirectory(Root);
        RecoverRenamedGroups();
        if (ListGroupFolders() is { Count: 0 }) Directory.CreateDirectory(FileOps.Unique(Root, "새 그룹"));
        Reconcile();

        StartRootWatch();

        // Windows 배율이 바뀌었는데 알림을 못 받은 카드는 새 배율로 다시 만든다.
        _dpiCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _dpiCheck.Tick += (_, _) =>
        {
            foreach (var card in AllCards.Where(c => c.IsDpiStale).ToList())
                RecreateCard(card);
        };
        _dpiCheck.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void StartRootWatch()
    {
        if (_shuttingDown) return;
        _rootWatcher?.Dispose();
        _rootWatcher = null;
        try
        {
            // 폴더(그룹)와 .dard 파일(카드)을 같이 지켜본다.
            _rootWatcher = new FileSystemWatcher(Root)
            {
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.Attributes | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler h = (_, _) => Bump();
            _rootWatcher.Created += h;
            _rootWatcher.Deleted += h;
            _rootWatcher.Changed += h;
            _rootWatcher.Renamed += (_, e) => _debounce.Dispatcher.BeginInvoke(() => OnFolderRenamed(e));
            _rootWatcher.Error += (_, _) => OnRootWatchError();
            _rootWatcher.EnableRaisingEvents = true;
            _rootWatchRetry?.Stop();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _rootWatcher?.Dispose();
            _rootWatcher = null;
            _rootWatchRetry ??= CreateRootWatchRetry();
            _rootWatchRetry.Start();
        }
    }

    private DispatcherTimer CreateRootWatchRetry()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { StartRootWatch(); Bump(); };
        return timer;
    }

    private void OnRootWatchError() => _debounce.Dispatcher.BeginInvoke(() =>
    {
        if (_shuttingDown) return;
        StartRootWatch();
        Bump();
    });

    private void RecreateCard(DeskCard card)
    {
        // 화면상 왼쪽 위(물리 픽셀)를 그대로 저장한 뒤 다시 띄운다.
        var hwnd = Hwnd.Of(card);
        if (Native.GetWindowRect(hwnd, out var r))
        {
            _cfg.Positions[card.Key] = new[] { (double)r.Left, r.Top };
            _cfg.Save();
        }
        if (card is DardWindow dard)
        {
            RecreateDardWindow(dard);
            return;
        }
        var folderCard = (CardWindow)card;
        string folder = folderCard.Group.Folder;
        RemoveCard(folderCard.Group.Name);
        CreateCard(folder);
    }

    private void Bump() => _debounce.Dispatcher.BeginInvoke(() =>
    {
        if (_shuttingDown) return;
        _debounce.Stop();
        _debounce.Start();
    });

    public void Reconcile()
    {
        if (_shuttingDown) return;
        var folders = ListGroupFolders();
        if (folders == null) return;
        var names = new HashSet<string>(folders.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

        foreach (var name in _cards.Keys.Where(k => !names.Contains(k)).ToList())
            RemoveCard(name);

        foreach (var folder in folders)
        {
            string name = Path.GetFileName(folder);
            if (!_cards.ContainsKey(name)) CreateCard(folder);
        }
        ReconcileDards();
        RaiseChanged();
    }

    private CardWindow CreateCard(string folder)
    {
        var group = new GroupModel(folder, _cfg.Orders.GetValueOrDefault(Path.GetFileName(folder)));
        group.Changed += RaiseChanged;
        var card = new CardWindow(group, this);
        _cards[group.Name] = card;
        // 카드 바 전용 그룹은 바탕화면에 띄우지 않는다(바가 이 카드의 그룹·모양만 쓴다).
        if (!IsBarOnly(group.Name)) PlaceAndShow(card, null);
        return card;
    }

    private void OnCardClosed(object? sender, EventArgs e)
    {
        if (sender is not DeskCard closed || closed.ClosingByManager || _shuttingDown) return;
        // 탐색기가 재시작되면 소유자(Progman)와 함께 카드가 파괴된다. 잠시 뒤 다시 띄운다.
        if (closed is CardWindow card)
        {
            _cards.Remove(card.Group.Name);
            Detach(card);
        }
        else if (closed is DardWindow dard)
        {
            UnloadDard(dard.Runtime.Package.Path);
        }
        ScheduleDesktopRecovery();
        RaiseChanged();
    }

    private void ScheduleDesktopRecovery()
    {
        if (_desktopRetry != null || _shuttingDown) return;
        var retry = _desktopRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        retry.Tick += (_, _) =>
        {
            if (_shuttingDown) { retry.Stop(); return; }
            if (Native.FindWindow("Progman", null) == IntPtr.Zero) return;
            retry.Stop();
            _desktopRetry = null;
            Reconcile();
        };
        retry.Start();
    }

    private void RemoveCard(string name)
    {
        if (!_cards.Remove(name, out var card)) return;
        card.ClosingByManager = true;
        card.Close();
        Detach(card);
    }

    /// <summary>목록에서 빠진 카드의 선택·펼친 창·폴더 감시를 정리한다.</summary>
    private void Detach(CardWindow card)
    {
        if (Selected == card) Select(null);
        ExpandedWindow.CloseFor(card);
        card.Group.Changed -= RaiseChanged;
        card.Group.Dispose();
    }

    public void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        DardStorage.BrowserLost -= OnBrowserLost;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _debounce.Stop();
        _dpiCheck?.Stop();
        _desktopRetry?.Stop();
        _desktopRetry = null;
        EndEditMode();
        _rootWatcher?.Dispose();
        _rootWatchRetry?.Stop();
        foreach (var name in _cards.Keys.ToList()) RemoveCard(name);
        foreach (var path in _dards.Keys.ToList()) UnloadDard(path);
    }

}
