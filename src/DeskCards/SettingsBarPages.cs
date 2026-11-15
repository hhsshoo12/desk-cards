using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCards;

/// <summary>카드 바: 켬/끔, 바 편집, 여는 디스플레이·가장자리, 여는 법, 모양.</summary>
internal sealed class BarPage : SettingsPage
{
    private static string? _display; // 마지막으로 고른 디스플레이(페이지를 다시 만들어도 유지)

    public BarPage(SettingsPageArgs a) : base(a) => Start();

    protected override void OnMgrChanged() { }

    /// <summary>디스플레이 목록. Windows 설정처럼 번호를 붙이려고 DISPLAY 번호 순으로 둔다.</summary>
    private static List<Screens.Monitor> Displays() => Screens.All()
        .OrderBy(s => int.TryParse(new string(s.DeviceName.Where(char.IsDigit).ToArray()), out int n) ? n : int.MaxValue)
        .ThenBy(s => s.Bounds.Left).ToList();

    protected override void Build()
    {
        Crumbs(("카드 바", null));
        Row("\uE8A7", "카드 바 사용", "끄면 가장자리에 마우스를 대도 카드 바가 나오지 않아요.", Switch(Mgr.BarEnabled, v => { Mgr.BarEnabled = v; Rebuild(); }));
        var edit = AccentBtn("편집", () => EdgeBar.OpenForEdit());
        edit.IsEnabled = Mgr.BarEnabled;
        Row("\uE70F", "카드 바 편집", "바를 열어 둔 채로 카드를 넣고, 원하는 자리에 끌어다 놓아요. 바탕화면처럼 안내선이 줄을 맞춰 줘요.", edit);
        Row("\uE946", "여는 법 요약",
            "조합키를 누른 채 화면 가장자리에 마우스를 대고 있으면 커서 둘레의 게이지가 한 바퀴 돌고 카드 바가 나와요. 바 밖으로 마우스를 옮기면 들어가요.");

        var screens = Displays();
        var selected = screens.FirstOrDefault(s => s.DeviceName == _display) ?? screens.FirstOrDefault(s => s.Primary) ?? screens[0];
        _display = selected.DeviceName;
        bool many = screens.Count > 1;
        int number = screens.IndexOf(selected) + 1;

        Header(many ? "디스플레이" : "여는 곳");
        if (many)
        {
            var combo = new ComboBox { MinWidth = 220 };
            for (int i = 0; i < screens.Count; i++)
            {
                var s = screens[i];
                combo.Items.Add($"디스플레이 {i + 1} · {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " · 주" : ""));
            }
            combo.SelectedIndex = number - 1;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex < 0 || combo.SelectedIndex == number - 1) return;
                _display = screens[combo.SelectedIndex].DeviceName;
                Ui.Post(Rebuild);
            };
            Row("\uE7F4", "설정할 디스플레이", "디스플레이마다 카드 바가 나오는 가장자리를 따로 정해요.", combo);
        }

        string device = selected.DeviceName;
        var taskbar = Native.TaskbarEdgeOn(selected);
        var options = new List<(ScreenEdge? Edge, string Text)> { (ScreenEdge.Top, "위"), (ScreenEdge.Bottom, "아래"), (ScreenEdge.Left, "왼쪽"), (ScreenEdge.Right, "오른쪽") };
        if (many) options.Add((null, "안 열기"));
        var edgeCombo = new ComboBox { MinWidth = 160 };
        foreach (var (edge, text) in options)
            edgeCombo.Items.Add(new ComboBoxItem { Content = text, IsEnabled = edge == null || edge != taskbar });
        var current = Mgr.BarEdgeFor(device);
        edgeCombo.SelectedIndex = options.FindIndex(o => o.Edge == current);
        edgeCombo.SelectionChanged += (_, _) =>
        {
            if (edgeCombo.SelectedIndex < 0) return;
            Mgr.SetBarEdge(device, options[edgeCombo.SelectedIndex].Edge);
        };
        string where = $"{selected.Bounds.Width}×{selected.Bounds.Height}" + (selected.Primary ? " · 주 디스플레이" : "");
        string edgeDesc = current is { } chosen && chosen == taskbar
            ? "고른 쪽에 작업 표시줄이 있어서 열리지 않아요. 다른 쪽을 골라 주세요."
            : $"{where} · 한 곳만 고를 수 있어요. 작업 표시줄이 있는 쪽은 고를 수 없어요.";
        Row("\uE8A0", many ? $"디스플레이 {number}에서 여는 가장자리" : "여는 가장자리", edgeDesc, edgeCombo);

