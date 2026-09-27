using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DeskCards;

/// <summary>
/// 편집 모드 동안 화면 위쪽 가운데에 뜨는 막대(Windows 캡처 도구 막대 모양).
/// 카드를 끌어 옮기고 모서리로 크기를 바꾸는 동안, 고른 카드의 이름·칸 수·폴더·삭제를 여기서 한다.
/// </summary>
internal partial class EditBar : Window
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>칸 수 빠른 선택(가로, 세로).</summary>
    private static readonly (int Cols, int Rows)[] GridPresets =
        { (1, 1), (2, 1), (2, 2), (3, 2), (3, 3), (4, 2), (4, 3), (4, 4) };

    private static EditBar? _bar;

    private readonly GroupManager _mgr;
    private readonly TextBlock _name;
    private readonly ToggleButton _guides;
    private readonly Button[] _cardButtons;
    private readonly Button _gridButton;
    private Popup? _gridPopup;

    private EditBar(GroupManager mgr)
    {
        InitializeComponent();
        _mgr = mgr;

        Items.Children.Add(Btn("", "새 그룹", () => _mgr.NewGroup()));
        _guides = new ToggleButton
        {
            Style = (Style)FindResource("BarToggle"),
            Content = Glyph(""),
            ToolTip = "안내선 · 자동 맞춤 (Alt를 누르고 있으면 잠시 꺼짐)",
            IsChecked = _mgr.ShowGuides,
        };
        AutomationProperties.SetName(_guides, "안내선");
        _guides.Click += (_, _) => _mgr.ShowGuides = _guides.IsChecked == true;
        Items.Children.Add(_guides);
        Items.Children.Add(Sep());

        _name = new TextBlock
        {
            FontSize = 13,
            MinWidth = 96,
            MaxWidth = 160,
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Items.Children.Add(_name);

        _gridButton = GridButton();
        _cardButtons = new[]
        {
            Btn("", "이름 바꾸기", () => { if (_mgr.Selected is { } c) ExpandedWindow.Open(c, _mgr, editTitle: true); }),
            _gridButton,
            Btn("", "삭제 (Delete, 항목은 바탕화면으로)", DeleteSelected),
        };
        foreach (var b in _cardButtons) Items.Children.Add(b);
        Items.Children.Add(Sep());
        Items.Children.Add(Btn("", "설정", () => SettingsWindow.Open(_mgr, _mgr.Selected)));
        Items.Children.Add(DoneButton());

        _mgr.EditChanged += Refresh;
        _mgr.Changed += Refresh;
        Refresh();

        SourceInitialized += OnSourceInitialized;
        BarRoot.SizeChanged += (_, _) =>
        {
            Width = BarRoot.ActualWidth;
            Height = BarRoot.ActualHeight;
            PlaceTop();
        };
        PreviewKeyDown += OnKey;
    }

    public static void Open(GroupManager mgr)
    {
        if (_bar == null)
        {
            _bar = new EditBar(mgr);
            _bar.Show();
        }
        _bar.Activate();
    }

    /// <summary>카드들을 막 위로 올린 뒤 막대가 그 아래로 가려지지 않게 다시 맨 위로.</summary>
    public static void BringToTop()
    {
        if (_bar == null) return;
        var hwnd = new WindowInteropHelper(_bar).Handle;
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public static void CloseBar()
    {
        var bar = _bar;
        _bar = null;
        bar?.Close();
    }

    private void Refresh()
    {
        var sel = _mgr.Selected;
        _name.Text = sel?.Group.Name ?? "카드를 누르세요";
        _name.SetResourceReference(TextBlock.ForegroundProperty, sel != null ? "Fg" : "SubFg");
        _name.ToolTip = sel?.Group.Name;
        foreach (var b in _cardButtons) b.IsEnabled = sel != null;
        _guides.IsChecked = _mgr.ShowGuides;
    }

    private void DeleteSelected()
    {
        if (_mgr.Selected is not { } card) return;
        _mgr.DeleteGroup(card); // 확인 창은 Dialogs가 이 막대를 소유자로 삼아 막대 위에 띄운다.
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var card = _mgr.Selected;
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Escape:
                _mgr.EndEditMode();
                break;
            case Key.Delete:
                DeleteSelected();
                break;
            case Key.Left: card?.Nudge(-step, 0); break;
            case Key.Right: card?.Nudge(step, 0); break;
            case Key.Up: card?.Nudge(0, -step); break;
            case Key.Down: card?.Nudge(0, step); break;
            default: return;
        }
        e.Handled = true;
    }

    // ----- 칸 수 -----

    private Button GridButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Glyph(""));
        var chevron = Glyph("", 9);
        chevron.Margin = new Thickness(6, 2, 0, 0);
        content.Children.Add(chevron);
        var b = new Button { Style = (Style)FindResource("BarButton"), Content = content, ToolTip = "미리보기 칸 수" };
        AutomationProperties.SetName(b, "미리보기 칸 수");
        b.Click += (_, _) => ShowGridPopup(b);
        return b;
    }

    private void ShowGridPopup(Button target)
    {
        if (_mgr.Selected is not { } card) return;
        var grid = new UniformGrid { Columns = 4 };
        foreach (var (cols, rows) in GridPresets)
        {
            bool current = card.CurrentLayout.Cols == cols && card.CurrentLayout.Rows == rows;
            var item = new Button
            {
                Style = (Style)FindResource("FlyoutItem"),
                Content = GridPreview(cols, rows, current),
                ToolTip = $"가로 {cols}칸 × 세로 {rows}칸",
                Margin = new Thickness(2),
            };
            item.Click += (_, _) =>
            {
                card.SetGrid(cols, rows);
                if (_gridPopup != null) _gridPopup.IsOpen = false;
            };
            grid.Children.Add(item);
        }

        var hint = new TextBlock
        {
            Text = "다른 칸 수는 설정에서 고를 수 있어요",
            FontSize = 12,
            Margin = new Thickness(8, 6, 8, 4),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(hint);

        var box = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Margin = new Thickness(10), // 그림자 자리
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.25 },
        };
        box.SetResourceReference(Border.BackgroundProperty, "FlyoutBg");
        box.SetResourceReference(Border.BorderBrushProperty, "CardBorder");

        _gridPopup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -10,
            VerticalOffset = -4,
            AllowsTransparency = true,
            StaysOpen = false,
            PopupAnimation = PopupAnimation.Fade,
            Child = box,
        };
        _gridPopup.IsOpen = true;
    }

    /// <summary>칸 모양을 작은 네모들로 그린 미리보기 + "3×2" 글자.</summary>
    private static UIElement GridPreview(int cols, int rows, bool current)
    {
        const double box = 26, gap = 2;
        double cell = (box - gap * (Math.Max(cols, rows) - 1)) / Math.Max(cols, rows);
        var canvas = new Canvas { Width = box, Height = box };
        double ox = (box - (cols * cell + (cols - 1) * gap)) / 2, oy = (box - (rows * cell + (rows - 1) * gap)) / 2;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var sq = new Border { Width = cell, Height = cell, CornerRadius = new CornerRadius(Math.Min(2, cell / 4)) };
                sq.SetResourceReference(Border.BackgroundProperty, current ? "Accent" : "SubFg");
                if (!current) sq.Opacity = 0.7;
                Canvas.SetLeft(sq, ox + c * (cell + gap));
                Canvas.SetTop(sq, oy + r * (cell + gap));
                canvas.Children.Add(sq);
            }

        var label = new TextBlock { Text = $"{cols}×{rows}", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, current ? "Accent" : "Fg");
        var stack = new StackPanel();
        stack.Children.Add(canvas);
        stack.Children.Add(label);
        return stack;
    }

    // ----- 만들기 도우미 -----

    private Button Btn(string glyph, string tip, Action act)
    {
        var b = new Button { Style = (Style)FindResource("BarButton"), Content = Glyph(glyph), ToolTip = tip };
        AutomationProperties.SetName(b, tip); // 화면 읽기 프로그램용
        b.Click += (_, _) => act();
        return b;
    }

    private Button DoneButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Glyph("", 14)); // 체크
        content.Children.Add(new TextBlock { Text = "완료", FontSize = 14, Margin = new Thickness(8, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = (Style)FindResource("BarAccentButton"), Content = content, ToolTip = "편집 끝내기 (Esc)" };
        AutomationProperties.SetName(b, "완료");
        b.Click += (_, _) => _mgr.EndEditMode();
        return b;
    }

    private static TextBlock Glyph(string glyph, double size = 16) =>
        new() { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    private static Border Sep()
    {
        var b = new Border { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0) };
        b.SetResourceReference(Border.BackgroundProperty, "ControlBorder");
        return b;
    }

    // ----- 창 -----

    private void PlaceTop()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + 12;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, new IntPtr(style & ~Native.WS_SYSMENU));
        var src = HwndSource.FromHwnd(hwnd);
        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsLight ? 0 : 1);
        Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2); // 둥근 모서리
        Native.SetDwm(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 3);      // 아크릴
        PlaceTop();
    }

    protected override void OnClosed(EventArgs e)
    {
        _mgr.EditChanged -= Refresh;
        _mgr.Changed -= Refresh;
        if (_gridPopup != null) _gridPopup.IsOpen = false;
        base.OnClosed(e);
        // Alt+F4 등으로 막대만 닫혔으면 편집 모드도 끝낸다.
        if (_bar == this)
        {
            _bar = null;
            _mgr.EndEditMode();
        }
    }
}
