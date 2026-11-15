using System.Linq;
using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeskCards;

/// <summary>
/// 카드 바를 편집하는 동안 바 옆 알약의 톱니에서 가로로 펼쳐지는 편집 막대. 메뉴는 화면 편집 막대와 같다.
/// 바 안에서 고른 카드의 이름·칸 수를 바꾸고 바에서 뺀다. 톱니를 누르면 접고 다시 누르면 펼친다.
/// </summary>
internal sealed class BarEditBar : ToolWindow
{
    private static BarEditBar? _bar;

    private readonly Button[] _cardButtons;
    private readonly Func<string?> _selected;
    private readonly GroupManager _mgr;
    private readonly int _dir;           // +1: 알약 아래로 펼침, -1: 위로 펼침
    private readonly bool _flyLeft;
    private readonly Windows.Foundation.Size _full; // 다 펼쳤을 때 크기(DIP)
    private readonly System.Drawing.Rectangle _gear; // 알약의 톱니 칸(물리 픽셀)
    private readonly Button _renameButton;
    private Flyout? _gridFlyout;
    private bool _expanded = true, _closed;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _anim;
    private System.Diagnostics.Stopwatch? _clock;
    private double _from, _to, _k;       // 펼친 정도(0~1)

    private BarEditBar(FrameworkElement outer, Button[] cardButtons, GroupManager mgr, Func<string?> selected,
        int dir, Windows.Foundation.Size full, System.Drawing.Rectangle gear, Button renameButton, bool flyLeft)
        : base("Desk Cards 카드 바 편집", outer)
    {
        _cardButtons = cardButtons;
        _mgr = mgr;
        _selected = selected;
        _dir = dir;
        _full = full;
        _gear = gear;
        _renameButton = renameButton;
        _flyLeft = flyLeft;
    }

    public interface IOwner
    {
        GroupManager Mgr { get; }
        string? Selected { get; }
        ScreenEdge Edge { get; }
        /// <summary>편집 중 알약(톱니 한 칸)의 자리(물리 픽셀).</summary>
        System.Drawing.Rectangle GearRect { get; }
        void ShowAddMenu();
        void RemoveFromBar(string group);
        void End();
    }

