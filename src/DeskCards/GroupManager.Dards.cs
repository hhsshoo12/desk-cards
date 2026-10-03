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

        bool busy = false;
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
                busy = true;
                continue; // 아직 쓰는 중일 수 있다. 다 쓰면 감시가 다시 알려 준다.
            }

            if (loaded != null && loaded.Package.Hash == pkg.Hash)
            {
                loaded.Stamp = stamp; // 내용은 같고 시각만 바뀌었다
                continue;
            }
            UnloadDard(path);
            _dardIds[path] = pkg.Id;

            if (_dards.Values.Any(d => d.Package.Id == pkg.Id))
            {
                _dardSkipped[path] = stamp; // 같은 id가 이미 떠 있다. 경고(DardIssues)로 알린다.
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
        _dardFilesBusy = busy;
        CheckDardIssues();
    }

    /// <summary>승인했으면 true, 거절했으면 false, 아직 모르면(처음 보거나 권한이 늘었으면) null.</summary>
    private bool? Approval(DardPackage pkg)
    {
        if (!_cfg.Dards.TryGetValue(pkg.Id, out var a)) return null;
        if (a.Hash == pkg.Hash) return a.Allowed;
        // 새 버전인데 권한이 같거나 줄었으면 그대로 승인한다.
        if (a.Allowed && pkg.Permissions.All(p => a.Permissions.Contains(p.Key)))
        {
            a.Hash = pkg.Hash;
            a.Permissions = pkg.Permissions.Select(p => p.Key).ToList();
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
        _cfg.Dards[pkg.Id] = new DardApproval { Hash = pkg.Hash, Allowed = answer == MessageBoxResult.OK, Permissions = pkg.Permissions.Select(p => p.Key).ToList() };
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
            : "요구하는 권한:\n" + string.Join("\n", pkg.PermissionLines.Select(p => "· " + p.Label + (p.Works ? "" : " (아직 동작하지 않음)")));
        return head + "\n\n" + perms + "\n\n추가하지 않으면 이 파일은 다시 묻지 않고 꺼 둬요. 파일을 치웠다가 다시 넣으면 다시 물어요.";
    }

    private void LoadDard(DardPackage pkg, DardStamp stamp)
    {
        // 업데이트로 저장소 위치(internet 권한, storage 방식)가 바뀌었으면 먼저 옮기고 나서 띄운다.
        if (_cfg.DardStorage.TryGetValue(pkg.Id, out var from) && from != pkg.StorageLocation)
        {
            _dardSkipped[pkg.Path] = stamp;
            _dardMovingNow.Add(pkg.Id);
            // 브라우저 객체는 UI 스레드에서만 다룬다. 이어지는 일이 UI 스레드로 돌아오게 Dispatcher에서 시작한다.
            _debounce.Dispatcher.BeginInvoke(() => MoveDardStorage(pkg, stamp, from));
            return;
        }
        if (from == null)
        {
            _cfg.DardStorage[pkg.Id] = pkg.StorageLocation;
            _cfg.Save();
        }
        var runtime = new DardRuntime(pkg, this, stamp);
        _dards[pkg.Path] = runtime;
        foreach (var info in pkg.Cards) CreateDardWindow(runtime, info);
    }

    /// <summary>
    /// 저장소를 옛 위치에서 새 위치로 옮긴 뒤 다시 맞춘다(그때 띄운다). 옮기는 동안 이 파일은 다시 보지 않는다.
    /// 실패해도 새 위치로 넘어가되 옛 저장소는 지우지 않고 남겨 둔다.
    /// </summary>
    private async void MoveDardStorage(DardPackage pkg, DardStamp stamp, string from)
    {
        _dardSkipped[pkg.Path] = stamp;
        // 옮기는 중이라는 표시. 앱이 도중에 꺼지면 남아 있다가, 다음에 이어서 옮긴 뒤 알린다(옛 저장소는 다 옮긴 뒤에야 지우므로 다시 해도 된다).
        bool resumed = _cfg.DardMoving.ContainsKey(pkg.Id);
        _cfg.DardMoving[pkg.Id] = pkg.StorageLocation;
        _cfg.Save();
        _dardMovingNow.Add(pkg.Id);
        string? error = null;
        int skipped = 0;
        try
        {
            skipped = await DardStorage.MoveAsync(pkg, from);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        _dardMovingNow.Remove(pkg.Id);
        if (_shuttingDown) return;
        _cfg.DardStorage[pkg.Id] = pkg.StorageLocation;
        _cfg.DardMoving.Remove(pkg.Id);
        _cfg.Save();
        _dardSkipped.Remove(pkg.Path);
        Reconcile();
        if (resumed && error == null)
            Dialogs.Show("지난번에 저장된 데이터를 옮기다가 앱이 꺼져서, 이번에 처음부터 다시 옮겼어요." + (skipped > 0 ? $"\n\n옮길 수 없는 값이 들어 있던 항목 {skipped}개는 빠졌어요." : ""),
                heading: $"'{pkg.Name}' 카드의 데이터를 이어서 옮겼어요");
        else if (error != null)
            Dialogs.Show($"카드는 새 저장소로 띄웠고, 예전 데이터는 지우지 않고 남겨 뒀어요. 설정 › 위젯 카드에서 지울 수 있어요.\n\n{error}", heading: $"'{pkg.Name}' 카드의 저장된 데이터를 옮기지 못했어요");
        else if (skipped > 0)
            Dialogs.Show($"옮길 수 없는 값이 들어 있던 항목 {skipped}개는 빠졌어요.", heading: $"'{pkg.Name}' 카드의 저장된 데이터를 옮겼어요");
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
            _cfg.Dards[pkg.Id] = new DardApproval { Hash = pkg.Hash, Allowed = false, Permissions = pkg.Permissions.Select(p => p.Key).ToList() };
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
        var r = Dialogs.Show($"{file}을 휴지통으로 옮기고, 이 카드가 저장한 데이터도 지워요.", MessageBoxButton.OKCancel, heading: $"'{file}'을 지울까요?", primary: "삭제");
        if (r != MessageBoxResult.OK) return false;
        FileOps.Recycle(path);
        if (File.Exists(path)) return false;
        if (_dardIds.Remove(path, out var id) && !_dardIds.ContainsValue(id))
        {
            _cfg.Dards.Remove(id);
            ForgetDardData(id);
            _cfg.Save();
        }
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
        var r = Dialogs.Show($"{file}을 휴지통으로 옮겨요. 이 파일로 띄운 카드 {pkg.Cards.Count}장과 카드가 저장한 데이터가 모두 사라져요.",
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
        }
        _cfg.Dards.Remove(pkg.Id);
        ForgetDardData(pkg.Id);
        _cfg.Save();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 카드 id의 저장된 데이터를 모두 지우고 기록도 지운다(두 브라우저 환경의 저장소 목록에 있는 그 id의 origin 전부).
    /// 설정 저장은 부르는 쪽이 한다.
    /// </summary>
    private void ForgetDardData(string id)
    {
        _cfg.DardStorage.Remove(id);
        _cfg.DardMoving.Remove(id);
        _cfg.KeptDardData.RemoveAll(k => k.EndsWith(":" + id, StringComparison.Ordinal));
        foreach (bool internet in new[] { false, true })
        {
            var origins = DardStorage.Recorded(internet).Where(p => p.Value == id).Select(p => p.Key).ToList();
            if (origins.Count > 0) _debounce.Dispatcher.BeginInvoke(() => ClearDardStorage(internet, origins));
        }
    }

    private async void ClearDardStorage(bool internet, IReadOnlyList<string> origins)
    {
        try { await DardStorage.ClearAsync(internet, origins); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("카드 저장소를 지우지 못함: " + ex.Message); }
        if (!_shuttingDown) CheckDardIssues();
    }
}

internal enum DardState { On, Off, Asking, Blocked }

/// <summary>그룹 폴더의 .dard 한 개. 불러왔으면 Runtime이 있다.</summary>
internal sealed record DardEntry(string Path, DardState State, DardRuntime? Runtime)
{
    public string Name => Runtime?.Package.Name ?? System.IO.Path.GetFileNameWithoutExtension(Path);
}
