using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace DeskCards;

// 그룹 루트의 .dard 파일 = 카드. 처음 보는 파일은 추가할지 한 번 묻고, 승인한 파일만 띄운다.
internal sealed partial class GroupManager
{
    private readonly Dictionary<string, DardRuntime> _dards = new(StringComparer.OrdinalIgnoreCase); // 파일 경로 → 불러온 카드
    private readonly Dictionary<string, DardStamp> _dardSkipped = new(StringComparer.OrdinalIgnoreCase); // 거절·오류·묻는 중이라 이 상태로는 다시 안 보는 파일
    private readonly Dictionary<string, string> _dardIds = new(StringComparer.OrdinalIgnoreCase); // 파일 경로 → 매니페스트 id
    private readonly Queue<DardPackage> _dardPrompts = new();
    private bool _dardPrompting;
    private string? _dardAsking; // 지금 확인 창을 띄운 파일

    /// <summary>불러온 .dard 목록.</summary>
    public IReadOnlyCollection<DardRuntime> Dards => _dards.Values;

    private List<string>? ListDardFiles()
    {
        try
        {
            return Directory.EnumerateFiles(Root, "*.dard")
                .Where(f => (File.GetAttributes(f) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .ToList();
        }
        catch
        {
            return null; // 읽기 실패를 빈 폴더로 취급하면 떠 있는 카드까지 모두 닫힌다.
        }
    }

    private static DardStamp? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new DardStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch { return null; }
    }

    private void ReconcileDards()
    {
        var files = ListDardFiles();
        if (files == null) return;
        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        foreach (var path in _dards.Keys.Where(p => !present.Contains(p)).ToList()) UnloadDard(path);
        foreach (var path in _dardSkipped.Keys.Where(p => !present.Contains(p)).ToList()) _dardSkipped.Remove(path);
        foreach (var path in _dardIds.Keys.Where(p => !present.Contains(p)).ToList())
        {
            // 거절했던 파일을 치우면 거절도 잊는다. 다시 넣으면 다시 묻는다.
            if (_cfg.Dards.TryGetValue(_dardIds[path], out var a) && !a.Allowed)
            {
                _cfg.Dards.Remove(_dardIds[path]);
                _cfg.Save();
            }
            _dardIds.Remove(path);
        }

        foreach (var path in files)
        {
            if (StampOf(path) is not { } stamp) continue;
            _dards.TryGetValue(path, out var loaded);
            if (loaded?.Stamp == stamp) continue;
            if (_dardSkipped.TryGetValue(path, out var skipped) && skipped == stamp) continue;

            DardPackage pkg;
            try
            {
                pkg = DardPackage.Load(path);
            }
            catch (DardException ex)
            {
                UnloadDard(path);
                _dardSkipped[path] = stamp;
                Dialogs.Show(ex.Message, heading: $"'{Path.GetFileName(path)}' 카드를 열 수 없어요");
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // 아직 쓰는 중일 수 있다. 다 쓰면 감시가 다시 알려 준다.
            }

            if (loaded != null && loaded.Package.Hash == pkg.Hash)
            {
                loaded.Stamp = stamp; // 내용은 같고 시각만 바뀌었다
                continue;
            }
            UnloadDard(path);
            _dardIds[path] = pkg.Id;

            if (_dards.Values.FirstOrDefault(d => d.Package.Id == pkg.Id) is { } twin)
            {
                _dardSkipped[path] = stamp;
                Dialogs.Show($"같은 카드(id: {pkg.Id})가 이미 {Path.GetFileName(twin.Package.Path)}로 떠 있어요. 둘 중 하나를 지워 주세요.",
                    heading: $"'{Path.GetFileName(path)}' 카드를 열지 않았어요");
                continue;
            }

            switch (Approval(pkg))
            {
                case true:
                    LoadDard(pkg, stamp);
                    break;
                case false:
                    _dardSkipped[path] = stamp;
                    break;
                default:
                    _dardSkipped[path] = stamp;
                    AskDard(pkg);
                    break;
            }
        }
    }

    /// <summary>승인했으면 true, 거절했으면 false, 아직 모르면(처음 보거나 권한이 늘었으면) null.</summary>
    private bool? Approval(DardPackage pkg)
    {
        if (!_cfg.Dards.TryGetValue(pkg.Id, out var a)) return null;
        if (a.Hash == pkg.Hash) return a.Allowed;
        // 새 버전인데 권한이 같거나 줄었으면 그대로 승인한다.
        if (a.Allowed && pkg.Permissions.All(p => a.Permissions.Contains(p)))
        {
            a.Hash = pkg.Hash;
            a.Permissions = pkg.Permissions.ToList();
            _cfg.Save();
            return true;
        }
        return null;
    }

    private void AskDard(DardPackage pkg)
    {
        _dardPrompts.Enqueue(pkg);
        if (!_dardPrompting) _debounce.Dispatcher.BeginInvoke(NextDardPrompt);
    }

    /// <summary>추가할지 하나씩 묻는다. 답하면 기록하고 다시 맞춘다.</summary>
    private void NextDardPrompt()
    {
        if (_dardPrompting || _shuttingDown || _dardPrompts.Count == 0) return;
        var pkg = _dardPrompts.Dequeue();
        _dardPrompting = true;
        _dardAsking = pkg.Path;
        MessageBoxResult answer;
        try
        {
            answer = Dialogs.Show(InstallText(pkg), MessageBoxButton.OKCancel, heading: $"'{pkg.Name}' 카드를 추가할까요?", primary: "추가");
        }
        finally
        {
            _dardPrompting = false;
            _dardAsking = null;
        }
        if (_shuttingDown) return;
        _cfg.Dards[pkg.Id] = new DardApproval { Hash = pkg.Hash, Allowed = answer == MessageBoxResult.OK, Permissions = pkg.Permissions.ToList() };
        _cfg.Save();
        _dardSkipped.Remove(pkg.Path);
        Reconcile();
        if (_dardPrompts.Count > 0) _debounce.Dispatcher.BeginInvoke(NextDardPrompt);
    }

    private static string InstallText(DardPackage pkg)
    {
        string head = $"{Path.GetFileName(pkg.Path)} · {pkg.Version} · 카드 {pkg.Cards.Count}장";
        string perms = pkg.Permissions.Count == 0
            ? "이 카드는 권한을 요구하지 않아요."
            : "요구하는 권한:\n" + string.Join("\n", pkg.Permissions.Select(p => "· " + p)) +
              "\n\n이 버전의 Desk Cards에는 아직 권한 기능이 없어서, 위 기능은 동작하지 않아요.";
        return head + "\n\n" + perms + "\n\n추가하지 않으면 이 파일은 다시 묻지 않고 꺼 둬요. 파일을 치웠다가 다시 넣으면 다시 물어요.";
    }

    private void LoadDard(DardPackage pkg, DardStamp stamp)
    {
        var runtime = new DardRuntime(pkg, this, stamp);
        _dards[pkg.Path] = runtime;
        foreach (var info in pkg.Cards) CreateDardWindow(runtime, info);
    }

    private void CreateDardWindow(DardRuntime runtime, DardCardInfo info)
    {
        var w = new DardWindow(runtime, info, this);
        runtime.AddWindow(w);
        PlaceAndShow(w, DardWindow.BaseSizeFor(info, CellSize));
    }

    private void RecreateDardWindow(DardWindow w)
    {
        var runtime = w.Runtime;
        if (Selected == w) Select(null);
        w.ClosingByManager = true;
        w.Close();
        CreateDardWindow(runtime, w.Info);
    }

    private void UnloadDard(string path)
    {
        if (!_dards.Remove(path, out var runtime)) return;
        DardSettingsWindow.CloseFor(runtime);
        foreach (var w in runtime.Windows.ToList())
        {
            if (Selected == w) Select(null);
            w.ClosingByManager = true;
            w.Close();
        }
    }

    /// <summary>그룹 폴더의 .dard 파일과 그 상태(설정의 위젯 카드 목록).</summary>
    public IReadOnlyList<DardEntry> DardEntries()
    {
        var files = ListDardFiles() ?? new List<string>();
        return files.OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase).Select(path =>
        {
            if (_dards.TryGetValue(path, out var runtime)) return new DardEntry(path, DardState.On, runtime);
            if (_dardIds.TryGetValue(path, out var id) && _cfg.Dards.TryGetValue(id, out var a) && !a.Allowed)
                return new DardEntry(path, DardState.Off, null);
            bool asking = string.Equals(_dardAsking, path, StringComparison.OrdinalIgnoreCase) ||
                          _dardPrompts.Any(q => string.Equals(q.Path, path, StringComparison.OrdinalIgnoreCase));
            return new DardEntry(path, asking ? DardState.Asking : DardState.Blocked, null);
        }).ToList();
    }

