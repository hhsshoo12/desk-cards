namespace DeskCards.Expanded;

/// <summary>바탕화면 카드 창 규칙 가운데 창 없이 확인할 수 있는 것(회귀 테스트가 부른다).</summary>
public static class DesktopCardRules
{
    /// <summary>
    /// 카드 창이 WM_SETFOCUS를 받았을 때 WPF에 넘기지 않고 삼키는지. 편집 중이 아니면 삼킨다.
    /// WPF는 창이 초점을 받으면 마지막 초점(웹 카드의 WebView2)을 되살리고, WebView2는 그때 브라우저 프로세스에
    /// MoveFocus를 비동기로 보낸다(WebView2Base.OnGotKeyboardFocus). 그 사이 카드가 맨 아래로 내려가 바탕화면(Progman)이
    /// 활성이 되면, 늦게 도착한 브라우저의 SetFocus가 카드 창을 다시 활성화한다 → 다시 내림 → 다시 활성 … 핑퐁이 되어
    /// 매번 Progman이 카드 위로 올라와 카드가 깜박였다. 바탕화면 카드는 편집 중이 아니면 키보드 초점이 필요 없다
    /// (웹 화면을 누르면 브라우저가 직접 초점을 가져간다).
    /// </summary>
    public static bool SwallowSetFocus(bool editing) => !editing;
}
