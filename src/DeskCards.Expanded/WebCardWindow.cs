using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeskCards.Expanded;

internal sealed class WebCardHandle : DesktopCardHandle<WebCardWindow>, IWebCardHandle
{
    public WebCardHandle(WebCardOptions options, IWebCardHost host) => Create(() => new WebCardWindow(options, host));

    public void ToPage(string json) => Run(() => Window.ToPage(json));
    public void Reload() => Run(Window.Reload);
    public void SetExe(bool running) => Run(() => Window.SetExe(running));
}

/// <summary>
/// 웹 카드 한 장(바탕화면). 창 규칙은 폴더 카드와 같은 <see cref="DesktopCardWindow"/>이고, 안에 WebView2를 담아 카드 폴더를 가상 주소로 열어 준다.
/// 페이지에는 window.deskcard(메시지 주고받기·창 조절)를 넣어 준다.
/// </summary>
internal sealed class WebCardWindow : DesktopCardWindow
{
    private static Task<CoreWebView2Environment>? _environment;

    private readonly WebCardOptions _o;
    private readonly IWebCardHost _host;
    private readonly WebView2 _web;
    private bool _booted, _exe;

    public WebCardWindow(WebCardOptions options, IWebCardHost host)
        : base(host, options.Id, "Desk Cards " + options.Name, options.Palette, options.X, options.Y, options.Width, options.Height, options.Visible)
    {
        _o = options;
        _host = host;
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
        Surface.Children.Add(_web);
        Closed += (_, _) => { try { _web.Dispose(); } catch (Exception) { } };
        Loaded += async (_, _) => await StartAsync();
    }

    protected override void OnBoundsChanged() => PushBounds();

    protected override void OnVisibilityChanged(bool visible) => PostEvent("visibility", visible);

    protected override void OnPaletteChanged(Palette palette) => PostEvent("theme", palette.Light ? "light" : "dark");

    internal void SetExe(bool running)
    {
        if (_exe == running) return;
        _exe = running;
        PostEvent("exe", running);
    }

    protected override void OnEditingChanged(bool editing)
    {
        PostEvent("editing", editing);
        // 편집 중에는 페이지 전체를 끌기 영역으로 만든다(웹 화면은 그대로 그려지고, 끌면 창이 옮겨진다).
        _ = _web?.CoreWebView2?.ExecuteScriptAsync(editing
            ? "document.documentElement.style.setProperty('app-region','drag')"
            : "document.documentElement.style.removeProperty('app-region')");
    }
    // ----- 웹 화면 -----

