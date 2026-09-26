using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskCards;

/// <summary>그룹 루트 폴더의 하위 폴더마다 카드 창을 하나씩 띄우고 동기화한다.</summary>
internal sealed class GroupManager
{
    private const double CardW = 196, CardH = 206;

    private readonly Config _cfg;
    private readonly Dictionary<string, CardWindow> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _rootWatcher;
    private DispatcherTimer? _dpiCheck;
    private readonly List<DispatcherTimer> _retries = new();
    private bool _shuttingDown;

    public GroupManager(string? root = null, Config? config = null)
    {
        Root = root ?? AppPaths.GroupsRoot;
        _cfg = config ?? Config.Load();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Reconcile(); };
    }

    public string Root { get; }
    public IEnumerable<GroupModel> Groups => _cards.Values.Select(c => c.Group);
    public bool IsShuttingDown => _shuttingDown;

    /// <summary>카드 목록, 이름 순.</summary>
    public IReadOnlyList<CardWindow> Cards =>
        _cards.Values.OrderBy(c => c.Group.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>그룹이 생기거나 없어지거나, 이름·모양·설정이 바뀌었을 때(설정 창 갱신용).</summary>
    public event Action? Changed;

    /// <summary>편집 모드가 켜지고 꺼지거나 고른 카드가 바뀌었을 때.</summary>
    public event Action? EditChanged;

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>카드를 옮기거나 크기를 바꿀 때 안내선을 보여 주고 줄에 맞출지.</summary>
    public bool ShowGuides
    {
        get => _cfg.ShowGuides;
        set
        {
            if (_cfg.ShowGuides == value) return;
            _cfg.ShowGuides = SmartGuides.Enabled = value;
            _cfg.Save();
            RaiseChanged();
        }
    }

    /// <summary>
    /// 켜져 있으면 Windows 배율이 바뀔 때 카드도 같은 비율로 따라 커지고 작아진다.
    /// 켜고 끄는 순간에는 지금 보이는 크기를 그대로 두고, 크기 조절은 어느 쪽이든 할 수 있다.
    /// </summary>
    public bool FollowWindowsScale
    {
        get => _cfg.FollowWindowsScale;
        set
        {
            if (_cfg.FollowWindowsScale == value) return;
            if (value)
            {
                // 고정해 두었던 크기를 지금 배율 기준 확대 비율로 옮겨 담는다.
                foreach (var card in _cards.Values)
                    card.RebaseZoom(_cfg.FixedScale / card.DpiScale);
                _cfg.DefaultZoom *= _cfg.FixedScale / Native.PrimaryScale();
            }
            else
            {
                _cfg.FixedScale = Native.PrimaryScale();
                foreach (var card in _cards.Values)
                    card.RebaseZoom(card.DpiScale / _cfg.FixedScale);
            }
            _cfg.FollowWindowsScale = value;
            _cfg.Save();
            foreach (var card in _cards.Values)
            {
                card.FitToScreen();
                SavePosition(card);
            }
            RaiseChanged();
        }
    }

    /// <summary>
    /// 카드 확대 비율에 추가로 곱할 값. 따라가기가 켜져 있으면 1(WPF가 배율대로 키워 준다),
    /// 꺼져 있으면 배율이 바뀐 만큼 되돌려 실제 크기를 유지한다.
    /// </summary>
    public double ZoomFactor(double dpiScale) => FollowWindowsScale ? 1 : _cfg.FixedScale / dpiScale;

    /// <summary>미리보기 칸 하나의 기준 크기(DIP).</summary>
    public double CellSize => _cfg.CellSize;

    /// <summary>예전 설정(Zoom에 배율이 따로 곱해지던 방식)을 지금 방식으로 바꾸고, 빠진 값을 채운다.</summary>
    private void MigrateScale()
    {
        double s = Native.PrimaryScale();
        bool dirty = false;
        if (_cfg.CellSize <= 0)
        {
            _cfg.CellSize = CardWindow.MeasureCellSize(Native.GetDpiForSystem() / 96.0);
            dirty = true;
        }
        if (_cfg.ScaleVersion < 2)
        {
            // 예전: 켜짐이면 크기 = 배율 × Zoom. 이제는 배율을 Zoom에 담아 둔다.
            if (_cfg.FollowWindowsScale)
                foreach (var l in _cfg.Layouts.Values) l.Zoom = Math.Round(l.Zoom * s, 3);
            _cfg.DefaultZoom = _cfg.FollowWindowsScale ? s : 1;
            _cfg.FixedScale = s;
            _cfg.ScaleVersion = 2;
            dirty = true;
        }
        if (_cfg.FixedScale <= 0) { _cfg.FixedScale = s; dirty = true; }
        if (_cfg.DefaultZoom <= 0) { _cfg.DefaultZoom = _cfg.FollowWindowsScale ? s : 1; dirty = true; }
        if (dirty) _cfg.Save();
    }

    /// <summary>카드 모양(칸 수·확대 비율). 저장된 게 없으면 2×2, 기본 확대 비율.</summary>
    public CardLayout GetLayout(string name) =>
        _cfg.Layouts.TryGetValue(name, out var l) ? l.Normalized() : new CardLayout { Zoom = _cfg.DefaultZoom }.Normalized();

    public void SaveLayout(CardWindow card, CardLayout layout)
    {
        var l = layout.Normalized();
        l.Zoom = Math.Round(l.Zoom, 3);
        _cfg.Layouts[card.Group.Name] = l;
        _cfg.Save();
        RaiseChanged();
    }

    /// <summary>다른 카드들의 화면 위치(픽셀). 옮길 때 안내선 기준으로 쓴다.</summary>
    public IReadOnlyList<Native.RECT> CardRects(CardWindow except) =>
        _cards.Values.Where(c => c != except)
            .Select(c => Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(c).Handle, out var r) ? r : default)
            .Where(r => r.Right > r.Left)
            .ToList();

    // ----- 편집 모드 -----
    // 모든 카드를 한꺼번에 옮기고 크기를 바꿀 수 있게 하고, 화면 위쪽에 편집 막대를 띄운다.
    // 막대의 이름 바꾸기·칸 수·삭제 등은 '고른 카드'(마지막으로 누른 카드)에 적용된다.

    public bool Editing { get; private set; }
    public CardWindow? Selected { get; private set; }

    public void BeginEditMode(CardWindow? select = null)
    {
        if (!Editing)
        {
            Editing = true;
            foreach (var c in _cards.Values) c.BeginEdit();
            EditBar.Open(this);
        }
        Select(select ?? Selected);
    }

    public void EndEditMode()
    {
        if (!Editing) return;
        Editing = false;
        Selected = null;
        foreach (var c in _cards.Values) c.EndEdit();
        EditBar.CloseBar();
        EditChanged?.Invoke();
    }

    public void Select(CardWindow? card)
    {
        Selected = card;
        foreach (var c in _cards.Values) c.SetSelected(c == card);
        EditChanged?.Invoke();
    }

    public void Start()
    {
        MigrateScale();
        SmartGuides.Enabled = _cfg.ShowGuides;
        Directory.CreateDirectory(Root);
        if (ListGroupFolders() is { Count: 0 }) Directory.CreateDirectory(FileOps.Unique(Root, "새 그룹"));
        Reconcile();

        _rootWatcher = new FileSystemWatcher(Root) { NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.Attributes, IncludeSubdirectories = false };
        FileSystemEventHandler h = (_, _) => Bump();
        _rootWatcher.Created += h;
        _rootWatcher.Deleted += h;
        _rootWatcher.Changed += h;
        _rootWatcher.Renamed += (_, e) => _debounce.Dispatcher.BeginInvoke(() => OnFolderRenamed(e));
        _rootWatcher.Error += (_, _) => Bump();
        _rootWatcher.EnableRaisingEvents = true;

        // Windows 배율이 바뀌었는데 알림을 못 받은 카드는 새 배율로 다시 만든다.
        _dpiCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _dpiCheck.Tick += (_, _) =>
        {
            foreach (var card in _cards.Values.Where(c => c.IsDpiStale).ToList())
                RecreateCard(card);
        };
        _dpiCheck.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _debounce.Dispatcher.BeginInvoke(() =>
    {
        if (_shuttingDown) return;
        foreach (var card in _cards.Values)
        {
            card.FitToScreen();
            SavePosition(card);
        }
        RaiseChanged();
    });

    private void OnFolderRenamed(RenamedEventArgs e)
    {
        if (_shuttingDown) return;
        string oldName = Path.GetFileName(e.OldFullPath), name = Path.GetFileName(e.FullPath);
        if (_cards.TryGetValue(oldName, out var card) &&
            string.Equals(card.Group.Folder, e.OldFullPath, StringComparison.Ordinal) && Directory.Exists(e.FullPath))
        {
            _cards.Remove(oldName);
            _cards[name] = card;
            if (_cfg.Positions.Remove(oldName, out var position)) _cfg.Positions[name] = position;
            if (_cfg.Layouts.Remove(oldName, out var layout)) _cfg.Layouts[name] = layout;
            _cfg.Save();
            card.Group.MovedTo(e.FullPath);
            EditChanged?.Invoke();
        }
        Bump();
    }

    private void RecreateCard(CardWindow card)
    {
        // 화면상 왼쪽 위(픽셀)는 그대로 두고 새 배율 기준 DIP로 저장한 뒤 다시 띄운다.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(card).Handle;
        if (Native.GetWindowRect(hwnd, out var r))
        {
            double ns = Native.MonitorScaleOf(hwnd);
            _cfg.Positions[card.Group.Name] = new[] { r.Left / ns, r.Top / ns };
            _cfg.Save();
        }
        string folder = card.Group.Folder;
        RemoveCard(card.Group.Name);
        CreateCard(folder);
    }

    private void Bump() => _debounce.Dispatcher.BeginInvoke(() =>
    {
        if (_shuttingDown) return;
        _debounce.Stop();
        _debounce.Start();
    });

    private List<string>? ListGroupFolders()
    {
        try
        {
            return Directory.EnumerateDirectories(Root)
                .Where(d => (File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .ToList();
        }
        catch
        {
            return null; // 읽기 실패를 빈 폴더로 취급하면 정상 카드까지 모두 닫힌다.
        }
    }

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
        RaiseChanged();
    }

    private CardWindow CreateCard(string folder)
    {
        var group = new GroupModel(folder);
        group.Changed += RaiseChanged;
        var card = new CardWindow(group, this);
        if (_cfg.Positions.TryGetValue(group.Name, out var pos) && pos.Length == 2 && IsOnScreen(pos[0], pos[1]))
        {
            card.Left = pos[0];
            card.Top = pos[1];
        }
        else
        {
            var p = NextFreeSlot();
            card.Left = p.X;
            card.Top = p.Y;
            _cfg.Positions[group.Name] = new[] { p.X, p.Y };
            _cfg.Save();
        }
        card.Closed += OnCardClosed;
        _cards[group.Name] = card;
        card.Show();
        // 해상도나 작업 표시줄이 바뀌었을 수 있으니 화면 안으로 맞춘다.
        card.FitToScreen();
        SavePosition(card);
        if (Editing) card.BeginEdit();
        return card;
    }

    private void OnCardClosed(object? sender, EventArgs e)
    {
        if (sender is not CardWindow card || card.ClosingByManager || _shuttingDown) return;
        // 탐색기가 재시작되면 소유자(Progman)와 함께 카드가 파괴된다. 잠시 뒤 다시 띄운다.
        _cards.Remove(card.Group.Name);
        if (Selected == card) Select(null);
        ExpandedWindow.CloseFor(card);
        card.Group.Changed -= RaiseChanged;
        card.Group.Dispose();
        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        retry.Tick += (_, _) =>
        {
            if (_shuttingDown) { retry.Stop(); return; }
            if (Native.FindWindow("Progman", null) == IntPtr.Zero) return;
            retry.Stop();
            _retries.Remove(retry);
            Reconcile();
        };
        _retries.Add(retry);
        retry.Start();
        RaiseChanged();
    }

    private void RemoveCard(string name)
    {
        if (!_cards.Remove(name, out var card)) return;
        if (Selected == card) Select(null);
        ExpandedWindow.CloseFor(card);
        card.ClosingByManager = true;
        card.Close();
        card.Group.Changed -= RaiseChanged;
        card.Group.Dispose();
    }

    public void SavePosition(CardWindow card)
    {
        var p = card.ActualPosition;
        _cfg.Positions[card.Group.Name] = new[] { p.X, p.Y };
        _cfg.Save();
    }

    /// <summary>빈 그룹을 만들고 이름을 바로 입력할 수 있게 펼친다. 편집 중이면 새 카드를 고른다.</summary>
    public CardWindow? NewGroup(bool openTitle = true)
    {
        string path = FileOps.Unique(Root, "새 그룹");
        Directory.CreateDirectory(path);
        Reconcile();
        if (!_cards.TryGetValue(Path.GetFileName(path), out var card)) return null;
        if (Editing) Select(card);
        if (openTitle) ExpandedWindow.Open(card, this, editTitle: true);
        return card;
    }

    public bool RenameGroup(CardWindow card, string newName)
    {
        if (!FileOps.IsValidGroupName(newName))
        {
            MessageBox.Show("사용할 수 없는 폴더 이름이에요. 예약된 이름, 끝의 점·공백, \\ / : * ? \" < > | 문자는 사용할 수 없어요.", "Desk Cards");
            return false;
        }
        string oldName = card.Group.Name;
        if (oldName == newName) return true;
        string dest = Path.Combine(Root, newName);
        bool caseOnly = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (Directory.Exists(dest) || File.Exists(dest)))
        {
            MessageBox.Show($"'{newName}' 그룹이 이미 있어요.", "Desk Cards");
            return false;
        }
        try
        {
            if (caseOnly)
            {
                string tmp = FileOps.Unique(Root, ".rename-" + Guid.NewGuid().ToString("N"));
                Directory.Move(card.Group.Folder, tmp);
                try { Directory.Move(tmp, dest); }
                catch
                {
                    Directory.Move(tmp, card.Group.Folder);
                    throw;
                }
            }
            else
            {
                Directory.Move(card.Group.Folder, dest);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Desk Cards");
            return false;
        }

        _cards.Remove(oldName);
        _cards[newName] = card;
        _cfg.Positions.Remove(oldName);
        if (_cfg.Layouts.Remove(oldName, out var layout)) _cfg.Layouts[newName] = layout;
        var p = card.ActualPosition;
        _cfg.Positions[newName] = new[] { p.X, p.Y };
        _cfg.Save();
        card.Group.MovedTo(dest);
        RaiseChanged();
        EditChanged?.Invoke();
        return true;
    }

    /// <summary>그룹을 지운다. 항목이 있으면 확인하고 바탕화면으로 옮긴다. 지웠으면 true.</summary>
    public bool DeleteGroup(CardWindow card)
    {
        var g = card.Group;
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(g.Folder); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Desk Cards");
            return false;
        }
        if (entries.Length > 0)
        {
            var r = MessageBox.Show($"'{g.Name}' 그룹을 삭제할까요?\n안에 있는 항목 {entries.Length}개는 바탕화면으로 옮겨져요.",
                "Desk Cards", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return false;
            foreach (var path in entries)
                if (!FileOps.MoveTo(path, FileOps.UserDesktop)) return false;
        }
        try
        {
            Directory.Delete(g.Folder, recursive: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"폴더를 지우지 못했어요: {ex.Message}", "Desk Cards");
            return false;
        }
        _cfg.Positions.Remove(g.Name);
        _cfg.Layouts.Remove(g.Name);
        _cfg.Save();
        RemoveCard(g.Name);
        RaiseChanged();
        return true;
    }

    public void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _debounce.Stop();
        _dpiCheck?.Stop();
        foreach (var retry in _retries) retry.Stop();
        _retries.Clear();
        EndEditMode();
        _rootWatcher?.Dispose();
        foreach (var name in _cards.Keys.ToList()) RemoveCard(name);
    }

    private static bool IsOnScreen(double x, double y)
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        return vs.Contains(new Point(x + 40, y + 40));
    }

    private Point NextFreeSlot()
    {
        // 주 모니터 오른쪽 위부터 아래로, 다음 열은 왼쪽으로 채운다.
        var wa = SystemParameters.WorkArea;
        var taken = _cards.Values.Select(c => new Rect(c.ActualPosition, new Size(c.Width, c.Height))).ToList();

        {
            // 카드 이동 격자(작업 영역 기준, 2칸 = 카드 하나)로 오른쪽 위부터 찾는다. 새 카드는 기본 크기다.
            double s = Native.PrimaryScale();
            double k = _cfg.DefaultZoom * ZoomFactor(s);
            var baseSize = CardWindow.BaseSize(new CardLayout(), CellSize);
            int wPx = (int)Math.Round(baseSize.Width * k * s), hPx = (int)Math.Round(baseSize.Height * k * s);
            var waPx = DesktopGrid.WorkAreaAt((int)(wa.Left * s) + 1, (int)(wa.Top * s) + 1);
            var (nx, ny) = DesktopGrid.Counts(waPx, wPx, hPx);
            for (int i = nx; i >= 0; i -= 2)
            {
                for (int j = 0; j <= ny; j += 2)
                {
                    var (px, py) = DesktopGrid.CellAt(waPx, wPx, hPx, i, j);
                    var r = new Rect(px / s, py / s, wPx / s, hPx / s);
                    var probe = new Rect(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8);
                    if (!taken.Any(t => t.IntersectsWith(probe))) return r.TopLeft;
                }
            }
        }

        for (int col = 0; col < 20; col++)
        {
            for (double y = wa.Top + 16; y + CardH <= wa.Bottom; y += CardH)
            {
                double x = wa.Right - 16 - CardW * (col + 1);
                var r = new Rect(x + 4, y + 4, CardW - 8, CardH - 8);
                if (!taken.Any(t => t.IntersectsWith(r)))
                    return new Point(Math.Round(x / 8) * 8, Math.Round(y / 8) * 8);
            }
        }
        return new Point(wa.Left + 40, wa.Top + 40);
    }
}
