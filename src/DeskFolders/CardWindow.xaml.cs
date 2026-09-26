using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DeskFolders;

/// <summary>바탕화면에 붙어 있는 그룹 카드 하나(2×2 미리보기).</summary>
internal partial class CardWindow : Window
{
    private const string OverflowTag = "overflow";
    private static uint _taskbarCreatedMsg;

    private readonly GroupManager _mgr;
    private Point _downPos;
    private object? _downTarget;
    private bool _pending, _altDown;
    private Native.POINT _moveCursorStart;
    private Native.RECT _moveWindowStart;

    public CardWindow(GroupModel group, GroupManager mgr)
    {
        InitializeComponent();
        Group = group;
        _mgr = mgr;
        Group.Changed += Rebuild;
        Rebuild();

        SourceInitialized += OnSourceInitialized;
        PreviewMouseLeftButtonDown += OnDown;
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += OnUp;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => SetDropHighlight(false);
        Drop += OnDrop;
        ContextMenu = BuildMenu();
    }

    public GroupModel Group { get; }
    public bool ClosingByManager { get; set; }

    public void Rebuild()
    {
        Label.Text = Group.Name;
        Cells.Children.Clear();
        var items = Group.Items;
        Placeholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int direct = items.Count > 4 ? 3 : items.Count;
        for (int i = 0; i < direct; i++)
        {
            var img = new Image { Source = items[i].Icon, Width = 40, Height = 40 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Cells.Children.Add(MakeCell(img, items[i], items[i].Name));
        }

        if (items.Count > 4)
        {
            var mini = new UniformGrid { Rows = 2, Columns = 2, Width = 46, Height = 46 };
            foreach (var e in items.Skip(3).Take(4))
            {
                var img = new Image { Source = e.Icon, Width = 19, Height = 19, Margin = new Thickness(2) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                mini.Children.Add(img);
            }
            Cells.Children.Add(MakeCell(mini, OverflowTag, null));
        }
    }

    private Border MakeCell(UIElement content, object tag, string? tip)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(3),
            Background = Brushes.Transparent,
            Tag = tag,
            Child = content,
            ToolTip = tip,
        };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }

    // ----- 입력: 클릭 = 실행/펼치기, 끌기 = 항목 꺼내기/카드 이동, Alt+끌기 = 어디서든 카드 이동 -----

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _downPos = e.GetPosition(this);
        _downTarget = FindTag(e.OriginalSource as DependencyObject);
        // 카드는 활성화되지 않는 창이라 WPF 키보드 상태 대신 실제 키 상태를 본다.
        _altDown = (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
        _pending = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pending || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downPos;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pending = false;

        if (_downTarget is ShellEntry entry && !_altDown)
        {
            FileOps.DragOut(this, entry.Path);
        }
        else
        {
            try { DragMove(); } catch (InvalidOperationException) { }
            SnapToGrid();
            _mgr.SavePosition(this);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pending) return;
        _pending = false;
        if (_altDown) return; // Alt를 누른 채 움직이지 않고 뗀 건 실수로 보고 아무것도 안 한다.
        if (_downTarget is ShellEntry entry) FileOps.Launch(entry.Path);
        else ExpandedWindow.Open(this, _mgr);
    }

