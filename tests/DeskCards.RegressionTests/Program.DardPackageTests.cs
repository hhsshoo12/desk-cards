using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeskCards;

internal static partial class Program
{
    // ----- .dard 카드: 임시 루트와 임시 브라우저 데이터만 쓴다 -----

    private const string ClockManifest = """
        { "dard": 1, "id": "com.test.clock", "name": "시계", "version": "1.0.0",
          "cards": [ { "id": "main", "ratio": [2, 1] }, { "id": "mini", "name": "작은 시계", "ratio": [1, 1] } ] }
        """;

    private static byte[] Dard(params (string Name, string Text)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in files)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open())) w.Write(text);
        return ms.ToArray();
    }

    private static void DardTests(string root, Application app)
    {
        Test(".dard manifest is read with cards, ratios and default settings ratio", () =>
        {
            var pkg = DardPackage.Parse(Dard(("manifest.json", ClockManifest), ("card.html", "<p>hi"), ("settings.html", "<p>s")), "x.dard");
            Check(pkg.Id == "com.test.clock" && pkg.Cards.Count == 2 && pkg.Cards[1].Name == "작은 시계" && pkg.Cards[0].Name == "시계");
            Check(pkg.Cards[0].RatioW == 2 && pkg.SettingsRatio == (3, 4) && pkg.Host.EndsWith(".s--v2.card.desk", StringComparison.Ordinal) && pkg.Permissions.Count == 0);
            var size = DardPackage.SizeFor(2, 1, DardPackage.CardArea);
            Check(Math.Abs(size.Width * size.Height - DardPackage.CardArea) < 0.01 && Math.Abs(size.Width / size.Height - 2) < 0.001);
        });
        Test(".dard rejects extra files, bad ratios, unknown permissions and zip bombs", () =>
        {
            bool Rejected(byte[] bytes)
            {
                try { DardPackage.Parse(bytes, "x.dard"); return false; }
                catch (DardException) { return true; }
            }
            Check(Rejected(Dard(("manifest.json", ClockManifest), ("card.html", ""), ("run.exe", "MZ"))));
            Check(Rejected(Dard(("manifest.json", ClockManifest), ("card.html", ""), ("sub/card.html", ""))));
            Check(Rejected(Dard(("manifest.json", ClockManifest.Replace("[2, 1]", "[9, 1]")), ("card.html", ""))));
            Check(Rejected(Dard(("manifest.json", ClockManifest.Replace("\"version\": \"1.0.0\",", "\"version\": \"1.0.0\", \"permissions\": { \"shell\": true },")), ("card.html", ""))));
            Check(Rejected(Dard(("manifest.json", ClockManifest.Replace("com.test.clock", "Com.Test")), ("card.html", ""))));
            Check(Rejected(Dard(("manifest.json", ClockManifest))));
            Check(Rejected(Dard(("manifest.json", ClockManifest), ("card.html", new string('a', 33 * 1024 * 1024)))));
            Check(Rejected(new byte[] { 1, 2, 3 }));
            var perms = DardPackage.Parse(Dard(("manifest.json", ClockManifest.Replace("\"version\": \"1.0.0\",",
                "\"version\": \"1.0.0\", \"permissions\": { \"system\": [\"memory\", \"cpu\"], \"internet\": true },")), ("card.html", "")), "x.dard");
            Check(perms.Permissions.Select(p => p.Key).SequenceEqual(new[] { "system:cpu", "system:memory", "internet" }) && perms.Internet);
            Check(perms.PermissionLines.Select(p => p.Label).SequenceEqual(new[] { "시스템 상태 읽기 (cpu, memory)", "인터넷과 통신 (내부망 제외)" }));
            Check(perms.Quota == DardPackage.DefaultQuota && perms.StorageLocation == "online/v2/shared" && perms.Origins.SequenceEqual(new[] { "https://" + perms.Host }));
            var large = DardPackage.Parse(Dard(("manifest.json", ClockManifest.Replace("\"version\": \"1.0.0\",",
                "\"version\": \"1.0.0\", \"storage\": \"card\", \"permissions\": { \"storage.large\": true },")), ("card.html", "")), "x.dard");
            Check(large.Quota == DardPackage.LargeQuota && large.StorageLocation == "offline/v2/card" && large.HostFor("mini") != large.HostFor("main"));
            Check(large.Origins.SequenceEqual(new[] { "https://" + large.HostFor("main"), "https://" + large.HostFor("mini") }));
            Check(large.OwnsHost(large.HostFor("mini")) && !large.OwnsHost("evil" + large.Host));
            Check(Rejected(Dard(("manifest.json", ClockManifest.Replace("\"version\": \"1.0.0\",", "\"version\": \"1.0.0\", \"storage\": \"window\",")), ("card.html", ""))));
            // internet은 있다/없다만 받는다(예전의 도메인 목록은 거부).
            Check(Rejected(Dard(("manifest.json", ClockManifest.Replace("\"version\": \"1.0.0\",",
                "\"version\": \"1.0.0\", \"permissions\": { \"internet\": [\"api.example.com\"] },")), ("card.html", ""))));
        });
        Test("approved .dard cards load, follow file changes and unload when removed", () =>
        {
            string groups = Path.Combine(root, "dard-groups");
            Directory.CreateDirectory(Path.Combine(groups, "그룹"));
            string file = Path.Combine(groups, "시계.dard");
            File.WriteAllBytes(file, Dard(("manifest.json", ClockManifest), ("card.html", "<p>1"), ("settings.html", "<p>s")));
            var cfg = Config.Load(Path.Combine(root, "dard.json"));
            cfg.Dards["com.test.clock"] = new DardApproval { Hash = DardPackage.Load(file).Hash, Allowed = true };
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                var cards = mgr.AllCards.OfType<DardWindow>().ToList();
                Check(mgr.Dards.Count == 1 && cards.Count == 2 && mgr.Cards.Count == 1);
                Check(cards.Select(c => c.Key).OrderBy(k => k).SequenceEqual(new[] { "dard:com.test.clock/main", "dard:com.test.clock/mini" }));
                Check(cfg.PositionsPx.ContainsKey("dard:com.test.clock/main"));
                var main = cards.Single(c => c.Info.Id == "main");
                Check(Math.Abs(main.Width / main.Height - 2) < 0.1); // 둥근 창은 여백·이름 줄 없이 비율 그대로
                Test(".dard 카드도 공통 아크릴 틀을 쓰고 활성화는 허용한다", () => CheckDwmCard(main, activatable: true));
                Test(".dard 웹 화면은 편집 중 캡처로 바뀌고 끝나면 같은 확대 비율로 돌아온다", () => CheckDardEditing(main));

                Check(Config.Load(Path.Combine(root, "dard.json")).DardStorage["com.test.clock"] == "offline/v2/shared");
                Check(Eval(WebOf(main), "localStorage.setItem('clock', 'h12'); location.host") == System.Text.Json.JsonSerializer.Serialize(main.Runtime.Package.Host));

                main.Runtime.OpenSettings("main");
                // 설정 창은 포커스를 잃으면 스스로 닫힌다. 테스트 중에 다른 창을 쓰면 여기서 실패할 수 있다.
                Pump(100);
                Check(app.Windows.OfType<DardSettingsWindow>().Count() == 1);
                // 설정 화면은 카드와 같은 주소라 같은 저장소를 본다(카드 설정은 localStorage로).
                Check(Eval(WebOf(app.Windows.OfType<DardSettingsWindow>().Single()), "localStorage.getItem('clock')") == "\"h12\"");

                // 승인한 뒤 파일이 바뀌면 권한이 같아도 다시 묻고, 답하기 전에는 띄우지 않는다(설정 창도 닫힌다).
                File.WriteAllBytes(file, Dard(("manifest.json", ClockManifest.Replace("\"version\": \"1.0.0\",",
                    "\"version\": \"1.0.0\", \"permissions\": { \"internet\": true },")), ("card.html", "<p>2"), ("settings.html", "<p>s")));
                var asked = CloseDialogs(() => Pump(1500)); // 닫으면 [계속 사용]을 누르지 않은 것과 같다.
                Check(asked.Any(t => t.Contains("카드 파일이 바뀌었어요") && t.Contains("새로 요구하는 권한: 인터넷과 통신")));
                Check(!mgr.AllCards.OfType<DardWindow>().Any() && !app.Windows.OfType<DardSettingsWindow>().Any());
                Check(cfg.Dards["com.test.clock"] is { Allowed: false } changed && changed.Hash == DardPackage.Load(file).Hash);

                File.Delete(file);
                Pump(900);
                Check(mgr.Dards.Count == 0 && !mgr.AllCards.OfType<DardWindow>().Any() && mgr.Cards.Count == 1);
            }
            finally { mgr.Shutdown(); }
            Check(!app.Windows.OfType<DardWindow>().Any());
        });
        Test("declined .dard is not loaded", () =>
        {
            string groups = Path.Combine(root, "dard-declined");
            Directory.CreateDirectory(Path.Combine(groups, "그룹"));
            string file = Path.Combine(groups, "시계.dard");
            File.WriteAllBytes(file, Dard(("manifest.json", ClockManifest), ("card.html", "<p>1")));
            var cfg = Config.Load(Path.Combine(root, "dard-declined.json"));
            cfg.Dards["com.test.clock"] = new DardApproval { Hash = DardPackage.Load(file).Hash, Allowed = false };
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                Check(mgr.Dards.Count == 0 && !mgr.AllCards.OfType<DardWindow>().Any());
            }
            finally { mgr.Shutdown(); }
        });
        DardLeakTests(root);
        DardStorageTests(root);
        DardIssueTests(root);
        DardRecoveryTests(root);
    }
}
