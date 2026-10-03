using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace DeskCards;

/// <summary>
/// .dard 카드의 브라우저 환경과 저장소(localStorage·IndexedDB·OPFS).
/// internet 권한이 있는 카드와 없는 카드는 브라우저 프로세스와 데이터 폴더(online/offline)를 따로 쓴다.
/// 환경마다 보이지 않는 관리용 화면을 하나 띄워 두고, 그 화면으로 용량 한도를 걸고 저장소를 지우고 옮긴다.
/// </summary>
internal static class DardStorage
{
    private static Task<CoreWebView2Environment>? _offline, _online;
    private static readonly Dictionary<bool, Task<Keeper>> _keepers = new();
    private static readonly SemaphoreSlim _moving = new(1, 1);

    /// <summary>
    /// 권한이 있으면 모든 연결이 앱의 프록시(<see cref="DardProxy"/>)를 지나 인터넷(공인 주소)에만 닿는다. 내부망은 막는다.
    /// 권한이 없으면 네트워크를 통째로 막는다: 이동·미리 연결은 요청 검사 전에 소켓부터 열기 때문에(회귀 테스트로 확인)
    /// 모든 연결을 앱이 쥐고 있는 막다른 길(<see cref="DeadEnd"/>)로 보내고(루프백도 예외 없이), 이름 풀이도 전부 실패시킨다.
    /// 어느 쪽이든 WebRTC는 프록시 밖 UDP를 쓰지 못하게 하고, 페이지에서도 지운다(로컬 IP가 드러나므로).
    /// 실행 인자는 데이터 폴더마다 하나라서 폴더도 따로 쓴다.
    /// </summary>
    public static Task<CoreWebView2Environment> Environment(bool internet)
    {
        const string webrtc = "--force-webrtc-ip-handling-policy=disable_non_proxied_udp --webrtc-ip-handling-policy=disable_non_proxied_udp";
        if (internet) return _online ??= CreateOnline();
        return _offline ??= CreateOffline();

        // 인터넷만: 모든 연결을 앱의 프록시에 맡기고(루프백도 예외 없이) 브라우저는 이름을 풀지 않는다. 프록시가 내부망을 막는다.
        static Task<CoreWebView2Environment> CreateOnline()
        {
            var proxy = DardProxy.Instance;
            return CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.WebDataDir, "online"),
                new CoreWebView2EnvironmentOptions(
                    $"--proxy-server=http://{proxy.EndPoint} --proxy-bypass-list=<-loopback> --disable-quic " +
                    $"--host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE {proxy.EndPoint.Address}\" " + webrtc));
        }

        static Task<CoreWebView2Environment> CreateOffline()
        {
            var deadEnd = DeadEnd.Instance; // 못 열면 예외(카드에 오류로 보인다). 막다른 길 없이 띄우지 않는다.
            return CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.WebDataDir, "offline"),
                new CoreWebView2EnvironmentOptions(
                    $"--proxy-server=http://{deadEnd.EndPoint} --proxy-bypass-list=<-loopback> " +
                    $"--host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE {deadEnd.EndPoint.Address}\" " + webrtc));
        }
    }

    /// <summary>
    /// internet 권한 없는 카드의 연결이 가는 곳. 127.255.255.1~32 중 하나에 빈 포트를 앱이 독점으로 열어 쥐고 있고,
    /// 들어오는 연결은 받자마자 끊는다. 다른 프로그램이 같은 주소·포트를 가로챌 수 없고, 카드는 기다리지 않고 바로 실패한다.
    /// 127 대역은 전부 이 PC 자신(루프백)이라 바깥으로는 아무것도 나가지 않는다.
    /// </summary>
    internal sealed class DeadEnd
    {
        private static DeadEnd? _instance;
        private readonly Socket _socket;
        private int _hits;

        private DeadEnd(Socket socket)
        {
            _socket = socket;
            _ = Task.Run(AcceptLoop);
        }

        public static DeadEnd Instance => _instance ??= new DeadEnd(Loopback.Listen());

        public IPEndPoint EndPoint => (IPEndPoint)_socket.LocalEndPoint!;

        /// <summary>지금까지 막은 연결 수(권한 없는 카드가 밖으로 나가려 한 횟수).</summary>
        public int Hits => Volatile.Read(ref _hits);

        private async Task AcceptLoop()
        {
            while (true)
            {
                Socket client;
                try { client = await _socket.AcceptAsync(); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return; }
                Interlocked.Increment(ref _hits);
                try
                {
                    client.LingerState = new LingerOption(true, 0); // 바로 끊는다(RST)
                    client.Close();
                }
                catch (SocketException) { }
            }
        }
    }

    private static Task<Keeper> KeeperFor(bool internet)
    {
        if (!_keepers.TryGetValue(internet, out var keeper)) _keepers[internet] = keeper = Keeper.CreateAsync(internet);
        return keeper;
    }

    /// <summary>
    /// 카드를 띄우기 전에 부른다: 이 .dard의 origin마다 용량 한도를 건다(기본 128MB, storage.large면 2GB).
    /// 한도는 그것을 건 화면이 살아 있는 동안만 유지되므로, 앱이 끝날 때까지 있는 관리용 화면에서 건다.
    /// </summary>
    public static async Task PrepareAsync(DardPackage pkg)
    {
        var keeper = await KeeperFor(pkg.Internet);
        UpdateIndex(pkg.Internet, index => { foreach (string origin in pkg.Origins) index[origin] = pkg.Id; });
        foreach (string origin in pkg.Origins)
            await keeper.Cdp("Storage.overrideQuotaForOrigin", new { origin, quotaSize = pkg.Quota });
    }

    /// <summary>origin들의 저장소를 모두 지운다(카드를 지울 때). 저장소 목록에서도 뺀다.</summary>
    public static async Task ClearAsync(bool internet, IEnumerable<string> origins)
    {
        var keeper = await KeeperFor(internet);
        var list = origins.ToList();
        foreach (string origin in list)
            await keeper.Cdp("Storage.clearDataForOrigin", new { origin, storageTypes = "all" });
        UpdateIndex(internet, index => list.ForEach(o => index.Remove(o)));
    }

    /// <summary>origin이 쓰는 용량(IndexedDB·OPFS·Cache, 바이트). 알 수 없으면 null.</summary>
    public static async Task<long?> UsageAsync(bool internet, string origin)
    {
        try
        {
            var keeper = await KeeperFor(internet);
            using var doc = JsonDocument.Parse(await keeper.Cdp("Storage.getUsageAndQuota", new { origin }));
            return (long)doc.RootElement.GetProperty("usage").GetDouble();
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    // ----- 저장소 목록: 데이터가 있을 수 있는 origin → 매니페스트 id -----
    // 앱 설정 파일과 따로, 브라우저 데이터 폴더 안에 둔다. 설정 파일이 날아가거나 되돌려져도 "어디에 무슨 데이터가 있나"는 남는다.

    private static readonly object _indexLock = new();

    private static string IndexPath(bool internet) => Path.Combine(AppPaths.WebDataDir, internet ? "online" : "offline", "desk-origins.json");

    /// <summary>환경 하나의 저장소 목록(origin → 매니페스트 id).</summary>
    public static Dictionary<string, string> Recorded(bool internet)
    {
        lock (_indexLock)
        {
            try
            {
                string path = IndexPath(internet);
                if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
                var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                return new Dictionary<string, string>(read ?? new(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void UpdateIndex(bool internet, Action<Dictionary<string, string>> change)
    {
        lock (_indexLock)
        {
            var index = Recorded(internet);
            change(index);
            string path = IndexPath(internet);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("저장소 목록을 쓰지 못함: " + ex.Message);
            }
        }
    }

    /// <summary>저장 위치 이름("online/shared" 등)을 환경·방식으로.</summary>
    public static (bool Internet, bool PerCard) ParseLocation(string location) =>
        (location.StartsWith("online", StringComparison.Ordinal), location.EndsWith("/card", StringComparison.Ordinal));

    /// <summary>
    /// 저장 위치가 바뀐 .dard의 저장소를 옮긴다(업데이트로 internet 권한이나 storage 방식이 바뀌었을 때).
    /// 카드마다 옛 origin → 새 origin. 여러 옛 origin이 한 새 origin으로 모이면(카드별 → 공용) 첫 카드 것을 쓴다.
    /// 다 옮긴 뒤에야 옛 저장소를 지운다. 실패하면 예외를 던지고 옛 저장소는 그대로 둔다.
    /// 옮기지 못하고 건너뛴 IndexedDB 레코드 수를 돌려준다.
    /// </summary>
    public static async Task<int> MoveAsync(DardPackage pkg, string fromLocation)
    {
        var (fromNet, fromPerCard) = ParseLocation(fromLocation);
        var pairs = pkg.Cards
            .Select(c => (From: pkg.OriginFor(c.Id, fromPerCard), To: pkg.OriginFor(c.Id, pkg.StoragePerCard)))
            .DistinctBy(p => p.To).ToList();
        bool sameEnv = fromNet == pkg.Internet;
        await _moving.WaitAsync();
        string temp = Path.Combine(Path.GetTempPath(), "DeskCards-move-" + Guid.NewGuid().ToString("N") + ".zip");
        int skipped = 0;
        try
        {
            var from = await KeeperFor(fromNet);
            var to = await KeeperFor(pkg.Internet);
            await PrepareAsync(pkg);
            foreach (var (src, dst) in pairs)
            {
                if (sameEnv && src == dst) continue;
                skipped += await from.RunAsync(src, MovePage.Export, temp);
                await to.Cdp("Storage.clearDataForOrigin", new { origin = dst, storageTypes = "all" });
                await to.RunAsync(dst, MovePage.Import, temp);
                File.Delete(temp);
            }
            var keep = sameEnv ? pairs.Select(p => p.To).ToHashSet() : new HashSet<string>();
            var cleared = pairs.Select(p => p.From).Distinct().Where(o => !keep.Contains(o)).ToList();
            foreach (string src in cleared)
                await from.Cdp("Storage.clearDataForOrigin", new { origin = src, storageTypes = "all" });
            UpdateIndex(fromNet, index => cleared.ForEach(o => index.Remove(o)));
            return skipped;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
            _moving.Release();
        }
    }

    /// <summary>
    /// 환경 하나의 보이지 않는 관리용 화면. 앱이 끝날 때까지 산다.
    /// 저장소를 옮길 때는 그 origin으로 옮기기 페이지를 열고, 페이지가 보내는 zip 조각을 받거나(내보내기) 준다(가져오기).
    /// </summary>
    private sealed class Keeper
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

        public static async Task<Keeper> CreateAsync(bool internet)
        {
            // 화면에 보이지 않는 창(WS_POPUP, WS_VISIBLE 없음) 안에 브라우저를 둔다.
            var host = new HwndSource(new HwndSourceParameters("DeskCards storage") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
            var env = await Environment(internet);
            var controller = await env.CreateCoreWebView2ControllerAsync(host.Handle);
            controller.IsVisible = false;
            return new Keeper(host, controller);
        }

        public Task<string> Cdp(string method, object args) => Core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(args));

        /// <summary>origin에서 옮기기 페이지를 열고 끝날 때까지 기다린다. 1분 동안 아무 소식이 없으면 실패로 본다. 건너뛴 레코드 수를 돌려준다.</summary>
        public async Task<int> RunAsync(string origin, string mode, string zipPath)
        {
            var job = new Job(new Uri(origin).Host, mode, zipPath);
            _job = job;
            try
            {
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
            var copy = entry.Length > 64L * 1024 * 1024
                ? (Stream)new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose)
                : new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(copy);
            copy.Position = 0;
            return copy;
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