    private object? FindTag(DependencyObject? d)
    {
        while (d != null && d != this)
        {
            if (d is Border { Tag: not null } b) return b.Tag;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ----- 드롭 -----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = FileOps.CanAccept(e.Data, Group.Folder);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        SetDropHighlight(ok);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            FileOps.AddToGroup(paths, Group.Folder, _mgr.Root);
        // 이동은 우리가 직접 했으므로 원본 쪽에서 삭제하지 않도록 None을 돌려준다.
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void SetDropHighlight(bool on)
    {
        if (on)
        {
            Card.SetResourceReference(Border.BorderBrushProperty, "Accent");
            Card.BorderThickness = new Thickness(2);
        }
        else
        {
            Card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            Card.BorderThickness = new Thickness(1);
        }
    }

    // ----- 메뉴 -----

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(Item("펼치기", () => ExpandedWindow.Open(this, _mgr)));
        m.Items.Add(Item("이름 바꾸기", () => ExpandedWindow.Open(this, _mgr, editTitle: true)));
        m.Items.Add(Item("폴더 열기", () => FileOps.OpenFolder(Group.Folder)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("새 그룹", () => _mgr.NewGroup()));
        m.Items.Add(Item("그룹 삭제 (항목은 바탕화면으로)", () => _mgr.DeleteGroup(this)));
        return m;
    }

    private static MenuItem Item(string header, Action act)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => act();
        return mi;
    }

    // ----- 바탕화면 층에 붙이기 -----

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        AttachToDesktop(hwnd);

        if (_taskbarCreatedMsg == 0) _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
    }

    private static void AttachToDesktop(IntPtr hwnd)
    {
        // Progman을 소유자로 두면 Win+D(바탕화면 보기) 때도 숨겨지지 않는다.
        var progman = Native.FindWindow("Progman", null);
        if (progman != IntPtr.Zero) Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, progman);
        Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_WINDOWPOSCHANGING)
        {
            // 항상 다른 창 뒤(바탕화면 바로 위)에 머문다.
            var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            if ((wp.flags & Native.SWP_NOZORDER) == 0)
            {
                wp.hwndInsertAfter = Native.HWND_BOTTOM;
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }
        else if (msg == Native.WM_ENTERSIZEMOVE)
        {
            Native.GetCursorPos(out _moveCursorStart);
            Native.GetWindowRect(hwnd, out _moveWindowStart);
        }
        else if (msg == Native.WM_MOVING)
        {
            // 끄는 동안에도 바탕화면 아이콘처럼 칸 단위로 움직인다.
            // 시스템이 주는 제안 위치는 직전(이미 칸에 맞춘) 위치 + 작은 이동량이라 매번 같은 칸으로
            // 반올림돼 버린다. 그래서 끌기 시작점부터의 전체 마우스 이동량으로 직접 계산한다.
            var r = Marshal.PtrToStructure<Native.RECT>(lParam);
            Native.GetCursorPos(out var cur);
            int wantX = _moveWindowStart.Left + (cur.X - _moveCursorStart.X);
            int wantY = _moveWindowStart.Top + (cur.Y - _moveCursorStart.Y);
            var (x, y) = SnapWindowPx(wantX, wantY);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            Marshal.StructureToPtr(new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AttachToDesktop(hwnd);
        }
        return IntPtr.Zero;
    }

    // ----- 바탕화면 격자 맞춤 -----

    /// <summary>창 안에서 실제로 보이는 영역(카드 + 이름). XAML의 Grid 여백/크기와 맞춘다.</summary>
    private static readonly Rect ContentRect = new(14, 6, 168, 194);

    /// <summary>현재 위치를 가장 가까운 바탕화면 칸에 맞춘다.</summary>
    public void SnapToGrid()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        var (x, y) = SnapWindowPx(r.Left, r.Top);
        if (x != r.Left || y != r.Top)
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private (int X, int Y) SnapWindowPx(int winX, int winY)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int offX = (int)Math.Round(ContentRect.X * dpi.DpiScaleX), offY = (int)Math.Round(ContentRect.Y * dpi.DpiScaleY);
        int w = (int)Math.Round(ContentRect.Width * dpi.DpiScaleX), h = (int)Math.Round(ContentRect.Height * dpi.DpiScaleY);
        var (cx, cy) = DesktopGrid.Snap(winX + offX, winY + offY, w, h);
        return (cx - offX, cy - offY);
    }

    protected override void OnClosed(EventArgs e)
    {
        Group.Changed -= Rebuild;
        base.OnClosed(e);
    }
}
