using System.Linq;
using System.Windows;

namespace DeskCards;

/// <summary>
/// 확인·안내 창(Windows 11 대화 상자 모양). 지금 활성인 우리 창(편집 중이면 편집 막대)을 소유자로 삼아,
/// 편집 모드의 어두운 막이나 맨 위 창들 뒤로 숨지 않게 한다.
/// </summary>
internal static class Dialogs
{
    /// <param name="heading">굵은 제목. 없으면 <paramref name="text"/>를 제목으로 쓴다.</param>
    /// <param name="primary">파란 버튼 글자(기본 "확인"). 예: "삭제".</param>
    public static MessageBoxResult Show(string text, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, string? heading = null, string? primary = null)
    {
        _ = image; // 아이콘은 쓰지 않는다(Windows 11 대화 상자처럼 제목으로 알린다).
        var dialog = heading == null
            ? new DialogWindow(text, "", buttons, primary)
            : new DialogWindow(heading, text, buttons, primary);
        var owner = Owner();
        if (owner != null)
        {
            dialog.Owner = owner;
            // 편집 막대는 작으니 화면 가운데에. 소유자로는 두어서 막대·어두운 막 위에 뜬다.
            dialog.WindowStartupLocation = owner is EditBar ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }
        dialog.ShowDialog();
        return dialog.Result;
    }

    private static Window? Owner()
    {
        var windows = Application.Current?.Windows.OfType<Window>()
            .Where(w => w.IsVisible && w is not EditDim && w is not DialogWindow && w is not CardWindow).ToList();
        if (windows == null) return null;
        return windows.FirstOrDefault(w => w.IsActive) ?? windows.OfType<EditBar>().FirstOrDefault();
    }
}
