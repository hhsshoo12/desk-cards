using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeskCards;

/// <summary>
/// .dard의 HTML 한 장(card.html 또는 settings.html)을 띄우는 WebView2.
/// 보통(자식 창) WebView2라 클릭·더블클릭·키보드가 브라우저 그대로 동작한다. 배경은 투명이라 창의 아크릴 배경이 비친다.
/// 자식 창 위에는 WPF가 그릴 수 없으므로, 편집 모드처럼 창이 누르기를 받아야 할 때는 화면을 그림으로 바꿔 둔다(Freeze).
/// 페이지와는 JSON 메시지 한 가지 모양으로만 주고받고, 요청은 전부 여기서 검사한다(desk 라이브러리 안의 검사는 믿지 않는다).
/// </summary>
internal sealed class DardView : Grid
{
    /// <summary>카드가 저장하는 설정·주고받는 메시지 하나의 최대 크기(JSON 글자 수).</summary>
    public const int MaxMessage = 64 * 1024;

    /// <summary>사용자가 누른 직후에만 되는 일(브라우저 열기, 설정 열기)의 허용 시간.</summary>
    private const int GestureMs = 1500;

    private static Task<CoreWebView2Environment>? _environment;

    private readonly DardRuntime _runtime;
    private readonly WebView2 _web;
    private readonly Image _snapshot;
    private readonly TextBlock _error;
    private readonly string _page;
    private double _zoom = 1;
    private int? _usedInput;
    private bool _frozen, _closed;

    /// <param name="settings">settings.html(설정 화면)인지, card.html(카드)인지.</param>
    /// <param name="viewport">페이지가 보는 화면 크기(CSS 픽셀). 실제 크기와의 차이는 확대 비율로 맞춘다.</param>
    public DardView(DardRuntime runtime, string cardId, bool settings, Size viewport)
    {
        _runtime = runtime;
        CardId = cardId;
        IsSettings = settings;
        Viewport = viewport;
        _page = settings ? DardPackage.SettingsPage : DardPackage.CardPage;

        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
        _snapshot = new Image { Stretch = Stretch.Fill, Visibility = Visibility.Collapsed };
        _error = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _error.SetResourceReference(TextBlock.ForegroundProperty, "SubFg");
        Children.Add(_web);
        Children.Add(_snapshot);
        Children.Add(_error);
        runtime.Register(this);

        Theme.Changed += OnThemeChanged;
        Loaded += async (_, _) => await StartAsync();
    }

    public string CardId { get; }
    public bool IsSettings { get; }
    public Size Viewport { get; }

    /// <summary>설정 화면이 닫아 달라고 할 때.</summary>
    public Action? CloseRequested { get; set; }

    /// <summary>카드 위에서 우클릭했을 때(카드 메뉴를 띄운다).</summary>
    public Action? MenuRequested { get; set; }

