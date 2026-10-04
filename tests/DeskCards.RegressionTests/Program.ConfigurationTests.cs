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
    private static void ConfigurationTests(string root)
    {
        Test("PositionsPx: property and JSON match without changing physical positions or legacy handling", () =>
        {
            string path = Path.Combine(root, "positions-px-name.json");
            File.WriteAllText(path, """{"PositionsPx":{"Card":[-2400.5,123.25]},"Positions":{"legacy":[1600,200]}}""");
            var cfg = Config.Load(path);
            Check(cfg.PositionsPx.Count == 1 && cfg.PositionsPx["card"].SequenceEqual(new[] { -2400.5, 123.25 }));
            cfg.PositionsPx["second"] = new[] { 2560.0, -1080.0 };
            Check(cfg.TrySave());
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Check(json.RootElement.TryGetProperty(nameof(Config.PositionsPx), out var positions));
            Check(!json.RootElement.TryGetProperty("Positions", out _));
            Check(positions.GetProperty("Card")[0].GetDouble() == -2400.5);
            var restored = Config.Load(path);
            Check(restored.PositionsPx.Count == 2 && restored.PositionsPx["SECOND"].SequenceEqual(new[] { 2560.0, -1080.0 }));
        });
        Test("null position entries are discarded", () =>
        {
            string path = Path.Combine(root, "null-position.json");
            File.WriteAllText(path, """{"PositionsPx":{"bad":null,"good":[12,34]},"ScaleVersion":2}""");
            var cfg = Config.Load(path);
            Check(!cfg.PositionsPx.ContainsKey("bad") && cfg.PositionsPx["good"][0] == 12);
        });
        Test("null layouts do not crash scale migration", () =>
        {
            string path = Path.Combine(root, "null-layout.json");
            File.WriteAllText(path, """{"Layouts":{"bad":null,"good":{"Cols":3}},"ScaleVersion":0}""");
            var cfg = Config.Load(path);
            Check(!cfg.Layouts.ContainsKey("bad") && cfg.Layouts["good"].Cols == 3);
        });
        Test("case-colliding keys preserve unrelated settings", () =>
        {
            string path = Path.Combine(root, "case.json");
            File.WriteAllText(path, """{"PositionsPx":{"Test":[1,2],"test":[3,4]},"ShowGuides":false}""");
            var cfg = Config.Load(path);
            Check(!cfg.ShowGuides && cfg.PositionsPx.Count == 1);
        });
        Test("hover expand delay is clamped to 0-1 s in 0.1 s steps", () =>
        {
            string path = Path.Combine(root, "hover.json");
            File.WriteAllText(path, """{"HoverExpandDelay":-50}""");
            var low = Config.Load(path);
            File.WriteAllText(path, """{"HoverExpandDelay":99999}""");
            var high = Config.Load(path);
            Check(low.HoverExpandDelay == 0 && high.HoverExpandDelay == 1000 && low.HoverExpand && Config.NormalizeHoverDelay(449) == 400);
        });
        Test("card bar settings are normalized", () =>
        {
            string path = Path.Combine(root, "bar.json");
            File.WriteAllText(path, """{"BarEdge":9,"BarKeys":[164,16,160,0,999,68],"BarSize":80,"BarDelay":2345,"ShowGuides":false}""");
            var cfg = Config.Load(path);
            Check(cfg.BarEdge == ScreenEdge.Right && string.Join(",", cfg.BarKeys) == "18,16,68" && KeyCombo.Text(cfg.BarKeys) == "Alt + Shift + D"
                && string.Join(",", KeyCombo.Clean(new[] { KeyCombo.Ctrl })) == "17" && KeyCombo.Clean(new[] { 0x41 }).Single() == KeyCombo.Shift
                && !KeyCombo.IsValid(new[] { KeyCombo.Ctrl, 0x41, 0x42 }) && KeyCombo.ToHotkey(cfg.BarKeys) == (0x5u, 0x44u) && cfg.BarSize == 33
                && cfg.BarDelay == 2000 && !cfg.ShowGuides && cfg.BarEnabled);
        });
    }

    private static void ConfigurationRecoveryTests(string root)
    {
        Test("non-finite zoom is normalized", () => Check(double.IsFinite(new CardLayout { Zoom = double.NaN }.Normalized().Zoom)));
        Test("saved backup recovers interrupted configuration", () =>
        {
            string path = Path.Combine(root, "atomic.json");
            var cfg = Config.Load(path);
            cfg.ShowGuides = false;
            cfg.Save();
            cfg.Save();
            File.WriteAllText(path, "{interrupted");
            Check(!Config.Load(path).ShowGuides);
        });
    }
}
