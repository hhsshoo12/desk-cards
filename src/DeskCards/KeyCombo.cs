using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace DeskCards;

/// <summary>
/// 카드 바 조합키: 보조키(Ctrl·Alt·Shift·Win) 하나 이상 + 일반 키 0개 또는 1개(예: Shift, Ctrl + Alt + D).
/// 일반 키가 있으면 Windows 단축키로 등록해서, 누르는 동안 앞의 앱에 키가 가지 않는다(알림음·메뉴 열림 없음).
/// 보조키만 있으면 등록할 수 없지만(Windows가 받지 않음), 보조키만 누르는 건 앱에 아무 일도 일으키지 않아 지켜보기만 한다.
/// 왼쪽·오른쪽 보조키는 하나로 친다.
/// </summary>
internal static class KeyCombo
{
    public const int Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Win = 0x5B, RWin = 0x5C, Esc = 0x1B;

    public static readonly IReadOnlyList<int> Default = new[] { Shift };

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

    public static bool IsModifier(int vk) => vk is Win or Ctrl or Alt or Shift;

    /// <summary>중복·잘못된 코드를 빼고 Win·Ctrl·Alt·Shift·일반 키 순으로 정리한다.</summary>
    public static List<int> Order(IEnumerable<int> keys) =>
        keys.Select(Normalize).Where(k => k != 0).Distinct().OrderBy(Rank).ThenBy(k => k).ToList();

    /// <summary>쓸 수 있는 모양인지(보조키 하나 이상 + 일반 키 0개 또는 1개).</summary>
    public static bool IsValid(IReadOnlyCollection<int> keys) =>
        keys.Count(IsModifier) >= 1 && keys.Count(k => !IsModifier(k)) <= 1;

    /// <summary>일반 키가 있어서 Windows 단축키로 등록하는 조합인지.</summary>
    public static bool NeedsHotkey(IEnumerable<int> keys) => keys.Any(k => !IsModifier(k));

    /// <summary>정리한 조합. 쓸 수 없는 모양이면 기본값(Shift).</summary>
    public static List<int> Clean(IEnumerable<int>? keys)
    {
        var list = Order(keys ?? Array.Empty<int>());
        return IsValid(list) ? list : Default.ToList();
    }

    /// <summary>RegisterHotKey에 넘길 보조키 플래그와 일반 키.</summary>
    public static (uint modifiers, uint key) ToHotkey(IEnumerable<int> keys)
    {
        uint mods = 0, key = 0;
        foreach (int k in keys)
            switch (k)
            {
                case Alt: mods |= 0x1; break;
                case Ctrl: mods |= 0x2; break;
                case Shift: mods |= 0x4; break;
                case Win: mods |= 0x8; break;
                default: key = (uint)k; break;
            }
        return (mods, key);
    }

    private static int Rank(int vk) => vk switch { Win => 0, Ctrl => 1, Alt => 2, Shift => 3, _ => 4 };

    /// <summary>키 이름들(Win·Ctrl·Alt·Shift·일반 키 순). 키캡 모양으로 보여 줄 때 쓴다.</summary>
    public static IEnumerable<string> Names(IEnumerable<int> keys) => Order(keys).Select(Name);

    /// <summary>쓸 수 없는 조합이면 그 까닭, 쓸 수 있으면 null.</summary>
    public static string? Problem(IReadOnlyCollection<int> keys) =>
        !keys.Any(IsModifier) ? "보조키(Ctrl · Alt · Shift · Win)를 하나 이상 넣어 주세요."
        : keys.Count(k => !IsModifier(k)) > 1 ? "일반 키는 하나까지만 넣을 수 있어요."
        : null;

    /// <summary>보여 줄 글씨(예: "Win + Shift").</summary>
    public static string Text(IEnumerable<int> keys) => string.Join(" + ", Order(keys).Select(Name));

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
