using System;
using System.IO;
using DeskCards;

internal static partial class Program
{
    private static void CardManifestTests(string root)
    {
        Test("card manifest: valid folder and optional background", () =>
        {
            string dir = Path.Combine(root, "manifest-valid");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "manifest.json");
            File.WriteAllText(path, "{\"id\":\"com.example.clock\",\"name\":\"시계\",\"size\":[200,150]}");
            var card = CardManifest.Load(dir);
            Check(card.Id == "com.example.clock" && card.Name == "시계" && card.Width == 200 && card.Height == 150 && !card.Background);
            File.WriteAllText(path, "{\"id\":\"com.example.clock\",\"name\":\"시계\",\"size\":[200,150],\"background\":true}");
            Check(CardManifest.Load(dir).Background);
        });
        Test("card manifest: invalid id, size and malformed files report an error", () =>
        {
            string dir = Path.Combine(root, "manifest-invalid");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "manifest.json");
            foreach (string json in new[] {
                "{\"id\":\"../outside\",\"name\":\"x\",\"size\":[1,1]}",
                "{\"id\":\"con.test\",\"name\":\"x\",\"size\":[1,1]}",
                "{\"id\":\"ok\",\"name\":\"x\",\"size\":[0,1]}",
                "{\"id\":\"ok\",\"name\":\"x\",\"size\":[1,1],\"background\":\"true\"}",
                "{" })
            {
                File.WriteAllText(path, json);
                bool rejected = false;
                try { CardManifest.Load(dir); }
                catch (CardManifestException ex) { rejected = !string.IsNullOrWhiteSpace(ex.Message); }
                Check(rejected);
            }
            File.Delete(path);
            try { CardManifest.Load(dir); throw new Exception("Missing manifest was accepted"); }
            catch (CardManifestException ex) { Check(!string.IsNullOrWhiteSpace(ex.Message)); }
        });
    }
}
