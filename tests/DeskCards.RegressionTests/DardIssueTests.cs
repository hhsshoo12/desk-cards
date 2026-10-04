using System;
using System.IO;
using System.Linq;
using DeskCards;

internal static partial class Program
{
    /// <summary>이 테스트 카드의 경고만(다른 테스트가 남긴 데이터는 같은 브라우저 데이터 폴더에 있어 여기서는 "파일 없는 데이터"로 보인다).</summary>
    private static System.Collections.Generic.List<DardIssue> Mine(GroupManager mgr) => mgr.DardIssues().Where(i => i.Id == "com.test.issue").ToList();

    /// <summary>카드 파일과 저장된 데이터가 어긋날 때 경고가 뜨고, 지우기·그대로 두기가 동작하는지.</summary>
    private static void DardIssueTests(string root)
    {
        Test(".dard issues: data without a card file, leftovers, duplicate files", () =>
        {
            const string id = "com.test.issue";
            string manifest = ClockManifest.Replace("com.test.clock", id);
            string groups = Path.Combine(root, "dard-issues");
            Directory.CreateDirectory(Path.Combine(groups, "그룹"));
            string file = Path.Combine(groups, "issue.dard");
            File.WriteAllBytes(file, Dard(("manifest.json", manifest), ("card.html", "<p>i")));
            var pkg = DardPackage.Load(file);
            var cfg = Config.Load(Path.Combine(root, "dard-issues.json"));
            cfg.Dards[id] = new DardApproval { Hash = pkg.Hash, Allowed = true };
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                var web = WebOf(mgr.AllCards.OfType<DardWindow>().First());
                Eval(web, "localStorage.setItem('k', 'v'); navigator.storage.getDirectory().then(r => r.getFileHandle('f', { create: true }))" +
                    ".then(h => h.createWritable()).then(async w => { await w.write(new Uint8Array(2 * 1024 * 1024)); await w.close(); window.__w = 'ok'; })");
                Check(WaitFor(web, "__w") == "ok");
                Check(Mine(mgr).Count == 0);

                // 같은 id 파일이 하나 더: 하나만 띄우고 경고.
                string twin = Path.Combine(groups, "issue-copy.dard");
                File.Copy(file, twin);
                Pump(900);
                var dup = Mine(mgr).Single();
                Check(dup.Kind == DardIssueKind.Duplicate && dup.Files.Count == 2 && mgr.Dards.Count == 1);
                File.Delete(twin);
                Pump(900);
                Check(Mine(mgr).Count == 0);

                // 지금 쓰지 않는 다른 환경(online)에도 이 카드의 저장소가 있다고 기록돼 있으면 예전 데이터로 본다.
                var online = DardPackage.Parse(Dard(("manifest.json", manifest.Replace("\"version\": \"1.0.0\",", "\"version\": \"1.0.0\", \"permissions\": { \"internet\": true },")),
                    ("card.html", "")), "x.dard");
                var prep = OnUi(() => DardStorage.PrepareAsync(online));
                WaitUntil(() => prep.IsCompleted);
                mgr.Reconcile();
                var leftover = Mine(mgr).Single();
                Check(leftover.Kind == DardIssueKind.Leftover && leftover.Internet && leftover.Key == "leftover:online:" + id);
                mgr.ResolveDardIssue(leftover, clear: true);
                WaitUntilLong(() => !DardStorage.Recorded(true).ContainsValue(id), 10000);
                Check(!DardStorage.Recorded(true).ContainsValue(id) && Mine(mgr).Count == 0);

                // 탐색기에서 파일만 지우면 데이터만 남는다. 용량도 알려 준다.
                var dialogs = CloseDialogs(() =>
                {
                    GroupManager.QuietDardIssues = false;
                    try
                    {
                        File.Delete(file);
                        WaitUntilLong(() => Mine(mgr).Any(i => i.Detail.Contains("MB")), 15000);
                        WaitUntilLong(() => false, 500);
                    }
                    finally { GroupManager.QuietDardIssues = true; }
                });
                var orphan = Mine(mgr).Single();
                Console.WriteLine("  orphan: " + orphan.Title + " / " + orphan.Detail);
                Check(orphan.Kind == DardIssueKind.Orphan && orphan.Key == "orphan:offline:" + id && orphan.Detail.Contains("약 2"));
                Check(dialogs.Count == 1 && dialogs[0].Contains("확인할 것"));

                // 설정 › 위젯 카드 위쪽에 경고 줄이 보인다.
                SettingsWindow.OpenWidgetCards(mgr);
                Pump(600);
                var settings = System.Windows.Application.Current.Windows.OfType<SettingsWindow>().Single();
                var texts = Visuals<System.Windows.Controls.TextBlock>(settings).Select(t => t.Text).ToList();
                Check(texts.Any(t => t.StartsWith("확인할 것")) && texts.Contains(orphan.Title));
                if (Environment.GetEnvironmentVariable("DESKCARDS_SNAPSHOT_DIR") is { Length: > 0 } dir)
                    Snapshot(settings, Path.Combine(dir, "settings-dard-issues.png"));
                settings.Close();
                Pump(100);

                // 그대로 두기: 다시 알리지 않는다. 지우기: 데이터와 기록을 모두 지운다.
                mgr.ResolveDardIssue(orphan, clear: false);
                Check(Mine(mgr).Count == 0 && cfg.KeptDardData.Contains(orphan.Key));
                cfg.KeptDardData.Clear();
                mgr.ResolveDardIssue(orphan, clear: true);
                WaitUntilLong(() => !DardStorage.Recorded(false).ContainsValue(id), 10000);
                Check(!DardStorage.Recorded(false).ContainsValue(id) && !cfg.Dards.ContainsKey(id) && !cfg.DardStorage.ContainsKey(id));
                Check(!cfg.PositionsPx.Keys.Any(k => k.StartsWith($"dard:{id}/")) && Mine(mgr).Count == 0);
            }
            finally { mgr.Shutdown(); }
        });
    }
}
