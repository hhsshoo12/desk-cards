using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DeskCards.Setup;

internal sealed class TransferProgress
{
    public long Received { get; }
    public long? Total { get; }
    public double BytesPerSecond { get; }
    public TransferProgress(long received, long? total, double speed) { Received = received; Total = total; BytesPerSecond = speed; }
}

internal interface IDownloads
{
    Task<string> ReadTextAsync(string url, CancellationToken token);
    Task DownloadAsync(string url, string destination, long limit, IProgress<TransferProgress>? progress, CancellationToken token);
}

internal sealed class HttpFailure : Exception
{
    public int Status { get; }
    public HttpFailure(int status) : base("HTTP " + status) { Status = status; }
}

internal sealed class HttpDownloads : IDownloads, IDisposable
{
    private readonly HttpClient _client;
    public HttpDownloads(Version version, HttpMessageHandler? handler = null)
    {
        _client = handler == null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromMinutes(5);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskCards-Setup/" + version.ToString(3));
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<string> ReadTextAsync(string url, CancellationToken token)
    {
        using var data = new MemoryStream();
        await ReceiveAsync(url, data, 8 * 1024 * 1024, null, token).ConfigureAwait(false);
        data.Position = 0;
        using var reader = new StreamReader(data, System.Text.Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    public async Task DownloadAsync(string url, string destination, long limit, IProgress<TransferProgress>? progress, CancellationToken token)
    {
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await ReceiveAsync(url, file, limit, progress, token).ConfigureAwait(false);
    }

    private async Task ReceiveAsync(string url, Stream output, long limit, IProgress<TransferProgress>? progress, CancellationToken token)
    {
        // Covers stalled response bodies as well as the initial HTTP headers.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpFailure((int)response.StatusCode);
        long? total = response.Content.Headers.ContentLength;
        if (total > limit) throw new SetupFailure("내려받는 파일이 허용 크기를 넘었어요.");
        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        byte[] buffer = new byte[81920];
        long received = 0;
        var watch = Stopwatch.StartNew();
        long lastReport = -100;
        int count;
        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
        {
            received += count;
            if (received > limit) throw new SetupFailure("내려받는 파일이 허용 크기를 넘었어요.");
            await output.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
            if (watch.ElapsedMilliseconds - lastReport >= 100)
            {
                progress?.Report(new TransferProgress(received, total, received / Math.Max(watch.Elapsed.TotalSeconds, 0.001)));
                lastReport = watch.ElapsedMilliseconds;
            }
        }
        if (total.HasValue && received != total.Value) throw new IOException("HTTP 응답이 도중에 끝났습니다.");
        progress?.Report(new TransferProgress(received, total, received / Math.Max(watch.Elapsed.TotalSeconds, 0.001)));
    }
    public void Dispose() => _client.Dispose();
}

internal sealed class AppRelease
{
    public Version Version { get; }
    public string ZipUrl { get; }
    public string HashUrl { get; }
    public AppRelease(Version version, string zip, string hash) { Version = version; ZipUrl = zip; HashUrl = hash; }
}

internal sealed class ReleaseService
{
    public const string NetworkMessage = "인터넷에 연결한 뒤 다시 시도해 주세요";
    public const string LimitMessage = "잠시 후 다시 시도해 주세요";
    public const string MissingMessage = "받을 수 있는 앱 버전이 없어요";
    public const string InvalidMessage = "최신 버전 정보를 확인하지 못했어요";
    private readonly IDownloads _downloads;
    private readonly SetupLog _log;
    private readonly Version _minimum;
    public ReleaseService(IDownloads downloads, SetupLog log, Version minimum) { _downloads = downloads; _log = log; _minimum = minimum; }

    public async Task<AppRelease> FindAsync(CancellationToken token)
    {
        try
        {
            _log.Write("버전 확인");
            string json = await _downloads.ReadTextAsync(SetupEnvironment.ReleasesUrl, token).ConfigureAwait(false);
            return Select(json, _minimum);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Write("버전 확인 실패", ex);
            throw Classify(ex);
        }
    }

    public static SetupFailure Classify(Exception ex)
    {
        if (ex is SetupFailure failure) return failure;
        if (ex is HttpFailure http) return new SetupFailure(http.Status == 403 || http.Status == 429 ? LimitMessage : InvalidMessage, true, ex);
        if (ex is HttpRequestException || ex is OperationCanceledException || ex is System.Net.WebException || ex is TimeoutException || ex is IOException)
            return new SetupFailure(NetworkMessage, true, ex);
        return new SetupFailure(InvalidMessage, true, ex);
    }

    public static AppRelease Select(string json, Version minimum)
    {
        var serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        if (!(serializer.DeserializeObject(json) is object[] rows)) throw new FormatException("릴리스 목록이 배열이 아닙니다.");
        var candidates = new List<Tuple<Version, Dictionary<string, object>>>();
        foreach (object row in rows)
        {
            if (!(row is Dictionary<string, object> entry)) throw new FormatException("릴리스 항목 형식 오류");
            if (!entry.TryGetValue("draft", out var draft) || !(draft is bool)
                || !entry.TryGetValue("prerelease", out var pre) || !(pre is bool)
                || !entry.TryGetValue("tag_name", out var tag) || !(tag is string)) throw new FormatException("릴리스 필드 형식 오류");
            var match = Regex.Match((string)tag, @"^app-v(\d+)\.(\d+)\.(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if ((bool)draft || (bool)pre || !match.Success) continue;
            if (!Version.TryParse(match.Groups[1].Value + "." + match.Groups[2].Value + "." + match.Groups[3].Value, out var version)) continue;
            if (version >= minimum) candidates.Add(Tuple.Create(version, entry));
        }
        if (candidates.Count == 0) throw new SetupFailure(MissingMessage, false);
        var selected = candidates.OrderByDescending(x => x.Item1).First();
        string? zip = null, hash = null;
        if (selected.Item2.TryGetValue("assets", out var assets) && assets is object[] list)
        {
            foreach (object asset in list)
            {
                if (!(asset is Dictionary<string, object> fields)) continue;
                if (!fields.TryGetValue("name", out var name) || !fields.TryGetValue("browser_download_url", out var address)
                    || !(address is string url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") continue;
                if (Equals(name, SetupEnvironment.ZipName)) zip = url;
                if (Equals(name, SetupEnvironment.HashName)) hash = url;
            }
        }
        if (zip == null || hash == null) throw new SetupFailure(MissingMessage, false);
        return new AppRelease(selected.Item1, zip, hash);
    }

    public static int? CompareInstalled(string? installed, Version available) =>
        Version.TryParse(installed, out var current) ? (int?)current.CompareTo(available) : null;
}
