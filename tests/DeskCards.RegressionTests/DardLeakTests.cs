using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeskCards;

internal static partial class Program
{
    /// <summary>
    /// 권한 없는 .dard가 밖으로 무언가 보낼 수 있는지 본다. 127.0.0.1에 TCP·UDP 수신기를 띄우고,
    /// 카드 페이지가 알려진 옆길(요청·이동·미리 연결·WebRTC 등)로 그 수신기에 닿으면 실패다.
    /// 수신기는 루프백이라 실제 바깥으로는 아무것도 나가지 않는다.
    /// </summary>
    private static void DardLeakTests(string root)
    {
        if (Environment.GetEnvironmentVariable("DESKCARDS_LNA") == "1") LnaProbe(root);
        Test(".dard without internet permission cannot reach the network", () =>
        {
            ProbeDeadEnd();
            var hits = RunLeakCard(root, "dard-leak", internet: false);
            foreach (var hit in hits) Console.WriteLine("  LEAK " + hit);
            Check(hits.Length == 0);
            // 나가려던 연결은 앱이 쥔 막다른 길(127.255.255.1~32)에서 끊겼다.
            var deadEnd = DardStorage.DeadEnd.Instance;
            Console.WriteLine($"  dead end {deadEnd.EndPoint}: {deadEnd.Hits} blocked");
            Check(deadEnd.EndPoint.Address.ToString().StartsWith("127.255.255.") && deadEnd.Hits > 0);
        });
        Test(".dard with internet permission still cannot reach this PC or the local network", () =>
        {
            // 같은 카드에 internet 권한만 준다. 수신기는 이 PC(루프백)라 내부망과 같이 막혀야 한다.
            int before = DardProxy.Instance.Blocked;
            var hits = RunLeakCard(root, "dard-online", internet: true);
            foreach (var hit in hits) Console.WriteLine("  LEAK " + hit);
            Console.WriteLine($"  proxy {DardProxy.Instance.EndPoint}: {DardProxy.Instance.Blocked - before} blocked");
            Check(hits.Length == 0 && DardProxy.Instance.Blocked > before);
        });
        Test("internet proxy lets only public addresses through", () =>
        {
            foreach (string ip in new[] { "127.0.0.1", "127.255.255.1", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.0.1", "169.254.1.1",
                "100.64.0.1", "0.0.0.0", "224.0.0.1", "255.255.255.255", "198.18.0.1", "::1", "::", "fe80::1", "fd00::1", "fc00::1",
                "::ffff:192.168.0.1", "::ffff:127.0.0.1", "64:ff9b::c0a8:1", "2002:c0a8:1::1", "2001:0:c0a8::1", "ff02::1" })
                Check(!DardProxy.IsPublicAddress(System.Net.IPAddress.Parse(ip)));
            foreach (string ip in new[] { "8.8.8.8", "1.1.1.1", "93.184.215.14", "172.32.0.1", "100.128.0.1", "::ffff:8.8.8.8", "2606:4700:4700::1111" })
                Check(DardProxy.IsPublicAddress(System.Net.IPAddress.Parse(ip)));
        });
        Test("internet proxy treats this PC's own network as local even for public IPv6", () =>
        {
            // 이 PC의 네트워크 어댑터 주소와 게이트웨이는 같은 네트워크라 막힌다. 공인 IPv6(2xxx:)를 받은 어댑터면 그 주소도.
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                            n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties()).Where(p => p.GatewayAddresses.Count > 0).ToList();
            foreach (var p in nics)
            {
                foreach (var a in p.UnicastAddresses.Select(u => u.Address).Where(a => !a.IsIPv6LinkLocal))
                {
                    Console.WriteLine($"  own {(a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4")}: on-link={DardProxy.IsOnLink(a)} public-address={DardProxy.IsPublicAddress(a)}");
                    Check(DardProxy.IsOnLink(a) && !DardProxy.IsPublic(a));
                }
            }
            Check(!DardProxy.IsOnLink(System.Net.IPAddress.Parse("8.8.8.8")) || nics.Count == 0);
            Check(DardProxy.IsPublic(System.Net.IPAddress.Parse("8.8.8.8")) || nics.Count == 0);
        });
        Test(".dard with internet permission reaches the internet", () =>
        {
            // 이 PC가 인터넷에 닿을 때만 본다.
            try
            {
                using var direct = new System.Net.Sockets.TcpClient();
                if (!direct.ConnectAsync("example.com", 443).Wait(5000)) throw new TimeoutException();
            }
            catch (Exception)
            {
                Console.WriteLine("  (skipped: no internet)");
                return;
            }
            string groups = Path.Combine(root, "dard-web");
            Directory.CreateDirectory(Path.Combine(groups, "그룹"));
            string file = Path.Combine(groups, "web.dard");
            File.WriteAllBytes(file, Dard(("manifest.json", LeakManifest.Replace("__PERMS__", """, "permissions": { "internet": true }""")),
                ("card.html", "<script>fetch('https://example.com/', { mode: 'no-cors' }).then(() => window.__net = 'ok', e => window.__net = String(e));</script>")));
            var pkg = DardPackage.Load(file);
            var cfg = Config.Load(Path.Combine(root, "dard-web.json"));
            cfg.Dards[pkg.Id] = new DardApproval { Hash = pkg.Hash, Allowed = true, Permissions = new() { "internet" } };
            var mgr = new GroupManager(groups, cfg);
            try
            {
                mgr.Start();
                string net = WaitFor(WebOf(mgr.AllCards.OfType<DardWindow>().Single()), "__net");
                Console.WriteLine("  fetch https://example.com: " + net);
                Check(net == "ok");
            }
            finally { mgr.Shutdown(); }
        });
    }

    /// <summary>권한 없는 환경에서 앱이 직접(CSP·가로채기 없이) 바깥 주소로 이동시켜 봐도 실패하고, 연결은 막다른 길로 가는지 본다.</summary>
    private static void ProbeDeadEnd()
    {
        var host = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("probe") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
        var envTask = DardStorage.Environment(false);
        WaitUntil(() => envTask.IsCompleted);
        var ctlTask = envTask.Result.CreateCoreWebView2ControllerAsync(host.Handle);
        WaitUntil(() => ctlTask.IsCompleted);
        var core = ctlTask.Result.CoreWebView2;
        foreach (string url in new[] { "http://example.com/", "https://example.com/", "http://127.0.0.1:1/", "http://192.168.0.1/" })
        {
            int before = DardStorage.DeadEnd.Instance.Hits;
            string? status = null;
            core.NavigationCompleted += Done;
            core.Navigate(url);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (status == null && sw.ElapsedMilliseconds < 15000) Pump(50);
            core.NavigationCompleted -= Done;
            int blocked = DardStorage.DeadEnd.Instance.Hits - before;
            Console.WriteLine($"  probe {url}: {status} dead-end +{blocked}");
            Check(status != null && status != "ok");
            if (!url.Contains("127.0.0.1")) Check(blocked > 0); // 루프백 주소는 이름 풀이 단계에서 이미 실패한다
            void Done(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) => status = e.IsSuccess ? "ok" : e.WebErrorStatus.ToString();
        }
        ctlTask.Result.Close();
        host.Dispose();
    }

    /// <summary>테스트 카드를 띄우고 수신기에 닿은 것을 돌려준다.</summary>
    private static string[] RunLeakCard(string root, string name, bool internet)
    {
        using var sink = new LeakSink();
        string html = LeakCard.Replace("__TCP__", sink.TcpPort.ToString()).Replace("__UDP__", sink.UdpPort.ToString())
            .Replace("__ONLY__", Environment.GetEnvironmentVariable("DESKCARDS_LEAK_ONLY") ?? ""); // 한 길만 시험할 때(진단용)
        string manifest = LeakManifest.Replace("__PERMS__", internet ? """, "permissions": { "internet": true }""" : "");
        string groups = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(groups, "그룹"));
        string file = Path.Combine(groups, "leak.dard");
        File.WriteAllBytes(file, Dard(("manifest.json", manifest), ("card.html", html)));
        var pkg = DardPackage.Load(file);
        Check(pkg.Internet == internet);
        var cfg = Config.Load(Path.Combine(root, name + ".json"));
        cfg.Dards["com.test.leak"] = new DardApproval { Hash = pkg.Hash, Allowed = true, Permissions = pkg.Permissions.Select(p => p.Key).ToList() };
        var mgr = new GroupManager(groups, cfg);
        try
        {
            mgr.Start();
            var card = mgr.AllCards.OfType<DardWindow>().Single();
            var web = Visuals<DardView>(card).Single().Children.OfType<Microsoft.Web.WebView2.Wpf.WebView2>().Single();
            WaitUntil(() => web.CoreWebView2 != null && web.Source?.Scheme == "https");
            string Report()
            {
                var t = web.ExecuteScriptAsync("JSON.stringify(window.__leak || null)");
                WaitUntil(() => t.IsCompleted);
                return t.GetAwaiter().GetResult();
            }
            WaitUntil(() => Report().Contains("done"));
            // WebRTC 후보 모으기와 미리 연결은 비동기라 조금 더 기다린다.
            Pump(6000);
            Console.WriteLine("  page: " + System.Text.Json.JsonSerializer.Deserialize<string>(Report()));
            return sink.Hits.ToArray();
        }
        finally { mgr.Shutdown(); }
    }

    private const string LeakManifest = """
        { "dard": 1, "id": "com.test.leak", "name": "leak", "version": "1.0.0", "cards": [ { "id": "main", "ratio": [1, 1] } ]__PERMS__ }
        """;

    /// <summary>경로 이름이 곧 어느 길로 샜는지다(수신기 기록에 그대로 찍힌다).</summary>
    private const string LeakCard = """
        <!doctype html><body><script>
        const P = __TCP__, U = __UDP__, base = 'http://127.0.0.1:' + P, r = window.__leak = {};
        const note = (k, v) => { r[k] = String(v); };
        const only = '__ONLY__';
        const tryIt = (k, f) => { if (only && !k.startsWith(only)) return; try { const v = f(); note(k, v === undefined ? 'called' : v); } catch (e) { note(k, 'threw ' + e.name); } };
        tryIt('fetch', () => { fetch(base + '/fetch').then(() => note('fetch', 'ok'), e => note('fetch', 'rejected')); });
        tryIt('xhr', () => { const x = new XMLHttpRequest(); x.open('GET', base + '/xhr'); x.send(); });
        tryIt('beacon', () => navigator.sendBeacon(base + '/beacon', 'x'));
        tryIt('websocket', () => { new WebSocket('ws://127.0.0.1:' + P + '/ws'); });
        tryIt('eventsource', () => { new EventSource(base + '/sse'); });
        tryIt('img', () => { new Image().src = base + '/img'; });
        for (const rel of ['preconnect', 'dns-prefetch', 'prefetch', 'preload', 'modulepreload', 'stylesheet', 'icon'])
          tryIt('link-' + rel, () => { const l = document.createElement('link'); l.rel = rel; l.href = base + '/link-' + rel; if (rel === 'preload') l.as = 'image'; document.head.appendChild(l); });
        tryIt('css', () => { const s = document.createElement('style'); s.textContent = `@import url(${base}/css-import); @font-face{font-family:x;src:url(${base}/css-font)} body{background:url(${base}/css-bg);font-family:x}`; document.head.appendChild(s); });
        tryIt('script-src', () => { const s = document.createElement('script'); s.src = base + '/script'; document.head.appendChild(s); });
        tryIt('worker', () => { new Worker(URL.createObjectURL(new Blob([`fetch('${base}/worker')`], { type: 'text/javascript' }))); });
        tryIt('sw', () => navigator.serviceWorker ? navigator.serviceWorker.register('/sw.js').then(() => note('sw', 'ok'), e => note('sw', 'rejected')) && 'pending' : 'none');
        tryIt('rtc-main', () => typeof window.RTCPeerConnection);
        const rtc = (name, PC) => {
          if (typeof PC !== 'function') return 'unavailable';
          const pc = new PC({ iceServers: [
            { urls: 'stun:127.0.0.1:' + U },
            { urls: 'turn:127.0.0.1:' + U + '?transport=udp', username: 'u', credential: 'p' },
            { urls: 'turn:127.0.0.1:' + P + '?transport=tcp', username: 'u', credential: 'p' } ] });
          pc.createDataChannel('x');
          let n = 0;
          pc.onicecandidate = e => { if (e.candidate) note(name + '-cand' + (n++), e.candidate.candidate.split(' ').slice(4, 8).join(' ')); };
          pc.onicecandidateerror = e => note(name + '-err', e.errorCode + ' ' + e.url);
          pc.createOffer().then(o => pc.setLocalDescription(o));
          return 'created';
        };
        tryIt('frame', () => {
          const f = document.createElement('iframe');
          document.body.appendChild(f);
          const w = f.contentWindow;
          note('frame-desk', typeof (w && w.desk));
          tryIt('frame-fetch', () => { w.fetch(base + '/frame-fetch').catch(() => {}); });
          tryIt('frame-rtc', () => rtc('frame-rtc', w && w.RTCPeerConnection));
          tryIt('frame-wasm', () => typeof (w && w.WebAssembly));
          return 'appended';
        });
        tryIt('proto-rtc', () => {
          // 지운 생성자를 다른 길로 되찾을 수 있는지(예: 남아 있는 RTC 객체의 constructor).
          const c = window.RTCSessionDescription && Object.getPrototypeOf(window.RTCSessionDescription.prototype);
          return typeof c;
        });
        tryIt('frame-src', () => { const f = document.createElement('iframe'); f.src = base + '/frame-src'; document.body.appendChild(f); });
        tryIt('object', () => { const o = document.createElement('object'); o.data = base + '/object'; document.body.appendChild(o); });
        tryIt('audio', () => { const a = new Audio(); a.src = base + '/audio'; a.load(); });
        tryIt('window-open', () => String(window.open(base + '/open')));
        tryIt('anchor-ping', () => { const a = document.createElement('a'); a.href = '#x'; a.ping = base + '/ping'; document.body.appendChild(a); a.click(); });
        setTimeout(() => {
          tryIt('form', () => { const f = document.createElement('form'); f.method = 'post'; f.action = base + '/form'; document.body.appendChild(f); f.submit(); });
          tryIt('meta-refresh', () => { const m = document.createElement('meta'); m.httpEquiv = 'refresh'; m.content = '0;url=' + base + '/meta'; document.head.appendChild(m); });
          tryIt('location', () => { location.href = base + '/location'; });
          note('done', 1);
        }, 500);
        </script>
        """;

    /// <summary>루프백 TCP·UDP 수신기. 연결이나 패킷이 오면 무엇이 왔는지 적는다.</summary>
    private sealed class LeakSink : IDisposable
    {
        private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        public ConcurrentQueue<string> Hits { get; } = new();

        public LeakSink()
        {
            _tcp.Start();
            _ = Task.Run(AcceptLoop);
            _ = Task.Run(ReceiveLoop);
        }

        public int TcpPort => ((IPEndPoint)_tcp.LocalEndpoint).Port;
        public int UdpPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _tcp.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var buf = new byte[512];
                        int n = 0;
                        try
                        {
                            using var timeout = new CancellationTokenSource(2000);
                            n = await client.GetStream().ReadAsync(buf, timeout.Token);
                        }
                        catch { }
                        string head = Encoding.ASCII.GetString(buf, 0, n).Split('\r', '\n')[0];
                        Hits.Enqueue("tcp " + (head.Length > 0 && head.All(c => c >= ' ' && c < 127) ? head : $"{n} bytes"));
                    }
                });
            }
        }

        private async Task ReceiveLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var r = await _udp.ReceiveAsync(_stop.Token);
                    Hits.Enqueue($"udp {r.Buffer.Length} bytes");
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _tcp.Stop();
            _udp.Dispose();
        }
    }
}
