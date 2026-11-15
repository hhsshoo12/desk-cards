using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeskCards.Expanded;

/// <summary>
/// 웹 카드 페이지가 deskcard.overlay.open으로 여는 임시 창. 카드 자리 밖으로 나오는 큰 화면(펼친 목록·설정 등)에 쓴다.
/// 카드와 같은 폴더의 다른 페이지를 열고, 다른 곳을 누르거나 Esc를 누르거나 카드가 닫으라고 하면 사라진다.
/// 카드 화면과는 deskcard.overlay.send / onmessage로 말을 주고받는다.
/// </summary>
internal sealed class CardOverlayWindow : Window
{
    private readonly WebCardWindow _owner;
    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private readonly string _host, _folder, _url, _browserDir;
    private readonly bool _light;
    private bool _closing;

    public CardOverlayWindow(WebCardWindow owner, string host, string folder, string relativeUrl, string browserDir, bool light, PixelRect screen)
    {
        _owner = owner;
        _host = host;
        _folder = folder;
        _browserDir = browserDir;
        _light = light;
        _url = $"https://{host}/{Uri.EscapeDataString(relativeUrl.Replace('\\', '/').TrimStart('/')).Replace("%2F", "/")}";

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;
        Width = Height = 1;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        Title = "Desk Cards overlay";
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Content = _web;
        _screen = screen;

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) => await StartAsync();
        Deactivated += (_, _) => { if (IsLoaded) _owner.OverlayDismissed(this); };
        Closed += (_, _) => { try { _web.Dispose(); } catch (Exception) { } _owner.OverlayDismissed(this); };
    }

    private readonly PixelRect _screen;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, new IntPtr(style & ~0x00080000L)); // WS_SYSMENU 제거
        long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new IntPtr(ex | 0x80L)); // 도구 창(Alt+Tab에 안 나옴)
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        SetDwm(hwnd, 20, _light ? 0 : 1);
        SetDwm(hwnd, 33, 2);
        SetDwm(hwnd, 38, 2);
        SetWindowPos(hwnd, new IntPtr(-1), _screen.Left, _screen.Top, _screen.Width, _screen.Height, 0x0010);
    }

    /// <summary>창을 띄운다(카드 창이 부른다).</summary>
    public void Present()
    {
        Show();
        Activate();
    }

    private async Task StartAsync()
    {
        try
        {
            await _web.EnsureCoreWebView2Async(await WebCardWindow.GetEnvironment(_browserDir));
        }
        catch (Exception)
        {
            Dismiss();
            return;
        }
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
        s.AreDevToolsEnabled = false;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) || !u.Host.EndsWith(".deskcard", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.SetVirtualHostNameToFolderMapping(_host, _folder, CoreWebView2HostResourceAccessKind.Allow);
        await core.AddScriptToExecuteOnDocumentCreatedAsync("window.__dcOverlay=true;" + WebCardWindow.Shim);
        core.Navigate(_url);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            string? t = root.GetProperty("t").GetString();
            if (t == "ready") Post(WebCardWindow.Evt("theme", _light ? "light" : "dark"));
            else _owner.FromOverlay(this, t, root);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    public void Post(string json)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(json); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException) { }
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch (InvalidOperationException) { }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private static void SetDwm(IntPtr hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}
