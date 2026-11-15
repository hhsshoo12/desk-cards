using System;
using System.Collections.Generic;

namespace DeskCards.Expanded;

/// <summary>바탕화면 카드(폴더 카드·웹 카드)의 창이 앱에 부탁하는 일. 모든 메서드는 WPF 창 스레드에서 불리므로 구현은 앱 UI 스레드로 넘긴다.</summary>
public interface IDesktopCardHost
{
    /// <summary>사용자가 옮기거나 크기를 바꿨다(또는 카드가 바꿨다). 저장할 새 자리(물리 픽셀, 카드 내용 영역).</summary>
    void Bounds(int x, int y, int width, int height);
    /// <summary>카드 위에서 우클릭했다(앱 메뉴를 띄운다).</summary>
    void Menu();
    /// <summary>편집 중 카드를 눌러 골랐다.</summary>
    void Selected();
    /// <summary>옮기는 중인 카드를 다른 카드·가장자리에 맞춰 주고 안내선을 그린다. 돌려주는 자리로 옮긴다.</summary>
    PixelRect Snap(string id, PixelRect rect);
    /// <summary>비율을 지키며 크기를 바꿀 때 안내선에 맞춘 배율(start는 바꾸기 시작할 때 자리).</summary>
    double SnapScale(string id, PixelRect start, double scale);
    /// <summary>옮기기·크기 조절이 끝났다(안내선을 지운다).</summary>
    void GuidesOff();
    /// <summary>카드가 눌려서 활성화됐다. 바탕화면(Progman)이 카드 앞으로 나온 순서를 앱이 바로잡는다.</summary>
    void Activated();
    /// <summary>모니터 배율이 바뀌어 창이 새 배율에 맞게 옮겨졌다. 새 자리(물리 픽셀, 카드 내용 영역).</summary>
    void DpiChanged(int x, int y, int width, int height) => Bounds(x, y, width, height);
    /// <summary>창이 닫혔다(앱이 닫은 경우 포함).</summary>
    void Closed();
}

/// <summary>웹 카드 창이 앱에 부탁하는 일.</summary>
public interface IWebCardHost : IDesktopCardHost
{
    /// <summary>페이지가 deskcard.send로 보낸 메시지(JSON 한 줄). 앱이 카드의 exe로 넘긴다.</summary>
    void FromPage(string json);
}

/// <summary>폴더 카드 안(항목 칸)이 앱에 부탁하는 일.</summary>
public interface ICardViewHost
{
    void Launch(string path);
    /// <summary>카드 안 항목의 우클릭 메뉴(앱이 연다).</summary>
    void ItemMenu(string path);
    /// <summary>카드의 아이콘이 아닌 곳을 우클릭했을 때의 메뉴(앱이 연다).</summary>
    void CardMenu(string group);
    /// <summary>카드를 펼친다. cardRect는 그 카드가 화면에 있는 자리(물리 픽셀).</summary>
    void Expand(string group, bool hover, PixelRect cardRect);
}

/// <summary>폴더 카드 창이 앱에 부탁하는 일.</summary>
public interface IFolderCardHost : IDesktopCardHost, ICardViewHost
{
    /// <summary>파일·폴더를 카드로 끌어 놓았다.</summary>
    void AddToGroup(IReadOnlyList<string> paths);
}

/// <summary>웹 카드 창 하나를 여는 데 필요한 것. 위치·크기는 화면 물리 픽셀(카드 내용 영역 기준).</summary>
public sealed record WebCardOptions(
    string Id, string Name, string Folder, string Html, string BrowserDataDir,
    int X, int Y, int Width, int Height, bool Visible, Palette Palette);

/// <summary>폴더 카드가 그릴 내용. LabelScale은 이름 글자에 곱할 확대 비율이다.</summary>
public sealed record FolderCardData(string Group, string Name, string Folder, int Cols, int Rows, double LabelScale,
    bool HoverExpand, int HoverDelayMs, IReadOnlyList<TileData> Items);

public sealed record FolderCardOptions(string Group, int X, int Y, int Width, int Height, Palette Palette, FolderCardData Data, bool Visible = false);

/// <summary>열려 있는 바탕화면 카드 창. 어느 스레드에서 불러도 된다.</summary>
public interface IDesktopCardHandle
{
    /// <summary>창 핸들.</summary>
    IntPtr Hwnd { get; }
    /// <summary>카드 내용 영역의 지금 자리(물리 픽셀). 편집 띠는 빠진다.</summary>
    PixelRect ContentRect { get; }
    /// <summary>카드 내용 영역을 옮기고 크기를 바꾼다(끝난 뒤 돌아온다).</summary>
    void SetBounds(int x, int y, int width, int height);
    void SetEditing(bool editing);
    /// <summary>편집 중 강조: 고른 카드, 안 되는 자리(빨강).</summary>
    void SetHighlight(bool selected, bool invalid);
    void SetVisible(bool visible);
    /// <summary>바탕화면 층(맨 아래)으로 다시 내린다.</summary>
    void Restack();
    void SetPalette(Palette palette);
    void Close();
}

public interface IWebCardHandle : IDesktopCardHandle
{
    /// <summary>exe가 보낸 메시지(JSON)를 페이지의 deskcard.onmessage로 보낸다.</summary>
    void ToPage(string json);
    void Reload();
}

public interface IFolderCardHandle : IDesktopCardHandle
{
    /// <summary>카드 내용(칸 수·항목·이름)을 바꾼다.</summary>
    void SetFolder(FolderCardData data);
    /// <summary>파일을 끌어다 놓을 대상으로 강조한다.</summary>
    void SetDrop(bool on);
}

public static class DesktopCards
{
    /// <summary>앞서 보낸 창 조작이 모두 끝난 뒤에 action을 WPF 창 스레드에서 실행한다.</summary>
    public static void After(Action action) => WpfThread.Dispatcher.BeginInvoke(action);

    public static IWebCardHandle OpenWeb(WebCardOptions options, IWebCardHost host) => new WebCardHandle(options, host);

    public static IFolderCardHandle OpenFolder(FolderCardOptions options, IFolderCardHost host) => new FolderCardHandle(options, host);
}