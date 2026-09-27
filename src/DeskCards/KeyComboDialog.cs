using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskCards;

/// <summary>
/// 카드 바 조합키를 새로 누르는 창. 누르는 동안 키가 키캡 모양으로 쌓이고, 모두 떼면 쓸 수 있는 조합인지
/// (모양, 이미 다른 곳에서 쓰는지) 바로 알려 준다. 쓸 수 있을 때만 [저장]을 누를 수 있다.
/// </summary>
internal static class KeyComboDialog
{
    /// <returns>저장한 조합. 취소하면 null.</returns>
    public static List<int>? Ask(Window owner, IReadOnlyList<int> current)
    {
        var dialog = new DialogWindow("조합키 만들기",
            "카드 바를 열 때 누르고 있을 키를 함께 눌렀다 떼 주세요.\n" +
            "보조키(Ctrl · Alt · Shift · Win)를 하나 이상 넣고, 필요하면 일반 키를 하나 더할 수 있어요. " +
            "일반 키가 들어간 조합은 Windows 단축키로 등록돼서 누르는 동안 다른 앱에 전달되지 않아요.",
            MessageBoxButton.OKCancel, "저장")
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var caps = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var box = new Border
        {
            MinHeight = 72,
            Margin = new Thickness(0, 16, 0, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = caps,
        };
        box.SetResourceReference(Border.BackgroundProperty, "ControlBg");
        box.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        var status = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, MinHeight = 18 };
        dialog.AddContent(box);
        dialog.AddContent(status);

        var combo = new List<int>();
        List<int>? result = null;
        bool finished = true; // 한 번 다 떼고 나서 다시 누르면 새 조합으로 시작한다

        void ShowKeys(IEnumerable<int> keys, bool dim)
        {
            caps.Children.Clear();
            foreach (var name in KeyCombo.Names(keys))
            {
                var cap = new Border
                {
                    MinWidth = 44,
                    Height = 40,
                    Margin = new Thickness(4),
                    Padding = new Thickness(12, 0, 12, 0),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(1, 1, 1, 3),
                    Opacity = dim ? 0.55 : 1,
                    Child = new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
                };
                cap.SetResourceReference(Border.BackgroundProperty, dim ? "ControlBg" : "RowBg");
                cap.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
                ((TextBlock)cap.Child).SetResourceReference(TextBlock.ForegroundProperty, "Fg");
                caps.Children.Add(cap);
            }
        }

        void Status(string text, string brush)
        {
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, brush);
        }

        // 뗀 것은 키 이벤트 대신 실제 키 상태로 본다(Win을 누르면 떼는 이벤트가 안 올 때가 있다).
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        poll.Tick += (_, _) =>
        {
            if (combo.Count == 0 || combo.Any(KeyCombo.IsPressed)) return;
            poll.Stop();
            finished = true;
            var keys = KeyCombo.Order(combo);
            ShowKeys(keys, dim: false);
            string? problem = KeyCombo.Problem(keys);
            if (problem == null && keys.SequenceEqual(current)) problem = "지금 쓰는 조합이에요.";
            if (problem == null && KeyCombo.NeedsHotkey(keys) && !EdgeBar.IsHotkeyFree(keys))
                problem = "이 조합은 Windows나 다른 앱이 이미 쓰고 있어요. 다른 조합을 눌러 주세요.";
            if (problem != null) { Status(problem, "Danger"); result = null; }
            else { Status("이 조합으로 저장할 수 있어요.", "SubFg"); result = keys; }
            dialog.Primary.IsEnabled = result != null;
        };

        dialog.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            int vk = KeyCombo.FromKey(key);
            if (vk == 0) return;
            if (vk == KeyCombo.Esc && (finished || combo.Count == 0)) { dialog.Close(); return; }
            if (finished)
            {
                combo.Clear();
                finished = false;
                result = null;
                dialog.Primary.IsEnabled = false;
                Status("", "SubFg");
            }
            if (combo.Contains(vk)) return;
            combo.Add(vk);
            if (vk is KeyCombo.Win or KeyCombo.Alt) KeyCombo.SuppressRelease(combo); // 시작 메뉴·창 메뉴가 열리지 않게
            ShowKeys(combo, dim: false);
            poll.Start();
        };

        ShowKeys(current, dim: true);
        Status("지금 조합이에요. 새 조합을 눌러 주세요.", "SubFg");
        dialog.Primary.IsEnabled = false;
        // 새 조합을 누르는 동안에는 지금 조합의 등록을 풀어 둔다(안 그러면 그 키가 이 창에 오지 않는다).
        EdgeBar.SuspendHotkey(true);
        try { dialog.ShowDialog(); }
        finally
        {
            poll.Stop();
            EdgeBar.SuspendHotkey(false);
        }
        return dialog.Result == MessageBoxResult.OK ? result : null;
    }
}
