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

    public void Start()
    {
        Directory.CreateDirectory(Root);
        if (!ListGroupFolders().Any()) Directory.CreateDirectory(Path.Combine(Root, "새 그룹"));
        Reconcile();

        _rootWatcher = new FileSystemWatcher(Root) { NotifyFilter = NotifyFilters.DirectoryName, IncludeSubdirectories = false };
        FileSystemEventHandler h = (_, _) => Bump();
        _rootWatcher.Created += h;
        _rootWatcher.Deleted += h;
        _rootWatcher.Renamed += (_, _) => Bump();
        _rootWatcher.EnableRaisingEvents = true;
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
        // 저장된 위치든 새 자리든 바탕화면 칸에 맞춘다(아이콘 크기를 바꿨을 수도 있다).
        card.SnapToGrid();
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
        _cfg.Positions[card.Group.Name] = new[] { card.Left, card.Top };
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
        _cfg.Positions[newName] = new[] { card.Left, card.Top };
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
        var taken = _cards.Values.Select(c => new Rect(c.Left, c.Top, CardW, CardH)).ToList();
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
