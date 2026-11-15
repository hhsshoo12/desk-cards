using System;
using System.Collections.Generic;

namespace DeskCards.Expanded;

/// <summary>항목 칸 하나. 아이콘은 미리 곱한 BGRA 픽셀(없으면 아직 못 읽은 것).</summary>
public sealed record TileData(string Path, string Name, int IconWidth, int IconHeight, byte[]? Icon);

/// <summary>색은 #AARRGGBB.</summary>
public sealed record Palette(string Fg, string SubFg, string HoverBg, string PopupBg, bool Light);

public sealed record ExpandedOptions(
    Palette Palette,
    bool EditTitle = false,
    bool Hover = false,
    bool Topmost = false,
    /// <summary>올려 두어서 연 창의 마우스 이탈 판단에 쓰는 창(카드 또는 카드 바).</summary>
    IntPtr Reference = default);

/// <summary>
/// 펼친 창이 앱에 부탁하는 일. 모든 메서드는 WPF 창 스레드에서 불리므로, 구현은 앱 UI 스레드로 넘겨서 처리한다.
/// Place·Rename만 결과가 필요해서 UI 스레드가 끝날 때까지 기다려도 된다.
/// </summary>
public interface IExpandedHost
{
    /// <summary>창 크기(물리 픽셀)를 받아 띄울 왼쪽 위 자리(물리 픽셀)를 돌려준다.</summary>
    (int X, int Y) Place(int widthPx, int heightPx);
    void Launch(string path);
    /// <summary>항목 우클릭 메뉴를 연다. 메뉴가 닫히면 ExpandedView.MenuClosed()를 불러야 한다.</summary>
    void ItemMenu(string path);
    void AddToGroup(IReadOnlyList<string> paths);
    void SetOrder(IReadOnlyList<string> paths);
    bool Rename(string name);
    void OpenSettings();
    /// <summary>창이 닫혔다.</summary>
    void Closed();
}
