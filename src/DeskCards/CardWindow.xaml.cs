using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskCards;

/// <summary>
/// 바탕화면에 붙어 있는 그룹 카드 하나(2×2 미리보기).
/// 평소에는 고정이고, 설정이나 우클릭 메뉴에서 편집을 시작하면(편집 막대) 옮기고 크기를 바꿀 수 있다.
/// </summary>
internal partial class CardWindow : Window
{
    private const string OverflowTag = "overflow";

    // 배율 적용 전 기준 단위(DIP)의 배치 값
    public const double Inset = 4, TopPad = 4, LabelH = 28;

    private static uint _taskbarCreatedMsg;

    private readonly GroupManager _mgr;
    private Point _downPos;
    private object? _downTarget;
    private bool _downOnLabel;
    private bool _pending, _editing, _selected;
    private CardLayout _layout = new();
    private double _iconSize = 40;
    private double _appliedScale = 1;
    private bool _closed;
    private Native.POINT _moveCursorStart;
    private Native.RECT _moveWindowStart;
    private System.Collections.Generic.IReadOnlyList<Native.RECT> _moveOthers = Array.Empty<Native.RECT>();

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

        GripCorner.DragStarted += (_, _) => OnGripStart();
        GripCorner.DragDelta += (_, _) => OnGripDelta();
        GripCorner.DragCompleted += (_, _) => OnGripDone();
    }

    public GroupModel Group { get; }
    private IntPtr Handle => Hwnd.Of(this);
    public bool ClosingByManager { get; set; }
    public bool IsEditing => _editing;
    public CardLayout CurrentLayout => _layout;

    public void Rebuild()
    {
        Label.Text = Group.Name;
        Cells.Children.Clear();
        Cells.Columns = _layout.Cols;
        Cells.Rows = _layout.Rows;
        var items = Group.Items;
        Placeholder.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 칸이 모자라면 마지막 칸을 '더보기' 묶음으로 쓴다.
        int slots = _layout.Cols * _layout.Rows;
        bool overflow = items.Count > slots;
        int direct = overflow ? slots - 1 : items.Count;
        for (int i = 0; i < direct; i++)
        {
            var img = new Image { Source = items[i].Icon, Width = _iconSize, Height = _iconSize };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Cells.Children.Add(MakeCell(img, items[i], items[i].Name));
        }

        if (overflow)
        {
            double mini = _iconSize * 0.48;
            var grid = new UniformGrid { Rows = 2, Columns = 2, Width = mini * 2 + 8, Height = mini * 2 + 8 };
            foreach (var e in items.Skip(direct).Take(4))
            {
                var img = new Image { Source = e.Icon, Width = mini, Height = mini, Margin = new Thickness(2) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                grid.Children.Add(img);
            }
            var more = MakeCell(grid, OverflowTag, null);
            more.MouseEnter += (_, _) => StartHoverExpand();
            more.MouseLeave += (_, _) => _hoverTimer?.Stop();
            Cells.Children.Add(more);
        }
    }

    private DispatcherTimer? _hoverTimer;

    /// <summary>더보기 칸에 마우스를 잠시 올려 두면 펼친다(일반 설정). 펼친 창은 그 밖으로 나가면 바로 접힌다.</summary>
    private void StartHoverExpand()
    {
        if (_editing || !_mgr.HoverExpand || Mouse.LeftButton == MouseButtonState.Pressed) return;
        _hoverTimer ??= new DispatcherTimer();
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(_mgr.HoverExpandDelay);
        _hoverTimer.Tick -= OnHoverTick;
        _hoverTimer.Tick += OnHoverTick;
        _hoverTimer.Start();
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        _hoverTimer?.Stop();
        if (_editing || _closed || Mouse.LeftButton == MouseButtonState.Pressed || ExpandedWindow.IsOpenFor(this)) return;
        ExpandedWindow.Open(this, _mgr, hover: true);
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
    // 평소: 아이콘 클릭 = 실행, 더보기 칸·이름 클릭 = 펼치기(빈 칸은 아무 일 없음), 아이콘 끌기 = 밖으로 꺼내기. 카드는 움직이지 않는다.
    // 편집 모드: 누르기 = 고르기, 아무 데나 끌기 = 자유 이동(안내선), 오른쪽 아래 모서리 끌기 = 크기 조절.

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (_editing) _mgr.Select(this);
        if (IsWithin<Thumb>(src))
        {
            _pending = false;
            return;
        }
        _downPos = e.GetPosition(this);
        _downTarget = FindTag(src);
        _downOnLabel = IsWithin(src, Label);
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
        else if (_downTarget as string == OverflowTag || _downOnLabel) ExpandedWindow.Open(this, _mgr);
    }

    private object? FindTag(DependencyObject? d)
    {
        while (d != null && d != this)
        {
            if (d is Border { Tag: not null } b) return b.Tag;
            d = GetParent(d);
        }
        return null;
    }

    private static bool IsWithin(DependencyObject? d, DependencyObject target)
    {
        for (; d != null; d = GetParent(d))
            if (d == target) return true;
        return false;
    }

    private static bool IsWithin<T>(DependencyObject? d) where T : DependencyObject
    {
        for (; d != null; d = GetParent(d))
            if (d is T) return true;
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject d) =>
        d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    // ----- 편집 모드 -----

    /// <summary>편집 모드로. 켜고 끄는 건 GroupManager.BeginEditMode/EndEditMode가 모든 카드에 한꺼번에 한다.</summary>
    public void BeginEdit()
    {
        _editing = true;
        GripCorner.Visibility = Visibility.Visible;
        Cursor = Cursors.SizeAll;
        UpdateBorder(false);
        RaiseForEdit();
    }

    /// <summary>편집 중에는 어두운 막(EditDim) 위로 올라온다. 편집이 끝나면 다시 바탕화면 층으로 내려간다.</summary>
    public void RaiseForEdit()
    {
        if (_editing) Hwnd.SetZOrder(Handle, Hwnd.Topmost);
    }

    public void EndEdit()
    {
        if (!_editing) return;
        _editing = _selected = false;
        GripCorner.Visibility = Visibility.Collapsed;
        Cursor = null;
        UpdateBorder(false);
        // HWND_BOTTOM은 맨 위(topmost) 상태도 함께 푼다.
        Hwnd.SetZOrder(Handle, Native.HWND_BOTTOM);
        _mgr.SavePosition(this);
    }

    /// <summary>편집 막대의 작업 대상으로 골랐는지. 고른 카드는 테두리가 굵어진다.</summary>
    public void SetSelected(bool on)
    {
        _selected = on && _editing;
        UpdateBorder(false);
    }

    /// <summary>화살표 키로 조금씩 옮긴다(물리 픽셀). 작업 영역 밖으로는 나가지 않는다.</summary>
    public void Nudge(int dx, int dy)
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var wa = DesktopGrid.WorkAreaAt(r.Left + w / 2, r.Top + h / 2);
        int x = Math.Max(wa.Left, Math.Min(r.Left + dx, wa.Right - w));
        int y = Math.Max(wa.Top, Math.Min(r.Top + dy, wa.Bottom - h));
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _mgr.SavePosition(this);
    }

    private double _gripZoom;

    private void OnGripStart()
    {
        BeginMoveTracking(Handle);
        _gripZoom = _appliedScale / ScaleFactor;
    }

    /// <summary>끌기·크기 조절 시작점(마우스·창 위치)과 안내선 기준이 될 다른 카드 위치를 기억한다.</summary>
    private void BeginMoveTracking(IntPtr hwnd)
    {
        Native.GetCursorPos(out _moveCursorStart);
        Native.GetWindowRect(hwnd, out _moveWindowStart);
        _moveOthers = _mgr.CardRects(except: this);
    }

    private void OnGripDelta()
    {
        // 비율 고정: 가로·세로 늘어난 비율의 평균만큼 카드 전체를 키우거나 줄인다.
        // 손잡이가 모서리와 함께 움직여 증분이 흔들리므로, 잡은 순간부터의 전체 마우스 이동량으로 계산한다.
        Native.GetCursorPos(out var cur);
        var r0 = _moveWindowStart;
        double w0 = r0.Right - r0.Left, h0 = r0.Bottom - r0.Top;
        double g = ((w0 + cur.X - _moveCursorStart.X) / w0 + (h0 + cur.Y - _moveCursorStart.Y) / h0) / 2;

        // 다른 카드·화면 가운데와 끝선이 맞으면 붙고 안내선을 보여 준다.
        var wa = DesktopGrid.WorkAreaAt(r0.Left + 1, r0.Top + 1);
        var (sg, lines) = SmartGuides.SnapScale(r0, g, _moveOthers, wa, Native.MonitorScaleOf(Handle));
        SmartGuides.Show(wa, lines);
        SetZoomClamped(_gripZoom * sg);
    }

    private void OnGripDone()
    {
        SmartGuides.Hide();
        CommitLayout();
    }

    /// <summary>바뀐 모양을 적용·저장하고, 화면 밖으로 나갔으면 당겨서 위치도 저장한다.</summary>
    private void CommitLayout()
    {
        FitToScreen();
        _mgr.SaveLayout(this, _layout);
        _mgr.SavePosition(this);
    }

    /// <summary>확대 비율을 바꾸되, 최소·최대와 작업 영역(오른쪽·아래 끝) 안으로 제한한다.</summary>
    private void SetZoomClamped(double zoom)
    {
        var wa = WorkAreaDip();
        var p = ActualPosition;
        var size = BaseSize(_layout, _mgr.CellSize);
        double k = ScaleFactor;
        double maxZoom = Math.Min((wa.Right - p.X) / (size.Width * k), (wa.Bottom - p.Y) / (size.Height * k));
        _layout.Zoom = Math.Clamp(zoom, CardLayout.MinZoom, Math.Max(CardLayout.MinZoom, Math.Min(CardLayout.MaxZoom, maxZoom)));
        LayoutFor();
    }

    /// <summary>
    /// 설정에 보여 주는 카드 크기(%). 기준 크기 대비 지금 보이는 크기로, Windows 배율을 따라가는 중이면
    /// 125% 배율에서 기본 카드는 125%가 된다.
    /// </summary>
    public int SizePercent => (int)Math.Round(_appliedScale * 100);

    /// <summary>카드 크기를 %로 정한다. 화면에 들어가지 않으면 들어가는 만큼만 키운다. 실제 적용된 값을 돌려준다.</summary>
    public int SetSizePercent(int percent)
    {
        SetZoomClamped(percent / 100.0 / ScaleFactor);
        CommitLayout();
        return SizePercent;
    }

    /// <summary>미리보기 칸 수를 바꾼다(카드 비율이 따라 바뀐다).</summary>
    public void SetGrid(int cols, int rows)
    {
        _layout.Cols = Math.Clamp(cols, CardLayout.MinCells, CardLayout.MaxCells);
        _layout.Rows = Math.Clamp(rows, CardLayout.MinCells, CardLayout.MaxCells);
        CommitLayout();
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

    /// <summary>드롭 대상이거나 편집 중이면 강조 테두리. 편집 중에 고른 카드는 더 굵게.</summary>
    private void UpdateBorder(bool dropTarget)
    {
        // 고른 카드는 배경화면 색에 묻히지 않도록 강조색으로 은은하게 빛나게 한다.
        var glow = (System.Windows.Media.Effects.DropShadowEffect)Card.Effect;
        bool strong = dropTarget || _selected;
        glow.Color = strong && TryFindResource("Accent") is SolidColorBrush accent ? accent.Color : Colors.Black;
        glow.ShadowDepth = strong ? 0 : 2;
        glow.BlurRadius = strong ? 18 : 10;
        glow.Opacity = strong ? 0.9 : 0.22;

        if (strong)
        {
            Card.SetResourceReference(Border.BorderBrushProperty, "Accent");
            Card.BorderThickness = new Thickness(2.5);
        }
        else if (_editing)
        {
            Card.SetResourceReference(Border.BorderBrushProperty, "Accent");
            Card.BorderThickness = new Thickness(1.5);
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
        m.Items.Add(Item("카드 편집 (이동 · 크기 · 삭제)", () => _mgr.BeginEditMode(this)));
        m.Items.Add(Item("이름 바꾸기", () => ExpandedWindow.Open(this, _mgr, editTitle: true)));
        m.Items.Add(Item("폴더 열기", () => FileOps.OpenFolder(Group.Folder)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("새 그룹", () => _mgr.NewGroup()));
        m.Items.Add(Item("그룹 삭제 (항목은 바탕화면으로)", () => _mgr.DeleteGroup(this)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("설정", () => SettingsWindow.Open(_mgr, this)));
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
        var hwnd = Handle;
        Hwnd.MakeNoActivateTool(hwnd);
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
        Hwnd.SetZOrder(hwnd, Native.HWND_BOTTOM);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_WINDOWPOSCHANGING)
        {
            // 항상 다른 창 뒤(바탕화면 바로 위)에 머문다. 편집 중에만 어두운 막 위로 올라온다.
            var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            if ((wp.flags & Native.SWP_NOZORDER) == 0 && !_editing)
            {
                wp.hwndInsertAfter = Native.HWND_BOTTOM;
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }
        else if (msg == Native.WM_ENTERSIZEMOVE)
        {
            BeginMoveTracking(hwnd);
        }
        else if (msg == Native.WM_MOVING)
        {
            // 자유 이동 + 스마트 가이드: 다른 카드·화면 가운데와 줄이 맞으면 살짝 붙고 안내선을 보여 준다.
            // 시스템이 주는 제안 위치는 직전(이미 붙은) 위치 기준이라 한번 붙으면 빠져나오지 못한다.
            // 그래서 끌기 시작점부터의 전체 마우스 이동량으로 직접 계산한다.
            var r = Marshal.PtrToStructure<Native.RECT>(lParam);
            Native.GetCursorPos(out var cur);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            int fx = _moveWindowStart.Left + (cur.X - _moveCursorStart.X);
            int fy = _moveWindowStart.Top + (cur.Y - _moveCursorStart.Y);
            var wa = DesktopGrid.WorkAreaAt(fx + w / 2, fy + h / 2);
            var (x, y, lines) = SmartGuides.Snap(fx, fy, w, h, _moveOthers, wa, Native.MonitorScaleOf(hwnd));
            SmartGuides.Show(wa, lines);
            Marshal.StructureToPtr(new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
        else if (msg == Native.WM_EXITSIZEMOVE)
        {
            SmartGuides.Hide();
        }
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AttachToDesktop(hwnd);
        }
        return IntPtr.Zero;
    }

    // ----- 크기 / 위치 -----

    /// <summary>이 카드가 있는 모니터의 지금 배율(125% = 1.25).</summary>
    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>확대 비율에 곱할 값. 따라가기를 끈 동안 Windows 배율이 바뀌어도 실제 크기를 유지한다.</summary>
    private double ScaleFactor => _mgr.ZoomFactor(DpiScale);

    /// <summary>
    /// 미리보기 칸 하나의 기준 크기(DIP)를 바탕화면 아이콘 간격으로 잰다. 기본 2×2 카드의 가로가 아이콘 2칸이 된다.
    /// 처음 한 번만 재서 설정에 고정한다(배율을 바꾼 직후에는 아이콘 간격이 늦게 바뀌어 값이 흔들린다).
    /// </summary>
    public static double MeasureCellSize(double dpiScale)
    {
        double w = 152;
        if (DesktopGrid.TryGetIconSpacing(out int cx)) w = DesktopGrid.CardCols * cx / dpiScale;
        return (w - 2 * Inset) / 2;
    }

    /// <summary>확대 비율을 적용하기 전의 카드 창 크기. 칸 수가 비율을 정한다.</summary>
    public static Size BaseSize(CardLayout layout, double cell) =>
        new(layout.Cols * cell + 2 * Inset, TopPad + layout.Rows * cell + LabelH);

    /// <summary>따라가기를 켜고 끌 때 보이는 크기가 그대로 남도록 확대 비율을 옮겨 담는다.</summary>
    public void RebaseZoom(double factor)
    {
        _layout.Zoom *= factor;
        _layout = _layout.Normalized();
        _mgr.SaveLayout(this, _layout);
    }

    /// <summary>
    /// 모니터 배율이 바뀌었는데 이 창이 그 알림을 못 받아 옛 배율로 그려지고 있는지.
    /// (바탕화면 층에 붙인 투명 창은 WM_DPICHANGED를 못 받는 경우가 있다.)
    /// </summary>
    public bool IsDpiStale
    {
        get
        {
            var hwnd = Handle;
            if (hwnd == IntPtr.Zero || _editing) return false;
            return Math.Abs(Native.MonitorScaleOf(hwnd) - DpiScale) > 0.01;
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Windows 배율이 바뀌면 크기를 다시 계산하고 화면 안으로 맞춘다.
        Dispatcher.BeginInvoke(() => { if (!_closed) Refit(); }, DispatcherPriority.Background);
    }

    /// <summary>저장된 모양(칸 수·확대 비율)을 불러와 적용한다.</summary>
    public void ApplySize()
    {
        _layout = _mgr.GetLayout(Group.Name);
        LayoutFor();
    }

    private void LayoutFor()
    {
        var size = BaseSize(_layout, _mgr.CellSize);
        // 비율 고정 확대: 카드 확대 비율(배율 보정 포함)을 내용 전체(아이콘·글자·모서리)에 똑같이 건다.
        double t = ScaleFactor * _layout.Zoom;
        var wa = System.Windows.Forms.Screen.FromHandle(Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        t = Math.Min(t, Math.Min(wa.Width / (size.Width * dpi.DpiScaleX), wa.Height / (size.Height * dpi.DpiScaleY)));
        _appliedScale = t;
        Width = size.Width * t;
        Height = size.Height * t;
        Layout.LayoutTransform = t == 1 ? Transform.Identity : new ScaleTransform(t, t);
        Layout.Margin = new Thickness(Inset * t, TopPad * t, Inset * t, 0); // 여백은 변환 밖이라 직접 곱한다

        // 기준 단위에서 칸 크기는 항상 같으므로 아이콘도 같다(기본 72 → 40).
        double cell = size.Width - 2 * Inset;
        cell /= _layout.Cols;
        double pad = cell * 0.17;
        Cells.Margin = new Thickness(pad);
        double inner = (cell * _layout.Cols - 2 * pad) / _layout.Cols - 6;
        double icon = Math.Clamp(Math.Round(inner * 0.74), 16, 128);
        if (Math.Abs(icon - _iconSize) > 0.5 || Cells.Columns != _layout.Cols || Cells.Rows != _layout.Rows)
        {
            _iconSize = icon;
            Rebuild();
        }
    }

    /// <summary>크기를 다시 적용하고, 작업 영역 밖으로 나갔으면 안쪽으로 당긴다.</summary>
    public void FitToScreen()
    {
        LayoutFor();
        var hwnd = Handle;
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

    /// <summary>화면 안으로 다시 맞추고 위치를 저장한다(배율·해상도·작업 표시줄이 바뀌었을 때).</summary>
    public void Refit()
    {
        FitToScreen();
        _mgr.SavePosition(this);
    }

    /// <summary>
    /// 실제 창 위치(DIP). SetWindowPos나 끌기로 옮긴 직후에는 WPF의 Left/Top이 늦게 갱신될 수 있어
    /// 저장할 때는 이 값을 쓴다.
    /// </summary>
    public Point ActualPosition
    {
        get
        {
            var hwnd = Handle;
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return new Point(Left, Top);
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Point(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _hoverTimer?.Stop();
        Group.Changed -= Rebuild;
        base.OnClosed(e);
    }
}
