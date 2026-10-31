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
    // 기존 패키지 자동 감시 없이도 브라우저 복구 회귀 검사는 카드 창을 직접 구성한다.
    private static void AttachRecoveryCards(GroupManager mgr, CardDocument package)
    {
        string folder = Path.Combine(Path.GetTempPath(), "DeskCards-browser-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "card.html"), "<p>recovered");
        var server = new CardStaticServer(folder, System.Net.IPAddress.Loopback, 0);
        package = package with { Url = $"http://127.0.0.1:{server.Port}/", Background = true };
        var runtime = new CardRuntime(package, mgr, default) { Backend = server };
        var field = typeof(GroupManager).GetField("_webCards", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var cards = (System.Collections.Generic.Dictionary<string, CardRuntime>)field.GetValue(mgr)!;
        cards.Add(package.Path, runtime);
        var create = typeof(GroupManager).GetMethod("CreateWebCardWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var card in package.Cards) create.Invoke(mgr, new object[] { runtime, card });
    }

}
