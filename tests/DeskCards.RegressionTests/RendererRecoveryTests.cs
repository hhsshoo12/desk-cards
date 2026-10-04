using DeskCards;
using Microsoft.Web.WebView2.Core;

internal static partial class Program
{
    private static void RendererRecoveryTests()
    {
        const CoreWebView2ProcessFailedKind hung = CoreWebView2ProcessFailedKind.RenderProcessUnresponsive;
        Test("renderer recovery: transient events wait and three repeated events reload once", () =>
        {
            var policy = new RendererRecoveryPolicy();
            Check(!policy.ShouldReload(hung, 0));
            Check(!policy.ShouldReload(hung, 15_000));
            Check(policy.ShouldReload(hung, 30_000));
            Check(!policy.ShouldReload(hung, 45_000)); // Reload 뒤 새 관찰 구간이다.
            Check(!policy.ShouldReload(hung, 60_000));
            Check(policy.ShouldReload(hung, 75_000));
        });
        Test("renderer recovery: separated episodes and navigation reset do not accumulate", () =>
        {
            var policy = new RendererRecoveryPolicy();
            Check(!policy.ShouldReload(hung, 0));
            Check(!policy.ShouldReload(hung, 15_000));
            Check(!policy.ShouldReload(hung, 45_001)); // 반복이 끊겼으므로 첫 통지로 센다.
            Check(!policy.ShouldReload(hung, 60_001));
            policy.Reset(); // 새 탐색·성공한 탐색·수동 Reload에서 호출한다.
            Check(!policy.ShouldReload(hung, 75_001));
            Check(!policy.ShouldReload(hung, 105_001)); // 주기가 달라져도 30초까지 연속으로 본다.
            Check(policy.ShouldReload(hung, 135_001));
        });
        Test("renderer recovery: renderer exit reloads immediately and other failures do not", () =>
        {
            var policy = new RendererRecoveryPolicy();
            Check(!policy.ShouldReload(hung, 0));
            Check(!policy.ShouldReload(CoreWebView2ProcessFailedKind.GpuProcessExited, 5_000));
            Check(!policy.ShouldReload(CoreWebView2ProcessFailedKind.BrowserProcessExited, 5_000));
            Check(!policy.ShouldReload(CoreWebView2ProcessFailedKind.UtilityProcessExited, 5_000));
            Check(policy.ShouldReload(CoreWebView2ProcessFailedKind.RenderProcessExited, 10_000));
            Check(!policy.ShouldReload(hung, 15_000));
            Check(!policy.ShouldReload(hung, 30_000));
        });
    }
}
