using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DeskCards;

/// <summary>
/// 카드 바 조합키를 새로 누르는 칸(설정 › 카드 바 › 조합키). [새 조합 누르기]를 누르면 키 입력을 기다리고,
/// 누르는 키가 키캡 모양으로 쌓이다가 모두 떼면 쓸 수 있는 조합인지(모양, 이미 다른 곳에서 쓰는지) 바로 알려 준다.
/// 쓸 수 있는 새 조합일 때만 [저장]을 누를 수 있다. 키 입력은 설정 창이 <see cref="HandleKey"/>로 넘겨준다.
/// </summary>
internal sealed class KeyComboEditor : StackPanel
{
    private readonly IReadOnlyList<int> _current;
    private readonly Action<List<int>> _save;
    private readonly WrapPanel _caps = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button _record, _saveButton;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly List<int> _combo = new();
    private List<int>? _result;
    private bool _finished = true; // 한 번 다 떼고 나서 다시 누르면 새 조합으로 시작한다

    /// <param name="button">설정 창 모양의 버튼을 만드는 함수(글씨, 누를 때, 파란색인지).</param>
    public KeyComboEditor(IReadOnlyList<int> current, Action<List<int>> save, Func<string, Action, bool, Button> button)
    {
        _current = current;
        _save = save;
        var box = new Border
        {
            MinHeight = 72,
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = _caps,
        };
        box.SetResourceReference(Border.BackgroundProperty, "ControlBg");
        box.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
        Children.Add(box);
        Children.Add(_status);

        _record = button("새 조합 누르기", Toggle, false);
        _saveButton = button("저장", () => { if (_result != null) _save(_result); }, true);
        _saveButton.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_record);
        buttons.Children.Add(_saveButton);
        Children.Add(buttons);

        _poll.Tick += (_, _) => OnPoll();
        ShowKeys(current, dim: true);
        Status("지금 조합이에요. [새 조합 누르기]를 누르고 원하는 키를 함께 눌렀다 떼 주세요.", "SubFg");
        _saveButton.IsEnabled = false;
    }

    public bool IsRecording { get; private set; }

    private void Toggle()
    {
        if (IsRecording) { Cancel(); return; }
        IsRecording = true;
        _finished = true;
        _record.Content = "그만 누르기";
        Status("키를 누르세요…", "SubFg");
        // 새 조합을 누르는 동안에는 지금 조합의 등록을 풀어 둔다(안 그러면 그 키가 설정 창에 오지 않는다).
        EdgeBar.SuspendHotkey(true);
    }

    /// <summary>입력을 그만두고 지금 조합으로 되돌린다.</summary>
    public void Cancel()
    {
        if (!IsRecording) return;
        Stop();
        _result = null;
        _saveButton.IsEnabled = false;
        ShowKeys(_current, dim: true);
        Status("지금 조합이에요.", "SubFg");
    }

    private void Stop()
    {
        IsRecording = false;
        _poll.Stop();
        _record.Content = "새 조합 누르기";
        EdgeBar.SuspendHotkey(false);
    }

    /// <summary>설정 창이 받은 키. 아무것도 누르지 않은 채 Esc를 누르면 그만둔다.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        int vk = KeyCombo.FromKey(key);
        if (vk == 0) return;
        if (vk == KeyCombo.Esc && _finished) { Cancel(); return; }
        if (_finished)
        {
            _combo.Clear();
            _finished = false;
            _result = null;
            _saveButton.IsEnabled = false;
            Status("", "SubFg");
        }
        if (_combo.Contains(vk)) return;
        _combo.Add(vk);
        if (vk is KeyCombo.Win or KeyCombo.Alt) KeyCombo.SuppressRelease(_combo); // 시작 메뉴·창 메뉴가 열리지 않게
        ShowKeys(_combo, dim: false);
        _poll.Start();
    }

    // 뗀 것은 키 이벤트 대신 실제 키 상태로 본다(Win을 누르면 떼는 이벤트가 안 올 때가 있다).
    private void OnPoll()
    {
        if (_combo.Count == 0 || _combo.Any(KeyCombo.IsPressed)) return;
        _poll.Stop();
        _finished = true;
        var keys = KeyCombo.Order(_combo);
        ShowKeys(keys, dim: false);
        string? problem = KeyCombo.Problem(keys);
        if (problem == null && keys.SequenceEqual(_current)) problem = "지금 쓰는 조합이에요.";
        if (problem == null && KeyCombo.NeedsHotkey(keys) && !EdgeBar.IsHotkeyFree(keys))
            problem = "이 조합은 Windows나 다른 앱이 이미 쓰고 있어요. 다른 조합을 눌러 주세요.";
        if (problem != null)
        {
            _result = null;
            Status(problem + " 다시 누르면 새로 시작해요.", "Danger");
        }
        else
        {
            _result = keys;
            Stop(); // 쓸 수 있는 조합이 나오면 입력을 멈추고 저장을 기다린다
            Status("이 조합으로 저장할 수 있어요.", "SubFg");
        }
        _saveButton.IsEnabled = _result != null;
    }

    private void ShowKeys(IEnumerable<int> keys, bool dim)
    {
        _caps.Children.Clear();
        foreach (var name in KeyCombo.Names(keys))
        {
            var label = new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var cap = new Border
            {
                MinWidth = 44,
                Height = 40,
                Margin = new Thickness(4),
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1, 1, 1, 3),
                Opacity = dim ? 0.55 : 1,
                Child = label,
            };
            cap.SetResourceReference(Border.BackgroundProperty, "RowBg");
            cap.SetResourceReference(Border.BorderBrushProperty, "ControlBorder");
            _caps.Children.Add(cap);
        }
    }

    private void Status(string text, string brush)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}
