using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace DeskCards;

/// <summary>
/// 카드 바 조합키: 가상 키 코드 목록(예: Alt + Shift, Win + Shift). 왼쪽·오른쪽 Ctrl·Shift·Alt·Win은 하나로 친다.
/// </summary>
internal static class KeyCombo
{
    public const int Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Win = 0x5B, RWin = 0x5C, Esc = 0x1B;

    public static readonly IReadOnlyList<int> Default = new[] { Ctrl };

    /// <summary>왼쪽·오른쪽 구분을 없앤 키 코드. 쓸 수 없는 키면 0.</summary>
    public static int Normalize(int vk) => vk switch
    {
        0xA0 or 0xA1 => Shift,
        0xA2 or 0xA3 => Ctrl,
        0xA4 or 0xA5 => Alt,
        RWin => Win,
        > 0 and < 0xFF => vk,
        _ => 0,
    };

    public static int FromKey(Key key) => Normalize(KeyInterop.VirtualKeyFromKey(key));

    /// <summary>중복·잘못된 코드를 빼고 Win·Ctrl·Alt·Shift·나머지 순으로 정리한다. 비면 기본값(Ctrl).</summary>
    public static List<int> Clean(IEnumerable<int>? keys)
    {
        var list = (keys ?? Array.Empty<int>()).Select(Normalize).Where(k => k != 0).Distinct().OrderBy(Rank).ThenBy(k => k).ToList();
        return list.Count == 0 ? Default.ToList() : list;
    }

    private static int Rank(int vk) => vk switch { Win => 0, Ctrl => 1, Alt => 2, Shift => 3, _ => 4 };

    /// <summary>보여 줄 글씨(예: "Win + Shift").</summary>
    public static string Text(IEnumerable<int> keys) => string.Join(" + ", Clean(keys).Select(Name));

    private static string Name(int vk) => vk switch
    {
        Win => "Win",
        Ctrl => "Ctrl",
        Alt => "Alt",
        Shift => "Shift",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(), // 숫자 키는 D1 대신 1로
        _ => KeyInterop.KeyFromVirtualKey(vk).ToString(),
    };

    /// <summary>조합의 키가 모두 눌려 있는지.</summary>
    public static bool IsDown(IEnumerable<int> keys) => keys.All(IsPressed);

    public static bool IsPressed(int vk) =>
        vk == Win ? Native.GetAsyncKeyState(Win) < 0 || Native.GetAsyncKeyState(RWin) < 0 : Native.GetAsyncKeyState(vk) < 0;

    /// <summary>
    /// Win이나 Alt만 눌렀다 떼면 시작 메뉴나 앱 메뉴가 열린다. 그 사이에 아무 뜻 없는 키를 한 번 눌러
    /// "다른 키와 같이 눌렀다"로 만들어 막는다.
    /// </summary>
    public static void SuppressRelease(IEnumerable<int> keys)
    {
        if (!keys.Any(k => k is Win or Alt)) return;
        const byte Unassigned = 0xE8;
        Native.keybd_event(Unassigned, 0, 0, UIntPtr.Zero);
        Native.keybd_event(Unassigned, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
    }
}