    public static void Open(IOwner o)
    {
        if (_bar != null) return;
        var mgr = o.Mgr;
        var items = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
        var root = new Border { Padding = new Thickness(6), Background = Theme.Brush("PopupBg"), Child = items };

        var guides = new ToggleButton { Content = EditBar.Glyph("\uE80A"), MinWidth = 40, Height = 36, IsChecked = mgr.ShowGuides };
        ToolTipService.SetToolTip(guides, "안내선 · 자동 맞춤 (Alt를 누르고 있으면 잠시 꺼짐)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(guides, "안내선");
        guides.Click += (_, _) => mgr.ShowGuides = guides.IsChecked == true;

        BarEditBar? self = null;
        var gridButton = EditBar.Btn("\uE80A", "미리보기 칸 수", () => { });
        gridButton.Content = EditBar.GridContent();
        gridButton.Click += (_, _) => self!.ShowGridFlyout(gridButton);
        var rename = EditBar.Btn("\uE8AC", "이름 바꾸기", () => { if (o.Selected is { } g) RenameFor(g); });
        var remove = EditBar.Btn("\uE738", "카드 바에서 빼기 (Delete)", () => { if (o.Selected is { } g) o.RemoveFromBar(g); });
        var cardButtons = new[] { rename, gridButton, remove };

        var done = new Button { Content = EditBar.Glyph("\uE73E", 14), Height = 32, Style = (Style)Application.Current.Resources["AccentButtonStyle"], Margin = new Thickness(0, 4, 0, 0) };
        ToolTipService.SetToolTip(done, "편집 끝내기 (Esc)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(done, "완료");
        done.Click += (_, _) => o.End();

        items.Children.Add(EditBar.Btn("\uE710", "카드 넣기", o.ShowAddMenu));
        items.Children.Add(guides);
        items.Children.Add(EditBar.Sep2());
        foreach (var b in cardButtons) items.Children.Add(b);
        items.Children.Add(EditBar.Sep2());
        items.Children.Add(EditBar.Btn("\uE713", "설정", () => SettingsWindow.Open(mgr, null)));
        items.Children.Add(done);
        foreach (var child in items.Children)
            if (child is Control c) { c.HorizontalAlignment = HorizontalAlignment.Stretch; c.HorizontalContentAlignment = HorizontalAlignment.Center; }

        // 다 펼쳤을 때 크기를 재고, 알약 쪽에 붙여 두어 창이 커지는 만큼만 드러나게 한다.
        root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var full = root.DesiredSize;
        int dir = o.Edge == ScreenEdge.Bottom ? -1 : 1;
        root.Height = full.Height;
        root.HorizontalAlignment = HorizontalAlignment.Left;
        root.VerticalAlignment = dir > 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        var outer = new Grid { Children = { root } };

        self = new BarEditBar(outer, cardButtons, mgr, () => o.Selected, dir, full, o.GearRect, rename, o.Edge == ScreenEdge.Right);
        _bar = self;
        self.Closed += (_, _) =>
        {
            if (_bar != self) return;
            _bar = null;
            o.End(); // Alt+F4 등으로 막대만 닫혔으면 바 편집도 끝낸다.
        };
        self.Refresh();
        self.Apply(0);
        self.AppWindow.Show();
        self.Animate(1);
    }
    public static void CloseBar()
    {
        var bar = _bar;
        _bar = null;
        if (bar == null) return;
        bar._closed = true;
        bar.StopAnim();
        bar.Close();
    }

    public static void Update() => _bar?.Refresh();

    /// <summary>톱니를 눌렀을 때: 펼쳐져 있으면 접고, 접혀 있으면 펼친다.</summary>
    public static void Toggle()
    {
        if (_bar is not { } b) return;
        b._expanded = !b._expanded;
        if (b._expanded) b.AppWindow.Show();
        b.Animate(b._expanded ? 1 : 0);
    }

    // ----- 펼치고 접기 -----

    private void Apply(double k)
    {
        _k = k;
        double scale = Native.MonitorScaleAt(_gear.Left, _gear.Top);
        int gap = (int)Math.Round(4 * scale);
        int w = (int)Math.Round(_full.Width * scale), h = Math.Max(1, (int)Math.Round(_full.Height * scale * k));
        int y = _dir > 0 ? _gear.Bottom + gap : _gear.Top - gap - h;
        Resize(new Windows.Graphics.RectInt32(_gear.Left, y, w, h));
    }

    private void Animate(double to)
    {
        StopAnim();
        _from = _k;
        _to = to;
        _clock = System.Diagnostics.Stopwatch.StartNew();
        _anim = Ui.Queue.CreateTimer();
        _anim.Interval = TimeSpan.FromMilliseconds(8);
        double ms = to > _from ? 280 : 200;
        _anim.Tick += (_, _) =>
        {
            double t = Math.Min(1, _clock!.ElapsedMilliseconds / ms);
            double c = Math.Cbrt(t), eased = t >= 1 ? 1 : 3 * (1 - c) * c * c + c * c * c; // 감속(cubic-bezier(0,0,0,1))
            Apply(_from + (_to - _from) * eased);
            if (t < 1) return;
            StopAnim();
            if (_to == 0 && !_closed) AppWindow.Hide();
        };
        _anim.Start();
    }

    private void StopAnim()
    {
        _anim?.Stop();
        _anim = null;
    }

    private void Refresh()
    {
        var sel = _selected();
        foreach (var b in _cardButtons) b.IsEnabled = sel != null;
    }

    /// <summary>카드 이름을 바로 고치는 작은 입력 창(펼친 창을 열지 않고). Enter로 바꾸고 Esc로 닫는다.</summary>
    public static void RenameFor(string group)
    {
        if (_bar is not { } b || b._mgr.GroupCard(group) is not { } card) return;
        var box = new TextBox { Text = card.CardName, MinWidth = 220, MaxLength = 80 };
        var flyout = new Flyout
        {
            Content = box,
            Placement = b._flyLeft ? FlyoutPlacementMode.Left : FlyoutPlacementMode.Right,
            ShouldConstrainToRootBounds = false,
        };
        bool done = false;
        void Commit()
        {
            if (done) return;
            done = true;
            string text = box.Text.Trim();
            if (text.Length > 0 && text != card.CardName && b._mgr.Cards.Contains(card)) b._mgr.RenameGroup(card, text);
            flyout.Hide();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { Commit(); e.Handled = true; }
        };
        flyout.Opened += (_, _) => { box.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); box.SelectAll(); };
        flyout.Closed += (_, _) => Commit();
        flyout.ShowAt(b._renameButton);
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