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
    private bool _pending;

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
            Cells.Children.Add(MakeCell(mini, OverflowTag, $"{items.Count - 3}개 더 보기"));
        }
    }

    private Border MakeCell(UIElement content, object tag, string tip)
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

    // ----- 입력: 클릭 = 실행/펼치기, 끌기 = 항목 꺼내기/카드 이동 -----

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _downPos = e.GetPosition(this);
        _downTarget = FindTag(e.OriginalSource as DependencyObject);
        _pending = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pending || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downPos;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pending = false;

        if (_downTarget is ShellEntry entry)
        {
            FileOps.DragOut(this, entry.Path);
        }
        else
        {
            try { DragMove(); } catch (InvalidOperationException) { }
            Left = Math.Round(Left / 8) * 8;
            Top = Math.Round(Top / 8) * 8;
            _mgr.SavePosition(this);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pending) return;
        _pending = false;
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
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AttachToDesktop(hwnd);
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        Group.Changed -= Rebuild;
        base.OnClosed(e);
    }
}
