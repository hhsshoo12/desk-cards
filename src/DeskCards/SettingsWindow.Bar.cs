using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeskCards;

internal partial class SettingsWindow
{
    /// <summary>디스플레이 목록. Windows 설정처럼 번호를 붙이려고 DISPLAY 번호 순으로 둔다.</summary>
    private static List<System.Windows.Forms.Screen> Displays() => System.Windows.Forms.Screen.AllScreens
        .OrderBy(s => int.TryParse(new string(s.DeviceName.Where(char.IsDigit).ToArray()), out int n) ? n : int.MaxValue)
        .ThenBy(s => s.Bounds.Left).ToList();

    private System.Windows.Forms.Screen SelectedDisplay(List<System.Windows.Forms.Screen> screens) =>
        screens.FirstOrDefault(s => s.DeviceName == _barDisplay) ?? screens.FirstOrDefault(s => s.Primary) ?? screens[0];

    /// <summary>카드 바 페이지: 제목 옆 켬/끔, 카드 바 편집, 디스플레이 배치에서 고른 디스플레이의 가장자리, 여는 법, 모양.</summary>
    private void BuildBar()
    {
        Crumb("카드 바");
        // 카드 바 전체 켬/끔은 페이지 제목 오른쪽 끝에 둔다(Windows 설정의 블루투스처럼).
        var enabled = Switch(_mgr.BarEnabled, v => _mgr.BarEnabled = v);
        enabled.ToolTip = "카드 바 사용";
        PageAction.Content = enabled;
        AddRow(Row("", "카드 바 편집",
            "바를 열어 둔 채로 카드를 넣고, 원하는 자리에 끌어다 놓아요. 바탕화면처럼 안내선이 줄을 맞춰 줘요.",
            Button("편집", EdgeBar.OpenForEdit, accent: true)));
        AddRow(Row("", "여는 법 요약",
            "조합키를 누른 채 화면 가장자리에 마우스를 대고 있으면 커서 둘레의 게이지가 한 바퀴 돌고 카드 바가 나와요. 바 밖으로 마우스를 옮기면 들어가요.", null));

        var screens = Displays();
        var selected = SelectedDisplay(screens);
        _barDisplay = selected.DeviceName;
        bool many = screens.Count > 1;
        int number = screens.IndexOf(selected) + 1;

        Header(many ? "디스플레이" : "여는 곳");
        if (many) AddRow(DisplayPicker(screens, selected));

        string device = selected.DeviceName;
        var taskbar = Native.TaskbarEdgeOn(selected);
        var options = new List<(ScreenEdge?, string)>
        {
            (ScreenEdge.Top, "위"), (ScreenEdge.Bottom, "아래"), (ScreenEdge.Left, "왼쪽"), (ScreenEdge.Right, "오른쪽"),
        };
        if (many) options.Add((null, "안 열기"));
        string where = $"{selected.Bounds.Width}×{selected.Bounds.Height}" + (selected.Primary ? " · 주 디스플레이" : "");
        string edgeDesc = _mgr.BarEdgeFor(device) is { } chosen && chosen == taskbar
            ? "고른 쪽에 작업 표시줄이 있어서 열리지 않아요. 다른 쪽을 골라 주세요."
            : $"{where} · 한 곳만 고를 수 있어요. 작업 표시줄이 있는 쪽은 고를 수 없어요.";
        AddRow(Row("", many ? $"디스플레이 {number}에서 여는 가장자리" : "여는 가장자리", edgeDesc,
            Choice(options, () => _mgr.BarEdgeFor(device), v => _mgr.SetBarEdge(device, v), v => v == null || v != taskbar)));

        Header("여는 법");
        string keyDesc = KeyCombo.Text(_mgr.BarKeys) + " · " + (EdgeBar.HotkeyRegistered == false
            ? "다른 앱이 쓰고 있어서 등록하지 못했어요."
            : "이 키를 누르고 있을 때만 열려요.");
        AddRow(Row("", "조합키", keyDesc, null, () => Go(PageKind.BarKeys)));

        AddRow(Row("", "대고 있을 시간", "게이지가 한 바퀴 도는 시간이에요.",
            Stepper(() => _mgr.BarDelay / 100, v => _mgr.BarDelay = v * 100,
                0, Config.BarDelayMax / 100, v => v == 0 ? "즉시" : $"{v / 10.0:0.0}초")));

        AddRow(Row("", "인식 영역",
            "가장자리에서 얼마나 안쪽까지 대고 있어도 되는지예요. 0%는 딱 붙었을 때만이고 2.5%까지 넓힐 수 있어요. 바꾸는 동안 화면에 어둡게 보여 줘요.",
            Stepper(() => _mgr.BarZone, v => { _mgr.BarZone = v; PreviewBar(zone: true); },
                0, Config.BarZoneMax, v => $"{v / 10.0:0.0}%")));

        Header("모양");
        AddRow(Row("", "바 두께", "화면 너비(위·아래 바는 높이)의 몇 %로 할지 정해요. 3분의 1(33%)까지예요. 바꾸는 동안 바가 나올 자리를 테두리로 보여 줘요.",
            Stepper(() => _mgr.BarSize, v => { _mgr.BarSize = v; PreviewBar(zone: false); }, Config.BarSizeMin, Config.BarSizeMax, v => $"{v}%")));
    }

    /// <summary>고른 디스플레이에 바 자리(테두리) 또는 인식 영역(어두운 띠)을 잠깐 보여 준다.</summary>
    private void PreviewBar(bool zone)
    {
        var screen = SelectedDisplay(Displays());
        if (_mgr.BarEdgeFor(screen.DeviceName) is not { } edge) return;
        if (zone) BarPreview.Zone(screen, edge, _mgr.BarZone);
        else BarPreview.Outline(screen, edge, _mgr.BarSize);
    }

