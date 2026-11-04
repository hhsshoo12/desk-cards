using System;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DeskCards;

/// <summary>입력을 받을 때 커서 모양을 바꿀 수 있는 Grid.</summary>
internal sealed class CursorGrid : Grid
{
    public void SetCursor(InputSystemCursorShape? shape) =>
        ProtectedCursor = shape is { } s ? InputSystemCursor.Create(s) : null;
}

/// <summary>
/// 바탕화면에 붙어 있는 카드 창 하나. 폴더 카드(CardWindow)의 바탕이다.
/// 평소에는 고정이고, 편집 모드(편집 막대)에서만 옮기고 비율을 유지한 채 크기를 바꿀 수 있다.
/// 모양(기준 크기)과 안의 내용은 쓰는 쪽이 정한다.
/// 모든 카드는 Windows 11이 모서리·그림자·아크릴 배경을 그려 주는 보통 창이다.
/// 카드는 눌러도 활성화되지 않는다.
/// </summary>
internal abstract partial class DeskCard : Window
{
    // 배율 적용 전 기준 단위(DIP)의 배치 값
    public const double LabelH = CardView.LabelH;

    private static uint _taskbarCreatedMsg;

    protected readonly GroupManager Mgr;
    private readonly CursorGrid _grip;
    private readonly CursorGrid _shield;
    private readonly Grid _rootGrid;
    private bool _pending, _editing, _selected, _dropTarget, _invalid, _ownZ;
    private double _appliedScale = 1;
    private Point? _restorePosition;
    private bool _closed;
    private Native.POINT _moveCursorStart;
    private Native.RECT _moveWindowStart;
    private System.Collections.Generic.IReadOnlyList<Native.RECT> _moveOthers = Array.Empty<Native.RECT>();
    private CardLabel? _label;
    private bool _triedFlush;
    private Native.Subclass? _subclass;

