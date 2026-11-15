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

/// <summary>
/// 카드 바 안에 놓인 웹 카드 한 장. 칸 크기에 맞춰 화면(WebView2)이 늘고 줄며, 페이지에는 deskcard.bounds로 그 크기를 알려 준다.
/// 바 편집 중에는 화면이 편집 덮개 뒤로 가려지므로(HWND 위에는 WPF가 못 그린다) 화면을 숨기고 이름 판을 대신 보여 준다.
/// </summary>
internal sealed class BarWebCard : Grid
{
    private readonly IBarHost _host;
    private readonly WebBarCard _c;
    private readonly bool _light;
    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Transparent };
    private readonly Border _hold;
    private bool _booted, _dead, _editing;

    public BarWebCard(IBarHost host, WebBarCard card, string name, bool light)
    {
        _host = host;
        _c = card;
        _light = light;
        var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        _hold = new Border { CornerRadius = new CornerRadius(8), Visibility = Visibility.Collapsed, Child = label };
        _hold.SetResourceReference(Border.BackgroundProperty, "HoverBg");
        Children.Add(_web);
        Children.Add(_hold);
        Loaded += async (_, _) => await StartAsync();
        SizeChanged += (_, _) => PushBounds();
    }

    public string Id => _c.Id;

    public void SetEditing(bool editing)
    {
        _editing = editing;
        _web.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        _hold.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        Post(WebCardWindow.Evt("editing", editing));
    }

    public void ToPage(string json) => Post("{\"t\":\"msg\",\"d\":" + json + "}");

    public void Dispose()
    {
        _dead = true;
        try { _web.Dispose(); } catch (Exception) { }
    }

    private async Task StartAsync()
    {
        if (_booted || _dead) return;
        _booted = true;
        try
        {
            await _web.EnsureCoreWebView2Async(await WebCardWindow.GetEnvironment(_c.BrowserDataDir));
            if (_dead) return;
            await WebCardPage.StartAsync(_web.CoreWebView2, _c.Id, _c.Folder, _c.Html, OnWebMessage, () => _dead);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or WebView2RuntimeNotFoundException or ArgumentException) { }
    }

    private void PushBounds() =>
        Post(WebCardWindow.Evt("bounds", new { x = 0, y = 0, w = Math.Round(ActualWidth), h = Math.Round(ActualHeight) }));

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
                case "msg": _host.WebMessage(_c.Id, root.GetProperty("d").GetRawText()); break;
                case "menu": _host.WebMenu(_c.Id); break;
                case "ready":
                    PushBounds();
                    Post(WebCardWindow.Evt("theme", _light ? "light" : "dark"));
                    Post(WebCardWindow.Evt("editing", _editing));
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }
}
