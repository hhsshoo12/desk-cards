using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeskCards;

internal static partial class Program
{
    private static void BarSettingsTests(string root, Application app)
    {
        Test("card bar edge is chosen per display", () =>
        {
            string path = Path.Combine(root, "bar-edges.json");
            File.WriteAllText(path, """{"BarEdge":0,"BarEdges":{"D2":null,"D3":7,"D4":1}}""");
            var cfg = Config.Load(path);
            var mgr = new GroupManager(Path.Combine(root, "bar-edge-groups"), cfg);
            try
            {
                bool loaded = mgr.BarEdgeFor("D2") == null && mgr.BarEdgeFor("D4") == ScreenEdge.Top
                    && mgr.BarEdgeFor("D3") == ScreenEdge.Left && mgr.BarEdgeFor("D9") == ScreenEdge.Left;
                mgr.SetBarEdge("D9", ScreenEdge.Bottom);
                Check(loaded && mgr.BarEdgeFor("D9") == ScreenEdge.Bottom && mgr.BarEdgeFor("D7") == ScreenEdge.Bottom
                    && Config.Load(path).BarEdges["D9"] == ScreenEdge.Bottom);
            }
            finally { mgr.Shutdown(); }
        });
        Test("card bar zone grows from the edge inside the work area", () =>
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;
            var b = screen.Bounds;
            var touch = EdgeBar.ZoneRect(screen, ScreenEdge.Right, 0);
            var wide = EdgeBar.ZoneRect(screen, ScreenEdge.Right, 25);
            Check(touch.Width == 1 && touch.Right == b.Right && wide.Right == b.Right
                && Math.Abs(wide.Width - (1 + b.Width * 0.025)) <= 1 && wide.Top == screen.WorkingArea.Top && wide.Bottom == screen.WorkingArea.Bottom);
        });
        Test("zone preview slides out, holds 1.5 s, then hides", () =>
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;
            BarPreview.Zone(screen, ScreenEdge.Right, 25);
            Pump(400);
            var band = app.Windows.OfType<ZoneBand>().Single();
            var fill = Visuals<Border>(band).Single(b => b.Background is SolidColorBrush { Color.A: 0x80 });
            double target = EdgeBar.ZoneRect(screen, ScreenEdge.Right, 25).Width / VisualTreeHelper.GetDpi(band).DpiScaleX;
            bool shown = band.IsVisible && Math.Abs(fill.ActualWidth - target) < 1.5;
            BarPreview.Zone(screen, ScreenEdge.Right, 10); // 바꾸면 유지 시간이 다시 시작된다
            Pump(1200);
            bool held = band.IsVisible;
            Pump(900);
            Check(shown && held && !band.IsVisible);
            band.Close();
        });
    }
}
