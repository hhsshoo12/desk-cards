using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskCards;

/// <summary>
/// 바탕화면에 붙어 있는 카드 창 하나. 폴더 카드(CardWindow)와 .dard 카드(DardWindow)가 같이 쓴다.
/// 평소에는 고정이고, 편집 모드(편집 막대)에서만 옮기고 비율을 유지한 채 크기를 바꿀 수 있다.
/// 모양(기준 크기)과 안의 내용은 쓰는 쪽이 정한다.
/// 모든 카드는 Windows 11이 모서리·그림자·아크릴 배경을 그려 주는 보통 창이다.
/// 폴더 카드는 눌러도 활성화되지 않고, 웹 화면이 있는 .dard 카드만 초점·키보드를 받는다.
/// </summary>
internal abstract class DeskCard : Window
{
    // 배율 적용 전 기준 단위(DIP)의 배치 값
    public const double LabelH = CardView.LabelH;

    private static uint _taskbarCreatedMsg;

    protected readonly GroupManager Mgr;
    private readonly Thumb _grip;
    private bool _pending, _editing, _selected, _dropTarget, _invalid, _ownZ;
    private double _appliedScale = 1;
    private bool _closed;
    private Native.POINT _moveCursorStart;
    private Native.RECT _moveWindowStart;
    private System.Collections.Generic.IReadOnlyList<Native.RECT> _moveOthers = Array.Empty<Native.RECT>();
    private CardLabel? _label;
    private bool _triedFlush;

    protected DeskCard(GroupManager mgr)
    {
        Mgr = mgr;
        WindowStyle = WindowStyle.SingleBorderWindow;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 152;
        Height = 176;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
        UseLayoutRounding = true;
        Title = "Desk Cards Card";

        // 크기·배율은 LayoutFor가 정한다. 편집 모드 크기 조절은 비율을 유지한 채 통째로 키우고 줄이므로 모서리 하나만 둔다.
        Layout = new Grid();
        _grip = new Thumb
        {
            Template = GripTemplate,
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
            Visibility = Visibility.Collapsed,
        };
        var root = new Grid();
        root.SetResourceReference(Panel.BackgroundProperty, "PopupBg");
        root.Children.Add(Layout);
        root.Children.Add(_grip);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += (_, _) => SyncLabel();
        PreviewMouseLeftButtonDown += OnDown;
        PreviewMouseMove += OnMove;
        PreviewMouseLeftButtonUp += OnUp;

        _grip.DragStarted += (_, _) => OnGripStart();
        _grip.DragDelta += (_, _) => OnGripDelta();
        _grip.DragCompleted += (_, _) => OnGripDone();
    }