    /// <summary>페이지 확대 비율(실제 DIP ÷ CSS 픽셀). 카드를 키우면 흐려지지 않게 페이지가 다시 그린다.</summary>
    public void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.25, 5);
        if (_web.CoreWebView2 != null) _web.ZoomFactor = _zoom;
    }

    private static Task<CoreWebView2Environment> SharedEnvironment()
    {
        // 한 브라우저 프로세스를 모든 카드가 같이 쓴다. 카드 파일은 WebResourceRequested가 직접 주므로 브라우저는 네트워크가 필요 없다.
        // 그래서 네트워크를 통째로 막는다: 이동·미리 연결은 요청 검사 전에 소켓부터 여므로(회귀 테스트로 확인)
        // 없는 프록시로 보내고(루프백도 예외 없이), 이름 풀이도 전부 실패시킨다. WebRTC도 프록시 밖 UDP를 쓰지 못하게 한다.
        // 외부 통신은 나중에 desk.fetch(C#)로만 한다.
        return _environment ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebDataDir,
            new CoreWebView2EnvironmentOptions(
                "--proxy-server=http://127.0.0.1:9 --proxy-bypass-list=<-loopback> --host-resolver-rules=\"MAP * ~NOTFOUND\" " +
                "--force-webrtc-ip-handling-policy=disable_non_proxied_udp --webrtc-ip-handling-policy=disable_non_proxied_udp"));
    }

    private async Task StartAsync()
    {
        if (_web.CoreWebView2 != null || _closed) return;
        try
        {
            await _web.EnsureCoreWebView2Async(await SharedEnvironment());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError("이 카드를 보여 주려면 Microsoft Edge WebView2 런타임이 필요해요.");
            return;
        }
        catch (Exception ex)
        {
            ShowError("카드를 띄우지 못했어요: " + ex.Message);
            return;
        }
        if (_closed) return;

        var core = _web.CoreWebView2!;
        var s = core.Settings;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = true;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDefaultScriptDialogsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
#if DEBUG
        s.AreDevToolsEnabled = true;
#else
        s.AreDevToolsEnabled = false;
#endif

        // 카드 파일 두 장 말고는 아무 데도 요청하지 못한다(외부 네트워크 권한은 아직 없다).
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += (_, e) => { if (e.Uri != PageUrl) e.Cancel = true; };
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.ScriptDialogOpening += (_, _) => { }; // Accept를 부르지 않으면 닫힌다(바탕화면에 경고 창을 띄우지 않는다).
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, e) =>
        {
            if (!_closed && e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) core.Reload();
        };

        await core.AddScriptToExecuteOnDocumentCreatedAsync(DeskScript());
        _web.ZoomFactor = _zoom;
        core.Navigate(PageUrl);
    }

    private string PageUrl => $"https://{_runtime.Package.Host}/{_page}";

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.Visibility = Visibility.Visible;
        _web.Visibility = Visibility.Collapsed;
    }

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = _web.CoreWebView2.Environment;
        var uri = new Uri(e.Request.Uri);
        var pkg = _runtime.Package;
        byte[]? body = null;
        if (uri.Scheme == "https" && string.Equals(uri.Host, pkg.Host, StringComparison.OrdinalIgnoreCase) && e.Request.Method == "GET")
        {
            if (uri.AbsolutePath == "/" + DardPackage.CardPage) body = pkg.CardHtml;
            else if (uri.AbsolutePath == "/" + DardPackage.SettingsPage) body = pkg.SettingsHtml;
        }
        if (body == null)
        {
            e.Response = env.CreateWebResourceResponse(null, 403, "Forbidden", "");
            return;
        }
        // 스크립트·스타일은 페이지 안에 있는 것만, 이미지·글꼴·소리는 data:/blob:만. 연결·폼·프레임·플러그인·wasm은 막는다.
        const string csp = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
            "img-src data: blob:; font-src data:; media-src data: blob:; connect-src 'none'; " +
            "base-uri 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; worker-src 'none'";
        e.Response = env.CreateWebResourceResponse(new MemoryStream(body), 200, "OK",
            "Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: " + csp + "\r\nCache-Control: no-store");
    }

    // ----- desk 라이브러리 -----

    private string DeskScript()
    {
        var info = new JsonObject
        {
            ["cardId"] = CardId,
            ["view"] = IsSettings ? "settings" : "card",
            ["width"] = Math.Round(Viewport.Width),
            ["height"] = Math.Round(Viewport.Height),
            ["cards"] = new JsonArray(_runtime.Package.Cards.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name }).ToArray()),
            ["theme"] = ThemeJson(),
        };
        return DeskLibrary.Replace("__INFO__", info.ToJsonString());
    }

    private static JsonObject ThemeJson()
    {
        var colors = new JsonObject();
        foreach (var (k, v) in Theme.CssColors()) colors[k] = v;
        return new JsonObject { ["dark"] = !Theme.IsLight, ["colors"] = colors };
    }

    /// <summary>페이지에 넣는 desk 라이브러리. 호스트에 JSON 메시지를 보내고 답을 Promise로 돌려준다.</summary>
    private const string DeskLibrary = """
        (() => {
          const wv = window.chrome && window.chrome.webview;
          if (!wv) return;
          const info = __INFO__;
          let theme = info.theme, seq = 0;
          const pending = new Map(), listeners = new Map();
          const sheet = new CSSStyleSheet();
          const applyTheme = () => {
            const vars = Object.entries(theme.colors).map(([k, v]) => `--desk-${k}:${v};`).join('');
            sheet.replaceSync(`:where(:root){${vars}color:var(--desk-fg);font-family:"Segoe UI Variable Text","Segoe UI","Malgun Gothic",sans-serif;}`);
          };
          applyTheme();
          document.adoptedStyleSheets = [sheet];
          const emit = (name, data) => (listeners.get(name) || []).slice().forEach(cb => { try { cb(data); } catch (e) { console.error(e); } });
          wv.addEventListener('message', ev => {
            const m = ev.data;
            if (!m || typeof m !== 'object') return;
            if (m.t === 'reply') {
              const p = pending.get(m.id);
              if (!p) return;
              pending.delete(m.id);
              if (m.ok) p.resolve(m.value); else { const e = new Error(m.error); e.name = m.name || 'Error'; p.reject(e); }
            } else if (m.t === 'event') {
              if (m.name === 'theme') { theme = m.data; applyTheme(); }
              emit(m.name, m.data);
            }
          });
          const call = (fn, args) => new Promise((resolve, reject) => {
            const id = ++seq;
            pending.set(id, { resolve, reject });
            wv.postMessage({ t: 'call', id, fn, args: args === undefined ? null : args });
          });
          const desk = {
            view: info.view,
            card: Object.freeze({
              id: () => info.cardId,
              size: () => Promise.resolve({ width: info.width, height: info.height }),
              settings: Object.freeze({ get: () => call('settings.get'), set: value => call('settings.set', value) }),
              openSettings: () => call('openSettings'),
              closeSettings: () => call('closeSettings'),
            }),
            cards: Object.freeze({
              list: () => Promise.resolve(info.cards.map(c => ({ ...c }))),
              post: (id, data) => call('cards.post', { to: String(id), data }),
            }),
            theme: () => Promise.resolve(JSON.parse(JSON.stringify(theme))),
            openUrl: url => call('openUrl', String(url)),
            on: (name, cb) => { if (typeof cb === 'function') { if (!listeners.has(name)) listeners.set(name, []); listeners.get(name).push(cb); } },
            off: (name, cb) => { const l = listeners.get(name); if (l) listeners.set(name, l.filter(x => x !== cb)); },
          };
          // 카드 위 우클릭은 언제나 카드 메뉴(페이지가 자기 메뉴를 만들지 않는다).
          window.addEventListener('contextmenu', e => {
            e.preventDefault();
            e.stopImmediatePropagation();
            if (e.isTrusted && info.view === 'card') wv.postMessage({ t: 'menu' });
          }, true);
          Object.defineProperty(window, 'desk', { value: Object.freeze(desk), writable: false, configurable: false });
          for (const k of ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCDataChannel', 'WebAssembly'])
            try { Object.defineProperty(window, k, { value: undefined, writable: false, configurable: false }); } catch { }
        })();
        """;

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) ||
            !string.Equals(source.Host, _runtime.Package.Host, StringComparison.OrdinalIgnoreCase)) return;
        JsonNode? msg;
        try { msg = JsonNode.Parse(e.WebMessageAsJson); }
        catch (JsonException) { return; }
        if (msg is JsonObject { } m && (string?)m["t"] == "menu")
        {
            if (!IsSettings && IsGesture()) MenuRequested?.Invoke();
            return;
        }
        if (msg is not JsonObject o || (string?)o["t"] != "call" || o["id"] is not JsonValue idValue || !idValue.TryGetValue(out int id)) return;
        string fn = (string?)o["fn"] ?? "";
        JsonNode? args = o["args"];
        try
        {
            Reply(id, Handle(fn, args));
        }
        catch (DardCallException ex)
        {
            Post(new JsonObject { ["t"] = "reply", ["id"] = id, ["ok"] = false, ["name"] = ex.Kind, ["error"] = ex.Message });
        }
    }

    private sealed class DardCallException : Exception
    {
        public DardCallException(string kind, string message) : base(message) => Kind = kind;
        public string Kind { get; }
    }

    private JsonNode? Handle(string fn, JsonNode? args)
    {
        switch (fn)
        {
            case "settings.get":
                return _runtime.GetSettings(CardId);
            case "settings.set":
                string json = args?.ToJsonString() ?? "null";
                if (json.Length > MaxMessage) throw new DardCallException("RangeError", "설정이 너무 커요(64KB까지).");
                _runtime.SetSettings(CardId, args, from: this);
                return null;
            case "openSettings":
                if (IsSettings) throw new DardCallException("Error", "설정 화면에서는 부를 수 없어요.");
                if (!_runtime.Package.SettingsRatio.HasValue) throw new DardCallException("Error", "이 카드에는 settings.html이 없어요.");
                RequireGesture();
                _runtime.OpenSettings(CardId);
                return null;
            case "closeSettings":
                if (!IsSettings) throw new DardCallException("Error", "설정 화면에서만 부를 수 있어요.");
                CloseRequested?.Invoke();
                return null;
            case "cards.post":
                string to = (string?)args?["to"] ?? "";
                string data = args?["data"]?.ToJsonString() ?? "null";
                if (data.Length > MaxMessage) throw new DardCallException("RangeError", "메시지가 너무 커요(64KB까지).");
                if (!_runtime.Deliver(CardId, to, args?["data"]?.DeepClone()))
                    throw new DardCallException("Error", $"'{to}' 카드가 없어요.");
                return null;
            case "openUrl":
                string url = args?.GetValue<string>() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
                    throw new DardCallException("Error", "http(s) 주소만 열 수 있어요.");
                RequireGesture();
                try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })?.Dispose(); } catch { }
                return null;
            default:
                throw new DardCallException("Error", $"모르는 기능이에요: {fn}");
        }
    }

    /// <summary>
    /// 사용자가 이 화면을 방금 눌렀는지(또는 키를 쳤는지). 누르면 이 창이 활성 창이 되므로,
    /// 활성 창이 이 창이고 마지막 입력이 방금이면 사용자가 한 일로 본다.
    /// </summary>
    private bool IsGesture()
    {
        if (Window.GetWindow(this) is not { } w || Native.GetForegroundWindow() != Hwnd.Of(w)) return false;
        return Native.LastInputTick() is int t && unchecked(Environment.TickCount - t) <= GestureMs;
    }

    /// <summary>아무 때나 브라우저·창을 띄우지 못하게 한다. 한 번 입력에 한 번만 된다.</summary>
    private void RequireGesture()
    {
        int? input = Native.LastInputTick();
        if (!IsGesture() || input == _usedInput)
            throw new DardCallException("NotAllowedError", "카드를 누른 직후에만 할 수 있어요.");
        _usedInput = input;
    }

    private void Reply(int id, JsonNode? value) =>
        Post(new JsonObject { ["t"] = "reply", ["id"] = id, ["ok"] = true, ["value"] = value });

    /// <summary>페이지에 이벤트를 보낸다(desk.on으로 받는다).</summary>
    public void Emit(string name, JsonNode? data) =>
        Post(new JsonObject { ["t"] = "event", ["name"] = name, ["data"] = data });

    private void Post(JsonObject message)
    {
        if (_closed || _web.CoreWebView2 == null) return;
        try { _web.CoreWebView2.PostWebMessageAsJson(message.ToJsonString()); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(() => Emit("theme", ThemeJson()));

    /// <summary>
    /// 켜면 지금 화면을 그림으로 찍어 웹 화면 대신 보여 준다. 그동안은 누르기가 페이지 대신 WPF(창)로 온다.
    /// </summary>
    public async void Freeze(bool on)
    {
        _frozen = on;
        if (!on)
        {
            _snapshot.Visibility = Visibility.Collapsed;
            _snapshot.Source = null;
            if (_error.Visibility != Visibility.Visible) _web.Visibility = Visibility.Visible;
            return;
        }
        var core = _web.CoreWebView2;
        if (core != null && _web.Visibility == Visibility.Visible)
        {
            try
            {
                var png = new MemoryStream();
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, png);
                png.Position = 0;
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = png;
                bmp.EndInit();
                bmp.Freeze();
                if (_frozen && !_closed) _snapshot.Source = bmp;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException) { }
        }
        if (!_frozen || _closed) return;
        _snapshot.Visibility = Visibility.Visible;
        if (_web.Visibility == Visibility.Visible) _web.Visibility = Visibility.Hidden;
    }

    /// <summary>키보드 입력을 이 화면으로(설정 창).</summary>
    public void FocusPage()
    {
        _web.Focus();
    }

    public void Reload() => _web.CoreWebView2?.Reload();

    /// <summary>창을 닫을 때 부른다. 브라우저 화면을 정리한다.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Theme.Changed -= OnThemeChanged;
        _runtime.Forget(this);
        try { _web.Dispose(); } catch { }
    }
}
