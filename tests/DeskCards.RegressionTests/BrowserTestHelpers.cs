using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DeskCards;

internal static partial class Program
{
    /// <summary>카드 창(또는 설정 창) 안의 웹 화면. 문서가 다 뜰 때까지 기다린다.</summary>
    private static Microsoft.Web.WebView2.Wpf.WebView2 WebOf(Window window)
    {
        var web = Visuals<CardWebView>(window).Single().Children.OfType<Microsoft.Web.WebView2.Wpf.WebView2>().Single();
        WaitUntil(() => web.CoreWebView2 != null && web.Source?.Scheme == "http");
        WaitUntil(() => Eval(web, "document.readyState", wait: false) == "\"complete\"");
        return web;
    }

    /// <summary>페이지에서 식 하나를 계산해 JSON 글자로 돌려준다.</summary>
    private static string Eval(Microsoft.Web.WebView2.Wpf.WebView2 web, string script, bool wait = true)
    {
        var t = web.ExecuteScriptAsync(script);
        if (wait) WaitUntil(() => t.IsCompleted);
        else { var sw = System.Diagnostics.Stopwatch.StartNew(); while (!t.IsCompleted && sw.ElapsedMilliseconds < 10000) Pump(20); }
        return t.IsCompleted ? t.GetAwaiter().GetResult() : "";
    }

    /// <summary>페이지가 window[name]에 글자를 넣을 때까지 기다린다(비동기 스크립트 결과).</summary>
    /// <summary>
    /// 브라우저 객체를 다루는 비동기 작업을 Dispatcher 안에서 시작한다. 콘솔 본문에는 WPF 동기화 컨텍스트가 없어서,
    /// 그냥 부르면 await 뒤가 다른 스레드에서 이어져 브라우저 객체를 잘못된 스레드에서 건드린다.
    /// </summary>
    private static System.Threading.Tasks.Task OnUi(Func<System.Threading.Tasks.Task> work) =>
        System.Threading.Tasks.TaskExtensions.Unwrap(Dispatcher.CurrentDispatcher.InvokeAsync(work).Task);

    private static void WaitUntilLong(Func<bool> condition, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < ms) Pump(100);
    }

}
