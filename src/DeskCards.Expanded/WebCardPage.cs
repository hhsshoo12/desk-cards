using System;
using Microsoft.Web.WebView2.Core;

namespace DeskCards.Expanded;

/// <summary>웹 카드 화면(WebView2) 설정을 한 곳에 둔다. 바탕화면 카드·카드 바 안 카드가 똑같이 쓴다.</summary>
internal static class WebCardPage
{
    /// <summary>카드 폴더를 가상 주소로 열고 deskcard 스크립트를 넣은 뒤 화면을 띄운다. beforeNavigate는 이동 직전에 불린다.</summary>
    public static async System.Threading.Tasks.Task StartAsync(CoreWebView2 core, string id, string folder, string html,
        EventHandler<CoreWebView2WebMessageReceivedEventArgs> onMessage, Func<bool> dead, Action<CoreWebView2>? beforeNavigate = null)
    {
        var s = core.Settings;
        s.IsWebMessageEnabled = true;
        s.IsNonClientRegionSupportEnabled = true;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif
        core.WebMessageReceived += onMessage;
        core.ProcessFailed += (_, e) =>
        {
            if (!dead() && e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                try { core.Reload(); } catch (Exception) { }
        };
        core.NavigationStarting += (_, e) =>
        {
            // 카드는 자기 폴더 주소 안에서만 움직인다(외부 사이트가 deskcard API를 쓰지 못하게).
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || !u.Host.EndsWith(".deskcard", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true; // 카드 안 링크가 새 창으로 열리지 않게
        beforeNavigate?.Invoke(core);
        string host = id + ".deskcard";
        core.SetVirtualHostNameToFolderMapping(host, folder, CoreWebView2HostResourceAccessKind.Allow);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WebCardWindow.Shim);
        core.Navigate(Url(host, html));
    }

    /// <summary>카드 폴더 안 상대 경로의 가상 주소.</summary>
    public static string Url(string host, string relative) =>
        $"https://{host}/{Uri.EscapeDataString(relative.Replace('\\', '/').TrimStart('/')).Replace("%2F", "/")}";
}
