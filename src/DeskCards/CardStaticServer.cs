using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskCards;

internal sealed class CardStaticServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _root;
    private readonly CancellationTokenSource _stop = new();
    public CardStaticServer(string root, IPAddress address, int port)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _listener = new TcpListener(address, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Accept();
    }
    public int Port { get; }
    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Serve(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? line = await reader.ReadLineAsync(timeout.Token);
                var request = line?.Split(' ');
                if (request is not { Length: >= 2 }) return;
                for (int n = 0; n < 100; n++)
                    if (string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) break;
                string relative = Uri.UnescapeDataString(request[1].Split('?')[0]).TrimStart('/');
                if (relative.Length == 0) relative = "card.html";
                string path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
                bool found = path.StartsWith(_root, StringComparison.OrdinalIgnoreCase) && File.Exists(path);
                if (request[0] is not ("GET" or "HEAD"))
                { await Header(stream, 405, 0, "text/plain", timeout.Token); return; }
                if (!found) { await Header(stream, 404, 0, "text/plain", timeout.Token); return; }
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
                string mime = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".html" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8",
                    ".js" or ".mjs" => "text/javascript; charset=utf-8", ".json" => "application/json",
                    ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif", ".webp" => "image/webp", ".woff2" => "font/woff2", ".wasm" => "application/wasm",
                    _ => "application/octet-stream",
                };
                await Header(stream, 200, file.Length, mime, timeout.Token);
                if (request[0] == "GET") await file.CopyToAsync(stream, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ArgumentException or UnauthorizedAccessException) { }
        }
    }
    private static Task Header(Stream stream, int status, long length, string type, CancellationToken token) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Length: {length}\r\nContent-Type: {type}\r\nConnection: close\r\nCache-Control: no-cache\r\n\r\n"), token).AsTask();
    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}
