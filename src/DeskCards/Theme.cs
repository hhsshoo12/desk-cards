using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Windows.UI;

namespace DeskCards;

internal static class Theme
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    public static bool IsLight { get; private set; } = true;

    /// <summary>창 배경에 Mica·아크릴을 쓸 수 있는지(22H2부터).</summary>
    public static bool HasBackdrop => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>테마(밝게·어둡게)를 다시 적용했을 때.</summary>
    public static event Action? Changed;

    public static ElementTheme Element => IsLight ? ElementTheme.Light : ElementTheme.Dark;

    /// <summary>이름으로 브러시를 찾는다. 테마가 바뀌면 같은 개체의 색이 바뀌므로 한 번 잡아 둔 브러시도 따라온다.</summary>
    public static SolidColorBrush Brush(string key) => Brushes[key];

    public static Color Color(string key) => Brushes[key].Color;

    public static void Apply()
    {
        IsLight = ReadAppsUseLightTheme();
        bool mica = HasBackdrop;
        if (IsLight)
        {
            Set("CardBg", "#D9F3F3F3");
            Set("CardBorder", "#1F000000");
            Set("HoverBg", "#14000000");
            Set("Fg", "#FF1B1B1B");
            Set("SubFg", "#FF5F5F5F");
            Set("PopupBg", mica ? "#B8FAFAFA" : "#F7F3F3F3");
            Set("Accent", "#FF005FB8");
            Set("AccentFg", "#FFFFFFFF");
            Set("Danger", "#FFC42B1C");
            Set("MenuLine", "#14000000");
            Set("MonitorBg", "#FFD6D6D6");
        }
        else
        {
            Set("CardBg", "#CC202020");
            Set("CardBorder", "#26FFFFFF");
            Set("HoverBg", "#18FFFFFF");
            Set("Fg", "#FFFFFFFF");
            Set("SubFg", "#FFC5C5C5");
            Set("PopupBg", mica ? "#B82C2C2C" : "#F7202020");
            Set("Accent", "#FF60CDFF");
            Set("AccentFg", "#FF000000");
            Set("Danger", "#FFFF99A4");
            Set("MenuLine", "#1FFFFFFF");
            Set("MonitorBg", "#FF3D3D3D");
        }
        Changed?.Invoke();
    }

    private static void Set(string key, string hex)
    {
        var color = Parse(hex);
        if (Brushes.TryGetValue(key, out var brush)) brush.Color = color;
        else Brushes[key] = new SolidColorBrush(color);
    }

    private static Color Parse(string hex)
    {
        uint v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return ColorHelper.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static bool ReadAppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }
}