    protected DeskCard(GroupManager mgr)
    {
        Mgr = mgr;
        Title = "Desk Cards Card";
        ExtendsContentIntoTitleBar = true;
        var aw = AppWindow;
        aw.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        if (aw.Presenter is OverlappedPresenter p)
        {
            // 제목 줄 없이 Windows가 그리는 1px 테두리·둥근 모서리·그림자만 남긴다.
            p.SetBorderAndTitleBar(true, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        aw.IsShownInSwitchers = false;

        // 크기·배율은 LayoutFor가 정한다. 편집 모드 크기 조절은 비율을 유지한 채 통째로 키우고 줄이므로 모서리 하나만 둔다.
        Layout = new Grid();
        _shield = new CursorGrid { Background = new SolidColorBrush(Colors.Transparent), Visibility = Visibility.Collapsed };
        _grip = new CursorGrid
        {
            Width = 22,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Colors.Transparent),
            Visibility = Visibility.Collapsed,
        };
        _grip.SetCursor(InputSystemCursorShape.SizeNorthwestSoutheast);
        _grip.Children.Add(MakeGripGlyph());
        _rootGrid = new Grid { Background = Theme.Brush("PopupBg") };
        _rootGrid.Children.Add(Layout);
        _rootGrid.Children.Add(_shield);
        _rootGrid.Children.Add(_grip);
        Content = _rootGrid;

        _rootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnDown), true);
        _rootGrid.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMove), true);
        _rootGrid.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnUp), true);
        _grip.PointerPressed += (_, e) => { _grip.CapturePointer(e.Pointer); OnGripStart(); e.Handled = true; };
        _grip.PointerMoved += (_, e) => { if (_gripActive) OnGripDelta(); };
        _grip.PointerReleased += (_, e) => { if (_gripActive) { _grip.ReleasePointerCapture(e.Pointer); OnGripDone(); } };
        _grip.PointerCaptureLost += (_, _) => { if (_gripActive) OnGripDone(); };

        aw.Changed += (_, e) => { if (e.DidVisibilityChange) SyncLabel(); };
    }

    /// <summary>파생 클래스의 생성자 끝에서 한 번 부른다(키·내용이 정해진 뒤에 창을 꾸미려고).</summary>
    protected void Init() => InitHandle();

    private bool _gripActive;

    private static UIElement MakeGripGlyph()
    {
        var geometry = new PathGeometry();
        void Segment(double x1, double y1, double x2, double y2)
        {
            var figure = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false };
            figure.Segments.Add(new LineSegment { Point = new Point(x2, y2) });
            geometry.Figures.Add(figure);
        }
        Segment(15, 5, 5, 15);
        Segment(15, 10, 10, 15);
        return new Path
        {
            Data = geometry,
            Stroke = Theme.Brush("Accent"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 2),
        };
    }

    /// <summary>카드 내용이 들어가는 칸. 크기·배율은 LayoutFor가 정한다.</summary>
    protected Grid Layout { get; }

    /// <summary>위치·모양을 저장하는 이름(config.json의 PositionsPx·Layouts 키).</summary>
    public abstract string Key { get; }

    /// <summary>편집 막대 등에 보여 주는 카드 이름.</summary>
    public abstract string CardName { get; }

    /// <summary>카드 안쪽 테두리. 편집 중·드롭 대상 강조를 여기에 칠한다.</summary>
    protected abstract Border? Frame { get; }

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

    /// <summary>창 크기(DIP). 실제 창은 물리 픽셀로 맞춘다.</summary>
    public double Width
    {
        get => AppWindow.Size.Width / DpiScale;
        set => AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(value * DpiScale), AppWindow.Size.Height));
    }

    public double Height
    {
        get => AppWindow.Size.Height / DpiScale;
        set => AppWindow.Resize(new Windows.Graphics.SizeInt32(AppWindow.Size.Width, (int)Math.Round(value * DpiScale)));
    }

    public bool IsVisible => Native.IsWindowVisible(Handle);

    /// <summary>활성화하지 않고 보인다.</summary>
    public void Show()
    {
        if (_closed) return;
        AppWindow.Show(false);
        Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>카드 창 이벤트(앱이 직접 닫는 경우 포함). 쓰는 쪽은 ClosingByManager로 구분한다.</summary>
    public event EventHandler? CardClosed;

    private void SyncLabel()
    {
        if (_closed || LabelText == null || Handle == IntPtr.Zero) return;
        if (!IsVisible) { _label?.Hide(); return; }
        _label ??= new CardLabel(Handle);
        _label.Text = LabelText;
        if (!_label.IsShown) _label.Show();
        FollowLabel();
    }

    private void FollowLabel()
    {
        if (_label == null || !Native.GetWindowRect(Handle, out var r)) return;
        _label.Place(r.Left, r.Bottom, r.Right - r.Left, LabelPx, _appliedScale, DpiScale);
    }

    /// <summary>이름이 바뀌었을 때 쓰는 쪽이 부른다.</summary>
    protected void RefreshLabel()
    {
        if (_label != null) _label.Text = LabelText ?? "";
    }

    /// <summary>편집 중에는 클릭·끌기를 카드 내용 대신 이 창이 받는다.</summary>
    protected bool Editing => _editing;

    protected IntPtr Handle => Hwnd.Of(this);
    public bool ClosingByManager { get; set; }
    public bool IsEditing => _editing;
    public CardLayout CurrentLayout => _layout;
    protected CardLayout _layout = new();

    /// <summary>드롭 대상이거나 편집 중이면 강조 테두리. 편집 중에 고른 카드는 더 굵게.</summary>
    protected void UpdateBorder(bool? dropTarget = null)
    {
        // 테마·선택 변경은 현재 드롭 상태를 보존하고, DragLeave·Drop에서만 명시적으로 끈다.
        if (dropTarget.HasValue) _dropTarget = dropTarget.Value;
        var card = Frame;
        if (card == null) return; // 파생 클래스가 아직 만드는 중
        bool strong = _dropTarget || _selected || _invalid;
        // Windows가 그리는 1px 창 테두리를 강조색으로 바꾸고, 고른 카드는 안쪽 테두리를 더한다.
        // 지금 설정에서 안 되는 자리(겹침·간격보다 가까움)는 빨간색으로.
        var hwnd = Handle;
        if (hwnd != IntPtr.Zero)
        {
            var c = _invalid ? InvalidColor : Theme.Color("Accent");
            Native.SetDwm(hwnd, Native.DWMWA_BORDER_COLOR,
                strong || _editing ? c.R | c.G << 8 | c.B << 16 : Native.DWMWA_COLOR_DEFAULT);
        }
        card.BorderBrush = _invalid ? InvalidBrush : Theme.Brush("Accent");
        card.BorderThickness = new Thickness(strong ? 2 : 0);
    }

    private static readonly Windows.UI.Color InvalidColor = ColorHelper.FromArgb(255, 0xE8, 0x11, 0x23);
    private static readonly Brush InvalidBrush = new SolidColorBrush(InvalidColor);

    private void OnThemeChanged() => Ui.Post(() =>
    {
        if (_closed || Handle == IntPtr.Zero) return;
        Hwnd.ApplyFluent(this, Hwnd.Backdrop.Acrylic);
        UpdateBorder();
    }, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

    /// <summary>앱이 카드를 닫는다. 쓰는 쪽이 ClosingByManager를 먼저 켜 둔다.</summary>
    protected virtual void OnClosed()
    {
        _closed = true;
        Theme.Changed -= OnThemeChanged;
        _label?.Dispose();
        _subclass?.Dispose();
        _subclass = null;
    }
}
