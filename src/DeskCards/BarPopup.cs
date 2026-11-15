using System.Linq;
using System;
using System.Drawing;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskCards;

/// <summary>
/// 카드 바 편집 단추 옆에 잠깐 뜨는 작은 창: 칸 수 고르기. 다른 곳을 누르면 닫힌다.
/// (카드 바 메뉴 자체는 WPF 라이브러리가 그리고, 키 입력과 Fluent 모양이 필요한 이 두 가지만 앱이 맡는다.)
/// </summary>
internal sealed class BarPopup : ToolWindow
{
    private static BarPopup? _current;
    private Action? _onClose;
    private bool _closed, _isGrid;

    private BarPopup(FrameworkElement content) : base("Desk Cards 카드 바", content) { }

    private static BarPopup Show(FrameworkElement content, Rectangle anchor, bool leftSide, Action? onClose = null)
    {
        _current?.Dismiss();
        var w = new BarPopup(content) { _onClose = onClose };
        _current = w;
        int gap = (int)Math.Round(6 * Native.MonitorScaleAt(anchor.Left, anchor.Top));
        if (leftSide) w.Place(anchor.Left - gap, anchor.Top + anchor.Height / 2, anchorX: 1, anchorY: 0.5);
        else w.Place(anchor.Right + gap, anchor.Top + anchor.Height / 2, anchorY: 0.5);
        w.AppWindow.Show();
        w.Activate();
        w.Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) w.Dismiss(); };
        w.Closed += (_, _) => { if (_current == w) _current = null; };
        return w;
    }

    private void Dismiss()
    {
        if (_closed) return;
        _closed = true;
        _onClose?.Invoke();
        Close();
    }

    /// <summary>미리보기 칸 수를 빠르게 고른다.</summary>
    public static void ShowGrid(GroupManager mgr, CardWindow card, Rectangle anchor, bool leftSide)
    {
        // 열려 있는 칸 수 창을 단추로 다시 누르면 닫는다.
        if (_current is { _isGrid: true, _closed: false } open) { open.Dismiss(); return; }
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < (EditBar.GridPresets.Length + 3) / 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        BarPopup? w = null;
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
            item.Click += (_, _) => { card.SetGrid(cols, rows); w!.Dismiss(); };
            Grid.SetColumn(item, i % 4);
            Grid.SetRow(item, i / 4);
            grid.Children.Add(item);
        }
        var hint = new TextBlock { Text = "다른 칸 수는 설정에서 고를 수 있어요", FontSize = 12, Margin = new Thickness(4, 8, 4, 0), Foreground = Theme.Brush("SubFg") };
        var root = new Border { Padding = new Thickness(10), Background = Theme.Brush("PopupBg"), Child = new StackPanel { Children = { grid, hint } } };
        w = Show(root, anchor, leftSide);
        w._isGrid = true;
    }
}