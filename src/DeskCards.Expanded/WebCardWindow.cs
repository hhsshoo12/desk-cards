using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeskCards.Expanded;

internal sealed class WebCardHandle : IWebCardHandle
{
    private WebCardWindow _window = null!;

    public WebCardHandle(WebCardOptions options, IWebCardHost host)
    {
        // 창은 WPF 창 스레드에서 만든다.
        WpfThread.Dispatcher.Invoke(() => _window = new WebCardWindow(options, host));
    }

    private void Run(Action action) => WpfThread.Dispatcher.BeginInvoke(action);

    public void ToPage(string json) => Run(() => _window.ToPage(json));
    public void SetBounds(int x, int y, int width, int height) => Run(() => _window.SetBounds(x, y, width, height));
    public void SetEditing(bool editing) => Run(() => _window.SetEditing(editing));
    public void SetVisible(bool visible) => Run(() => _window.SetVisible(visible));
    public void Reload() => Run(_window.Reload);
    public void Close() => Run(_window.Close);
}

/// <summary>
/// 웹 카드 한 장(바탕화면). 보통 WPF 창에 WebView2를 담고, 카드 폴더를 가상 주소로 열어 준다.
/// 눌러도 활성화되지 않고 바탕화면 층(Progman 소유, 맨 아래)에 머문다. 편집 중에는 가장자리에 띠가 생겨서 끌어 옮기고 크기를 바꾼다.
/// 페이지에는 window.deskcard(메시지 주고받기·창 조절)를 넣어 준다.
/// </summary>
internal sealed class WebCardWindow : Window
{
    private const int Band = 12, EdgeBand = 6; // 편집 띠 두께(DIP): 바깥쪽 EdgeBand는 크기 조절, 안쪽은 끌기

    private static Task<CoreWebView2Environment>? _environment;

    private readonly WebCardOptions _o;
    private readonly IWebCardHost _host;
    private readonly WebView2 _web;
    private readonly Border _outline;
    private readonly Grid _root;
    private bool _editing, _closed, _movingZ, _booted;
    private int _x, _y, _w, _h; // 내용 영역(물리 픽셀)

