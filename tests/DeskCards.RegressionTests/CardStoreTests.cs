using System;
using System.IO;
using System.IO.Compression;
using DeskCards;

internal static partial class Program
{
    private static void CardStoreTests(string root)
    {
        Test("card store: copies arbitrary nested assets and preserves originals", () =>
        {
            string source = Path.Combine(root, "store-source");
            Directory.CreateDirectory(Path.Combine(source, "data"));
            File.WriteAllText(Path.Combine(source, "manifest.json"), "{\"id\":\"com.test.copy\",\"name\":\"copy\",\"size\":[200,100]}");
            File.WriteAllText(Path.Combine(source, "data", "state.bin"), "original");
            var store = new CardStore(Path.Combine(root, "store"));
            var imported = store.ImportFolder(source);
            File.WriteAllText(Path.Combine(source, "data", "state.bin"), "changed");
            Check(File.ReadAllText(Path.Combine(imported.Directory, "data", "state.bin")) == "original");
            bool duplicate = false;
            try { store.ImportFolder(source); } catch (IOException) { duplicate = true; }
            Check(duplicate && File.ReadAllText(Path.Combine(imported.Directory, "data", "state.bin")) == "original");
            var linked = store.ImportFolder(source, linked: true);
            Check(linked.Linked && linked.Directory == source);
        });
        Test("card store: archive is extracted once and failed imports leave no installed folder", () =>
        {
            string source = Path.Combine(root, "zip-source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "manifest.json"), "{\"id\":\"com.test.zip\",\"name\":\"zip\",\"size\":[100,100]}");
            File.WriteAllText(Path.Combine(source, "card.html"), "<p>zip");
            string archive = Path.Combine(root, "card.dard"); ZipFile.CreateFromDirectory(source, archive);
            var store = new CardStore(Path.Combine(root, "zip-store"));
            var imported = store.ImportArchive(archive);
            Check(imported.Source == archive && File.Exists(Path.Combine(imported.Directory, "card.html")));
            Check(Directory.GetDirectories(store.Root).Length == 1);
        });
    }
}
