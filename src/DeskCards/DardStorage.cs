using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace DeskCards;

/// <summary>
/// .dard 카드의 브라우저 환경과 저장소(localStorage·IndexedDB·OPFS).
/// internet 권한이 있는 카드와 없는 카드는 브라우저 프로세스와 데이터 폴더(online/offline)를 따로 쓴다.
/// 환경마다 보이지 않는 관리용 화면을 하나 띄워 두고, 그 화면으로 용량 한도를 걸고 저장소를 지우고 옮긴다.
/// </summary>
internal static partial class DardStorage
{
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> _environments = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<Keeper>> _keepers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BrowserRecoveryPolicy> _browserRecovery = new(StringComparer.OrdinalIgnoreCase);
    internal const string BrowserRecoveryError = "브라우저가 반복해서 종료되어 자동 복구를 멈췄어요. 카드 메뉴에서 '다시 불러오기'를 누르거나 앱을 다시 켜 주세요.";
    private static readonly SemaphoreSlim _moving = new(1, 1);
    internal static event Action<string>? BrowserLost;

    static DardStorage()
    {
        if (System.Windows.Application.Current is { } app)
            app.Exit += (_, _) =>
            {
                foreach (var keeper in _keepers.Values.Where(t => t.IsCompletedSuccessfully)) keeper.Result.Dispose();
                _keepers.Clear();
                _environments.Clear();
            };
    }

    /// <summary>
    /// 권한이 있으면 모든 연결이 앱의 프록시(<see cref="DardProxy"/>)를 지나 인터넷(공인 주소)에만 닿는다. 내부망은 막는다.
    /// 권한이 없으면 네트워크를 통째로 막는다: 이동·미리 연결은 요청 검사 전에 소켓부터 열기 때문에(회귀 테스트로 확인)
    /// 모든 연결을 앱이 쥐고 있는 막다른 길(<see cref="DeadEnd"/>)로 보내고(루프백도 예외 없이), 이름 풀이도 전부 실패시킨다.
    /// 어느 쪽이든 WebRTC는 프록시 밖 UDP를 쓰지 못하게 하고, 페이지에서도 지운다(로컬 IP가 드러나므로).
    /// 실행 인자는 데이터 폴더마다 하나라서 폴더도 따로 쓴다.
    /// </summary>
    public static Task<CoreWebView2Environment> Environment(bool internet)
        => EnvironmentAt(ProfileDir(internet), internet);

    internal static string ProfileDir(bool internet) => Path.GetFullPath(Path.Combine(AppPaths.WebDataDir, internet ? "online" : "offline"));

    internal static bool BrowserRecoveryBlocked(string path) =>
        _browserRecovery.TryGetValue(Path.GetFullPath(path), out var recovery) && recovery.Blocked;

    internal static void ResetBrowserRecovery(bool internet) => _browserRecovery.Remove(ProfileDir(internet));

    private static Task<CoreWebView2Environment> EnvironmentAt(string path, bool internet)
    {
        const string webrtc = "--force-webrtc-ip-handling-policy=disable_non_proxied_udp --webrtc-ip-handling-policy=disable_non_proxied_udp";
        path = Path.GetFullPath(path);
        if (BrowserRecoveryBlocked(path)) throw new InvalidOperationException(BrowserRecoveryError);
        if (_environments.TryGetValue(path, out var cached) && !cached.IsFaulted) return cached;
        // 잘못된 저장 경로는 브라우저 시작 전에 I/O 오류로 돌려준다.
        Directory.CreateDirectory(path);
        var endpoint = internet ? DardProxy.Instance.EndPoint : DeadEnd.Instance.EndPoint;
        return _environments[path] = CreateEnvironment(path, endpoint, webrtc);
    }

    private static async Task<CoreWebView2Environment> CreateEnvironment(string path, IPEndPoint endpoint, string webrtc)
    {
        var env = await CoreWebView2Environment.CreateAsync(null, path,
            new CoreWebView2EnvironmentOptions(
                $"--proxy-server=http://{endpoint} --proxy-bypass-list=<-loopback> --disable-quic " +
                $"--host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE {endpoint.Address}\" " + webrtc));
        env.BrowserProcessExited += (_, e) => InvalidateBrowser(path, env, e.BrowserProcessExitKind == CoreWebView2BrowserProcessExitKind.Failed);
        return env;
    }

    private static void InvalidateBrowser(string path, CoreWebView2Environment env, bool failed)
    {
        if (!_environments.TryGetValue(path, out var current) || !current.IsCompletedSuccessfully || !ReferenceEquals(current.Result, env)) return;
        _environments.Remove(path);
        if (_keepers.Remove(path, out var keeper)) _ = DisposeKeeper(keeper);
        // 모든 화면을 정상적으로 닫은 경우도 통지된다. 정상 종료는 복구 횟수에 넣지 않는다.
        if (!failed) return;
        if (!_browserRecovery.TryGetValue(path, out var recovery))
            _browserRecovery[path] = recovery = new BrowserRecoveryPolicy();
        recovery.RecordExit(System.Environment.TickCount64);
        BrowserLost?.Invoke(path);
    }

    private static async Task DisposeKeeper(Task<Keeper> task)
    {
        try { (await task).Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
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
        string path = ProfileDir(internet);
        if (!_keepers.TryGetValue(path, out var keeper) || keeper.IsFaulted)
            _keepers[path] = keeper = Keeper.CreateAsync(path, internet);
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
        await _moving.WaitAsync();
        try
        {
            var keeper = await KeeperFor(internet);
            var list = origins.ToList();
            foreach (string origin in list)
                await keeper.Cdp("Storage.clearDataForOrigin", new { origin, storageTypes = "all" });
            UpdateIndex(internet, index => list.ForEach(o => index.Remove(o)));
        }
        finally { _moving.Release(); }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

}
