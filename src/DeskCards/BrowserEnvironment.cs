using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace DeskCards;

/// <summary>WebView2 브라우저 환경과 종료 복구를 관리한다.</summary>
internal static partial class BrowserEnvironment
{
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> _environments = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BrowserRecoveryRegistry _browserRecovery = new();
    internal const string BrowserRecoveryError = "브라우저가 반복해서 종료되어 자동 복구를 멈췄어요. 카드 메뉴에서 '다시 불러오기'를 누르거나 앱을 다시 켜 주세요.";
    internal static event Action<string>? BrowserLost;

    static BrowserEnvironment()
    {
        if (System.Windows.Application.Current is { } app)
            app.Exit += (_, _) =>
            {
                _environments.Clear();
            };
    }

    /// <summary>WebView2 브라우저 환경과 종료 복구를 관리한다.</summary>
    public static Task<CoreWebView2Environment> Environment(string? profile = null)
        => EnvironmentAt(profile ?? ProfileDir());

    internal static string ProfileDir() => Path.GetFullPath(AppPaths.WebDataDir);

    internal static bool BrowserRecoveryBlocked(string path) =>
        _browserRecovery.IsBlocked(path);

    internal static void ResetBrowserRecovery(string? profile = null) => _browserRecovery.Reset(profile ?? ProfileDir());

    private static Task<CoreWebView2Environment> EnvironmentAt(string path)
    {
        path = Path.GetFullPath(path);
        if (BrowserRecoveryBlocked(path)) throw new InvalidOperationException(BrowserRecoveryError);
        if (_environments.TryGetValue(path, out var cached) && !cached.IsFaulted) return cached;
        // 잘못된 저장 경로는 브라우저 시작 전에 I/O 오류로 돌려준다.
        Directory.CreateDirectory(path);
        return _environments[path] = CreateEnvironment(path);
    }

    private static async Task<CoreWebView2Environment> CreateEnvironment(string path)
    {
        var env = await CoreWebView2Environment.CreateAsync(null, path);
        env.BrowserProcessExited += (_, e) => InvalidateBrowser(path, env, e.BrowserProcessExitKind == CoreWebView2BrowserProcessExitKind.Failed);
        return env;
    }

    private static void InvalidateBrowser(string path, CoreWebView2Environment env, bool failed)
    {
        if (!_environments.TryGetValue(path, out var current) || !current.IsCompletedSuccessfully || !ReferenceEquals(current.Result, env)) return;
        _environments.Remove(path);
        // 모든 화면을 정상적으로 닫은 경우도 통지된다. 정상 종료는 복구 횟수에 넣지 않는다.
        if (!failed) return;
        _browserRecovery.RecordExit(path, System.Environment.TickCount64);
        BrowserLost?.Invoke(path);
    }

}