        Header("여는 법");
        string keyDesc = KeyCombo.Text(Mgr.BarKeys) + " · " + (EdgeBar.HotkeyRegistered == false
            ? "다른 앱이 쓰고 있어서 등록하지 못했어요."
            : "이 키를 누르고 있을 때만 열려요.");
        Row("\uE765", "조합키", keyDesc, null, () => Host.Go(SettingsPageKind.BarKeys));
        Row("\uE916", "대고 있을 시간", "게이지가 한 바퀴 도는 시간이에요.",
            Stepper(() => Mgr.BarDelay / 100, v => Mgr.BarDelay = v * 100, 0, Config.BarDelayMax / 100, v => v == 0 ? "즉시" : $"{v / 10.0:0.0}초"));
        Row("\uE7C9", "인식 영역", "가장자리에서 얼마나 안쪽까지 대고 있어도 되는지예요. 0%는 딱 붙었을 때만이고 2.5%까지 넓힐 수 있어요.",
            Stepper(() => Mgr.BarZone, v => Mgr.BarZone = v, 0, Config.BarZoneMax, v => $"{v / 10.0:0.0}%"));

        Header("모양");
        Row("\uE740", "바 두께", "화면 너비(위·아래 바는 높이)의 몇 %로 할지 정해요. 3분의 1(33%)까지예요.",
            Stepper(() => Mgr.BarSize, v => Mgr.BarSize = v, Config.BarSizeMin, Config.BarSizeMax, v => $"{v}%"));
    }
}

/// <summary>카드 바 › 조합키: 새 조합 누르기, 등록 상태와 알아 둘 점.</summary>
internal sealed class BarKeysPage : SettingsPage
{
    public BarKeysPage(SettingsPageArgs a) : base(a) => Start();

    protected override void OnMgrChanged() { }

    protected override void Build()
    {
        Crumbs(("카드 바", SettingsPageKind.Bar), ("조합키", null));
        Add(new TextBlock
        {
            Text = "카드 바는 이 키를 누르고 있는 동안에만 열려요. 보조키(Ctrl · Alt · Shift · Win)를 하나 이상 넣고, 필요하면 일반 키를 하나 더할 수 있어요.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(1, 0, 0, 12),
        });
        Add(new KeyComboEditor(Mgr.BarKeys, keys => { Mgr.BarKeys = keys; Ui.Post(Rebuild); }));

        Header("등록 상태");
        string state = !KeyCombo.NeedsHotkey(Mgr.BarKeys)
            ? "보조키만 있는 조합이라 Windows에 등록하지 않고, 누르고 있는지만 봐요. 보조키만 누르는 건 다른 앱에 아무 일도 일으키지 않아요."
            : !Mgr.BarEnabled
                ? "카드 바가 꺼져 있어서 등록하지 않았어요. 카드 바를 켜면 Windows 단축키로 등록해요."
            : EdgeBar.HotkeyRegistered == false
                ? "Windows나 다른 앱이 이 조합을 쓰고 있어서 등록하지 못했어요. 다른 조합으로 바꿔 주세요."
                : "Windows 단축키로 등록돼 있어요. 누르는 동안 다른 앱에는 전달되지 않고, 카드 바를 끄면 등록도 풀려요.";
        Row("\uE765", KeyCombo.Text(Mgr.BarKeys), state);

        Header("알아 두기");
        Row("\uE946", "입력 언어 전환과 겹치는 조합",
            "Alt + Shift, Ctrl + Shift는 Windows에서 입력 언어나 키보드 배열 전환에 쓰일 수 있어요. 누를 때마다 입력 언어가 바뀌면 다른 조합을 써 주세요.");
        Row("\uE946", "Win · Alt",
            "Win이나 Alt만 눌렀다 떼면 시작 메뉴나 앱 메뉴가 열리는데, 카드 바가 열릴 때와 조합을 누를 때는 열리지 않게 막아요.");
    }
}