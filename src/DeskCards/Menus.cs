using System;

namespace DeskCards;

/// <summary>카드·항목·트레이의 우클릭 메뉴. 카드 위 아이콘과 펼친 창의 타일이 같은 항목 메뉴를 쓴다.</summary>
internal static class Menus
{
    /// <param name="launched">열기·관리자 권한 실행 뒤에 할 일(펼친 창 닫기 등).</param>
    public static FluentMenu ForEntry(ShellEntry entry, Action? launched = null) => new FluentMenu()
        .Item("", "열기", () => { FileOps.Launch(entry.Path); launched?.Invoke(); }, image: entry.Icon)
        .Item("", "관리자 권한으로 실행", () => { FileOps.Launch(entry.Path, admin: true); launched?.Invoke(); })
        .Item("", "파일 위치 열기", () => FileOps.Reveal(entry.Path))
        .Separator()
        .Item("", "바탕화면으로 꺼내기", () => FileOps.MoveTo(entry.Path, FileOps.UserDesktop))
        .Item("", "휴지통으로 이동", () => FileOps.Recycle(entry.Path));

    public static FluentMenu ForCard(CardWindow card, GroupManager mgr) => new FluentMenu()
        .Item("", "펼치기", () => ExpandedWindow.Open(card, mgr))
        .Item("", "카드 편집 (이동 · 크기 · 삭제)", () => mgr.BeginEditMode(card))
        .Item("", "이름 바꾸기", () => ExpandedWindow.Open(card, mgr, editTitle: true))
        .Item("", "폴더 열기", () => FileOps.OpenFolder(card.Group.Folder))
        .Separator()
        .Item("", "새 그룹", () => mgr.NewGroup())
        .Item("", "그룹 삭제 (항목은 바탕화면으로)", () => mgr.DeleteGroup(card))
        .Separator()
        .Item("", "설정", () => SettingsWindow.Open(mgr, card));

    /// <summary>.dard 카드 메뉴. 카드 위 어디를 우클릭해도 이 메뉴가 뜬다(페이지는 우클릭을 받지 않는다).</summary>
    public static FluentMenu ForDard(DardWindow card, GroupManager mgr) => new FluentMenu()
        .Item("", "카드 설정", () => card.Runtime.OpenSettings(card.Info.Id), enabled: card.Runtime.Package.SettingsRatio != null)
        .Item("", "카드 편집 (이동 · 크기 · 삭제)", () => mgr.BeginEditMode(card))
        .Item("", "다시 불러오기", card.Reload)
        .Item("", "파일 위치 열기", () => FileOps.Reveal(card.Runtime.Package.Path))
        .Separator()
        .Item("", "카드 삭제 (.dard를 휴지통으로)", () => mgr.DeleteDard(card), danger: true)
        .Separator()
        .Item("", "Desk Cards 설정", () => SettingsWindow.Open(mgr));

    public static FluentMenu ForTray(GroupManager mgr, Action quit) => new FluentMenu()
        .Item("", "설정", () => SettingsWindow.Open(mgr))
        .Item("", "카드 편집 (이동 · 크기 · 삭제)", () => mgr.BeginEditMode())
        .Item("", "새 그룹", () => mgr.NewGroup())
        .Item("", "그룹 폴더 열기", () => FileOps.OpenFolder(mgr.Root))
        .Separator()
        .Item("", "종료", quit);
}
