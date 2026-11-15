using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCards;

/// <summary>
/// 카드 바 조합키를 새로 누르는 칸(설정 › 카드 바 › 조합키). [새 조합 누르기]를 누르면 키 상태를 지켜보고,
/// 누르는 키가 키캡 모양으로 쌓이다가 모두 떼면 쓸 수 있는 조합인지 바로 알려 준다. 쓸 수 있을 때만 [저장]이 켜진다.
/// 키는 이벤트가 아니라 실제 키 상태를 40ms마다 읽어서 본다(Win·Alt 같은 키가 창에 안 올 때도 있어서).
/// </summary>
internal sealed class KeyComboEditor : StackPanel
{
    private readonly IReadOnlyList<int> _current;
    private readonly Action<List<int>> _save;
    private readonly StackPanel _caps = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 8 };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MinHeight = 18, Margin = new Thickness(0, 0, 0, 12) };
    private readonly Button _record, _saveButton;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _poll = Ui.Queue.CreateTimer();
    private readonly List<int> _combo = new();
    private List<int>? _result;
    private bool _recording;
    private bool _finished = true; // 한 번 다 떼고 나서 다시 누르면 새 조합으로 시작한다

    public KeyComboEditor(IReadOnlyList<int> current, Action<List<int>> save)
    {
        _current = current;
        _save = save;
        Spacing = 0;
        var box = new Border
        {
            MinHeight = 72,
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Background = Res("ControlFillColorDefaultBrush"),
            BorderBrush = Res("ControlStrokeColorDefaultBrush"),
            Child = _caps,
        };
        Children.Add(box);
        Children.Add(_status);

        _record = new Button { Content = "새 조합 누르기", MinWidth = 80 };
        _record.Click += (_, _) => Toggle();
        _saveButton = new Button { Content = "저장", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsEnabled = false, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        _saveButton.Click += (_, _) => { if (_result != null) _save(_result); };
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _record, _saveButton } });

        _poll.Interval = TimeSpan.FromMilliseconds(40);
        _poll.Tick += (_, _) => OnPoll();
        Unloaded += (_, _) => Cancel();
        ShowKeys(current, dim: true);
        Status("지금 조합이에요. [새 조합 누르기]를 누르고 원하는 키를 함께 눌렀다 떼 주세요.", "SubFg");
    }

    private void Toggle()
    {
        if (_recording) { Cancel(); return; }
        _recording = true;
        _finished = true;
        _combo.Clear();
        _record.Content = "그만 누르기";
        Status("키를 누르세요…", "SubFg");
        // 새 조합을 누르는 동안에는 지금 조합의 등록을 풀어 둔다(안 그러면 그 키가 앱에 오지 않는다).
        EdgeBar.SuspendHotkey(true);
        _poll.Start();
    }

    /// <summary>입력을 그만두고 지금 조합으로 되돌린다.</summary>
    private void Cancel()
    {
        if (!_recording) return;
        Stop();
        _result = null;
        _saveButton.IsEnabled = false;
        ShowKeys(_current, dim: true);
        Status("지금 조합이에요.", "SubFg");
    }

    private void Stop()
    {
        _recording = false;
        _poll.Stop();
        _record.Content = "새 조합 누르기";
        EdgeBar.SuspendHotkey(false);
    }

    private static IEnumerable<int> PressedKeys()
    {
        // 마우스 버튼(1~6)은 빼고 키보드 키만 본다.
        for (int vk = 8; vk <= 0xFE; vk++)
        {
            if (vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) continue; // 좌우 구분 키는 일반 키로 본다
            if (Native.GetAsyncKeyState(vk) < 0) yield return KeyCombo.Normalize(vk);
        }
    }

    private void OnPoll()
    {
        var down = PressedKeys().Distinct().ToList();
        if (down.Count > 0)
        {
            if (_finished)
            {
                // Esc만 눌렀으면 그만둔다.
                if (down.Count == 1 && down[0] == KeyCombo.Esc) { Cancel(); return; }
                _combo.Clear();
                _finished = false;
                _result = null;
                _saveButton.IsEnabled = false;
                Status("", "SubFg");
            }
            bool added = false;
            foreach (var vk in down)
                if (!_combo.Contains(vk)) { _combo.Add(vk); added = true; }
            if (added)
            {
                if (_combo.Any(v => v is KeyCombo.Win or KeyCombo.Alt)) KeyCombo.SuppressRelease(_combo); // 시작 메뉴·창 메뉴가 열리지 않게
                ShowKeys(_combo, dim: false);
            }
            return;
        }
        if (_finished || _combo.Count == 0) return;
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
            _caps.Children.Add(new Border
            {
                MinWidth = 44,
                Height = 40,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1, 1, 1, 3),
                Opacity = dim ? 0.55 : 1,
                Background = Res("CardBackgroundFillColorDefaultBrush"),
                BorderBrush = Res("ControlStrokeColorDefaultBrush"),
                Child = new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Theme.Brush("Fg") },
            });
        }
    }

    private static Microsoft.UI.Xaml.Media.Brush Res(string key) => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];

    private void Status(string text, string brush)
    {
        _status.Text = text;
        _status.Foreground = Theme.Brush(brush);
    }
}