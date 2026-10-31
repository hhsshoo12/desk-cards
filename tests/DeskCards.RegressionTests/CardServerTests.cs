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
        Test("python runtime: embedded zip is extracted once, replaces older versions and runs a card with sibling modules", () =>
        {
            string zipPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "installer", "cache", "python-3.14.10-embed-amd64.zip"));
            if (!File.Exists(zipPath)) { Console.WriteLine("  skipped: installer\\cache has no Python zip (run installer\\build.ps1 once)"); return; }
            string pythons = Path.Combine(root, "python-runtime");
            Directory.CreateDirectory(Path.Combine(pythons, "0.0.1")); File.WriteAllText(Path.Combine(pythons, "0.0.1", "old.txt"), "old");
            string exe;
            using (var stream = File.OpenRead(zipPath)) exe = PythonRuntime.Extract(stream, pythons, "9.9.9");
            Check(exe == Path.Combine(pythons, "9.9.9", "python.exe") && File.Exists(exe) && !Directory.Exists(Path.Combine(pythons, "0.0.1")));
            File.WriteAllText(Path.Combine(pythons, "9.9.9", "marker.txt"), "kept");
            using (var stream = File.OpenRead(zipPath)) PythonRuntime.Extract(stream, pythons, "9.9.9");
            Check(File.Exists(Path.Combine(pythons, "9.9.9", "marker.txt")));

            string folder = Path.Combine(root, "python-embedded"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "helper.py"), "WORD = b'embedded-ready'\n");
            File.WriteAllText(Path.Combine(folder, "main.py"), "import os\nfrom http.server import HTTPServer,BaseHTTPRequestHandler\nimport helper\nclass Handler(BaseHTTPRequestHandler):\n def do_GET(self):\n  self.send_response(200); self.end_headers(); self.wfile.write(helper.WORD)\nHTTPServer((os.environ['DESK_ADDR'],int(os.environ['DESK_PORT'])),Handler).serve_forever()\n");
            var address = IPAddress.Parse("127.2.3.6");
            var probe = new System.Net.Sockets.TcpListener(address, 0); probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            using var process = new CardPythonProcess(folder, address.ToString(), port, exe);
            process.Ready.GetAwaiter().GetResult();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            Check(client.GetStringAsync($"http://{address}:{port}/").GetAwaiter().GetResult() == "embedded-ready");
        });
        Test("python packages: pip installs wheels from requirements.txt into lib once and reports failures", () =>
        {
            string cache = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "installer", "cache"));
            string pythonZip = Path.Combine(cache, "python-3.14.10-embed-amd64.zip"), pipWheel = Path.Combine(cache, "pip-26.2.2-py3-none-any.whl");
            if (!File.Exists(pythonZip) || !File.Exists(pipWheel)) { Console.WriteLine("  skipped: installer\\cache has no Python or pip (run installer\\build.ps1 once)"); return; }
            string pythons = Path.Combine(root, "python-pip"), exe;
            using (var zip = File.OpenRead(pythonZip)) using (var pip = File.OpenRead(pipWheel)) exe = PythonRuntime.Extract(zip, pythons, "9.9.8", pip);
            string wheelPath = Path.Combine(pythons, "9.9.8", "pip.whl");
            Check(File.Exists(wheelPath));

            // 인터넷 없이 확인하려고 requirements.txt에 로컬 wheel 파일을 적는다.
            string demo = Path.Combine(root, "demo_pkg-1.0-py3-none-any.whl");
            using (var stream = File.Create(demo)) using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
            {
                void Add(string name, string text) { using var w = new StreamWriter(archive.CreateEntry(name).Open()); w.Write(text); }
                Add("demo_pkg/__init__.py", "WORD = b'pip-ready'\n");
                Add("demo_pkg-1.0.dist-info/METADATA", "Metadata-Version: 2.1\nName: demo-pkg\nVersion: 1.0\n");
                Add("demo_pkg-1.0.dist-info/WHEEL", "Wheel-Version: 1.0\nGenerator: test\nRoot-Is-Purelib: true\nTag: py3-none-any\n");
                Add("demo_pkg-1.0.dist-info/RECORD", "");
            }
            string folder = Path.Combine(root, "python-packages"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "requirements.txt"), demo + "\n");
            File.WriteAllText(Path.Combine(folder, "main.py"), "import os\nfrom http.server import HTTPServer,BaseHTTPRequestHandler\nimport demo_pkg\nclass Handler(BaseHTTPRequestHandler):\n def do_GET(self):\n  self.send_response(200); self.end_headers(); self.wfile.write(demo_pkg.WORD)\nHTTPServer((os.environ['DESK_ADDR'],int(os.environ['DESK_PORT'])),Handler).serve_forever()\n");
            Check(CardPackages.NeedsInstall(folder));
            var address = IPAddress.Parse("127.2.3.7");
            int Free() { var probe = new System.Net.Sockets.TcpListener(address, 0); probe.Start(); int p = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); return p; }
            int port = Free();
            using (var process = new CardPythonProcess(folder, address.ToString(), port, exe, wheelPath))
            {
                process.Ready.GetAwaiter().GetResult();
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
                Check(client.GetStringAsync($"http://{address}:{port}/").GetAwaiter().GetResult() == "pip-ready");
            }
            Check(!CardPackages.NeedsInstall(folder) && Directory.Exists(Path.Combine(folder, "lib", "demo_pkg")));

            // requirements.txt가 바뀌면 다시 설치해야 하고, wheel이 없는 패키지는 설치 오류로 알린다.
            File.WriteAllText(Path.Combine(folder, "requirements.txt"), Path.Combine(root, "missing-1.0-py3-none-any.whl") + "\n");
            Check(CardPackages.NeedsInstall(folder));
            using var failing = new CardPythonProcess(folder, address.ToString(), Free(), exe, wheelPath);
            string error = "";
            try { failing.Ready.GetAwaiter().GetResult(); }
            catch (IOException ex) { error = ex.Message; }
            Check(error.Contains("패키지를 설치하지 못했어요") && CardPackages.NeedsInstall(folder));
        });
        Test("python system mode: a missing interpreter is reported on the card", () =>
        {
            string folder = Path.Combine(root, "python-missing"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "main.py"), "pass\n");
            using var process = new CardPythonProcess(folder, "127.2.3.8", 47999, "deskcards-no-such-python");
            string error = "";
            try { process.Ready.GetAwaiter().GetResult(); }
            catch (IOException ex) { error = ex.Message; }
            Check(error.Contains("PATH에서 Python을 찾지 못했어요"));
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
