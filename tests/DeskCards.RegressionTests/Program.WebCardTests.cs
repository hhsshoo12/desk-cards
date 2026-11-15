using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using DeskCards;

internal static partial class Program
{
    private static void WebCardTests(string root)
    {
        string work = Path.Combine(root, "webcard");
        Directory.CreateDirectory(work);

        string MakeCard(string name, string json, params string[] files)
        {
            string dir = Path.Combine(work, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), json);
            foreach (string f in files) File.WriteAllText(Path.Combine(dir, f), "x");
            return dir;
        }

        Test("web card: manifest reads html card and background exe widget", () =>
        {
            var a = WebCardManifest.Load(MakeCard("a", "{\"id\":\"com.test.a\",\"name\":\"A\",\"size\":[320,180],\"html\":\"card.html\"}", "card.html"));
            Check(a is { Id: "com.test.a", Width: 320, Height: 180, Html: "card.html", Exe: null, Background: false });
            var b = WebCardManifest.Load(MakeCard("b", "{\"id\":\"b\",\"name\":\"B\",\"exe\":\"w.exe\",\"mode\":\"background\"}", "w.exe"));
            Check(b is { Html: null, Exe: "w.exe", Background: true, Width: 300, Height: 200 });
        });

        Test("web card: manifest rejects bad id, missing files, escaping paths and empty cards", () =>
        {
            void Bad(string json, params string[] files)
            {
                try { WebCardManifest.Load(MakeCard("bad" + Guid.NewGuid().ToString("N")[..6], json, files)); }
                catch (WebCardException) { return; }
                throw new Exception("Assertion failed: expected manifest error for " + json);
            }
            Bad("{\"id\":\"Bad Id\",\"name\":\"x\",\"html\":\"c.html\"}", "c.html");
            Bad("{\"id\":\"x\",\"name\":\"x\",\"html\":\"missing.html\"}");
            Bad("{\"id\":\"x\",\"name\":\"x\",\"html\":\"..\\\\evil.html\"}");
            Bad("{\"id\":\"x\",\"name\":\"x\"}");
            Bad("{\"id\":\"x\",\"name\":\"x\",\"html\":\"c.html\",\"mode\":\"weird\"}", "c.html");
        });

        Test("web card: store imports a .dard zip, lists it, replaces on update and removes", () =>
        {
            string src = MakeCard("pack", "{\"id\":\"com.test.zip\",\"name\":\"Zip\",\"html\":\"c.html\"}", "c.html");
            string dard = Path.Combine(work, "zip.dard");
            ZipFile.CreateFromDirectory(src, dard);
            var store = new WebCardStore(Path.Combine(work, "store"));
            var first = store.Import(dard);
            Check(File.Exists(Path.Combine(first.Directory, "c.html")));
            Check(store.List().Count == 1);
            File.WriteAllText(Path.Combine(src, "c.html"), "new");
            File.Delete(dard);
            ZipFile.CreateFromDirectory(src, dard);
            store.Import(dard);
            Check(File.ReadAllText(Path.Combine(first.Directory, "c.html")) == "new");
            Check(store.List().Count == 1);
            store.Remove("com.test.zip");
            Check(store.List().Count == 0);
        });

        Test("web card: store imports a folder and a zip wrapped in one folder", () =>
        {
            string src = MakeCard("wrapped", "{\"id\":\"com.test.wrap\",\"name\":\"W\",\"html\":\"c.html\"}", "c.html");
            string outer = Path.Combine(work, "outer");
            Directory.CreateDirectory(outer);
            Directory.Move(src, Path.Combine(outer, "inner"));
            string dard = Path.Combine(work, "wrapped.dard");
            ZipFile.CreateFromDirectory(outer, dard);
            var store = new WebCardStore(Path.Combine(work, "store2"));
            store.Import(dard);
            string folder = MakeCard("folder", "{\"id\":\"com.test.folder\",\"name\":\"F\",\"html\":\"c.html\"}", "c.html");
            store.Import(folder);
            Check(store.List().Count == 2);
        });

        Test("web card: process relays stdout lines, accepts stdin lines and stops with the app", () =>
        {
            // PowerShell로 표준 입력의 줄을 그대로 표준 출력으로 돌려준다.
            string findstr = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var lines = new System.Collections.Concurrent.BlockingCollection<string>();
            using var process = new WebCardProcess(findstr, work, "com.test.echo", Path.Combine(work, "data"), "-NoProfile -Command \"while(($l=[Console]::In.ReadLine()) -ne $null){[Console]::Out.WriteLine($l);[Console]::Out.Flush()}\"");
            process.Message += lines.Add;
            process.Start();
            process.Send("{\"hello\":1}");
            Check(lines.TryTake(out var line, 15000) && line == "{\"hello\":1}");
            Check(!process.HasExited);
            process.Dispose();
            Check(process.HasExited);
        });
    }
}