    public WebCardWindow(WebCardOptions options, IWebCardHost host)
    {
        _o = options;
        _host = host;
        (_x, _y, _w, _h) = (options.X, options.Y, options.Width, options.Height);

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        Width = Height = 1;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        Title = "Desk Cards " + options.Name;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
        _outline = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _outline.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(options.Palette.Accent));
        _root = new Grid();
        _root.Children.Add(_web);
        _root.Children.Add(_outline);
        Content = _root;

        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => { _closed = true; _host.Closed(); };
        Loaded += async (_, _) => await StartAsync();
        if (options.Visible) Show();
    }

    // ----- 창 -----

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = Hwnd;
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, new IntPtr(style & ~0x00080000L)); // WS_SYSMENU 제거
        long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new IntPtr(ex | 0x80L | 0x08000000L)); // 도구 창 + 눌러도 활성화 안 됨
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        SetDwm(hwnd, 20, _o.Palette.Light ? 0 : 1);
        SetDwm(hwnd, 33, 2);
        SetDwm(hwnd, 38, 3);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        SetWindowLongPtr(hwnd, -8, FindWindow("Progman", null)); // 소유자 = 바탕화면(Win+D에도 안 숨겨짐)
        ApplyBounds();
        SendToBottom();
    }

    private int Pad => _editing ? (int)Math.Round(Band * Scale) : 0;

    private void ApplyBounds()
    {
        int pad = Pad;
        SetWindowPos(Hwnd, IntPtr.Zero, _x - pad, _y - pad, _w + 2 * pad, _h + 2 * pad, 0x0004 | 0x0010);
        _root.Margin = new Thickness(0);
        _web.Margin = new Thickness(pad / Scale);
        _outline.Margin = new Thickness(pad / Scale - 2);
    }

    internal void SetBounds(int x, int y, int w, int h)
    {
        if (_closed) return;
        (_x, _y, _w, _h) = (x, y, Math.Max(1, w), Math.Max(1, h));
        ApplyBounds();
        PushBounds();
    }

    internal void SetEditing(bool editing)
    {
        if (_closed || (_editing == editing && !editing)) return;
        _editing = editing;
        _outline.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        ApplyBounds();
        if (editing) SetWindowPos(Hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // 어두운 막 위로
        else SendToBottom();
    }

    internal void SetVisible(bool visible)
    {
        if (_closed) return;
        if (visible) { if (!IsVisible) { Show(); SendToBottom(); } }
        else if (IsVisible) Hide();
        PostEvent("visibility", visible);
    }

    private void SendToBottom()
    {
        _movingZ = true;
        try { SetWindowPos(Hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }
        finally { _movingZ = false; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0046 when !_editing: // WM_WINDOWPOSCHANGING: 눌러도 앞으로 나오지 않게
            {
                var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if ((wp.flags & 0x0004) != 0) break;
                if (_movingZ) wp.hwndInsertAfter = new IntPtr(1); else wp.flags |= 0x0004;
                Marshal.StructureToPtr(wp, lParam, false);
                break;
            }
            case 0x0084 when _editing: // WM_NCHITTEST: 편집 띠 = 크기 조절(바깥)·끌기(안쪽)
            {
                handled = true;
                return new IntPtr(HitTest(lParam));
            }
            case 0x0232: // WM_EXITSIZEMOVE
                ReadBounds();
                break;
        }
        return IntPtr.Zero;
    }

    private int HitTest(IntPtr lParam)
    {
        GetWindowRect(Hwnd, out var r);
        int px = (short)(lParam.ToInt64() & 0xFFFF), py = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        int band = Pad, edge = (int)Math.Round(EdgeBand * Scale), corner = (int)Math.Round(24 * Scale);
        bool l = px < r.Left + band, rt = px >= r.Right - band, t = py < r.Top + band, b = py >= r.Bottom - band;
        if (!(l || rt || t || b)) return 1; // HTCLIENT
        bool el = px < r.Left + edge, er = px >= r.Right - edge, et = py < r.Top + edge, eb = py >= r.Bottom - edge;
        bool cl = px < r.Left + corner, cr = px >= r.Right - corner, ct = py < r.Top + corner, cb = py >= r.Bottom - corner;
        if ((el || et) && cl && ct) return 13;
        if ((er || et) && cr && ct) return 14;
        if ((el || eb) && cl && cb) return 16;
        if ((er || eb) && cr && cb) return 17;
        if (el) return 10;
        if (er) return 11;
        if (et) return 12;
        if (eb) return 15;
        return 2; // HTCAPTION: 끌어서 옮기기
    }

    private void ReadBounds()
    {
        if (_closed || !GetWindowRect(Hwnd, out var r)) return;
        int pad = Pad;
        (_x, _y, _w, _h) = (r.Left + pad, r.Top + pad, Math.Max(1, r.Right - r.Left - 2 * pad), Math.Max(1, r.Bottom - r.Top - 2 * pad));
        _host.Bounds(_x, _y, _w, _h);
        PushBounds();
    }

    // ----- 웹 화면 -----

    private async Task StartAsync()
    {
        if (_booted || _closed) return;
        _booted = true;
        try
        {
            _environment ??= CoreWebView2Environment.CreateAsync(null, _o.BrowserDataDir);
            await _web.EnsureCoreWebView2Async(await _environment);
        }
        catch (Exception ex)
        {
            ShowError(ex is WebView2RuntimeNotFoundException ? "이 카드를 보여 주려면 Microsoft Edge WebView2 런타임이 필요해요." : "카드를 띄우지 못했어요: " + ex.Message);
            return;
        }
        if (_closed) return;
        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.IsWebMessageEnabled = true;
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
        core.ProcessFailed += (_, e) =>
        {
            if (!_closed && e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                try { core.Reload(); } catch (Exception) { }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true; // 카드 안 링크가 새 창으로 열리지 않게
        string host = _o.Id + ".deskcard";
        core.SetVirtualHostNameToFolderMapping(host, _o.Folder, CoreWebView2HostResourceAccessKind.Allow);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Shim);
        core.Navigate($"https://{host}/{Uri.EscapeDataString(_o.Html).Replace("%2F", "/")}");
    }

    private void ShowError(string text)
    {
        _web.Visibility = Visibility.Collapsed;
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        label.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_o.Palette.Fg));
        _root.Children.Insert(0, label);
    }

    internal void Reload()
    {
        try { _web.CoreWebView2?.Reload(); } catch (Exception) { }
    }

    internal void ToPage(string json) => Post("{\"t\":\"msg\",\"d\":" + json + "}");

    private void PostEvent(string name, object value) =>
        Post(JsonSerializer.Serialize(new Dictionary<string, object> { ["t"] = "evt", ["n"] = name, ["v"] = value }));

    private void PushBounds()
    {
        double k = Scale;
        PostEvent("bounds", new { x = _x / k, y = _y / k, w = _w / k, h = _h / k });
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
                case "ready": PushBounds(); break;
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
                SetBounds(_x, _y, (int)Math.Round(Math.Clamp(Num("w", _w / k), 80, 8000) * k), (int)Math.Round(Math.Clamp(Num("h", _h / k), 60, 8000) * k));
                _host.Bounds(_x, _y, _w, _h);
                break;
            case "move":
                SetBounds((int)Math.Round(Num("x", _x / k) * k), (int)Math.Round(Num("y", _y / k) * k), _w, _h);
                _host.Bounds(_x, _y, _w, _h);
                break;
            case "visible":
                SetVisible(m.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.True);
                break;
        }
    }

    private const string Shim = """
(()=>{const w=window.chrome.webview;
const d=window.deskcard={bounds:null,visible:true,onmessage:null,onbounds:null,onvisibility:null,
 send:x=>w.postMessage({t:'msg',d:x}),
 window:{resize:(width,height)=>w.postMessage({t:'win',a:'resize',w:width,h:height}),
  move:(x,y)=>w.postMessage({t:'win',a:'move',x:x,y:y}),
  show:()=>w.postMessage({t:'win',a:'visible',v:true}),
  hide:()=>w.postMessage({t:'win',a:'visible',v:false})}};
w.addEventListener('message',e=>{const m=e.data;if(!m)return;
 if(m.t==='msg'){if(typeof d.onmessage==='function')d.onmessage(m.d);window.dispatchEvent(new CustomEvent('deskcard',{detail:m.d}));}
 else if(m.t==='evt'){if(m.n==='bounds'){d.bounds=m.v;if(typeof d.onbounds==='function')d.onbounds(m.v);}
  else if(m.n==='visibility'){d.visible=m.v;if(typeof d.onvisibility==='function')d.onvisibility(m.v);}}});
window.addEventListener('contextmenu',e=>{e.preventDefault();w.postMessage({t:'menu'});},true);
w.postMessage({t:'ready'});})();
""";

    // ----- 네이티브 -----

    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private static void SetDwm(IntPtr hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}