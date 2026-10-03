using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskCards;

internal sealed partial class GroupManager
{
    private void OnFolderRenamed(RenamedEventArgs e)
    {
        if (_shuttingDown) return;
        if (Path.GetFileName(e.FullPath).StartsWith(".rename-", StringComparison.OrdinalIgnoreCase)) { Bump(); return; }
        string oldName = Path.GetFileName(e.OldFullPath), name = Path.GetFileName(e.FullPath);
        if (_cards.TryGetValue(oldName, out var card) &&
            string.Equals(card.Group.Folder, e.OldFullPath, StringComparison.Ordinal) && Directory.Exists(e.FullPath))
        {
            Rekey(card, oldName, name);
            card.Group.MovedTo(e.FullPath);
            EditChanged?.Invoke();
        }
        Bump();
    }

    /// <summary>카드 안 항목 순서를 정하고 저장한다(펼친 창에서 끌어 옮겼을 때).</summary>
    public void SetOrder(GroupModel group, IEnumerable<ShellEntry> items)
    {
        var names = items.Select(e => Path.GetFileName(e.Path)).ToList();
        _cfg.Orders[group.Name] = names;
        _cfg.Save();
        group.Order = names;
    }

    /// <summary>그룹 이름이 바뀌었을 때 카드 목록과 저장된 위치·모양을 새 이름으로 옮긴다.</summary>
    private void Rekey(CardWindow card, string oldName, string newName)
    {
        _cards.Remove(oldName);
        _cards[newName] = card;
        if (_cfg.Positions.Remove(oldName, out var position)) _cfg.Positions[newName] = position;
        if (_cfg.Layouts.Remove(oldName, out var layout)) _cfg.Layouts[newName] = layout;
        if (_cfg.Orders.Remove(oldName, out var order)) _cfg.Orders[newName] = order;
        RenameInBar(oldName, newName);
        _cfg.Save();
    }

    private List<string>? ListGroupFolders()
    {
        try
        {
            return Directory.EnumerateDirectories(Root)
                .Where(d => !Path.GetFileName(d).StartsWith(".rename-", StringComparison.OrdinalIgnoreCase))
                .Where(d => (File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .ToList();
        }
        catch
        {
            return null; // 읽기 실패를 빈 폴더로 취급하면 정상 카드까지 모두 닫힌다.
        }
    }

    private void RecoverRenamedGroups()
    {
        foreach (string folder in Directory.EnumerateDirectories(Root).Where(d => Path.GetFileName(d).StartsWith(".rename-", StringComparison.OrdinalIgnoreCase)))
        {
            try { Directory.Move(folder, FileOps.Unique(Root, "복구된 그룹")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceError("그룹 복구 보류: {0}: {1}", folder, ex); }
        }
    }

    /// <summary>빈 그룹을 만들고 이름을 바로 입력할 수 있게 펼친다. 편집 중이면 새 카드를 고른다.</summary>
    public CardWindow? NewGroup(bool openTitle = true)
    {
        string path = FileOps.Unique(Root, "새 그룹");
        Directory.CreateDirectory(path);
        Reconcile();
        if (!_cards.TryGetValue(Path.GetFileName(path), out var card)) return null;
        if (Editing) Select(card);
        if (openTitle) ExpandedWindow.Open(card, this, editTitle: true);
        return card;
    }

    public bool RenameGroup(CardWindow card, string newName)
    {
        if (!FileOps.IsValidGroupName(newName))
        {
            Dialogs.Show("예약된 이름, 끝의 점·공백, \\ / : * ? \" < > | 문자는 사용할 수 없어요.", heading: "사용할 수 없는 이름이에요");
            return false;
        }
        string oldName = card.Group.Name;
        if (oldName == newName) return true;
        string dest = Path.Combine(Root, newName);
        bool caseOnly = string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (Directory.Exists(dest) || File.Exists(dest)))
        {
            Dialogs.Show("다른 이름을 골라 주세요.", heading: $"'{newName}' 그룹이 이미 있어요");
            return false;
        }
        try
        {
            if (caseOnly)
            {
                RenameCaseOnly(card.Group.Folder, dest);
            }
            else
            {
                Directory.Move(card.Group.Folder, dest);
            }
        }
        catch (Exception ex)
        {
            Dialogs.Show(ex.Message, heading: "문제가 생겼어요");
            return false;
        }

        Rekey(card, oldName, newName);
        card.Group.MovedTo(dest);
        RaiseChanged();
        EditChanged?.Invoke();
        return true;
    }

    internal static void RenameCaseOnly(string source, string destination, Action<string, string>? move = null)
    {
        move ??= Directory.Move;
        string temp = Path.Combine(Path.GetDirectoryName(source)!, ".rename-" + Guid.NewGuid().ToString("N"));
        move(source, temp);
        try { move(temp, destination); }
        catch
        {
            move(temp, source);
            throw;
        }
    }

    /// <summary>편집 막대의 삭제: 폴더 카드는 그룹을, .dard 카드는 카드 파일을 지운다.</summary>
    public bool DeleteCard(DeskCard card) => card switch
    {
        CardWindow folder => DeleteGroup(folder),
        DardWindow dard => DeleteDard(dard),
        _ => false,
    };

    /// <summary>그룹을 지운다. 항목이 있으면 확인하고 바탕화면으로 옮긴다. 지웠으면 true.</summary>
    public bool DeleteGroup(CardWindow card)
    {
        var g = card.Group;
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(g.Folder); }
        catch (Exception ex)
        {
            Dialogs.Show(ex.Message, heading: "문제가 생겼어요");
            return false;
        }
        if (entries.Length > 0)
        {
            var r = Dialogs.Show($"안에 있는 항목 {entries.Length}개는 바탕화면으로 옮겨져요.",
                MessageBoxButton.OKCancel, heading: $"'{g.Name}' 그룹을 삭제할까요?", primary: "삭제");
            if (r != MessageBoxResult.OK) return false;
        }
        try
        {
            FileOps.MoveContentsAndDelete(g.Folder, FileOps.UserDesktop);
        }
        catch (Exception ex)
        {
            Dialogs.Show(ex.Message, heading: "폴더를 지우지 못했어요");
            return false;
        }
        _cfg.Positions.Remove(g.Name);
        _cfg.Layouts.Remove(g.Name);
        _cfg.Orders.Remove(g.Name);
        RemoveFromBar(g.Name);
        _cfg.Save();
        RemoveCard(g.Name);
        RaiseChanged();
        return true;
    }
}
