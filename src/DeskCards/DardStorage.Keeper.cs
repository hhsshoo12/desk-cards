using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace DeskCards;

internal static partial class DardStorage
{
    internal static Stream CopyResponse(Stream source, long length, string? tempDirectory = null)
    {
        Stream copy = length > 64L * 1024 * 1024
            ? new FileStream(Path.Combine(tempDirectory ?? Path.GetTempPath(), "DeskCards-" + Guid.NewGuid().ToString("N") + ".tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose)
            : new MemoryStream();
        try { source.CopyTo(copy); copy.Position = 0; return copy; }
        catch { copy.Dispose(); throw; }
    }

    /// <summary>
    /// 환경 하나의 보이지 않는 관리용 화면. 앱이 끝날 때까지 산다.
    /// 저장소를 옮길 때는 그 origin으로 옮기기 페이지를 열고, 페이지가 보내는 zip 조각을 받거나(내보내기) 준다(가져오기).
    /// </summary>
    private sealed class Keeper : IDisposable
    {
        private readonly HwndSource _host;
        private readonly CoreWebView2Controller _controller;
        private Job? _job;

        private Keeper(HwndSource host, CoreWebView2Controller controller)
        {
            _host = host;
            _controller = controller;
            Core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            Core.WebResourceRequested += OnRequest;
            Core.NewWindowRequested += (_, e) => e.Handled = true;
            Core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            Core.DownloadStarting += (_, e) => e.Cancel = true;
            Core.Settings.AreDefaultScriptDialogsEnabled = false;
            Core.Settings.AreHostObjectsAllowed = false;
        }

        private CoreWebView2 Core => _controller.CoreWebView2;

        public void Dispose()
        {
            _job?.Dispose();
            _controller.Close();
            _host.Dispose();
        }

        public static async Task<Keeper> CreateAsync(string path, bool internet)
        {
            // 화면에 보이지 않는 창(WS_POPUP, WS_VISIBLE 없음) 안에 브라우저를 둔다.
            var host = new HwndSource(new HwndSourceParameters("DeskCards storage") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
            try
            {
                var env = await EnvironmentAt(path, internet);
                var controller = await env.CreateCoreWebView2ControllerAsync(host.Handle);
                controller.IsVisible = false;
                return new Keeper(host, controller);
            }
            catch { host.Dispose(); throw; }
        }

        public Task<string> Cdp(string method, object args) => Core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(args));

        /// <summary>origin에서 옮기기 페이지를 열고 끝날 때까지 기다린다. 1분 동안 아무 소식이 없으면 실패로 본다. 건너뛴 레코드 수를 돌려준다.</summary>
        public async Task<int> RunAsync(string origin, string mode, string zipPath)
        {
            var job = new Job(new Uri(origin).Host, mode, zipPath);
            _job = job;
            try
            {
                await Cdp("Network.setBypassServiceWorker", new { bypass = true });
                Core.Navigate($"{origin}/__desk/{mode}");
                while (true)
                {
                    var done = await Task.WhenAny(job.Done.Task, Task.Delay(5000));
                    if (done == job.Done.Task) break;
                    if (DateTime.UtcNow - job.LastSeen > TimeSpan.FromMinutes(1))
                        throw new IOException("저장소를 옮기는 화면이 응답하지 않아요.");
                }
                return await job.Done.Task;
            }
            finally
            {
                job.Dispose();
                _job = null;
                Core.Navigate("about:blank");
            }
        }

        private void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var env = Core.Environment;
            var job = _job;
            var uri = new Uri(e.Request.Uri);
            CoreWebView2WebResourceResponse Text(int status, string body, string type = "text/plain") =>
                env.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body)), status, status == 200 ? "OK" : "Error",
                    $"Content-Type: {type}; charset=utf-8\r\nCache-Control: no-store");
            if (job == null || uri.Scheme != "https" || !string.Equals(uri.Host, job.Host, StringComparison.OrdinalIgnoreCase))
            {
                e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "");
                return;
            }
            job.LastSeen = DateTime.UtcNow;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            try
            {
                switch (uri.AbsolutePath)
                {
                    case "/__desk/export" or "/__desk/import" when uri.AbsolutePath == "/__desk/" + job.Mode:
                        e.Response = Text(200, MovePage.Html(job.Mode), "text/html");
                        return;
                    case "/__desk/put" when job.Mode == MovePage.Export && e.Request.Method == "POST":
                        job.Put(query["name"] ?? "", query["part"] == "0", e.Request.Content);
                        e.Response = Text(200, "");
                        return;
                    case "/__desk/get" when job.Mode == MovePage.Import:
                        e.Response = job.Get(query["name"] ?? "") is { } stream
                            ? env.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: application/octet-stream\r\nCache-Control: no-store")
                            : Text(404, "");
                        return;
                    case "/__desk/metadata" when job.Mode == MovePage.Export:
                        ReadMetadata(e, job, "https://" + job.Host, query["db"] ?? "", query["store"] ?? "");
                        return;
                    case "/__desk/done" when e.Request.Method == "POST":
                        job.Finish(null, int.TryParse(query["skipped"], out int n) ? n : 0);
                        e.Response = Text(200, "");
                        return;
                    case "/__desk/fail" when e.Request.Method == "POST":
                        using (var reader = new StreamReader(e.Request.Content ?? Stream.Null)) job.Finish(reader.ReadToEnd());
                        e.Response = Text(200, "");
                        return;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                job.Finish(ex.Message);
            }
            e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "");
        }

        private async void ReadMetadata(CoreWebView2WebResourceRequestedEventArgs e, Job job, string origin, string database, string store)
        {
            using var deferral = e.GetDeferral();
            try
            {
                string json = await Cdp("IndexedDB.getMetadata", new { securityOrigin = origin, databaseName = database, objectStoreName = store });
                e.Response = Core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(json)), 200, "OK", "Content-Type: application/json");
            }
            catch (Exception ex)
            {
                job.Finish(ex.Message);
                e.Response = Core.Environment.CreateWebResourceResponse(null, 500, "Error", "");
            }
        }
    }

    /// <summary>옮기기 한 번. 내보내기면 받은 조각을 zip에 쓰고, 가져오기면 zip에서 꺼내 준다.</summary>
    private sealed class Job : IDisposable
    {
        private ZipArchive? _zip;
        private Stream? _entry;
        private string? _entryName;

        public Job(string host, string mode, string zipPath)
        {
            Host = host;
            Mode = mode;
            _zip = mode == MovePage.Export
                ? new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create)
                : ZipFile.OpenRead(zipPath);
        }

        public string Host { get; }
        public string Mode { get; }
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public TaskCompletionSource<int> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>조각 하나를 쓴다. 큰 파일은 같은 이름으로 여러 번 나눠 온다(part=0이 처음).</summary>
        public void Put(string name, bool first, Stream? content)
        {
            if (_zip == null || name.Length == 0 || name.Contains("..")) throw new ArgumentException("잘못된 조각이에요.");
            if (first || _entryName != name)
            {
                if (!first) throw new InvalidOperationException("조각 순서가 맞지 않아요.");
                _entry?.Dispose();
                _entry = _zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                _entryName = name;
            }
            content?.CopyTo(_entry!);
        }

        public Stream? Get(string name)
        {
            var entry = _zip?.GetEntry(name);
            if (entry == null) return null;
            // 응답 스트림은 브라우저가 따로 읽으므로 zip과 떼어 둔다. 큰 항목은 임시 파일로.
            using var source = entry.Open();
            return CopyResponse(source, entry.Length);
        }

        public void Finish(string? error, int skipped = 0)
        {
            _entry?.Dispose();
            _entry = null;
            _zip?.Dispose();
            _zip = null;
            if (error == null) Done.TrySetResult(skipped);
            else Done.TrySetException(new IOException(error));
        }

        public void Dispose() => Finish("중단됐어요.");
    }
}
