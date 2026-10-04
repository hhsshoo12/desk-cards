using System.Collections.Generic;
using Microsoft.Web.WebView2.Core;

namespace DeskCards;

internal sealed class RendererRecoveryPolicy
{
    internal const string Error = "카드 화면이 반복해서 꺼져서 자동으로 다시 열지 않아요. 카드 메뉴에서 '다시 불러오기'를 눌러 주세요.";

    private long? _lastUnresponsive;
    private int _consecutive;
    private readonly Queue<long> _exits = new();

    /// <summary>렌더러가 1분 안에 네 번째로 꺼져 자동 다시 열기를 멈췄는지. 사용자가 다시 불러올 때(<see cref="Clear"/>)만 풀린다.</summary>
    public bool Blocked { get; private set; }

    public bool ShouldReload(CoreWebView2ProcessFailedKind kind, long now)
    {
        if (Blocked) return false;
        if (kind == CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            Reset();
            // 메모리를 계속 쓰다 렌더러를 죽이는 카드는 다시 열어도 또 죽는다. 브라우저 복구 한도와 같은 기준으로 멈춘다.
            while (_exits.TryPeek(out long first) && now - first >= 60_000) _exits.Dequeue();
            _exits.Enqueue(now);
            Blocked = _exits.Count > 3;
            return !Blocked;
        }
        if (kind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive) return false;

        // 공식 문서는 반복 주기를 "예: 15초"로 설명한다(고정 주기를 보장하지 않는다).
        // 30초 이내 간격으로 3회 연속 통지될 때만 재시도한다. 15초 주기라면 첫 통지 뒤 30초다.
        // https://learn.microsoft.com/microsoft-edge/webview2/concepts/process-related-events#handle-unresponsive-renderers
        if (_lastUnresponsive is not { } last || now - last > 30_000) _consecutive = 0;
        _lastUnresponsive = now;
        if (++_consecutive < 3) return false;
        Reset();
        return true;
    }

    /// <summary>무응답 관찰만 초기화한다(새 탐색·성공한 탐색). 렌더러 종료 기록은 남긴다.</summary>
    public void Reset()
    {
        _lastUnresponsive = null;
        _consecutive = 0;
    }

    /// <summary>사용자가 다시 불러올 때: 종료 기록과 중단 상태까지 모두 초기화한다.</summary>
    public void Clear()
    {
        Reset();
        _exits.Clear();
        Blocked = false;
    }
}
