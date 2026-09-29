namespace DeskCards;

internal partial class SettingsWindow
{
    private void BuildGeneral()
    {
        Crumb("일반");
        AddRow(Row("", "Windows 시작 시 실행", "로그인하면 카드가 바로 바탕화면에 나타나요.",
            Switch(AutoStart.Enabled, v => AutoStart.Enabled = v)));
        AddRow(Row("", "Windows 배율 따라가기",
            "Windows 디스플레이 배율을 바꾸면 카드도 같은 비율로 커지고 작아져요. 켜고 끄는 순간에는 지금 크기가 그대로 남아요.",
            Switch(_mgr.FollowWindowsScale, v => _mgr.FollowWindowsScale = v)));
        AddRow(Row("", "안내선 · 자동 맞춤",
            "카드를 옮기거나 크기를 바꿀 때 다른 카드·화면 가운데와 줄이 맞으면 보라색 선을 보여 주고 붙여요. Alt를 누르고 있으면 잠시 꺼져요.",
            Switch(_mgr.ShowGuides, v => _mgr.ShowGuides = v)));
        // 한 칸 = 0.1초. 0 = 즉시, 1~10 = 0.1~1초, 11 = 꺼짐.
        const int hoverOff = Config.HoverDelayMax / 100 + 1;
        AddRow(Row("", "더보기에 올려 두면 펼치기",
            "카드의 더보기 칸에 마우스를 올려 두면 이 시간 뒤에 누르지 않아도 펼쳐지고, 펼쳐진 창 밖으로 옮기면 바로 접혀요. 1초에서 더 늘리면 꺼져요.",
            Stepper(() => _mgr.HoverExpand ? _mgr.HoverExpandDelay / 100 : hoverOff,
                v =>
                {
                    _mgr.HoverExpand = v < hoverOff;
                    if (v < hoverOff) _mgr.HoverExpandDelay = v * 100;
                },
                0, hoverOff, v => v == 0 ? "즉시" : v == hoverOff ? "꺼짐" : $"{v / 10.0:0.0}초")));
        if (Updater.Instance is { } updater)
            AddRow(Row("", "자동 업데이트",
                "새 버전이 나오면 미리 받아 두었다가, 다음에 앱을 켤 때(보통 PC를 다시 켤 때) 새 버전으로 열어요.",
                Switch(updater.AutoUpdate, v => updater.AutoUpdate = v)));

        Header("폴더");
        AddRow(Row("", "그룹 폴더", _mgr.Root, Button("열기", () => FileOps.OpenFolder(_mgr.Root))));

        Header("실험");
        AddRow(Row("", "겹치기 · 레이어",
            "카드를 다른 카드 위에 겹쳐 둘 수 있어요. 그림자가 꼬이거나 카드 앞뒤가 바뀌는 등 여러 오류가 생길 수 있어요. 켜면 완전히 붙이기도 함께 켜져요.",
            Switch(_mgr.AllowOverlap, v => _mgr.AllowOverlap = v)));
        var flush = Switch(_mgr.FlushSnap, v => _mgr.FlushSnap = v);
        flush.IsEnabled = !_mgr.AllowOverlap;
        AddRow(Row("", "완전히 붙이기",
            _mgr.AllowOverlap
                ? "겹치기 · 레이어를 켜 두는 동안은 늘 켜져 있어요."
                : "카드끼리 간격 없이 딱 붙여 둘 수 있어요. 그림자가 옆 카드에 겹쳐 보이거나, 카드를 누를 때마다 그림자 방향이 바뀌는 등 모양이 어색해질 수 있어요.",
            flush));
    }
}
