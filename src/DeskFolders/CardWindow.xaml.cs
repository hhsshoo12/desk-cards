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

/// <summary>
/// 바탕화면에 붙어 있는 그룹 카드 하나(2×2 미리보기).
/// 평소에는 고정이고, 펼친 창의 설정에서 '위치 옮기기 · 크기 조절'을 고르면 편집 모드가 된다.
/// </summary>
internal partial class CardWindow : Window
{
    private const string OverflowTag = "overflow";

    // 배율 적용 전 기준 단위(DIP)의 배치 값
    public const double Inset = 4, TopPad = 4, LabelH = 28;
    private const double MinW = 110, MinH = 130;

    private static uint _taskbarCreatedMsg;

    private readonly GroupManager _mgr;
    private Point _downPos;
    private object? _downTarget;
    private bool _pending, _editing;
    private double _baseW, _baseH, _iconSize = 40;
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
        DragLeave += (_, _) => UpdateBorder(false);
        Drop += OnDrop;
        ContextMenu = BuildMenu();

        DoneButton.MouseLeftButtonUp += (_, e) => { EndEdit(); e.Handled = true; };
        foreach (var grip in new[] { GripRight, GripBottom, GripCorner })
        {
            grip.DragDelta += OnGripDelta;
            grip.DragCompleted += (_, _) => OnGripDone();
        }
    }

    public GroupModel Group { get; }
    public bool ClosingByManager { get; set; }
    public bool IsEditing => _editing;

    public void Rebuild()
    {
        Label.Text = Group.Name;
        Cells.Children.Clear();
        var items = Group.Items;
        Placeholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int direct = items.Count > 4 ? 3 : items.Count;
        for (int i = 0; i < direct; i++)
        {
            var img = new Image { Source = items[i].Icon, Width = _iconSize, Height = _iconSize };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Cells.Children.Add(MakeCell(img, items[i], items[i].Name));
        }

        if (items.Count > 4)
        {
            double mini = _iconSize * 0.48;
            var grid = new UniformGrid { Rows = 2, Columns = 2, Width = mini * 2 + 8, Height = mini * 2 + 8 };
            foreach (var e in items.Skip(3).Take(4))
            {
                var img = new Image { Source = e.Icon, Width = mini, Height = mini, Margin = new Thickness(2) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                grid.Children.Add(img);
            }
            Cells.Children.Add(MakeCell(grid, OverflowTag, null));
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
        b.MouseEnter += (_, _) => { if (!_editing) b.SetResourceReference(Border.BackgroundProperty, "HoverBg"); };
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }

    // ----- 입력 -----
    // 평소: 아이콘 클릭 = 실행, 빈 곳 클릭 = 펼치기, 아이콘 끌기 = 밖으로 꺼내기. 카드는 움직이지 않는다.
    // 편집 모드: 아무 데나 끌기 = 격자에 맞춰 이동, 가장자리/모서리 끌기 = 크기 조절.

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (IsWithin<Thumb>(src) || IsWithin(src, DoneButton))
        {
            _pending = false;
            return;
        }
        _downPos = e.GetPosition(this);
        _downTarget = FindTag(src);
        _pending = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pending || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downPos;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pending = false;

        if (_editing)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
            _mgr.SavePosition(this);
        }
        else if (_downTarget is ShellEntry entry)
        {
            FileOps.DragOut(this, entry.Path);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pending) return;
        _pending = false;
        if (_editing) return;
        if (_downTarget is ShellEntry entry) FileOps.Launch(entry.Path);
        else ExpandedWindow.Open(this, _mgr);
    }

    private object? FindTag(DependencyObject? d)
    {
        while (d != null && d != this)
        {
            if (d is Border { Tag: not null } b) return b.Tag;
            d = Parent(d);
        }
        return null;
    }

    private static bool IsWithin<T>(DependencyObject? d) where T : DependencyObject
    {
        for (; d != null; d = Parent(d))
            if (d is T) return true;
        return false;
    }

    private static bool IsWithin(DependencyObject? d, DependencyObject target)
    {
        for (; d != null; d = Parent(d))
            if (d == target) return true;
        return false;
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    // ----- 편집 모드 -----

    public void BeginEdit()
    {
        _mgr.EndOtherEdits(this);
        _editing = true;
        DoneButton.Visibility = GripRight.Visibility = GripBottom.Visibility = GripCorner.Visibility = Visibility.Visible;
        Cursor = Cursors.SizeAll;
        UpdateBorder(false);
    }

    public void EndEdit()
    {
        if (!_editing) return;
        _editing = false;
        DoneButton.Visibility = GripRight.Visibility = GripBottom.Visibility = GripCorner.Visibility = Visibility.Collapsed;
        Cursor = null;
        UpdateBorder(false);
        _mgr.SavePosition(this);
    }

    private void OnGripDelta(object sender, DragDeltaEventArgs e)
    {
        // 손잡이는 배율 변환 밖(창 좌표)에 있으므로 이동량을 배율로 나눠 기준 단위로 바꾼다.
        double k = ScaleFactor;
        var wa = WorkAreaDip();
        var p = ActualPosition;
        double bw = _baseW, bh = _baseH;
        if (sender != GripBottom) bw = Math.Clamp(_baseW + e.HorizontalChange / k, MinW, Math.Max(MinW, (wa.Right - p.X) / k));
        if (sender != GripRight) bh = Math.Clamp(_baseH + e.VerticalChange / k, MinH, Math.Max(MinH, (wa.Bottom - p.Y) / k));
        LayoutFor(bw, bh);
    }

    private void OnGripDone()
    {
        _mgr.SaveCardSize(this, _baseW, _baseH);
        FitToScreen();
        _mgr.SavePosition(this);
    }

    private Rect WorkAreaDip()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var p = ActualPosition;
        var wa = DesktopGrid.WorkAreaAt((int)(p.X * dpi.DpiScaleX) + 1, (int)(p.Y * dpi.DpiScaleY) + 1);
        return new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY, wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
    }

    // ----- 드롭 -----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = FileOps.CanAccept(e.Data, Group.Folder);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        UpdateBorder(ok);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        UpdateBorder(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            FileOps.AddToGroup(paths, Group.Folder, _mgr.Root);
        // 이동은 우리가 직접 했으므로 원본 쪽에서 삭제하지 않도록 None을 돌려준다.
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>드롭 대상이거나 편집 중이면 강조 테두리.</summary>
    private void UpdateBorder(bool dropTarget)
    {
        if (dropTarget || _editing)
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
        m.Items.Add(Item("위치 옮기기 · 크기 조절", BeginEdit));
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
        ApplySize();

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
            // 끄는 동안 격자 칸 단위로 움직인다.
            // 시스템이 주는 제안 위치는 직전(이미 칸에 맞춘) 위치 + 작은 이동량이라 매번 같은 칸으로
            // 반올림돼 버린다. 그래서 끌기 시작점부터의 전체 마우스 이동량으로 직접 계산한다.
            var r = Marshal.PtrToStructure<Native.RECT>(lParam);
            Native.GetCursorPos(out var cur);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            var (x, y) = DesktopGrid.Snap(_moveWindowStart.Left + (cur.X - _moveCursorStart.X),
                                          _moveWindowStart.Top + (cur.Y - _moveCursorStart.Y), w, h);
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

    // ----- 크기 / 위치 -----

    /// <summary>'Windows 배율 따라가기'가 켜져 있으면 모니터 배율(125% = 1.25), 아니면 1.</summary>
    private double ScaleFactor => _mgr.FollowWindowsScale ? VisualTreeHelper.GetDpi(this).DpiScaleX : 1;

    /// <summary>
    /// 기본 크기(배율 적용 전): 가로는 바탕화면 아이콘 2칸, 세로는 정사각형 카드 + 이름 줄.
    /// </summary>
    public static Size DefaultBaseSize(double dpiScale)
    {
        double w = 152;
        if (DesktopGrid.TryGet(out _, out _, out int cx, out _)) w = DesktopGrid.CardCols * cx / dpiScale;
        return new Size(w, TopPad + (w - 2 * Inset) + LabelH);
    }

    /// <summary>저장된 크기(없으면 기본 크기)에 배율을 곱해 적용한다.</summary>
    public void ApplySize()
    {
        var size = _mgr.GetCardSize(Group.Name) ?? DefaultBaseSize(VisualTreeHelper.GetDpi(this).DpiScaleX);
        LayoutFor(size.Width, size.Height);
    }

    private void LayoutFor(double bw, double bh)
    {
        _baseW = bw;
        _baseH = bh;
        double k = ScaleFactor;
        Width = bw * k;
        Height = bh * k;
        // 아이콘·글자·모서리까지 같은 비율로 커지도록 내용 전체에 배율을 건다(여백은 변환 밖이라 직접 곱한다).
        Layout.LayoutTransform = k == 1 ? Transform.Identity : new ScaleTransform(k, k);
        Layout.Margin = new Thickness(Inset * k, TopPad * k, Inset * k, 0);

        // 아이콘 크기는 칸 크기를 따라간다(기본 크기에서 40).
        double bodyW = bw - 2 * Inset, bodyH = bh - TopPad - LabelH;
        double pad = Math.Min(bodyW, bodyH) * 0.085;
        Cells.Margin = new Thickness(pad);
        double cell = Math.Min((bodyW - 2 * pad) / 2, (bodyH - 2 * pad) / 2) - 6;
        double icon = Math.Clamp(Math.Round(cell * 0.74), 16, 128);
        if (Math.Abs(icon - _iconSize) > 0.5)
        {
            _iconSize = icon;
            Rebuild();
        }
    }

    /// <summary>크기를 다시 적용하고, 작업 영역 밖으로 나갔으면 안쪽으로 당긴다.</summary>
    public void FitToScreen()
    {
        ApplySize();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(Width * dpi.DpiScaleX), h = (int)Math.Round(Height * dpi.DpiScaleY);
        var wa = DesktopGrid.WorkAreaAt(r.Left + w / 2, r.Top + h / 2);
        int x = Math.Max(wa.Left, Math.Min(r.Left, wa.Right - w));
        int y = Math.Max(wa.Top, Math.Min(r.Top, wa.Bottom - h));
        if (x != r.Left || y != r.Top)
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 실제 창 위치(DIP). SetWindowPos나 끌기로 옮긴 직후에는 WPF의 Left/Top이 늦게 갱신될 수 있어
    /// 저장할 때는 이 값을 쓴다.
    /// </summary>
    public Point ActualPosition
    {
        get
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return new Point(Left, Top);
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Point(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Group.Changed -= Rebuild;
        base.OnClosed(e);
    }
}
