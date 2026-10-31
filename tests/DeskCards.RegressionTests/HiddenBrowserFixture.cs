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
    private static CardDocument RecoveryPackage(string id) => new()
    {
        Id = id, Path = id, Name = "recovery",
        Cards = new[] { new WebCardInfo("main", "main", 200, 200), new WebCardInfo("mini", "mini", 200, 200) },
        CardHtml = Encoding.UTF8.GetBytes("<p>recovered"),
    };
    private static void RecoveryTest(string name, Func<Task> body) => Test("storage recovery: " + name, () =>
    {
        var task = OnUi(body);
        WaitUntilLong(() => task.IsCompleted, 90000);
        if (!task.IsCompleted) throw new TimeoutException(name);
        task.GetAwaiter().GetResult();
    });

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
