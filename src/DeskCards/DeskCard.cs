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
internal abstract partial class DeskCard : Window
{
    // 배율 적용 전 기준 단위(DIP)의 배치 값
    public const double LabelH = CardView.LabelH;

    private static uint _taskbarCreatedMsg;

    protected readonly GroupManager Mgr;
    private readonly Thumb _grip;
    private bool _pending, _editing, _selected, _dropTarget, _invalid, _ownZ;
    private double _appliedScale = 1;
    private Point? _restorePosition;
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
