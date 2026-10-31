using System;
using System.IO;
using System.Net;
using System.Net.Http;
using DeskCards;

internal static partial class Program
{
    private static void CardServerTests(string root)
    {
        Test("card server: Python receives endpoint and serves requests", () =>
        {
            string folder = Path.Combine(root, "python-server"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "main.py"), "import os\nfrom http.server import HTTPServer,BaseHTTPRequestHandler\nclass Handler(BaseHTTPRequestHandler):\n def do_GET(self):\n  self.send_response(200); self.end_headers(); self.wfile.write(b'python-ready')\nHTTPServer((os.environ['DESK_ADDR'],int(os.environ['DESK_PORT'])),Handler).serve_forever()\n");
            var address = IPAddress.Parse("127.2.3.5");
            var probe = new System.Net.Sockets.TcpListener(address, 0); probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            using var process = new CardPythonProcess(folder, address.ToString(), port);
            process.Ready.GetAwaiter().GetResult();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            Check(client.GetStringAsync($"http://{address}:{port}/").GetAwaiter().GetResult() == "python-ready");
        });
        Test("card server: root, nested assets and missing resources", () =>
        {
            string folder = Path.Combine(root, "static-server");
            Directory.CreateDirectory(Path.Combine(folder, "assets"));
            File.WriteAllText(Path.Combine(folder, "card.html"), "<p>hello");
            File.WriteAllText(Path.Combine(folder, "assets", "main.js"), "window.loaded=true;");
            using var server = new CardStaticServer(folder, IPAddress.Parse("127.2.3.4"), 0);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            client.BaseAddress = new Uri($"http://127.2.3.4:{server.Port}/");
            Check(client.GetStringAsync("").GetAwaiter().GetResult() == "<p>hello");
            using var script = client.GetAsync("assets/main.js").GetAwaiter().GetResult();
            Check(script.Content.Headers.ContentType?.MediaType == "text/javascript");
            Check(script.Content.ReadAsStringAsync().GetAwaiter().GetResult() == "window.loaded=true;");
            using var missing = client.GetAsync("missing").GetAwaiter().GetResult();
            Check(missing.StatusCode == HttpStatusCode.NotFound);
        });
    }
}
