using Microsoft.Web.WebView2.Core;

namespace DeskCards;

internal sealed class RendererRecoveryPolicy
{
    private long? _lastUnresponsive;
    private int _consecutive;

    public bool ShouldReload(CoreWebView2ProcessFailedKind kind, long now)
    {
        if (kind == CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            Reset();
            return true;
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

    public void Reset()
    {
        _lastUnresponsive = null;
        _consecutive = 0;
    }
}
