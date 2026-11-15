using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeskCards;

/// <summary>
/// 카드 바를 편집하는 동안 바 옆(바 길이의 가운데)에 세로로 뜨는 막대. 메뉴는 화면 편집 막대와 같다.
/// 바 안에서 고른 카드의 이름·칸 수를 바꾸고 바에서 뺀다.
/// </summary>
internal sealed class BarEditBar : ToolWindow
{
    private static BarEditBar? _bar;

    private readonly TextBlock _name;
    private readonly Button[] _cardButtons;
    private readonly Func<string?> _selected;
    private readonly Action _ended;
    private Flyout? _gridFlyout;
    private readonly GroupManager _mgr;
    private readonly bool _flyLeft;

    private BarEditBar(Border root, TextBlock name, Button[] cardButtons, GroupManager mgr, Func<string?> selected, Action ended, bool flyLeft)
        : base("Desk Cards 카드 바 편집", root)
    {
        _name = name;
        _cardButtons = cardButtons;
        _mgr = mgr;
        _selected = selected;
        _ended = ended;
        _flyLeft = flyLeft;
    }

    public interface IOwner
    {
        GroupManager Mgr { get; }
        string? Selected { get; }
        ScreenEdge Edge { get; }
        System.Drawing.Rectangle Final { get; }
        void ShowAddMenu();
        void Rename(string group);
        void RemoveFromBar(string group);
        void End();
    }

    public static void Open(IOwner o)
    {
        if (_bar != null) return;
        var mgr = o.Mgr;
        var items = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
        var root = new Border { Padding = new Thickness(6), Background = Theme.Brush("PopupBg"), Child = items };

        // 화면 편집 막대와 같은 단추(폭 40, 높이 36)·간격·강조색을 그대로 쓰고 세로로만 늘어놓는다. 이름은 바 안의 선택 테두리로 알 수 있어 글자는 두지 않는다.
        var name = new TextBlock(); // 안 보이는 자리표시(선택 이름은 단추 툴팁으로)
        BarEditBar? self = null;
        var gridButton = EditBar.Btn("\uE80A", "미리보기 칸 수", () => { });
        gridButton.Content = EditBar.GridContent();
        gridButton.Click += (_, _) => self!.ShowGridFlyout(gridButton);
        var rename = EditBar.Btn("\uE8AC", "이름 바꾸기", () => { if (o.Selected is { } g) o.Rename(g); });
        var remove = EditBar.Btn("\uE738", "카드 바에서 빼기 (Delete)", () => { if (o.Selected is { } g) o.RemoveFromBar(g); });
        var cardButtons = new[] { rename, gridButton, remove };

        items.Children.Add(EditBar.Btn("\uE710", "카드 넣기", o.ShowAddMenu));
        var guides = new ToggleButton { Content = EditBar.Glyph("\uE80A"), MinWidth = 40, Height = 36, IsChecked = mgr.ShowGuides };
        ToolTipService.SetToolTip(guides, "안내선 · 자동 맞춤 (Alt를 누르고 있으면 잠시 꺼짐)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(guides, "안내선");
        guides.Click += (_, _) => mgr.ShowGuides = guides.IsChecked == true;
        items.Children.Add(guides);
        items.Children.Add(EditBar.Sep2());
        foreach (var b in cardButtons) items.Children.Add(b);
        items.Children.Add(EditBar.Sep2());
        items.Children.Add(EditBar.Btn("\uE713", "설정", () => SettingsWindow.Open(mgr, null)));
        var done = new Button
        {
            Content = EditBar.Glyph("\uE73E", 14),
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Margin = new Thickness(0, 4, 0, 0),
        };
        ToolTipService.SetToolTip(done, "편집 끝내기 (Esc)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(done, "완료");
        done.Click += (_, _) => o.End();
        items.Children.Add(done);
        foreach (var child in items.Children)
            if (child is Control btn) { btn.HorizontalAlignment = HorizontalAlignment.Stretch; btn.HorizontalContentAlignment = HorizontalAlignment.Center; }

        self = new BarEditBar(root, name, cardButtons, mgr, () => o.Selected, o.End, o.Edge == ScreenEdge.Right);
        _bar = self;
        self.Closed += (_, _) =>
        {
            if (_bar != self) return;
            _bar = null;
            o.End(); // Alt+F4 등으로 막대만 닫혔으면 바 편집도 끝낸다.
        };
        self.Refresh();
        self.PlaceBeside(o.Edge, o.Final);
        self.AppWindow.Show();
    }

    public static void CloseBar()
    {
        var bar = _bar;
        _bar = null;
        bar?.Close();
    }

    public static void Update() => _bar?.Refresh();

    /// <summary>바 옆, 바 길이의 가운데에 둔다. 왼쪽·위 바는 오른쪽·아래에, 오른쪽·아래 바는 왼쪽·위에 붙는다.</summary>
    private void PlaceBeside(ScreenEdge edge, System.Drawing.Rectangle bar)
    {
        var wa = Screens.FromPoint(bar.Left + bar.Width / 2, bar.Top + bar.Height / 2).WorkArea;
        double s = Native.MonitorScaleAt(bar.Left + bar.Width / 2, bar.Top + bar.Height / 2);
        int gap = (int)Math.Round(8 * s);
        int cx = bar.Left + bar.Width / 2, cy = bar.Top + bar.Height / 2;
        switch (edge)
        {
            case ScreenEdge.Left: Place(bar.Right + gap, cy, anchorY: 0.5); break;
            case ScreenEdge.Right: Place(bar.Left - gap, cy, anchorX: 1, anchorY: 0.5); break;
            case ScreenEdge.Top: Place(cx, bar.Bottom + gap, centerX: true); break;
            default: Place(cx, bar.Top - gap, centerX: true, anchorY: 1); break;
        }
        _ = wa;
    }

    private void Refresh()
    {
        var sel = _selected();
        foreach (var b in _cardButtons) b.IsEnabled = sel != null;
    }

    private void ShowGridFlyout(Button target)
    {
        if (_selected() is not { } g || _mgr.GroupCard(g) is not { } card) return;
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < (EditBar.GridPresets.Length + 3) / 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < EditBar.GridPresets.Length; i++)
        {
            var (cols, rows) = EditBar.GridPresets[i];
            bool current = card.CurrentLayout.Cols == cols && card.CurrentLayout.Rows == rows;
            var item = new Button
            {
                Content = EditBar.GridPreview(cols, rows, current),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 6),
            };
            ToolTipService.SetToolTip(item, $"가로 {cols}칸 × 세로 {rows}칸");
            item.Click += (_, _) => { card.SetGrid(cols, rows); _gridFlyout?.Hide(); };
            Grid.SetColumn(item, i % 4);
            Grid.SetRow(item, i / 4);
            grid.Children.Add(item);
        }
        var hint = new TextBlock { Text = "다른 칸 수는 설정에서 고를 수 있어요", FontSize = 12, Margin = new Thickness(4, 8, 4, 0), Foreground = Theme.Brush("SubFg") };
        _gridFlyout = new Flyout
        {
            Content = new StackPanel { Children = { grid, hint } },
            Placement = _flyLeft ? FlyoutPlacementMode.Left : FlyoutPlacementMode.Right,
            ShouldConstrainToRootBounds = false,
        };
        _gridFlyout.ShowAt(target);
    }
}