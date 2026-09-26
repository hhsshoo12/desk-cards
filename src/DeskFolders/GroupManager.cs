using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace DeskFolders;

/// <summary>그룹 루트 폴더의 하위 폴더마다 카드 창을 하나씩 띄우고 동기화한다.</summary>
internal sealed class GroupManager
{
    private const double CardW = 196, CardH = 206;

    private readonly Config _cfg = Config.Load();
    private readonly Dictionary<string, CardWindow> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce;
    private FileSystemWatcher? _rootWatcher;
    private bool _shuttingDown;

    public GroupManager()
    {
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskFolders");
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Reconcile(); };
    }

    public string Root { get; }
    public IEnumerable<GroupModel> Groups => _cards.Values.Select(c => c.Group);

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
            }
            _cfg.FollowWindowsScale = value;
            _cfg.Save();
            foreach (var card in _cards.Values)
            {
                card.FitToScreen();
                SavePosition(card);
            }
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
    }

    /// <summary>다른 카드들의 화면 위치(픽셀). 옮길 때 안내선 기준으로 쓴다.</summary>
    public IReadOnlyList<Native.RECT> CardRects(CardWindow except) =>
        _cards.Values.Where(c => c != except)
            .Select(c => Native.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(c).Handle, out var r) ? r : default)
            .Where(r => r.Right > r.Left)
            .ToList();

    /// <summary>편집 모드는 한 번에 카드 하나만.</summary>
    public void EndOtherEdits(CardWindow except)
    {
        foreach (var c in _cards.Values)
            if (c != except) c.EndEdit();
    }

    public void Start()
    {
        MigrateScale();
        Directory.CreateDirectory(Root);
        if (!ListGroupFolders().Any()) Directory.CreateDirectory(Path.Combine(Root, "새 그룹"));
        Reconcile();

        _rootWatcher = new FileSystemWatcher(Root) { NotifyFilter = NotifyFilters.DirectoryName, IncludeSubdirectories = false };
        FileSystemEventHandler h = (_, _) => Bump();
        _rootWatcher.Created += h;
        _rootWatcher.Deleted += h;
        _rootWatcher.Renamed += (_, _) => Bump();
        _rootWatcher.EnableRaisingEvents = true;

        // Windows 배율이 바뀌었는데 알림을 못 받은 카드는 새 배율로 다시 만든다.
        var dpiCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        dpiCheck.Tick += (_, _) =>
        {
            foreach (var card in _cards.Values.Where(c => c.IsDpiStale).ToList())
                RecreateCard(card);
        };
        dpiCheck.Start();
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

    private void Bump() => _debounce.Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });

    private IEnumerable<string> ListGroupFolders()
    {
        try
        {
            return Directory.EnumerateDirectories(Root)
                .Where(d => (File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Reconcile()
    {
        if (_shuttingDown) return;
        var folders = ListGroupFolders().ToList();
        var names = new HashSet<string>(folders.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

        foreach (var name in _cards.Keys.Where(k => !names.Contains(k)).ToList())
            RemoveCard(name);

        foreach (var folder in folders)
        {
            string name = Path.GetFileName(folder);
            if (!_cards.ContainsKey(name)) CreateCard(folder);
        }
    }

    private CardWindow CreateCard(string folder)
    {
        var group = new GroupModel(folder);
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
        return card;
    }

    private void OnCardClosed(object? sender, EventArgs e)
    {
        if (sender is not CardWindow card || card.ClosingByManager || _shuttingDown) return;
        // 탐색기가 재시작되면 소유자(Progman)와 함께 카드가 파괴된다. 잠시 뒤 다시 띄운다.
        _cards.Remove(card.Group.Name);
        card.Group.Dispose();
        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        retry.Tick += (_, _) =>
        {
            if (Native.FindWindow("Progman", null) == IntPtr.Zero) return;
            retry.Stop();
            Reconcile();
        };
        retry.Start();
    }

    private void RemoveCard(string name)
    {
        if (!_cards.Remove(name, out var card)) return;
        ExpandedWindow.CloseFor(card);
        card.ClosingByManager = true;
        card.Close();
        card.Group.Dispose();
    }

    public void SavePosition(CardWindow card)
    {
        var p = card.ActualPosition;
        _cfg.Positions[card.Group.Name] = new[] { p.X, p.Y };
        _cfg.Save();
    }

    public void NewGroup()
    {
        string path = FileOps.Unique(Root, "새 그룹");
        Directory.CreateDirectory(path);
        Reconcile();
        if (_cards.TryGetValue(Path.GetFileName(path), out var card))
            ExpandedWindow.Open(card, this, editTitle: true);
    }

    public bool RenameGroup(CardWindow card, string newName)
    {
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName.Trim('.').Length == 0)
        {
            MessageBox.Show("이름에 쓸 수 없는 문자가 있어요: \\ / : * ? \" < > |", "DeskFolders");
            return false;
        }
        string oldName = card.Group.Name;
        string dest = Path.Combine(Root, newName);
        bool caseOnly = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Directory.Exists(dest))
        {
            MessageBox.Show($"'{newName}' 그룹이 이미 있어요.", "DeskFolders");
            return false;
        }
        try
        {
            if (caseOnly)
            {
                string tmp = dest + ".~tmp";
                Directory.Move(card.Group.Folder, tmp);
                Directory.Move(tmp, dest);
            }
            else
            {
                Directory.Move(card.Group.Folder, dest);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DeskFolders");
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
        return true;
    }

    public void DeleteGroup(CardWindow card)
    {
        var g = card.Group;
        if (g.Items.Count > 0)
        {
            var r = MessageBox.Show($"'{g.Name}' 그룹을 삭제할까요?\n안에 있는 항목 {g.Items.Count}개는 바탕화면으로 옮겨져요.",
                "DeskFolders", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
            foreach (var e in g.Items.ToList()) FileOps.MoveTo(e.Path, FileOps.UserDesktop);
        }
        try
        {
            Directory.Delete(g.Folder, recursive: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"폴더를 지우지 못했어요: {ex.Message}", "DeskFolders");
            return;
        }
        _cfg.Positions.Remove(g.Name);
        _cfg.Layouts.Remove(g.Name);
        _cfg.Save();
        RemoveCard(g.Name);
    }

    public void Shutdown()
    {
        _shuttingDown = true;
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
