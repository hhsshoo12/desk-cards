using System;
using Windows.Foundation;

namespace DeskCards;

// WinUI 3 이식 중 아직 옮기지 못한 창들의 임시 자리. 하나씩 진짜 구현으로 바꾸며 지운다.

internal static class BalloonTip
{
    public sealed record Tip(string Title, string Subtitle, string Action, Action OnAction, Action? NeverShow = null);
    public static void Show(Native.RECT target, Tip content) { }
    public static void CloseCurrent() { }
}
