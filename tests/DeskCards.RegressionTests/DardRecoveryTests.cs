using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Interop;
using DeskCards;
using Microsoft.Web.WebView2.Core;

internal static partial class Program
{
    private static DardPackage RecoveryPackage(string id, bool online = false, bool perCard = false) =>
        DardPackage.Parse(Dard(("manifest.json", JsonSerializer.Serialize(new
        {
            dard = 1, id, name = "recovery", version = "1", storage = perCard ? "card" : "shared",
            cards = new[] { new { id = "main", ratio = new[] { 1, 1 } }, new { id = "mini", ratio = new[] { 1, 1 } } },
            permissions = new { internet = online },
        })), ("card.html", "<p>recovery")), id + ".dard");

    private static void RecoveryTest(string name, Func<Task> body) => Test("storage recovery: " + name, () =>
    {
        var task = OnUi(body);
        WaitUntilLong(() => task.IsCompleted, 90000);
        if (!task.IsCompleted) throw new TimeoutException(name);
        task.GetAwaiter().GetResult();
    });

    private static void DardRecoveryTests(string root)
    {
        RecoveryTest("different package IDs cannot share a card origin", async () =>
        {
            var a = RecoveryPackage("com.test.vault", perCard: true);
            var b = RecoveryPackage("main.com.test.vault");
            Check(!a.Origins.Intersect(b.Origins).Any());
            await DardStorage.PrepareAsync(a);
            await DardStorage.PrepareAsync(b);
            using var first = await HiddenPage.Open(await DardStorage.Environment(false), a.Origins[0]);
            await first.Eval("localStorage.setItem('secret','private')");
            using var second = await HiddenPage.Open(await DardStorage.Environment(false), b.Origins[0]);
            Check(await second.Eval("localStorage.getItem('secret')") == "null");
            Check(DardStorage.Recorded(false)[a.Origins[0]] == a.Id);
        });

        RecoveryTest("interrupted completion and restored config cannot erase imported data", async () =>
        {
            var source = RecoveryPackage("com.test.resume");
            var target = RecoveryPackage(source.Id, online: true);
            await DardStorage.PrepareAsync(source);
            using (var page = await HiddenPage.Open(await DardStorage.Environment(false), source.Origins[0]))
                await page.Eval("localStorage.setItem('important','saved')");
            await DardStorage.MoveAsync(target, source.StorageLocation);
            // MoveAsync 끝~config 저장 사이에 종료된 경우: 재실행은 빈 원본을 가져오면 안 된다.
            await DardStorage.MoveAsync(target, source.StorageLocation);
            using (var page = await HiddenPage.Open(await DardStorage.Environment(true), target.Origins[0]))
                Check(await page.Eval("localStorage.getItem('important')") == "\"saved\"");
            DardStorage.AcknowledgeMove(source.Id);
            // 오래된 config 백업으로 복구된 경우도 완료 기록으로 보호한다.
            await DardStorage.MoveAsync(target, source.StorageLocation);
            using (var page = await HiddenPage.Open(await DardStorage.Environment(true), target.Origins[0]))
                Check(await page.Eval("localStorage.getItem('important')") == "\"saved\"");
            // 다음 업데이트는 반대 방향으로 정상 이동할 수 있다.
            await DardStorage.MoveAsync(source, target.StorageLocation);
            using var restored = await HiddenPage.Open(await DardStorage.Environment(false), source.Origins[0]);
            Check(await restored.Eval("localStorage.getItem('important')") == "\"saved\"");
            DardStorage.AcknowledgeMove(source.Id);
        });

        RecoveryTest("special keys and deleted autoIncrement keys survive migration", async () =>
        {
            var source = RecoveryPackage("com.test.keys");
            var target = RecoveryPackage(source.Id, online: true);
            await DardStorage.PrepareAsync(source);
            using (var page = await HiddenPage.Open(await DardStorage.Environment(false), source.Origins[0]))
                Check(await page.Async("""
                    localStorage.setItem('__proto__','keep-me');
                    const req=r=>new Promise((ok,no)=>{r.onsuccess=()=>ok(r.result);r.onerror=()=>no(r.error)});
                    const o=indexedDB.open('db',1);
                    o.onupgradeneeded=()=>{o.result.createObjectStore('raw',{autoIncrement:true});o.result.createObjectStore('nested',{keyPath:'id.n',autoIncrement:true});};
                    const db=await req(o), tx=db.transaction(['raw','nested'],'readwrite');
                    for(const name of ['raw','nested']) {
                      const s=tx.objectStore(name), value=JSON.parse('{"__proto__":{"kept":true},"id":{"n":1}}');
                      value.invalidDate=new Date(NaN);
                      if(name==='raw') {s.put(value,1);s.put('deleted',100);} else {s.put(value);s.put({id:{n:100}});}
                      s.delete(100);
                    }
                    await new Promise((ok,no)=>{tx.oncomplete=ok;tx.onabort=()=>no(tx.error)});db.close();return 'ready';
                    """) == "\"ready\"");
            Check(await DardStorage.MoveAsync(target, source.StorageLocation) == 0);
            using var moved = await HiddenPage.Open(await DardStorage.Environment(true), target.Origins[0]);
            string result = await moved.Async("""
                const req=r=>new Promise((ok,no)=>{r.onsuccess=()=>ok(r.result);r.onerror=()=>no(r.error)});
                const db=await req(indexedDB.open('db'));
                const raw=await req(db.transaction('raw').objectStore('raw').get(1));
                const n1=await req(db.transaction('raw','readwrite').objectStore('raw').add('next'));
                const n2=await req(db.transaction('nested','readwrite').objectStore('nested').add({}));
                db.close();return {ls:localStorage.getItem('__proto__'),own:Object.hasOwn(raw,'__proto__'),kept:raw.__proto__.kept,invalidDate:Number.isNaN(raw.invalidDate.getTime()),n1,n2};
                """);
            using var doc = JsonDocument.Parse(result);
            var o = doc.RootElement;
            Check(o.GetProperty("ls").GetString() == "keep-me" && o.GetProperty("own").GetBoolean() && o.GetProperty("kept").GetBoolean());
            Check(o.GetProperty("n1").GetInt32() == 101 && o.GetProperty("n2").GetInt32() == 101);
            Check(o.GetProperty("invalidDate").GetBoolean());
            DardStorage.AcknowledgeMove(source.Id);
        });

        RecoveryTest("old origins and the original unsplit profile are migrated", async () =>
        {
            var pkg = RecoveryPackage("com.test.oldorigin");
            using (var page = await HiddenPage.Open(await DardStorage.Environment(false), pkg.LegacyOriginFor("", false)))
                await page.Eval("localStorage.setItem('old','split-profile-value')");
            await DardStorage.MoveAsync(pkg, "offline/shared");
            using (var page = await HiddenPage.Open(await DardStorage.Environment(false), pkg.Origins[0]))
                Check(await page.Eval("localStorage.getItem('old')") == "\"split-profile-value\"");
            DardStorage.AcknowledgeMove(pkg.Id);

            pkg = RecoveryPackage("com.test.original");
            var legacy = (Task<CoreWebView2Environment>)typeof(DardStorage).GetMethod("EnvironmentAt", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { AppPaths.WebDataDir, false })!;
            using (var page = await HiddenPage.Open(await legacy, pkg.LegacyOriginFor("", false)))
                await page.Eval("localStorage.setItem('old','original-profile-value')");
            Check(DardStorage.FindLocation(pkg) == "legacy/shared");
            await DardStorage.MoveAsync(pkg, "legacy/shared");
            using (var page = await HiddenPage.Open(await DardStorage.Environment(false), pkg.Origins[0]))
                Check(await page.Eval("localStorage.getItem('old')") == "\"original-profile-value\"");
            DardStorage.AcknowledgeMove(pkg.Id);
        });

        RecoveryTest("browser environments follow isolated paths", async () =>
        {
            string original = AppPaths.WebDataDir;
            var first = await DardStorage.Environment(false);
            try
            {
                AppPaths.WebDataDir = Path.Combine(root, "separate-profile");
                var second = await DardStorage.Environment(false);
                Check(!ReferenceEquals(first, second) && second.UserDataFolder.StartsWith(AppPaths.WebDataDir, StringComparison.OrdinalIgnoreCase));
            }
            finally { AppPaths.WebDataDir = original; }
        });

        RecoveryTest("damaged storage index is preserved", async () =>
        {
            string original = AppPaths.WebDataDir;
            try
            {
                AppPaths.WebDataDir = Path.Combine(root, "damaged-index");
                Directory.CreateDirectory(Path.Combine(AppPaths.WebDataDir, "offline"));
                string index = Path.Combine(AppPaths.WebDataDir, "offline", "desk-origins.json");
                File.WriteAllText(index, "{broken");
                bool failed = false;
                try { await DardStorage.PrepareAsync(RecoveryPackage("com.test.corrupt")); }
                catch (JsonException) { failed = true; }
                Check(failed && File.ReadAllText(index) == "{broken");
            }
            finally { AppPaths.WebDataDir = original; }
        });

        RecoveryTest("deleting one duplicate preserves approval and data", async () =>
        {
            var pkg = RecoveryPackage("com.test.duplicate-delete");
            await DardStorage.PrepareAsync(pkg);
            using var page = await HiddenPage.Open(await DardStorage.Environment(false), pkg.Origins[0]);
            await page.Eval("localStorage.setItem('kept','yes')");
            string groups = Path.Combine(root, "duplicate-delete");
            var cfg = Config.Load(Path.Combine(root, "duplicate-delete.json"));
            cfg.Dards[pkg.Id] = new DardApproval { Hash = pkg.Hash, Allowed = true };
            cfg.DardStorage[pkg.Id] = pkg.StorageLocation;
            cfg.PositionsPx[$"dard:{pkg.Id}/main"] = new[] { 1.0, 2.0 };
            // 루트가 아직 없으면 Reconcile은 창을 만들지 않는다. 파일 정리 로직만 실제로 실행한다.
            var mgr = new GroupManager(groups, cfg);
            try
            {
                var ids = (System.Collections.Generic.Dictionary<string, string>)typeof(GroupManager)
                    .GetField("_dardIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mgr)!;
                string first = Path.Combine(groups, "first.dard"), second = Path.Combine(groups, "second.dard");
                ids[first] = pkg.Id; ids[second] = pkg.Id;
                typeof(GroupManager).GetMethod("RemoveDardFileState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(mgr, new object[] { first });
                Check(cfg.Dards.ContainsKey(pkg.Id) && cfg.PositionsPx.ContainsKey($"dard:{pkg.Id}/main"));
                Check(await page.Eval("localStorage.getItem('kept')") == "\"yes\"");
                // 재삽입된 카드에 이전에 생성한 orphan 경고를 적용해도 데이터를 지우지 않는다.
                var stale = new DardIssue("orphan:offline:" + pkg.Id, DardIssueKind.Orphan, pkg.Id, false,
                    pkg.Origins, Array.Empty<string>(), "old", "old");
                mgr.ResolveDardIssue(stale, clear: true);
                Check(cfg.Dards.ContainsKey(pkg.Id));
            }
            finally { mgr.Shutdown(); }
        });

        RecoveryTest("deletion removes migration journals and both browser stores", async () =>
        {
            var source = RecoveryPackage("com.test.delete-journal");
            var target = RecoveryPackage(source.Id, online: true);
            await DardStorage.PrepareAsync(source);
            using (var page = await HiddenPage.Open(await DardStorage.Environment(false), source.Origins[0]))
                await page.Eval("localStorage.setItem('removed','yes')");
            await DardStorage.MoveAsync(target, source.StorageLocation);
            await DardStorage.DeleteAsync(source.Id);
            Check(!DardStorage.Recorded(false).ContainsValue(source.Id) && !DardStorage.Recorded(true).ContainsValue(source.Id));
            using var cleared = await HiddenPage.Open(await DardStorage.Environment(true), target.Origins[0]);
            Check(await cleared.Eval("localStorage.getItem('removed')") == "null");
            // 원본 프로필은 다른 테스트가 만들었으므로 legacy/shared를 찾을 수 있지만, 삭제한 이전 기록은 없어야 한다.
            Check(DardStorage.FindLocation(source) != source.StorageLocation && DardStorage.FindLocation(source) != target.StorageLocation);
        });

        RecoveryTest("shared and per-card storage migrate within one browser", async () =>
        {
            var shared = RecoveryPackage("com.test.modes");
            var cards = RecoveryPackage(shared.Id, perCard: true);
            await DardStorage.PrepareAsync(shared);
            using (var page = await HiddenPage.Open(await DardStorage.Environment(false), shared.Origins[0]))
                await page.Eval("localStorage.setItem('mode','shared-value')");
            await DardStorage.MoveAsync(cards, shared.StorageLocation);
            DardStorage.AcknowledgeMove(shared.Id);
            using (var first = await HiddenPage.Open(await DardStorage.Environment(false), cards.Origins[0]))
            using (var second = await HiddenPage.Open(await DardStorage.Environment(false), cards.Origins[1]))
            {
                Check(await first.Eval("localStorage.getItem('mode')") == "\"shared-value\"");
                Check(await second.Eval("localStorage.getItem('mode')") == "\"shared-value\"");
                await second.Eval("localStorage.setItem('mode','second-card-value')");
            }
            await DardStorage.MoveAsync(shared, cards.StorageLocation);
            DardStorage.AcknowledgeMove(shared.Id);
            using (var page = await HiddenPage.Open(await DardStorage.Environment(false), shared.Origins[0]))
                Check(await page.Eval("localStorage.getItem('mode')") == "\"shared-value\"");
            // 공용으로 합칠 때 채택하지 않은 두 번째 카드 데이터는 경고 목록에서 정리할 때까지 보존한다.
            using (var second = await HiddenPage.Open(await DardStorage.Environment(false), cards.Origins[1]))
                Check(await second.Eval("localStorage.getItem('mode')") == "\"second-card-value\"");
            await DardStorage.DeleteAsync(shared.Id);
        });
    }

    /// <summary>화면·포커스를 건드리지 않고 실제 브라우저 저장소를 검사하는 페이지.</summary>
    private sealed class HiddenPage : IDisposable
    {
        private readonly HwndSource _host;
        private readonly CoreWebView2Controller _controller;
        public CoreWebView2 Core => _controller.CoreWebView2;
        private HiddenPage(HwndSource host, CoreWebView2Controller controller) { _host = host; _controller = controller; }
        public static async Task<HiddenPage> Open(CoreWebView2Environment env, string origin, string html = "<!doctype html><p>test", string headers = "", bool interceptAll = true)
        {
            var host = new HwndSource(new HwndSourceParameters("hidden storage test") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
            try
            {
                var controller = await env.CreateCoreWebView2ControllerAsync(host.Handle);
                controller.IsVisible = false;
                var page = new HiddenPage(host, controller);
                page.Core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                page.Core.WebResourceRequested += (_, e) =>
                {
                    if (interceptAll || e.Request.Uri.StartsWith(origin + "/", StringComparison.Ordinal))
                        e.Response = env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html)), 200, "OK",
                            "Content-Type: text/html; charset=utf-8\r\n" + headers);
                };
                var done = new TaskCompletionSource<bool>();
                page.Core.NavigationCompleted += (_, e) => done.TrySetResult(e.IsSuccess);
                page.Core.Navigate(origin + "/card.html");
                Check(await done.Task.WaitAsync(TimeSpan.FromSeconds(20)));
                return page;
            }
            catch { host.Dispose(); throw; }
        }
        public Task<string> Eval(string script) => Core.ExecuteScriptAsync(script);
        public async Task<string> Async(string script)
        {
            await Eval("window.__result=null;(async()=>{" + script + "})().then(v=>window.__result=JSON.stringify(v),e=>window.__result='ERROR '+e)");
            for (int i = 0; i < 600; i++)
            {
                string result = await Eval("window.__result");
                if (result != "null") return JsonSerializer.Deserialize<string>(result)!;
                await Task.Delay(50);
            }
            throw new TimeoutException("숨겨진 저장소 테스트가 응답하지 않아요.");
        }
        public void Dispose() { _controller.Close(); _host.Dispose(); }
    }
}
