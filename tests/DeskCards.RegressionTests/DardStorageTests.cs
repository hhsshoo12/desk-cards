using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DeskCards;

internal static partial class Program
{
    /// <summary>카드 창(또는 설정 창) 안의 웹 화면. 문서가 다 뜰 때까지 기다린다.</summary>
    private static Microsoft.Web.WebView2.Wpf.WebView2 WebOf(Window window)
    {
        var web = Visuals<DardView>(window).Single().Children.OfType<Microsoft.Web.WebView2.Wpf.WebView2>().Single();
        WaitUntil(() => web.CoreWebView2 != null && web.Source?.Scheme == "https");
        WaitUntil(() => Eval(web, "document.readyState", wait: false) == "\"complete\"");
        return web;
    }

    /// <summary>페이지에서 식 하나를 계산해 JSON 글자로 돌려준다.</summary>
    private static string Eval(Microsoft.Web.WebView2.Wpf.WebView2 web, string script, bool wait = true)
    {
        var t = web.ExecuteScriptAsync(script);
        if (wait) WaitUntil(() => t.IsCompleted);
        else { var sw = System.Diagnostics.Stopwatch.StartNew(); while (!t.IsCompleted && sw.ElapsedMilliseconds < 10000) Pump(20); }
        return t.IsCompleted ? t.GetAwaiter().GetResult() : "";
    }

    /// <summary>페이지가 window[name]에 글자를 넣을 때까지 기다린다(비동기 스크립트 결과).</summary>
    private static string WaitFor(Microsoft.Web.WebView2.Wpf.WebView2 web, string name, int ms = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string v = "null";
        while (sw.ElapsedMilliseconds < ms && (v = Eval(web, $"window.{name} ?? null")) == "null") Pump(100);
        return v == "null" ? "" : JsonSerializer.Deserialize<string>(v) ?? "";
    }

