using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DeskCards;

internal static partial class Program
{
    private static void RevisionTests(string root)
    {
        Test("revision A7: transient replace lock retries and permanent failure logs without losing config", () =>
        {
            string path = Path.Combine(root, "save-retry.json"); var cfg = Config.Load(path); cfg.Save();
            var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = Task.Run(async () => { await Task.Delay(65); locked.Dispose(); });
            cfg.ShowGuides = false;
            Check(cfg.TrySave()); release.GetAwaiter().GetResult();
            Check(!Config.Load(path).ShowGuides);
            using (var permanent = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                cfg.ShowGuides = true; Check(!cfg.TrySave());
                Check(!Config.Load(path).ShowGuides && File.ReadAllText(path + ".log").Contains("설정 저장 실패"));
            }
            Check(!Directory.EnumerateFiles(root, "save-retry.json.*.tmp").Any());
        });
    }
    private sealed class BrokenReadStream : MemoryStream
    {
        public override void CopyTo(Stream destination, int bufferSize) { destination.WriteByte(1); throw new IOException("read failed"); }
    }
}