    /// <summary>편집 모드의 크기 조절 손잡이. 투명 배경도 WPF 입력을 받는다.</summary>
    private static readonly ControlTemplate GripTemplate = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate TargetType="Thumb"
                         xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Border Background="Transparent">
                <Path Data="M 15,5 L 5,15 M 15,10 L 10,15" Stroke="{DynamicResource Accent}"
                      StrokeThickness="1.6" StrokeStartLineCap="Round" StrokeEndLineCap="Round"
                      HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="0,0,2,2" />
            </Border>
        </ControlTemplate>
        """);

    /// <summary>눌렀을 때 활성화할지. 웹 화면의 초점·키보드가 필요한 카드만 켠다.</summary>
    protected virtual bool Activatable => false;

    /// <summary>카드 내용이 들어가는 칸. 크기·배율은 LayoutFor가 정한다.</summary>
    protected Grid Layout { get; }

    /// <summary>위치·모양을 저장하는 이름(config.json의 Positions·Layouts 키).</summary>
    public abstract string Key { get; }

    /// <summary>편집 막대 등에 보여 주는 카드 이름.</summary>
    public abstract string CardName { get; }

    /// <summary>카드 안쪽 테두리. 편집 중·드롭 대상 강조를 여기에 칠한다.</summary>
    protected abstract Border Frame { get; }

    /// <summary>확대 비율을 적용하기 전의 카드 창 크기(DIP).</summary>
    protected abstract Size BaseSize();

    /// <summary>카드 아래에 따로 띄우는 이름. null이면 이름 줄이 없다.</summary>
    protected virtual string? LabelText => null;

    /// <summary>카드 아래 이름 줄 높이(기준 DIP). 이름이 없는 카드는 0.</summary>
    private double LabelBelow => LabelText != null ? LabelH : 0;

    /// <summary>지금 크기의 이름 줄 높이(물리 픽셀).</summary>
    private int LabelPx => (int)Math.Round(LabelBelow * _appliedScale * DpiScale);

    /// <summary>
    /// 카드와 그 아래 이름 줄을 합친 자리(물리 픽셀). 안내선·화면 맞춤·빈 자리 찾기는 이 자리로 한다.
    /// 그래야 붙여 놓은 카드가 위 카드의 이름을 덮지 않는다.
    /// </summary>
    public Native.RECT Footprint
    {
        get
        {
            if (!Native.GetWindowRect(Handle, out var r)) return default;
            r.Bottom += LabelPx;
            return r;
        }
    }

    /// <summary>이름 줄까지 합친 높이(DIP).</summary>
    public double FootprintHeight => Height + LabelBelow * _appliedScale;

    /// <summary>이름이 바뀌었을 때 쓰는 쪽이 부른다.</summary>
    protected void RefreshLabel()
    {
        if (_label != null) _label.Text = LabelText ?? "";
    }

    /// <summary>카드가 보이면 이름 창을 카드 바로 아래에 맞춰 보이고, 숨으면 같이 숨긴다.</summary>
    private void SyncLabel()
    {
        if (_closed || LabelText == null || Handle == IntPtr.Zero) return;
        if (!IsVisible) { _label?.Hide(); return; }
        _label ??= new CardLabel(Handle);
        _label.Text = LabelText;
        if (!_label.IsVisible) _label.Show();
        FollowLabel();
    }

    private void FollowLabel()
    {
        if (_label == null || !Native.GetWindowRect(Handle, out var r)) return;
        _label.Place(r.Left, r.Bottom, r.Right - r.Left, LabelPx, _appliedScale);
    }

    /// <summary>편집 중에는 클릭·끌기를 카드 내용 대신 이 창이 받는다.</summary>
    protected bool Editing => _editing;

    protected IntPtr Handle => Hwnd.Of(this);
    public bool ClosingByManager { get; set; }
    public bool IsEditing => _editing;
    public CardLayout CurrentLayout => _layout;
    protected CardLayout _layout = new();

    // ----- 입력 -----
    // 평소 클릭·끌기·우클릭은 카드 내용이 한다. 카드 자체는 움직이지 않는다.
    // 편집 모드: 누르기 = 고르기, 아무 데나 끌기 = 자유 이동(안내선), 오른쪽 아래 모서리 끌기 = 크기 조절.

    private Point _downPos;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editing) return;
        Mgr.Select(this);
        _pending = !IsWithin<Thumb>(e.OriginalSource as DependencyObject);
        _downPos = e.GetPosition(this);
        if (_pending) e.Handled = true; // 편집 중에는 카드 내용(아이콘·HTML)이 누르기를 받지 않는다.
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pending || !_editing || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _downPos;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pending = false;
        try { DragMove(); } catch (InvalidOperationException) { }
        Mgr.SavePosition(this);
    }

    private void OnUp(object sender, MouseButtonEventArgs e) => _pending = false;

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
        _grip.Visibility = Visibility.Visible;
        Cursor = Cursors.SizeAll;
        UpdateBorder();
        OnEditChanged(true);
        RaiseForEdit();
    }

    /// <summary>편집 모드가 켜지고 꺼질 때. 누르기를 창이 직접 받지 못하는 내용(웹 화면)은 여기서 멈춰 둔다.</summary>
    protected virtual void OnEditChanged(bool editing) { }

    /// <summary>편집 중에는 어두운 막(EditDim) 위로 올라온다. 편집이 끝나면 다시 바탕화면 층으로 내려간다.</summary>
    public void RaiseForEdit()
    {
        if (_editing) Hwnd.SetZOrder(Handle, Hwnd.Topmost);
    }

    public void EndEdit()
    {
        if (!_editing) return;
        _editing = _selected = _invalid = false;
        _grip.Visibility = Visibility.Collapsed;
        Cursor = null;
        UpdateBorder();
        OnEditChanged(false);
        // HWND_BOTTOM은 맨 위(topmost) 상태도 함께 푼다.
        SendToBottom();
        Mgr.SavePosition(this);
    }

    /// <summary>편집 막대의 작업 대상으로 골랐는지. 고른 카드는 테두리가 굵어진다.</summary>
    public void SetSelected(bool on)
    {
        _selected = on && _editing;
        UpdateBorder();
    }

    /// <summary>화살표 키로 조금씩 옮긴다(물리 픽셀). 작업 영역 밖으로는 나가지 않는다.</summary>
    public void Nudge(int dx, int dy)
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top + LabelPx;
        var wa = DesktopGrid.WorkAreaAt(r.Left + w / 2, r.Top + h / 2);
        int x = Math.Max(wa.Left, Math.Min(r.Left + dx, wa.Right - w));
        int y = Math.Max(wa.Top, Math.Min(r.Top + dy, wa.Bottom - h));
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Mgr.SavePosition(this);
        Mgr.RefreshPlacement();
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
        _moveWindowStart = Footprint;
        _moveOthers = Mgr.CardRects(except: this);
        _triedFlush = false;
        BalloonTip.CloseCurrent(); // 앞 안내는 이전 자리 이야기라 새로 옮기기 시작하면 치운다
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
        SetInvalid(Placement() != SmartGuides.Placement.Ok);
    }

    private void OnGripDone()
    {
        SmartGuides.Hide();
        // 겹치거나 간격보다 가까워지는 크기는 저장하지 않고 잡기 전 크기로 되돌린다.
        var p = Placement();
        if (p != SmartGuides.Placement.Ok)
        {
            _layout.Zoom = _gripZoom;
            LayoutFor();
        }
        CommitLayout();
        Mgr.RefreshPlacement();
        if (p != SmartGuides.Placement.Ok) Warn(p);
    }

    /// <summary>지금 자리를 지금 설정으로 검사한다.</summary>
    private SmartGuides.Placement Placement()
    {
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero) return SmartGuides.Placement.Ok;
        return SmartGuides.Check(Footprint, Mgr.CardRects(except: this), SmartGuides.GapPx(Native.MonitorScaleOf(hwnd)));
    }

    /// <summary>편집 중이면 지금 자리가 안 되는 자리인지 검사해 빨간 테두리로 표시한다.</summary>
    public void CheckPlacement() => SetInvalid(_editing && Placement() != SmartGuides.Placement.Ok);

    private void SetInvalid(bool on)
    {
        on &= _editing;
        if (_invalid == on) return;
        _invalid = on;
        UpdateBorder();
    }

    /// <summary>안 되는 자리에 놓았을 때 이유와 실험 설정을 알려 준다.</summary>
    /// <remarks>편집 모드마다 종류별로 처음 한 번만 말풍선을 띄운다. 그 뒤로는 빨간 테두리와 되돌리기로 충분하다.</remarks>
    private void Warn(SmartGuides.Placement p) => Dispatcher.BeginInvoke(() =>
    {
        bool overlap = p == SmartGuides.Placement.Overlap;
        string id = overlap ? "placement.overlap" : "placement.flush";
        if (!_editing || Mgr.TipHidden(id)) return;
        BalloonTip.Show(Footprint, overlap
            ? new BalloonTip.Tip("카드는 겹칠 수 없어요",
                "빈자리에 놓아 주세요. 겹쳐 두려면 실험 설정에서 '겹치기 · 레이어'를 켜 주세요.",
                "실험 설정 열기", () => SettingsWindow.OpenGeneral(Mgr), () => Mgr.HideTip(id))
            : new BalloonTip.Tip("카드를 조금 띄워 뒀어요",
                "카드끼리는 딱 붙일 수 없어요. 붙여 두려면 실험 설정에서 '완전히 붙이기'를 켜 주세요.",
                "실험 설정 열기", () => SettingsWindow.OpenGeneral(Mgr), () => Mgr.HideTip(id)));
    }, DispatcherPriority.Background);

    /// <summary>바뀐 모양을 적용·저장하고, 화면 밖으로 나갔으면 당겨서 위치도 저장한다.</summary>
    protected void CommitLayout()
    {
        FitToScreen();
        Mgr.SaveLayout(this, _layout);
        Mgr.SavePosition(this);
    }

    /// <summary>확대 비율을 바꾸되, 최소·최대와 작업 영역(오른쪽·아래 끝) 안으로 제한한다.</summary>
    private void SetZoomClamped(double zoom)
    {
        var wa = WorkAreaDip();
        var p = ActualPosition;
        var size = BaseSize();
        double k = ScaleFactor;
        double maxZoom = Math.Min((wa.Right - p.X) / (size.Width * k), (wa.Bottom - p.Y) / ((size.Height + LabelBelow) * k));
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

    private Rect WorkAreaDip()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var p = ActualPosition;
        var wa = DesktopGrid.WorkAreaAt((int)(p.X * dpi.DpiScaleX) + 1, (int)(p.Y * dpi.DpiScaleY) + 1);
        return new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY, wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
    }

    /// <summary>드롭 대상이거나 편집 중이면 강조 테두리. 편집 중에 고른 카드는 더 굵게.</summary>
    protected void UpdateBorder(bool? dropTarget = null)
    {
        // 테마·선택 변경은 현재 드롭 상태를 보존하고, DragLeave·Drop에서만 명시적으로 끈다.
        if (dropTarget.HasValue) _dropTarget = dropTarget.Value;
        var card = Frame;
        bool strong = _dropTarget || _selected || _invalid;
        var accent = TryFindResource("Accent") as SolidColorBrush;
        // Windows가 그리는 1px 창 테두리를 강조색으로 바꾸고, 고른 카드는 안쪽 테두리를 더한다.
        // 지금 설정에서 안 되는 자리(겹침·간격보다 가까움)는 빨간색으로.
        var hwnd = Handle;
        if (hwnd != IntPtr.Zero)
        {
            var c = _invalid ? InvalidColor : accent?.Color ?? Colors.DodgerBlue;
            Native.SetDwm(hwnd, Native.DWMWA_BORDER_COLOR,
                strong || _editing ? c.R | c.G << 8 | c.B << 16 : Native.DWMWA_COLOR_DEFAULT);
        }
        if (_invalid) card.BorderBrush = InvalidBrush;
        else card.SetResourceReference(Border.BorderBrushProperty, "Accent");
        card.BorderThickness = new Thickness(strong ? 2 : 0);
    }

    private static readonly Color InvalidColor = Color.FromRgb(0xE8, 0x11, 0x23);
    private static readonly Brush InvalidBrush = MakeInvalidBrush();

    private static Brush MakeInvalidBrush()
    {
        var b = new SolidColorBrush(InvalidColor);
        b.Freeze();
        return b;
    }

    // ----- 바탕화면 층에 붙이기 -----

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Handle;
        Hwnd.RemoveSysMenu(hwnd);
        if (Activatable) Hwnd.MakeTool(hwnd);
        else Hwnd.MakeNoActivateTool(hwnd);
        Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        Theme.Changed += OnThemeChanged;
        UpdateBorder();
        AttachToDesktop(hwnd);
        ApplySize();

        if (_taskbarCreatedMsg == 0) _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
    }

    private void AttachToDesktop(IntPtr hwnd)
    {
        // Progman을 소유자로 두면 Win+D(바탕화면 보기) 때도 숨겨지지 않는다.
        var progman = Native.FindWindow("Progman", null);
        if (progman != IntPtr.Zero) Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, progman);
        SendToBottom();
    }

    /// <summary>바탕화면 바로 위(맨 아래)로 보낸다. 카드 앞뒤 순서는 앱이 이렇게 직접 정할 때만 바뀐다.</summary>
    private void SendToBottom()
    {
        _ownZ = true;
        try { Hwnd.SetZOrder(Handle, Native.HWND_BOTTOM); }
        finally { _ownZ = false; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_NCACTIVATE)
        {
            // Windows 11 아크릴 배경은 창이 비활성이면 단색으로 바뀐다. 바탕화면 카드는 거의 늘 비활성이라,
            // 창 틀을 늘 활성 상태로 그리게 해서 아크릴을 유지한다(실제 활성·초점은 그대로).
            handled = true;
            return Native.DefWindowProc(hwnd, msg, new IntPtr(1), lParam);
        }
        if (msg == Native.WM_WINDOWPOSCHANGED)
        {
            FollowLabel();
        }
        else if (msg == Native.WM_WINDOWPOSCHANGING)
        {
            // 항상 다른 창 뒤(바탕화면 바로 위)에 머문다. 편집 중에만 어두운 막 위로 올라온다.
            // 누르기(활성화)로 순서가 바뀌면 카드끼리 앞뒤가 뒤바뀌며 그림자가 이쪽저쪽 드리우므로,
            // 앱이 직접 맨 아래로 보낼 때 말고는 순서를 바꾸지 않는다.
            var wp = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            if ((wp.flags & Native.SWP_NOZORDER) == 0 && !_editing)
            {
                if (_ownZ) wp.hwndInsertAfter = Native.HWND_BOTTOM;
                else wp.flags |= Native.SWP_NOZORDER;
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
            int w = r.Right - r.Left, h = r.Bottom - r.Top, lh = LabelPx;
            int fx = _moveWindowStart.Left + (cur.X - _moveCursorStart.X);
            int fy = _moveWindowStart.Top + (cur.Y - _moveCursorStart.Y);
            var wa = DesktopGrid.WorkAreaAt(fx + w / 2, fy + h / 2);
            double scale = Native.MonitorScaleOf(hwnd);
            var (x, y, lines) = SmartGuides.Snap(fx, fy, w, h + lh, _moveOthers, wa, scale);
            // 간격을 두는 중인데 옆 카드에 딱 붙이려고(간격 절반보다 가까이, 살짝 겹치는 데까지) 끌었는지.
            int gap = SmartGuides.GapPx(scale);
            if (gap > 0)
            {
                var raw = new Native.RECT { Left = fx, Top = fy, Right = fx + w, Bottom = fy + h + lh };
                int slack = (int)Math.Round(8 * scale);
                var (sx, sy) = SmartGuides.Separation(raw, _moveOthers);
                bool Near(int? d) => d is { } v && v > -slack && v < gap / 2;
                _triedFlush |= Near(sx) || Near(sy);
            }
            // 지금 설정에서 안 되는 자리면 끄는 동안 빨간 테두리.
            var placed = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h + lh };
            SetInvalid(SmartGuides.Check(placed, _moveOthers, gap) != SmartGuides.Placement.Ok);
            SmartGuides.Show(wa, lines);
            Marshal.StructureToPtr(new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
        else if (msg == Native.WM_EXITSIZEMOVE)
        {
            SmartGuides.Hide();
            Settle(hwnd);
        }
        else if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AttachToDesktop(hwnd);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 옮기기를 마쳤을 때 지금 설정으로 자리를 검사한다.
    /// 겹쳤으면(겹치기 허용이 꺼져 있을 때) 저장하지 않고 끌기 전 자리로 돌려놓는다.
    /// 나란한 카드와 간격보다 가까우면(완전히 붙이기가 꺼져 있을 때) 간격만큼 떼어 놓는다.
    /// 어느 쪽이든, 또는 딱 붙이려 했던 것이면 이유와 실험 설정을 알려 준다.
    /// </summary>
    private void Settle(IntPtr hwnd)
    {
        var others = Mgr.CardRects(except: this);
        var me = Footprint;
        int gap = SmartGuides.GapPx(Native.MonitorScaleOf(hwnd));
        var p = SmartGuides.Check(me, others, gap);
        int x = me.Left, y = me.Top;
        if (p == SmartGuides.Placement.Overlap)
        {
            x = _moveWindowStart.Left;
            y = _moveWindowStart.Top;
        }
        else if (p == SmartGuides.Placement.TooClose)
        {
            var (dx, dy) = SmartGuides.PushApart(me, others, gap);
            int w = me.Right - me.Left, h = me.Bottom - me.Top;
            var wa = DesktopGrid.WorkAreaAt(me.Left + w / 2, me.Top + h / 2);
            x = Math.Max(wa.Left, Math.Min(me.Left + dx, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(me.Top + dy, wa.Bottom - h));
        }
        if (x != me.Left || y != me.Top)
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        if (p == SmartGuides.Placement.Ok && _triedFlush) p = SmartGuides.Placement.TooClose;
        _triedFlush = false;
        Dispatcher.BeginInvoke(() => Mgr.RefreshPlacement(), DispatcherPriority.Background);
        if (p != SmartGuides.Placement.Ok) Warn(p);
    }

    // ----- 크기 / 위치 -----

    /// <summary>이 카드가 있는 모니터의 지금 배율(125% = 1.25).</summary>
    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>확대 비율에 곱할 값. 따라가기를 끈 동안 Windows 배율이 바뀌어도 실제 크기를 유지한다.</summary>
    private double ScaleFactor => Mgr.ZoomFactor(DpiScale);

    /// <summary>따라가기를 켜고 끌 때 보이는 크기가 그대로 남도록 확대 비율을 옮겨 담는다.</summary>
    public void RebaseZoom(double factor)
    {
        _layout.Zoom *= factor;
        _layout = _layout.Normalized();
        Mgr.SaveLayout(this, _layout);
    }

    /// <summary>
    /// 모니터 배율이 바뀌었는데 이 창이 그 알림을 못 받아 옛 배율로 그려지고 있는지.
    /// 바탕화면 층에서 배율 알림이 누락된 경우에도 실제 모니터 배율로 확인한다.
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
        _layout = Mgr.GetLayout(Key);
        LayoutFor();
    }

    protected void LayoutFor()
    {
        var size = BaseSize();
        // 비율 고정 확대: 카드 확대 비율(배율 보정 포함)을 내용 전체(아이콘·글자·모서리)에 똑같이 건다.
        double t = ScaleFactor * _layout.Zoom;
        var wa = System.Windows.Forms.Screen.FromHandle(Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        t = Math.Min(t, Math.Min(wa.Width / (size.Width * dpi.DpiScaleX), wa.Height / ((size.Height + LabelBelow) * dpi.DpiScaleY)));
        _appliedScale = t;
        Width = size.Width * t;
        Height = size.Height * t;
        ApplyScale(t);
        FollowLabel();
    }

    /// <summary>
    /// 확대 비율 t를 내용에 건다. 기본은 내용 전체를 그림처럼 키운다.
    /// 흐려지면 안 되는 내용(웹 화면)은 쓰는 쪽이 크기를 직접 정한다.
    /// </summary>
    protected virtual void ApplyScale(double t) =>
        Layout.LayoutTransform = t == 1 ? Transform.Identity : new ScaleTransform(t, t);

    /// <summary>크기를 다시 적용하고, 작업 영역 밖으로 나갔으면 안쪽으로 당긴다.</summary>
    public void FitToScreen()
    {
        LayoutFor();
        var hwnd = Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Round(Width * dpi.DpiScaleX), h = (int)Math.Round(FootprintHeight * dpi.DpiScaleY);
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
        Mgr.SavePosition(this);
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

    private void OnThemeChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_closed || Handle == IntPtr.Zero) return;
        Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        UpdateBorder();
    });

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        Theme.Changed -= OnThemeChanged;
        _label?.Close();
        base.OnClosed(e);
    }
}
