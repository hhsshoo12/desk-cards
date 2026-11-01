using System;
using System.IO;
using System.Linq;
using System.Windows;
using DeskCards;

internal static partial class Program
{
    private static void RevisionWindowTests(string root)
    {
        Test("revision UI B1: physical position survives HWND recreation and config reload", () =>
        {
            string folder = Path.Combine(root, "position-restart");
            Directory.CreateDirectory(Path.Combine(folder, "position"));
            string configPath = Path.Combine(root, "position-restart.json");
            var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var expected = new Point(area.Left + 80, area.Top + 80);
            var cfg = Config.Load(configPath);
            cfg.PositionsPx["position"] = new[] { expected.X, expected.Y };
            cfg.Save();
            for (int run = 0; run < 2; run++)
            {
                var mgr = new GroupManager(folder, Config.Load(configPath));
                try
                {
                    mgr.Start(); Pump(100);
                    Check(mgr.Cards.Single().PhysicalPosition == expected);
                    Check(Config.Load(configPath).PositionsPx["position"].SequenceEqual(new[] { expected.X, expected.Y }));
                }
                finally { mgr.Shutdown(); }
            }
        });
    }
}
