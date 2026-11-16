namespace DeskCards.Expanded;

/// <summary>바탕화면 카드 창 규칙 가운데 창 없이 확인할 수 있는 것(회귀 테스트가 부른다).</summary>
public static class DesktopCardRules
{
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpNoOwnerZOrder = 0x0200;

    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const long WsDisabled = 0x08000000L;
    public const long WsExTopmost = 0x00000008L;

    /// <summary>
    /// 바탕화면 층(HWND_BOTTOM)으로 내릴 때 쓰는 SetWindowPos 플래그.
    /// SWP_NOOWNERZORDER를 빼야 Windows(ZOrderByOwner)가 소유자(Progman)를 카드 아래에 두고 카드가 Progman 뒤로 빠지지 않는다.
    /// </summary>
    public const uint RestackBottomFlags = SwpNoSize | SwpNoMove | SwpNoActivate;

    /// <summary>
    /// 편집 모드(HWND_TOPMOST / HWND_NOTOPMOST) 전환 시 쓰는 플래그.
    /// 소유자(Progman)의 z 순서를 함께 끌어올리거나 건드리지 않도록 SWP_NOOWNERZORDER를 포함한다.
    /// </summary>
    public const uint TopmostFlags = SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder;

    /// <summary>
    /// 카드 창이 WM_SETFOCUS를 받았을 때 WPF에 넘기지 않고 삼키는지. 편집 중이 아니면 삼킨다.
    /// WPF는 창이 초점을 받으면 마지막 초점(웹 카드의 WebView2)을 되살리고, WebView2는 그때 브라우저 프로세스에
    /// MoveFocus를 비동기로 보낸다(WebView2Base.OnGotKeyboardFocus). 그 사이 카드가 맨 아래로 내려가 바탕화면(Progman)이
    /// 활성이 되면, 늦게 도착한 브라우저의 SetFocus가 카드 창을 다시 활성화한다 → 다시 내림 → 다시 활성 … 핑퐁이 되어
    /// 매번 Progman이 카드 위로 올라와 카드가 깜박였다. 바탕화면 카드는 편집 중이 아니면 키보드 초점이 필요 없다.
    /// </summary>
    public static bool SwallowSetFocus(bool editing) => !editing;

    /// <summary>
    /// WebView2 같은 자식 HWND(별도 프로세스)를 품은 카드가 일반 상태에서 WM_MOUSEACTIVATE를 받을 때
    /// 자식 HWND의 후속 SetFocus 호출이 부모(카드 창)를 활성화하지 못하게 일시적으로 WS_DISABLED를 걸어야 하는지.
    /// 자식 HWND가 없는 순수 WPF 폴더 카드는 클릭 시 SetFocus를 부르지 않으므로 걸지 않는다.
    /// </summary>
    public static bool ShouldSuppressChildFocusActivation(bool editing, bool hasChildHwnd) => !editing && hasChildHwnd;

    /// <summary>
    /// WM_WINDOWPOSCHANGING에서 z 순서 변경을 막을지 결정한다.
    /// 앱이 직접 z를 옮기는 중(movingZ)이거나 편집 중이면 통과시키고(hwndInsertAfter도 덮어쓰지 않는다),
    /// 그 외에는 SWP_NOZORDER를 붙여 의도치 않은 상승·하강을 막는다.
    /// </summary>
    public static uint FilterPosChangingFlags(uint flags, bool movingZ, bool editing) =>
        (flags & SwpNoZOrder) == 0 && !movingZ && !editing ? flags | SwpNoZOrder : flags;
}