    /// <summary>
    /// 위젯을 끄거나 켠다. 끄면 거절한 것과 같이 기록하고 카드를 닫는다.
    /// 켜면 기록을 지우고 다시 맞춘다(처음 넣은 것처럼 추가할지 묻는다).
    /// </summary>
    public void SetDardEnabled(string path, bool on)
    {
        if (!on)
        {
            if (!_dards.TryGetValue(path, out var runtime)) return;
            var pkg = runtime.Package;
            _cfg.Dards[pkg.Id] = new DardApproval { Hash = pkg.Hash, Allowed = false, Permissions = pkg.Permissions.ToList() };
            _cfg.Save();
            UnloadDard(path);
            if (StampOf(path) is { } stamp) _dardSkipped[path] = stamp;
            RaiseChanged();
            return;
        }
        if (_dardIds.TryGetValue(path, out var id) && _cfg.Dards.Remove(id)) _cfg.Save();
        _dardSkipped.Remove(path);
        Reconcile();
    }

    /// <summary>열지 못했던 .dard를 다시 읽어 본다.</summary>
    public void RetryDard(string path)
    {
        _dardSkipped.Remove(path);
        Reconcile();
    }

    /// <summary>.dard 파일을 그룹 폴더로 복사한다. 감시가 알아채고 추가할지 묻는다. 실패하면 이유를 돌려준다.</summary>
    public string? ImportDard(string file)
    {
        string dest = Path.Combine(Root, Path.GetFileName(file));
        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) return null;
        if (File.Exists(dest)) return $"그룹 폴더에 같은 이름의 파일({Path.GetFileName(file)})이 이미 있어요.";
        try
        {
            File.Copy(file, dest);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>불러오지 않은(꺼 두었거나 열 수 없는) .dard 파일을 휴지통으로 옮긴다. 지웠으면 true.</summary>
    public bool DeleteDardFile(string path)
    {
        if (_dards.TryGetValue(path, out var runtime)) return DeleteDard(runtime);
        string file = Path.GetFileName(path);
        var r = Dialogs.Show($"{file}을 휴지통으로 옮겨요.", MessageBoxButton.OKCancel, heading: $"'{file}'을 지울까요?", primary: "삭제");
        if (r != MessageBoxResult.OK) return false;
        FileOps.Recycle(path);
        if (File.Exists(path)) return false;
        if (_dardIds.Remove(path, out var id) && _cfg.Dards.Remove(id)) _cfg.Save();
        _dardSkipped.Remove(path);
        RaiseChanged();
        return true;
    }

    /// <summary>카드 파일(.dard)을 휴지통으로 옮기고 그 카드들을 치운다. 지웠으면 true.</summary>
    public bool DeleteDard(DardWindow card) => DeleteDard(card.Runtime);

    public bool DeleteDard(DardRuntime runtime)
    {
        var pkg = runtime.Package;
        string file = Path.GetFileName(pkg.Path);
        var r = Dialogs.Show($"{file}을 휴지통으로 옮겨요. 이 파일로 띄운 카드 {pkg.Cards.Count}장이 모두 사라져요.",
            MessageBoxButton.OKCancel, heading: $"'{pkg.Name}' 카드를 지울까요?", primary: "삭제");
        if (r != MessageBoxResult.OK) return false;
        FileOps.Recycle(pkg.Path);
        if (File.Exists(pkg.Path)) return false;
        UnloadDard(pkg.Path);
        foreach (var info in pkg.Cards)
        {
            string key = runtime.KeyFor(info.Id);
            _cfg.Positions.Remove(key);
            _cfg.Layouts.Remove(key);
            _cfg.DardSettings.Remove(key);
        }
        _cfg.Dards.Remove(pkg.Id);
        _cfg.Save();
        RaiseChanged();
        return true;
    }

    // ----- 카드 설정(desk.card.settings) -----

    public JsonNode? GetDardSettings(string key) =>
        _cfg.DardSettings.TryGetValue(key, out var v) ? JsonNode.Parse(v.GetRawText()) : null;

    public void SetDardSettings(string key, JsonNode? value)
    {
        if (value == null) _cfg.DardSettings.Remove(key);
        else _cfg.DardSettings[key] = JsonSerializer.SerializeToElement(value);
        _cfg.Save();
    }
}

internal enum DardState { On, Off, Asking, Blocked }

/// <summary>그룹 폴더의 .dard 한 개. 불러왔으면 Runtime이 있다.</summary>
internal sealed record DardEntry(string Path, DardState State, DardRuntime? Runtime)
{
    public string Name => Runtime?.Package.Name ?? System.IO.Path.GetFileNameWithoutExtension(Path);
}
