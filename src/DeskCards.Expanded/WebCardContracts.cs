using System;

namespace DeskCards.Expanded;

/// <summary>웹 카드 창 하나를 여는 데 필요한 것. 위치·크기는 화면 물리 픽셀(카드 내용 영역 기준).</summary>
public sealed record WebCardOptions(
    string Id, string Name, string Folder, string Html, string BrowserDataDir,
    int X, int Y, int Width, int Height, bool Visible, Palette Palette);

/// <summary>웹 카드 창이 앱에 부탁하는 일. 모든 메서드는 WPF 창 스레드에서 불리므로 구현은 앱 UI 스레드로 넘긴다.</summary>
public interface IWebCardHost
{
    /// <summary>사용자가 옮기거나 크기를 바꿨다(또는 페이지가 바꿨다). 저장할 새 자리.</summary>
    void Bounds(int x, int y, int width, int height);
    /// <summary>카드 위에서 우클릭했다(앱 메뉴를 띄운다).</summary>
    void Menu();
    /// <summary>페이지가 deskcard.send로 보낸 메시지(JSON 한 줄). 앱이 카드의 exe로 넘긴다.</summary>
    void FromPage(string json);
    /// <summary>창이 닫혔다(앱이 닫은 경우 포함).</summary>
    void Closed();
}

/// <summary>열려 있는 웹 카드 창. 어느 스레드에서 불러도 된다.</summary>
public interface IWebCardHandle
{
    /// <summary>exe가 보낸 메시지(JSON)를 페이지의 deskcard.onmessage로 보낸다.</summary>
    void ToPage(string json);
    void SetBounds(int x, int y, int width, int height);
    void SetEditing(bool editing);
    void SetVisible(bool visible);
    void Reload();
    void Close();
}

public static class WebCards
{
    public static IWebCardHandle Open(WebCardOptions options, IWebCardHost host) => new WebCardHandle(options, host);
}