    private async Task StartAsync()
    {
        if (_booted || Dead) return;
        _booted = true;
        try
        {
            _environment ??= CoreWebView2Environment.CreateAsync(null, _o.BrowserDataDir);
            await _web.EnsureCoreWebView2Async(await _environment);
        }
        catch (Exception ex)
        {
            _environment = null; // 실패한 환경을 붙들어 두지 않는다
            ShowError(ex is WebView2RuntimeNotFoundException ? "이 카드를 보여 주려면 Microsoft Edge WebView2 런타임이 필요해요." : "카드를 띄우지 못했어요: " + ex.Message);
            return;
        }
        if (Dead) return;
        var core = _web.CoreWebView2;
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
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, _) => { if (Editing) OnEditingChanged(true); }; // 편집 중에 열리거나 다시 불러와도 끌기 영역을 다시 건다
        core.ProcessFailed += (_, e) =>
        {
            if (!Dead && e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                try { core.Reload(); } catch (Exception) { }
        };
        core.NavigationStarting += (_, e) =>
        {
            // 카드는 자기 폴더 주소 안에서만 움직인다(외부 사이트가 deskcard API를 쓰지 못하게).
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || !u.Host.EndsWith(".deskcard", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true; // 카드 안 링크가 새 창으로 열리지 않게
        string host = _o.Id + ".deskcard";
        core.SetVirtualHostNameToFolderMapping(host, _o.Folder, CoreWebView2HostResourceAccessKind.Allow);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim);
        core.Navigate($"https://{host}/{Uri.EscapeDataString(_o.Html.Replace('\\', '/')).Replace("%2F", "/")}");
    }

    private void ShowError(string text)
    {
        _web.Visibility = Visibility.Collapsed;
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        Surface.Children.Insert(0, label);
    }

    internal void Reload()
    {
        try { _web.CoreWebView2?.Reload(); } catch (Exception) { }
    }

    internal void ToPage(string json) => Post("{\"t\":\"msg\",\"d\":" + json + "}");

    private void PostEvent(string name, object value) =>
        Post(JsonSerializer.Serialize(new Dictionary<string, object> { ["t"] = "evt", ["n"] = name, ["v"] = value }));

    /// <summary>페이지가 준비되면 현재 상태를 한꺼번에 알려 준다.</summary>
    private void PushState()
    {
        PushBounds();
        PostEvent("theme", Light ? "light" : "dark");
        PostEvent("editing", Editing);
        PostEvent("exe", _exe);
    }

    private void PushBounds()
    {
        double k = Scale;
        PostEvent("bounds", new { x = X / k, y = Y / k, w = W / k, h = H / k });
    }

    private void Post(string json)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(json); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException) { }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            switch (root.GetProperty("t").GetString())
            {
                case "msg": _host.FromPage(root.GetProperty("d").GetRawText()); break;
                case "menu": _host.Menu(); break;
                case "ready": PushState(); break;
                case "win": OnWindowRequest(root); break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    /// <summary>페이지의 창 조절 요청. 크기·자리는 CSS 픽셀(=DIP)이고 화면 좌표는 주 모니터 기준 물리 픽셀을 배율로 나눈 값이다.</summary>
    private void OnWindowRequest(JsonElement m)
    {
        double k = Scale;
        double Num(string key, double fallback) => m.TryGetProperty(key, out var v) && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : fallback;
        switch (m.GetProperty("a").GetString())
        {
            case "resize":
                SetBounds(X, Y, (int)Math.Round(Math.Clamp(Num("w", W / k), 80, 8000) * k), (int)Math.Round(Math.Clamp(Num("h", H / k), 60, 8000) * k));
                _host.Bounds(X, Y, W, H);
                break;
            case "move":
                SetBounds((int)Math.Round(Num("x", X / k) * k), (int)Math.Round(Num("y", Y / k) * k), W, H);
                _host.Bounds(X, Y, W, H);
                break;
            case "visible":
            {
                bool show = m.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.True;
                SetVisible(show);
                _host.VisibilityChanged(show);
            }
                break;
        }
    }

    private const string Shim = """
(()=>{const w=window.chrome.webview;
const d=window.deskcard={bounds:null,visible:true,theme:'light',editing:false,exe:false,onmessage:null,onbounds:null,onvisibility:null,ontheme:null,onediting:null,onexe:null,
 send:x=>w.postMessage({t:'msg',d:x}),
 window:{resize:(width,height)=>w.postMessage({t:'win',a:'resize',w:width,h:height}),
  move:(x,y)=>w.postMessage({t:'win',a:'move',x:x,y:y}),
  show:()=>w.postMessage({t:'win',a:'visible',v:true}),
  hide:()=>w.postMessage({t:'win',a:'visible',v:false})}};
w.addEventListener('message',e=>{const m=e.data;if(!m)return;
 if(m.t==='msg'){if(typeof d.onmessage==='function')d.onmessage(m.d);window.dispatchEvent(new CustomEvent('deskcard',{detail:m.d}));}
 else if(m.t==='evt'){if(m.n==='bounds'){d.bounds=m.v;if(typeof d.onbounds==='function')d.onbounds(m.v);}
  else if(m.n==='visibility'){d.visible=m.v;if(typeof d.onvisibility==='function')d.onvisibility(m.v);}
  else if(m.n==='theme'){d.theme=m.v;document.documentElement.dataset.theme=m.v;if(typeof d.ontheme==='function')d.ontheme(m.v);}
  else if(m.n==='editing'){d.editing=m.v;if(typeof d.onediting==='function')d.onediting(m.v);}
  else if(m.n==='exe'){d.exe=m.v;if(typeof d.onexe==='function')d.onexe(m.v);}}});
window.addEventListener('contextmenu',e=>{e.preventDefault();w.postMessage({t:'menu'});},true);
w.postMessage({t:'ready'});})();
""";
}
