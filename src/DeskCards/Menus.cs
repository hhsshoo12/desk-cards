using System;
using System.Linq;

namespace DeskCards;

/// <summary>카드·항목·트레이의 우클릭 메뉴. 카드 위 아이콘과 펼친 창의 타일이 같은 항목 메뉴를 쓴다.</summary>
internal static class Menus
{
    /// <param name="launched">열기·관리자 권한 실행 뒤에 할 일(펼친 창 닫기 등).</param>
    public static FluentMenu ForEntry(ShellEntry entry, GroupModel group, GroupManager mgr, Action? launched = null)
    {
        var others = mgr.Groups.Where(g => g != group).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new FluentMenu()
            .Item("", "열기", () => { FileOps.Launch(entry.Path); launched?.Invoke(); }, image: entry.Icon)
            .Item("", "관리자 권한으로 실행", () => { FileOps.Launch(entry.Path, admin: true); launched?.Invoke(); })
            .Item("", "파일 위치 열기", () => FileOps.Reveal(entry.Path))
            .Separator()
            .Sub("", "다른 그룹으로 이동", sub =>
            {
                foreach (var g in others)
                {
                    var target = g;
                    sub.Item("", g.Name, () => FileOps.MoveTo(entry.Path, target.Folder));
                }
            }, enabled: others.Count > 0)
            .Item("", "바탕화면으로 꺼내기", () => FileOps.MoveTo(entry.Path, FileOps.UserDesktop))
            .Item("", "휴지통으로 이동", () => FileOps.Recycle(entry.Path));
    }

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

    public static FluentMenu ForTray(GroupManager mgr, Action quit) => new FluentMenu()
        .Item("", "설정", () => SettingsWindow.Open(mgr))
        .Item("", "카드 편집 (이동 · 크기 · 삭제)", () => mgr.BeginEditMode())
        .Item("", "새 그룹", () => mgr.NewGroup())
        .Item("", "그룹 폴더 열기", () => FileOps.OpenFolder(mgr.Root))
        .Separator()
        .Item("", "종료", quit);
}
