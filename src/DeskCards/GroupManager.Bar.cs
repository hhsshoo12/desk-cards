using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeskCards;

// 카드 바에 놓은 카드와 카드 바 전용 그룹.
internal sealed partial class GroupManager
{
    /// <summary>카드 바 배치가 바뀌었을 때(떠 있는 바가 다시 늘어놓는다).</summary>
    public event Action? BarChanged;

    /// <summary>카드 바에 놓은 카드. 바 두께 단위 위치·너비.</summary>
    public IReadOnlyList<BarItem> BarItems => _cfg.BarItems;

    public void SetBarItems(IEnumerable<BarItem> items)
    {
        _cfg.BarItems = items.Select(i => i.Clone()).ToList();
        _cfg.Save();
        BarChanged?.Invoke();
        RaiseChanged();
    }

    /// <summary>바탕화면에는 없고 카드 바에만 있는 그룹인지.</summary>
    public bool IsBarOnly(string group) => _cfg.BarOnlyGroups.Contains(group, StringComparer.OrdinalIgnoreCase);

    /// <summary>그룹 이름으로 카드를 찾는다(카드 바 전용 그룹 포함).</summary>
    public CardWindow? GroupCard(string group) => _cards.GetValueOrDefault(group);

    /// <summary>카드 바에 넣을 수 있는 바탕화면 카드(아직 바에 없는 것).</summary>
    public IEnumerable<CardWindow> CardsNotInBar() =>
        Cards.Where(c => !IsBarOnly(c.Group.Name) && !_cfg.BarItems.Any(i => string.Equals(i.Group, c.Group.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>카드 바에만 있는 빈 그룹을 만든다(바탕화면에는 띄우지 않는다).</summary>
    public CardWindow? NewBarGroup()
    {
        string path = FileOps.Unique(Root, "새 그룹");
        string name = Path.GetFileName(path);
        _cfg.BarOnlyGroups.Add(name); // 폴더 감시가 먼저 알아채도 바탕화면에 띄우지 않게 폴더보다 먼저 적어 둔다
        _cfg.Save();
        Directory.CreateDirectory(path);
        Reconcile();
        return _cards.GetValueOrDefault(name);
    }

    /// <summary>카드 바 전용 그룹을 바탕화면에도 띄운다.</summary>
    public void ShowOnDesktop(CardWindow card)
    {
        if (_cfg.BarOnlyGroups.RemoveAll(n => string.Equals(n, card.Group.Name, StringComparison.OrdinalIgnoreCase)) == 0) return;
        _cfg.Save();
        PlaceAndShow(card, null);
        RaiseChanged();
    }

    private void RenameInBar(string oldName, string newName)
    {
        foreach (var item in _cfg.BarItems.Where(i => string.Equals(i.Group, oldName, StringComparison.OrdinalIgnoreCase)))
            item.Group = newName;
        int only = _cfg.BarOnlyGroups.FindIndex(n => string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase));
        if (only >= 0) _cfg.BarOnlyGroups[only] = newName;
        BarChanged?.Invoke();
    }

    private void RemoveFromBar(string group)
    {
        _cfg.BarItems.RemoveAll(i => string.Equals(i.Group, group, StringComparison.OrdinalIgnoreCase));
        _cfg.BarOnlyGroups.RemoveAll(n => string.Equals(n, group, StringComparison.OrdinalIgnoreCase));
        BarChanged?.Invoke();
    }
}
