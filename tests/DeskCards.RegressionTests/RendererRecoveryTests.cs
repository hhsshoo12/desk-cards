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
        const CoreWebView2ProcessFailedKind exited = CoreWebView2ProcessFailedKind.RenderProcessExited;
        Test("renderer recovery: fourth renderer exit within a minute stops automatic reload until cleared", () =>
        {
            var policy = new RendererRecoveryPolicy();
            Check(policy.ShouldReload(exited, 0));
            Check(policy.ShouldReload(exited, 10_000));
            Check(policy.ShouldReload(exited, 20_000));
            Check(!policy.ShouldReload(exited, 30_000));
            Check(policy.Blocked);
            Check(!policy.ShouldReload(exited, 200_000)); // 시간이 지나도 저절로 풀리지 않는다.
            for (long t = 200_000; t <= 230_000; t += 15_000) Check(!policy.ShouldReload(hung, t));
            policy.Reset(); // 탐색 초기화는 중단을 풀지 않는다.
            Check(policy.Blocked);
            policy.Clear(); // 사용자가 다시 불러오기.
            Check(!policy.Blocked);
            Check(policy.ShouldReload(exited, 240_000));
        });
        Test("renderer recovery: exits spread over more than a minute and navigation resets keep reloading", () =>
        {
            var policy = new RendererRecoveryPolicy();
            for (long t = 0; t < 10 * 25_000; t += 25_000)
            {
                Check(policy.ShouldReload(exited, t)); // 1분 창에 최대 세 번만 들어간다.
                policy.Reset();
            }
            Check(!policy.Blocked);
        });
    }
}
