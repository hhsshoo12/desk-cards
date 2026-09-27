using System.Linq;
using System.Windows;

namespace DeskCards;

/// <summary>
/// 확인·안내 창. 지금 활성인 우리 창(편집 중이면 편집 막대)을 소유자로 삼아,
/// 편집 모드의 어두운 막이나 맨 위 창들 뒤로 숨지 않게 한다.
/// </summary>
internal static class Dialogs
{
    public static MessageBoxResult Show(string text, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        var owner = Owner();
        return owner != null
            ? MessageBox.Show(owner, text, "Desk Cards", buttons, image)
            : MessageBox.Show(text, "Desk Cards", buttons, image);
    }

    private static Window? Owner()
    {
        var windows = Application.Current?.Windows.OfType<Window>().Where(w => w.IsVisible && w is not EditDim).ToList();
        if (windows == null) return null;
        return windows.FirstOrDefault(w => w.IsActive) ?? windows.OfType<EditBar>().FirstOrDefault();
    }
}