    /// <summary>뜨는 확인 창을 바로 닫고 내용을 모은다(모달 창이 테스트를 멈추지 않게).</summary>
    private static List<string> CloseDialogs(Action body)
    {
        var seen = new List<string>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            foreach (var w in Application.Current.Windows.OfType<DialogWindow>().ToList())
            {
                seen.Add(string.Join(" / ", Visuals<System.Windows.Controls.TextBlock>(w).Select(t => t.Text)));
                w.Close();
            }
        };
        timer.Start();
        try { body(); }
        finally { timer.Stop(); }
        return seen;
    }

    private static void DardStorageTests(string root)
    {
        Test(".dard storage: quota, per-card origins and clearing", () =>
        {
            string groups = Path.Combine(root, "dard-storage");
            Directory.CreateDirectory(Path.Combine(groups, "그룹"));
            string file = Path.Combine(groups, "s.dard");
            string manifest = ClockManifest.Replace("com.test.clock", "com.test.store").Replace("\"version\": \"1.0.0\",",
                "\"version\": \"1.0.0\", \"storage\": \"card\", \"permissions\": { \"storage.large\": true },");
            File.WriteAllBytes(file, Dard(("manifest.json", manifest), ("card.html", "<p>s")));
            var pkg = DardPackage.Load(file);
            var cfg = Config.Load(Path.Combine(root, "dard-storage.json"));
            cfg.Dards[pkg.Id] = new DardApproval { Hash = pkg.Hash, Allowed = true, Permissions = pkg.Permissions.Select(p => p.Key).ToList() };
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                var main = WebOf(mgr.AllCards.OfType<DardWindow>().Single(c => c.Info.Id == "main"));
                var mini = WebOf(mgr.AllCards.OfType<DardWindow>().Single(c => c.Info.Id == "mini"));
                Check(Eval(main, "location.host") == JsonSerializer.Serialize(pkg.HostFor("main")));
                // 한도는 실제로 써 봐서 확인한다(estimate()의 quota는 Chromium이 지문 방지로 고정값을 보여 준다).
                // storage.large 카드는 기본 한도(128MB)를 넘겨 쓸 수 있다.
                Check(WriteOpfs(main, 140) == "wrote");
                Eval(main, "localStorage.setItem('k', 'main')");
                // 카드마다 저장소가 따로다.
                Check(Eval(mini, "localStorage.getItem('k')") == "null");

                bool cleared = false;
                OnUi(() => DardStorage.ClearAsync(false, pkg.Origins)).ContinueWith(_ => cleared = true);
                WaitUntil(() => cleared);
                Eval(main, "location.reload()");
                Pump(500);
                main = WebOf(mgr.AllCards.OfType<DardWindow>().Single(c => c.Info.Id == "main"));
                Check(Eval(main, "localStorage.getItem('k')") == "null");
            }
            finally { mgr.Shutdown(); }
        });

        Test(".dard storage moves when an update adds internet permission", () =>
        {
            string groups = Path.Combine(root, "dard-move");
            Directory.CreateDirectory(Path.Combine(groups, "그룹"));
            string file = Path.Combine(groups, "m.dard");
            string v1 = """{ "dard": 1, "id": "com.test.move", "name": "move", "version": "1.0.0", "cards": [ { "id": "main", "ratio": [1, 1] } ] }""";
            string v2 = """{ "dard": 1, "id": "com.test.move", "name": "move", "version": "2.0.0", "cards": [ { "id": "main", "ratio": [1, 1] } ], "permissions": { "internet": true } }""";
            File.WriteAllBytes(file, Dard(("manifest.json", v1), ("card.html", MoveWriter)));
            var cfg = Config.Load(Path.Combine(root, "dard-move.json"));
            // 앞으로 붙을 internet까지 승인해 둔다(권한이 늘었다고 다시 묻지 않게).
            cfg.Dards["com.test.move"] = new DardApproval { Hash = DardPackage.Load(file).Hash, Allowed = true, Permissions = new() { "internet" } };
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                var web = WebOf(mgr.AllCards.OfType<DardWindow>().Single());
                string ready = WaitFor(web, "__ready");
                Console.WriteLine("  v1: " + ready);
                Check(ready.StartsWith("ok"));
                Check(cfg.DardStorage["com.test.move"] == "offline/v2/shared");

                // 지난번에 옮기다 앱이 꺼진 것처럼 표시를 남겨 둔다. 이번에 이어서 옮기고 그렇다고 알려야 한다.
                cfg.DardMoving["com.test.move"] = "online/v2/shared";
                var dialogs = CloseDialogs(() =>
                {
                    File.WriteAllBytes(file, Dard(("manifest.json", v2), ("card.html", MoveReader)));
                    WaitUntilLong(() => cfg.DardStorage["com.test.move"] == "online/v2/shared" && mgr.AllCards.OfType<DardWindow>().Any(), 60000);
                    web = WebOf(mgr.AllCards.OfType<DardWindow>().Single());
                    string got = WaitFor(web, "__got");
                    Console.WriteLine("  v2: " + got);
                    using var doc = JsonDocument.Parse(got);
                    var o = doc.RootElement;
                    Check(o.GetProperty("ls").GetString() == "v" && o.GetProperty("ver").GetInt32() == 3);
                    Check(o.GetProperty("count").GetInt32() == 451 && o.GetProperty("idx").GetInt32() == 450 && o.GetProperty("raw").GetString() == "plain");
                    Check(!o.TryGetProperty("crypto", out _));
                    Check(o.GetProperty("date").GetInt32() == 5 && o.GetProperty("blob").GetString() == "hello" && o.GetProperty("blobType").GetString() == "text/plain");
                    Check(o.GetProperty("map").GetString() == "one" && o.GetProperty("u").GetRawText() == "[1,2,3]" && o.GetProperty("nan").GetBoolean() && o.GetProperty("set").GetBoolean());
                    Check(o.GetProperty("next").GetInt32() == 452);
                    Check(o.GetProperty("f").GetString() == "opfs-data" && o.GetProperty("empty").GetString() == "directory");
                    Check(o.GetProperty("bigSize").GetInt64() == 9L * 1024 * 1024 && o.GetProperty("bigLast").GetInt32() == 7);
                    // 기본 한도(128MB)는 옮긴 뒤 새 쪽에서도 걸려 있다.
                    Check(WriteOpfs(web, 140).Contains("QuotaExceededError"));
                    WaitUntilLong(() => false, 1500); // 옮긴 뒤 알림이 뜰 시간
                });
                foreach (var d in dialogs) Console.WriteLine("  dialog: " + d);
                // 이어서 옮겼다고, 그리고 CryptoKey 레코드 하나는 옮길 수 없어서 빠졌다고 알린다.
                Check(dialogs.Count == 1 && dialogs[0].Contains("이어서") && dialogs[0].Contains("1개"));
                Check(!cfg.DardMoving.ContainsKey("com.test.move") && !mgr.DardIssues().Any(i => i.Id == "com.test.move"));
            }
            finally { mgr.Shutdown(); }
        });
    }

    /// <summary>
    /// 브라우저 객체를 다루는 비동기 작업을 Dispatcher 안에서 시작한다. 콘솔 본문에는 WPF 동기화 컨텍스트가 없어서,
    /// 그냥 부르면 await 뒤가 다른 스레드에서 이어져 브라우저 객체를 잘못된 스레드에서 건드린다.
    /// </summary>
    private static System.Threading.Tasks.Task OnUi(Func<System.Threading.Tasks.Task> work) =>
        System.Threading.Tasks.TaskExtensions.Unwrap(Dispatcher.CurrentDispatcher.InvokeAsync(work).Task);

    /// <summary>OPFS에 mb만큼 써 본다. "wrote" 또는 오류 글자.</summary>
    private static string WriteOpfs(Microsoft.Web.WebView2.Wpf.WebView2 web, int mb)
    {
        Eval(web, "window.__w = null; navigator.storage.getDirectory().then(r => r.getFileHandle('quota-' + Math.random(), { create: true })).then(h => h.createWritable())" +
            $".then(async w => {{ for (let i = 0; i < {mb}; i++) await w.write(new Uint8Array(1024 * 1024)); await w.close(); window.__w = 'wrote'; }}).catch(e => window.__w = String(e))");
        return WaitFor(web, "__w", 60000);
    }

    private static void WaitUntilLong(Func<bool> condition, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < ms) Pump(100);
    }

    /// <summary>v1 카드: 여러 가지 값을 저장소에 써 둔다.</summary>
    private const string MoveWriter = """
        <!doctype html><script>
        (async () => { try {
          const req = r => new Promise((ok, no) => { r.onsuccess = () => ok(r.result); r.onerror = () => no(r.error); });
          localStorage.setItem('k', 'v');
          const open = indexedDB.open('db', 3);
          open.onupgradeneeded = () => {
            const s = open.result.createObjectStore('items', { keyPath: 'id', autoIncrement: true });
            s.createIndex('by', 'tag', { unique: false });
            open.result.createObjectStore('raw');
          };
          const db = await req(open);
          const key = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 128 }, false, ['encrypt']);
          const tx = db.transaction(['items', 'raw'], 'readwrite');
          const items = tx.objectStore('items');
          items.put({ tag: 'a', d: new Date(5), b: new Blob(['hello'], { type: 'text/plain' }), m: new Map([[1, 'one']]), u: new Uint8Array([1, 2, 3]), n: NaN, s: new Set(['x']) });
          for (let i = 0; i < 450; i++) items.put({ tag: 'bulk', i });
          tx.objectStore('raw').put('plain', [1, 'x']);
          tx.objectStore('raw').put(key, 'crypto');
          await new Promise((ok, no) => { tx.oncomplete = ok; tx.onerror = () => no(tx.error); });
          db.close();
          const root = await navigator.storage.getDirectory();
          const d = await root.getDirectoryHandle('d', { create: true });
          await d.getDirectoryHandle('empty', { create: true });
          let w = await (await d.getFileHandle('f.txt', { create: true })).createWritable(); await w.write('opfs-data'); await w.close();
          const big = new Uint8Array(9 * 1024 * 1024); big[big.length - 1] = 7;
          w = await (await root.getFileHandle('big.bin', { create: true })).createWritable(); await w.write(big); await w.close();
          window.__ready = 'ok';
        } catch (e) { window.__ready = 'err ' + e; } })();
        </script>
        """;

    /// <summary>v2 카드(internet 권한, 다른 브라우저 데이터 폴더): 옮겨진 값을 읽어 본다.</summary>
    private const string MoveReader = """
        <!doctype html><script>
        (async () => { try {
          const req = r => new Promise((ok, no) => { r.onsuccess = () => ok(r.result); r.onerror = () => no(r.error); });
          const out = {};
          out.ls = localStorage.getItem('k');
          const db = await req(indexedDB.open('db'));
          out.ver = db.version;
          const tx = db.transaction(['items', 'raw']);
          const items = tx.objectStore('items');
          const first = await req(items.get(1));
          out.count = await req(items.count());
          out.idx = await req(items.index('by').count('bulk'));
          out.raw = await req(tx.objectStore('raw').get([1, 'x']));
          out.crypto = await req(tx.objectStore('raw').get('crypto'));
          out.date = first.d instanceof Date ? first.d.getTime() : null;
          out.blob = await first.b.text(); out.blobType = first.b.type;
          out.map = first.m.get(1); out.u = Array.from(first.u); out.nan = Number.isNaN(first.n); out.set = first.s.has('x');
          out.next = await req(db.transaction('items', 'readwrite').objectStore('items').add({ tag: 'new' }));
          const root = await navigator.storage.getDirectory();
          const d = await root.getDirectoryHandle('d');
          out.f = await (await (await d.getFileHandle('f.txt')).getFile()).text();
          out.empty = (await d.getDirectoryHandle('empty')).kind;
          const big = await (await root.getFileHandle('big.bin')).getFile();
          out.bigSize = big.size; out.bigLast = new Uint8Array(await big.slice(big.size - 1).arrayBuffer())[0];
          window.__got = JSON.stringify(out);
        } catch (e) { window.__got = 'err ' + e; } })();
        </script>
        """;
}
