using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DeskCards;

/// <summary>
/// 편집 모드 동안 화면 위쪽 가운데에 뜨는 막대(Windows 캡처 도구 막대 모양).
/// 카드를 끌어 옮기고 모서리로 크기를 바꾸는 동안, 고른 카드의 이름·칸 수·삭제를 여기서 한다.
/// </summary>
internal sealed class EditBar : ToolWindow
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>칸 수 빠른 선택(가로, 세로).</summary>
    private static readonly (int Cols, int Rows)[] GridPresets =
        { (1, 1), (2, 1), (2, 2), (3, 2), (3, 3), (4, 2), (4, 3), (4, 4) };

    private static EditBar? _bar;

    private readonly GroupManager _mgr;
    private readonly TextBlock _name;
    private readonly ToggleButton _guides;
    private readonly Button[] _cardButtons, _folderButtons;
    private readonly Button _gridButton;
    private Flyout? _gridFlyout;

    private EditBar(GroupManager mgr, StackPanel items, Border root, TextBlock name, ToggleButton guides,
        Button[] cardButtons, Button[] folderButtons, Button gridButton)
        : base("Desk Cards 편집", root)
    {
        _mgr = mgr;
        _name = name;
        _guides = guides;
        _cardButtons = cardButtons;
        _folderButtons = folderButtons;
        _gridButton = gridButton;
        _ = items;
    }

    public static void Open(GroupManager mgr)
    {
        if (_bar == null)
        {
            _bar = Create(mgr);
            _bar.Refresh();
            _bar.PlaceTop();
            _bar.AppWindow.Show();
        }
        _bar.Activate();
    }

    private static EditBar Create(GroupManager mgr)
    {
        var items = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var root = new Border { Padding = new Thickness(6), Background = Theme.Brush("PopupBg"), Child = items };

        var guides = new ToggleButton
        {
            Content = Glyph(""),
            MinWidth = 40,
            Height = 36,
            IsChecked = mgr.ShowGuides,
        };
        ToolTipService.SetToolTip(guides, "안내선 · 자동 맞춤 (Alt를 누르고 있으면 잠시 꺼짐)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(guides, "안내선");
        guides.Click += (_, _) => mgr.ShowGuides = guides.IsChecked == true;

        var name = new TextBlock
        {
            FontSize = 13,
            MinWidth = 96,
            MaxWidth = 160,
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        EditBar? self = null;
        var gridButton = new Button { Content = GridContent(), MinWidth = 40, Height = 36, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(gridButton, "미리보기 칸 수");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gridButton, "미리보기 칸 수");
        gridButton.Click += (_, _) => self!.ShowGridFlyout(gridButton);

        var folderButtons = new[]
        {
            Btn("", "이름 바꾸기", () => { if (mgr.Selected is CardWindow c) ExpandedWindow.Open(c, mgr, editTitle: true); }),
            gridButton,
        };
        var cardButtons = new[] { Btn("", "삭제 (Delete, 폴더 카드의 항목은 바탕화면으로)", () => { if (mgr.Selected is { } card) mgr.DeleteCard(card); }) };

        items.Children.Add(Btn("", "새 그룹", () => mgr.NewGroup()));
        items.Children.Add(guides);
        items.Children.Add(Sep());
        items.Children.Add(name);
        foreach (var b in folderButtons) items.Children.Add(b);
        foreach (var b in cardButtons) items.Children.Add(b);
        items.Children.Add(Sep());
        items.Children.Add(Btn("", "설정", () => SettingsWindow.Open(mgr, mgr.Selected as CardWindow)));
        items.Children.Add(DoneButton(mgr));

        self = new EditBar(mgr, items, root, name, guides, cardButtons, folderButtons, gridButton);
        mgr.EditChanged += self.Refresh;
        mgr.Changed += self.Refresh;
        root.PreviewKeyDown += self.OnKey;
        self.Closed += (_, _) =>
        {
            mgr.EditChanged -= self.Refresh;
            mgr.Changed -= self.Refresh;
            // Alt+F4 등으로 막대만 닫혔으면 편집 모드도 끝낸다.
            if (_bar == self)
            {
                _bar = null;
                mgr.EndEditMode();
            }
        };
        return self;
    }

    /// <summary>카드들을 막 위로 올린 뒤 막대가 그 아래로 가려지지 않게 다시 맨 위로.</summary>
    public static void BringToTop()
    {
        if (_bar != null) Hwnd.SetZOrder(Hwnd.Of(_bar), Hwnd.Topmost);
    }

    public static void CloseBar()
    {
        var bar = _bar;
        _bar = null;
        bar?.Close();
    }

    private void PlaceTop()
    {
        var wa = Screens.Primary.WorkArea;
        var root = (FrameworkElement)Content;
        root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double s = Native.MonitorScaleAt(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
        int w = (int)Math.Round(root.DesiredSize.Width * s);
        Place(wa.Left + (wa.Width - w) / 2, wa.Top + (int)Math.Round(12 * s));
    }

    private void Refresh()
    {
        var sel = _mgr.Selected;
        _name.Text = sel?.CardName ?? "카드를 누르세요";
        _name.Foreground = Theme.Brush(sel != null ? "Fg" : "SubFg");
        ToolTipService.SetToolTip(_name, sel?.CardName);
        foreach (var b in _cardButtons) b.IsEnabled = sel != null;
        foreach (var b in _folderButtons) b.IsEnabled = sel is CardWindow;
        _guides.IsChecked = _mgr.ShowGuides;
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var card = _mgr.Selected;
        int step = (Native.GetAsyncKeyState(0x10) & 0x8000) != 0 ? 10 : 1;
        switch (e.Key)
        {
            case VirtualKey.Escape: _mgr.EndEditMode(); break;
            case VirtualKey.Delete: if (card != null) _mgr.DeleteCard(card); break;
            case VirtualKey.Left: card?.Nudge(-step, 0); break;
            case VirtualKey.Right: card?.Nudge(step, 0); break;
            case VirtualKey.Up: card?.Nudge(0, -step); break;
            case VirtualKey.Down: card?.Nudge(0, step); break;
            default: return;
        }
        e.Handled = true;
    }

    // ----- 칸 수 -----

    private void ShowGridFlyout(Button target)
    {
        if (_mgr.Selected is not CardWindow card) return;
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < (GridPresets.Length + 3) / 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < GridPresets.Length; i++)
        {
            var (cols, rows) = GridPresets[i];
            bool current = card.CurrentLayout.Cols == cols && card.CurrentLayout.Rows == rows;
            var item = new Button
            {
                Content = GridPreview(cols, rows, current),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 6),
            };
            ToolTipService.SetToolTip(item, $"가로 {cols}칸 × 세로 {rows}칸");
            item.Click += (_, _) =>
            {
                card.SetGrid(cols, rows);
                _gridFlyout?.Hide();
            };
            Grid.SetColumn(item, i % 4);
            Grid.SetRow(item, i / 4);
            grid.Children.Add(item);
        }
        var hint = new TextBlock { Text = "다른 칸 수는 설정에서 고를 수 있어요", FontSize = 12, Margin = new Thickness(4, 8, 4, 0), Foreground = Theme.Brush("SubFg") };
        var stack = new StackPanel { Children = { grid, hint } };

        _gridFlyout = new Flyout { Content = stack, Placement = FlyoutPlacementMode.Bottom, ShouldConstrainToRootBounds = false };
        _gridFlyout.ShowAt(target);
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
                var sq = new Border
                {
                    Width = cell,
                    Height = cell,
                    CornerRadius = new CornerRadius(Math.Min(2, cell / 4)),
                    Background = Theme.Brush(current ? "Accent" : "SubFg"),
                    Opacity = current ? 1 : 0.7,
                };
                Canvas.SetLeft(sq, ox + c * (cell + gap));
                Canvas.SetTop(sq, oy + r * (cell + gap));
                canvas.Children.Add(sq);
            }
        var label = new TextBlock
        {
            Text = $"{cols}×{rows}",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = Theme.Brush(current ? "Accent" : "Fg"),
        };
        return new StackPanel { Children = { canvas, label } };
    }

    // ----- 만들기 도우미 -----

    private static UIElement GridContent()
    {
        var chevron = Glyph("", 9);
        chevron.Margin = new Thickness(6, 2, 0, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { Glyph(""), chevron } };
    }

    private static Button Btn(string glyph, string tip, Action act)
    {
        var b = new Button
        {
            Content = Glyph(glyph),
            MinWidth = 40,
            Height = 36,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip); // 화면 읽기 프로그램용
        b.Click += (_, _) => act();
        return b;
    }

    private static Button DoneButton(GroupManager mgr)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Glyph("", 14),
                new TextBlock { Text = "완료", FontSize = 14, Margin = new Thickness(8, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var b = new Button { Content = content, Height = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"], Margin = new Thickness(4, 0, 2, 0) };
        ToolTipService.SetToolTip(b, "편집 끝내기 (Esc)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, "완료");
        b.Click += (_, _) => mgr.EndEditMode();
        return b;
    }

    private static TextBlock Glyph(string glyph, double size = 16) =>
        new() { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = size, VerticalAlignment = VerticalAlignment.Center };

    private static Border Sep() =>
        new() { Width = 1, Height = 22, Margin = new Thickness(6, 0, 6, 0), Background = Theme.Brush("MenuLine") };
}