    /// <summary>
    /// Windows 설정의 디스플레이 배치처럼 모니터를 실제 배치대로 그린다. 누르면 그 디스플레이를 고른다.
    /// 모니터마다 카드 바가 나오는 가장자리에 굵은 선을 긋는다. [식별]은 화면마다 큰 번호를 띄운다.
    /// </summary>
    private UIElement DisplayPicker(List<System.Windows.Forms.Screen> screens, System.Windows.Forms.Screen selected)
    {
        const double MaxW = 440, MaxH = 150, Gap = 3, Line = 4;
        int left = screens.Min(s => s.Bounds.Left), top = screens.Min(s => s.Bounds.Top);
        int right = screens.Max(s => s.Bounds.Right), bottom = screens.Max(s => s.Bounds.Bottom);
        double k = Math.Min(MaxW / (right - left), MaxH / (bottom - top));
        var canvas = new Canvas { Width = (right - left) * k, Height = (bottom - top) * k, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 12) };

        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            bool on = s.DeviceName == selected.DeviceName;
            double w = s.Bounds.Width * k - Gap, h = s.Bounds.Height * k - Gap;
            var label = new TextBlock { Text = (i + 1).ToString(), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentFg" : "Fg");
            var grid = new Grid();
            grid.Children.Add(label);
            if (_mgr.BarEdgeFor(s.DeviceName) is { } edge)
            {
                bool side = edge is ScreenEdge.Left or ScreenEdge.Right;
                var line = new Border
                {
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(5),
                    Width = side ? Line : double.NaN,
                    Height = side ? double.NaN : Line,
                    HorizontalAlignment = edge == ScreenEdge.Left ? HorizontalAlignment.Left : edge == ScreenEdge.Right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                    VerticalAlignment = edge == ScreenEdge.Top ? VerticalAlignment.Top : edge == ScreenEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
                };
                line.SetResourceReference(Border.BackgroundProperty, on ? "AccentFg" : "Accent");
                grid.Children.Add(line);
            }
            var monitor = new Border { Width = w, Height = h, CornerRadius = new CornerRadius(4), Child = grid, Cursor = Cursors.Hand };
            monitor.SetResourceReference(Border.BackgroundProperty, on ? "Accent" : "MonitorBg");
            string device = s.DeviceName;
            monitor.MouseLeftButtonUp += (_, e) => { e.Handled = true; _barDisplay = device; ScheduleBuild(); };
            Canvas.SetLeft(monitor, (s.Bounds.Left - left) * k);
            Canvas.SetTop(monitor, (s.Bounds.Top - top) * k);
            canvas.Children.Add(monitor);
        }

        var identify = Button("식별", () => BarPreview.Identify(screens));
        identify.HorizontalAlignment = HorizontalAlignment.Right;
        var hint = new TextBlock { Text = "디스플레이를 눌러 고르면 아래에서 그 디스플레이의 가장자리를 정해요. 선은 카드 바가 나오는 쪽이에요.", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");

        var panel = new StackPanel();
        panel.Children.Add(canvas);
        panel.Children.Add(identify);
        panel.Children.Add(hint);
        return RowBox(panel, new Thickness(16, 12, 16, 12));
    }

    /// <summary>카드 바 › 조합키: 새 조합 누르기, 등록 상태와 알아 둘 점.</summary>
    private void BuildBarKeys()
    {
        Crumb("카드 바", () => Go(PageKind.Bar));
        Crumb("조합키");

        var intro = new TextBlock
        {
            Text = "카드 바는 이 키를 누르고 있는 동안에만 열려요. 보조키(Ctrl · Alt · Shift · Win)를 하나 이상 넣고, 필요하면 일반 키를 하나 더할 수 있어요.",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        AddRow(intro);

        _keyEditor = new KeyComboEditor(_mgr.BarKeys, keys => _mgr.BarKeys = keys, (text, act, accent) => Button(text, act, accent));
        AddRow(RowBox(_keyEditor, new Thickness(16)));

        Header("등록 상태");
        string state = !KeyCombo.NeedsHotkey(_mgr.BarKeys)
            ? "보조키만 있는 조합이라 Windows에 등록하지 않고, 누르고 있는지만 봐요. 보조키만 누르는 건 다른 앱에 아무 일도 일으키지 않아요."
            : !_mgr.BarEnabled
                ? "카드 바가 꺼져 있어서 등록하지 않았어요. 카드 바를 켜면 Windows 단축키로 등록해요."
            : EdgeBar.HotkeyRegistered == false
                ? "Windows나 다른 앱이 이 조합을 쓰고 있어서 등록하지 못했어요. 다른 조합으로 바꿔 주세요."
                : "Windows 단축키로 등록돼 있어요. 누르는 동안 다른 앱에는 전달되지 않고, 카드 바를 끄면 등록도 풀려요.";
        AddRow(Row("", KeyCombo.Text(_mgr.BarKeys), state, null));

        Header("알아 두기");
        AddRow(Row("", "입력 언어 전환과 겹치는 조합",
            "Alt + Shift, Ctrl + Shift는 Windows에서 입력 언어나 키보드 배열 전환에 쓰일 수 있어요. 누를 때마다 입력 언어가 바뀌면 다른 조합을 써 주세요.", null));
        AddRow(Row("", "Win · Alt",
            "Win이나 Alt만 눌렀다 떼면 시작 메뉴나 앱 메뉴가 열리는데, 카드 바가 열릴 때와 조합을 누를 때는 열리지 않게 막아요.", null));
    }
}
