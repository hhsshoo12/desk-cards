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
/// 웹 카드의 HTML 한 장(card.html 또는 settings.html)을 띄우는 WebView2.
/// 보통(자식 창) WebView2라 클릭·더블클릭·키보드가 브라우저 그대로 동작한다. 배경은 투명이라 창의 아크릴 배경이 비친다.
/// 자식 창 위에는 WPF가 그릴 수 없으므로, 편집 모드처럼 창이 누르기를 받아야 할 때는 화면을 그림으로 바꿔 둔다(Freeze).
/// 페이지와는 JSON 메시지 한 가지 모양으로만 주고받고, 요청은 전부 여기서 검사한다(desk 라이브러리 안의 검사는 믿지 않는다).
/// </summary>
internal sealed class CardWebView : Grid
{
    private readonly CardRuntime _runtime;
    private readonly WebView2 _web;
    private readonly Image _snapshot;
    private readonly TextBlock _error;
    private readonly Button _restart;
    private readonly string _page;
    private double _zoom = 1;
    private bool _frozen, _closed;
    private bool _suspended;
    public async void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        var core = _web.CoreWebView2;
        if (_closed || core == null) return;
        try
        {
            if (suspended)
            {
                _web.Visibility = Visibility.Hidden;
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                if (_suspended && !_closed) await core.TrySuspendAsync();
            }
            if (!_suspended && !_closed)
            {
                core.Resume();
                if (!_frozen && _error.Visibility != Visibility.Visible) _web.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }
    private readonly RendererRecoveryPolicy _rendererRecovery = new();

    /// <param name="settings">settings.html(설정 화면)인지, card.html(카드)인지.</param>
    /// <param name="viewport">페이지가 보는 화면 크기(CSS 픽셀). 실제 크기와의 차이는 확대 비율로 맞춘다.</param>
    public CardWebView(CardRuntime runtime, string cardId, bool settings, Size viewport)
    {
        _runtime = runtime;
        CardId = cardId;
        IsSettings = settings;
        Viewport = viewport;
        _page = settings ? CardDocument.SettingsPage : CardDocument.CardPage;

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
        _restart = new Button { Content = "다시 시작", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12) };
        _restart.Click += (_, _) => { if (_runtime.Restart is { } restart) restart(); else Reload(); };
        Children.Add(_restart);

        Theme.Changed += OnThemeChanged;
        Loaded += async (_, _) => await StartAsync();
    }

    public string CardId { get; }
    public bool IsSettings { get; }
    public Size Viewport { get; }

    /// <summary>카드 위에서 우클릭했을 때(카드 메뉴를 띄운다).</summary>
    public Action? MenuRequested { get; set; }

    /// <summary>페이지 확대 비율(실제 DIP ÷ CSS 픽셀). 카드를 키우면 흐려지지 않게 페이지가 다시 그린다.</summary>
    public void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.25, 5);
        if (_web.CoreWebView2 != null) _web.ZoomFactor = _zoom;
    }

    private async Task StartAsync()
    {
        if (_web.CoreWebView2 != null || _closed) return;
        try
        {
            await _runtime.Ready;
            if (_closed) return;
            await _web.EnsureCoreWebView2Async(await BrowserEnvironment.Environment());
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
        s.IsWebMessageEnabled = true;
        s.AreDefaultContextMenusEnabled = false;
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

        // 카드 자신의 주소는 파일 두 장만 준다. 다른 주소는 internet 권한이 있을 때만 그대로 내보낸다.
        core.NavigationStarting += (_, _) => _rendererRecovery.Reset();
        core.NavigationCompleted += (_, e) => { if (e.IsSuccess) _rendererRecovery.Reset(); if (_suspended) SetSuspended(true); };
        core.WebMessageReceived += OnMessage;
        core.ProcessFailed += (_, e) =>
        {
            if (_closed) return;
            // BrowserProcessExited는 환경의 종료 통지가 캐시/keeper와 카드들을 함께 복구한다.
            // GPU·보조 프로세스는 런타임의 자동 복구에 맡긴다. 일시적인 무응답은 기다린다.
            if (_rendererRecovery.ShouldReload(e.ProcessFailedKind, Environment.TickCount64))
            {
                try { core.Reload(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                { ShowError("카드를 다시 열지 못했어요: " + ex.Message); }
            }
            else if (_rendererRecovery.Blocked && e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                ShowError(RendererRecoveryPolicy.Error);
        };

        await core.AddScriptToExecuteOnDocumentCreatedAsync(ThemeScript() + "window.addEventListener('contextmenu',e=>{e.preventDefault();window.chrome.webview.postMessage('menu');},true);");
        _web.ZoomFactor = _zoom;
        core.Navigate(PageUrl);
    }

    /// <summary>이 화면이 뜨는 호스트. 설정 화면은 설정하는 카드와 같은 주소라 저장소를 같이 본다.</summary>
    private string PageUrl => new Uri(new Uri(_runtime.Package.Url ?? throw new InvalidOperationException("카드 서버 주소가 없어요.")), IsSettings ? "settings.html" : "").AbsoluteUri;

    internal void ShowError(string text)
    {
        _error.Text = text;
        _restart.Visibility = Visibility.Visible;
        _error.Visibility = Visibility.Visible;
        _snapshot.Visibility = Visibility.Collapsed;
        _web.Visibility = Visibility.Collapsed;
    }

    private static string ThemeCss()
    {
        var vars = string.Join("", Theme.CssColors().Select(kv => $"--desk-{kv.Key}:{kv.Value};"));
        return DesignKit + $":root{{{vars}color-scheme:{(Theme.IsLight ? "light" : "dark")};}}";
    }

    private static readonly string DesignKit = ReadDesignKit();
    private static string ReadDesignKit()
    {
        using var stream = typeof(CardWebView).Assembly.GetManifestResourceStream("DeskCards.desk.css")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ThemeScript() =>
        "(()=>{const apply=()=>{let s=document.getElementById('desk-theme');" +
        "if(!s){s=document.createElement('style');s.id='desk-theme';document.documentElement.appendChild(s);}" +
        "s.textContent=" + System.Text.Json.JsonSerializer.Serialize(ThemeCss()) + ";};" +
        "if(document.documentElement)apply();else document.addEventListener('DOMContentLoaded',apply,{once:true});})();";

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try { if (!IsSettings && e.TryGetWebMessageAsString() == "menu") MenuRequested?.Invoke(); }
        catch (ArgumentException) { }
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(async () =>
    {
        if (!_closed && _web.CoreWebView2 != null)
            try
            {
                _web.CoreWebView2.Profile.PreferredColorScheme = Theme.IsLight ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark;
                await _web.CoreWebView2.ExecuteScriptAsync(ThemeScript());
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    });
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
            if (_error.Visibility != Visibility.Visible && !_suspended) _web.Visibility = Visibility.Visible;
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

    public void Reload()
    {
        _restart.Visibility = Visibility.Collapsed;
        _rendererRecovery.Clear();
        if (_web.CoreWebView2 == null) return;
        // 자동 다시 열기를 멈추며 띄운 오류를 걷고 페이지를 다시 보인다.
        if (_error.Visibility == Visibility.Visible)
        {
            _error.Visibility = Visibility.Collapsed;
            _web.Visibility = _frozen || _suspended ? Visibility.Hidden : Visibility.Visible;
        }
        _web.CoreWebView2.Reload();
    }

    /// <summary>창을 닫을 때 부른다. 브라우저 화면을 정리한다.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        Theme.Changed -= OnThemeChanged;
        try { _web.Dispose(); } catch { }
    }
}
