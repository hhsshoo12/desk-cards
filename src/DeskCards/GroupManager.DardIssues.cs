using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace DeskCards;

// 카드 파일(.dard)과 저장된 데이터가 맞지 않는 곳을 찾아 알린다.
// 데이터가 있는 곳은 브라우저 데이터 폴더 옆의 저장소 목록(DardStorage.Recorded)을 기준으로 본다(앱 설정 파일이 날아가도 남는다).
internal sealed partial class GroupManager
{
    private readonly HashSet<string> _dardMovingNow = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dardNotified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long?> _dardUsage = new(StringComparer.Ordinal);
    private string _dardIssueSignature = "";
    private bool _dardFilesBusy;

    /// <summary>테스트용: 새 경고를 확인 창으로 알리지 않는다(설정 화면 목록에는 그대로 나온다).</summary>
    internal static bool QuietDardIssues { get; set; }

    /// <summary>지금 확인할 것 목록. "그대로 두기"를 고른 것은 빠진다.</summary>
    public IReadOnlyList<DardIssue> DardIssues()
    {
        var list = new List<DardIssue>();
        var files = _dardIds.GroupBy(p => p.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Key).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase).ToList(), StringComparer.Ordinal);

        foreach (var (id, paths) in files.Where(f => f.Value.Count > 1))
            list.Add(new DardIssue("dup:" + id, DardIssueKind.Duplicate, id, false, Array.Empty<string>(), paths,
                $"같은 카드 파일이 {paths.Count}개 있어요",
                $"{string.Join(", ", paths.Select(Path.GetFileName))} · 같은 카드(id: {id})라서 하나만 띄웠어요. 필요 없는 파일을 지워 주세요."));

        foreach (bool internet in new[] { false, true })
        {
            string env = internet ? "online" : "offline";
            foreach (var g in DardStorage.Recorded(internet).GroupBy(p => p.Value, StringComparer.Ordinal))
            {
                string id = g.Key;
                var origins = g.Select(p => p.Key).OrderBy(o => o, StringComparer.Ordinal).ToList();
                if (_dardMovingNow.Contains(id)) continue;
                if (!files.ContainsKey(id))
                {
                    string key = $"orphan:{env}:{id}";
                    list.Add(new DardIssue(key, DardIssueKind.Orphan, id, internet, origins, Array.Empty<string>(),
                        $"카드 파일 없이 데이터만 남아 있어요 ({id})",
                        $"{UsageText(key)}카드 파일을 지웠거나, 옮겼거나, 열 수 없어요. 같은 카드를 다시 넣으면 이 데이터를 그대로 써요."));
                }
                else if (_dards.Values.FirstOrDefault(d => d.Package.Id == id) is { } runtime)
                {
                    var pkg = runtime.Package;
                    var stray = pkg.Internet == internet ? origins.Except(pkg.Origins, StringComparer.OrdinalIgnoreCase).ToList() : origins;
                    if (stray.Count == 0) continue;
                    string key = $"leftover:{env}:{id}";
                    list.Add(new DardIssue(key, DardIssueKind.Leftover, id, internet, stray, new[] { pkg.Path },
                        $"'{pkg.Name}' 카드의 예전 데이터가 남아 있어요",
                        $"{UsageText(key)}지금 카드는 다른 저장소를 써요. 저장소를 옮기다 실패했거나 옮기기 전 데이터예요."));
                }
            }
        }
        return list.Where(i => !_cfg.KeptDardData.Contains(i.Key)).ToList();
    }

    private string UsageText(string key) => _dardUsage.TryGetValue(key, out var bytes)
        ? bytes is { } b ? $"약 {FormatBytes(b)} · " : ""
        : "용량 확인 중 · ";

    private static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b}B",
        < 1024 * 1024 => $"{b / 1024.0:0.#}KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#}MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##}GB",
    };

    /// <summary>
    /// 확인할 것을 다시 계산한다. 목록이 바뀌면 설정 화면을 다시 그리고, 처음 보는 것이 있으면 한 번 알린다.
    /// 카드 파일을 아직 쓰는 중이라 못 읽었으면 잘못 판단하지 않게 다음 번으로 미룬다.
    /// </summary>
    private void CheckDardIssues()
    {
        if (_shuttingDown || _dardFilesBusy) return;
        var issues = DardIssues();
        foreach (var issue in issues.Where(i => i.Origins.Count > 0 && !_dardUsage.ContainsKey(i.Key)))
        {
            _dardUsage[issue.Key] = null; // 확인 중(다시 시작하지 않게)
            var i = issue;
            // 브라우저 객체는 UI 스레드에서만 다룬다. 어디서 불려도 이어지는 일이 UI 스레드로 돌아오게 Dispatcher에서 시작한다.
            _debounce.Dispatcher.BeginInvoke(() => MeasureDardUsage(i));
        }
        string signature = string.Join("|", issues.Select(i => i.Key + "=" + i.Detail));
        if (signature != _dardIssueSignature)
        {
            _dardIssueSignature = signature;
            RaiseChanged();
        }
        var fresh = issues.Where(i => _dardNotified.Add(i.Key)).ToList();
        if (fresh.Count == 0 || QuietDardIssues) return;
        _debounce.Dispatcher.BeginInvoke(() =>
        {
            if (_shuttingDown) return;
            var r = Dialogs.Show(string.Join("\n", fresh.Select(i => "· " + i.Title)) + "\n\n설정 › 카드 › 위젯 카드에서 지우거나 그대로 둘 수 있어요.",
                MessageBoxButton.OKCancel, heading: "카드 데이터에 확인할 것이 있어요", primary: "설정에서 보기");
            if (r == MessageBoxResult.OK) SettingsWindow.OpenWidgetCards(this);
        });
    }

    private async void MeasureDardUsage(DardIssue issue)
    {
        long total = 0;
        bool known = true;
        foreach (string origin in issue.Origins)
        {
            if (await DardStorage.UsageAsync(issue.Internet, origin) is { } bytes) total += bytes;
            else known = false;
        }
        _dardUsage[issue.Key] = known ? total : null;
        if (!_shuttingDown) CheckDardIssues();
    }

    /// <summary>확인할 것 하나를 처리한다. clear면 데이터를 지우고(되돌릴 수 없음), 아니면 그대로 두고 다시 알리지 않는다.</summary>
    public void ResolveDardIssue(DardIssue issue, bool clear)
    {
        if (!clear)
        {
            _cfg.KeptDardData.Add(issue.Key);
            _cfg.Save();
            CheckDardIssues();
            return;
        }
        switch (issue.Kind)
        {
            case DardIssueKind.Orphan:
                // 카드 파일이 없으니 이 카드의 기록(승인·위치·모양)도 같이 정리한다.
                _cfg.Dards.Remove(issue.Id);
                foreach (string key in _cfg.Positions.Keys.Concat(_cfg.Layouts.Keys).Where(k => k.StartsWith($"dard:{issue.Id}/", StringComparison.Ordinal)).ToList())
                {
                    _cfg.Positions.Remove(key);
                    _cfg.Layouts.Remove(key);
                }
                ForgetDardData(issue.Id);
                break;
            case DardIssueKind.Leftover:
                _debounce.Dispatcher.BeginInvoke(() => ClearDardStorage(issue.Internet, issue.Origins));
                break;
        }
        _dardUsage.Remove(issue.Key);
        _cfg.Save();
        CheckDardIssues();
    }
}

internal enum DardIssueKind { Orphan, Leftover, Duplicate }

/// <summary>
/// 카드 파일과 데이터가 맞지 않는 곳 하나.
/// Orphan = 카드 파일 없이 데이터만 남음, Leftover = 카드가 지금 쓰지 않는 예전 저장소가 남음, Duplicate = 같은 id의 카드 파일이 여럿.
/// Key는 "그대로 두기"를 기억하는 이름이고 언제나 ":id"로 끝난다.
/// </summary>
internal sealed record DardIssue(string Key, DardIssueKind Kind, string Id, bool Internet, IReadOnlyList<string> Origins,
    IReadOnlyList<string> Files, string Title, string Detail);
