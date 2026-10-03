using System;
using System.IO;
using System.Linq;
using Microsoft.Web.WebView2.Core;

internal static partial class Program
{
    /// <summary>
    /// 진단용(DESKCARDS_LNA=1): 프록시 없는 WebView2에서 Chromium의 로컬 네트워크 접근 차단이 카드 페이지에 걸리는지 본다.
    /// 앱이 가로채서 준 페이지(https://probe.card.desk)가 127.0.0.1 수신기에 닿는지, 권한 요청이 오는지 기록한다.
    /// </summary>
    private static void LnaProbe(string root)
    {
        foreach (string args in new[] { "", "--enable-features=LocalNetworkAccessChecks" })
        {
            using var sink = new LeakSink();
            var host = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("lna") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
            var envTask = CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "lna-" + Guid.NewGuid().ToString("N")), new CoreWebView2EnvironmentOptions(args));
            WaitUntil(() => envTask.IsCompleted);
            var ctlTask = envTask.Result.CreateCoreWebView2ControllerAsync(host.Handle);
            WaitUntil(() => ctlTask.IsCompleted);
            var core = ctlTask.Result.CoreWebView2;
            var perms = new System.Collections.Generic.List<string>();
            core.PermissionRequested += (_, e) => { perms.Add(e.PermissionKind + " " + e.Uri); e.State = CoreWebView2PermissionState.Deny; };
            core.AddWebResourceRequestedFilter("https://probe.card.desk/*", CoreWebView2WebResourceContext.All);
            string page = $"<script>window.__r = {{}}; fetch('http://127.0.0.1:{sink.TcpPort}/lna-fetch').then(() => __r.fetch = 'ok', e => __r.fetch = String(e));" +
                $"new Image().src = 'http://127.0.0.1:{sink.TcpPort}/lna-img';</script>";
            core.WebResourceRequested += (_, e) => e.Response = core.Environment.CreateWebResourceResponse(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(page)), 200, "OK", "Content-Type: text/html");
            core.Navigate("https://probe.card.desk/");
            Pump(5000);
            var r = core.ExecuteScriptAsync("JSON.stringify(window.__r)");
            WaitUntil(() => r.IsCompleted);
            Console.WriteLine($"  LNA [{(args.Length == 0 ? "default" : args)}] page {r.Result} perms [{string.Join("; ", perms)}] sink [{string.Join("; ", sink.Hits)}]");

            // 비교: 진짜 인터넷 페이지(example.com)에서 같은 요청을 보내면?
            while (sink.Hits.TryDequeue(out _)) { }
            perms.Clear();
            core.Navigate("https://example.com/");
            Pump(5000);
            var r2 = core.ExecuteScriptAsync($"window.__r = {{}}; fetch('http://127.0.0.1:{sink.TcpPort}/web-fetch').then(() => __r.fetch = 'ok', e => __r.fetch = String(e)); location.host");
            WaitUntil(() => r2.IsCompleted);
            Pump(4000);
            var r3 = core.ExecuteScriptAsync("JSON.stringify(window.__r)");
            WaitUntil(() => r3.IsCompleted);
            Console.WriteLine($"  LNA [{(args.Length == 0 ? "default" : args)}] from {r2.Result}: page {r3.Result} perms [{string.Join("; ", perms)}] sink [{string.Join("; ", sink.Hits)}]");
            ctlTask.Result.Close();
            host.Dispose();
        }
    }
}
