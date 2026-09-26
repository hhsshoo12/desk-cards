using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DeskCards;

internal static class Theme
{
    public static bool IsLight { get; private set; } = true;

    public static void Apply()
    {
        IsLight = ReadAppsUseLightTheme();
        var r = Application.Current.Resources;
        // 22H2부터는 설정 창에 Mica를 깔고 배경을 비운다.
        bool mica = Environment.OSVersion.Version.Build >= 22621;
        if (IsLight)
        {
            r["CardBg"] = Brush("#D9F3F3F3");
            r["CardBorder"] = Brush("#1F000000");
            r["HoverBg"] = Brush("#14000000");
            r["Fg"] = Brush("#FF1B1B1B");
            r["SubFg"] = Brush("#FF5F5F5F");
            r["PopupBg"] = Brush(Environment.OSVersion.Version.Build >= 22621 ? "#B8FAFAFA" : "#F7F3F3F3");
            r["Accent"] = Brush("#FF005FB8");
            r["SectionBg"] = Brush("#B3FFFFFF");
            r["SectionLine"] = Brush("#0F000000");
            r["SettingsBg"] = Brush(mica ? "#00000000" : "#FFF3F3F3");
            r["RowBg"] = Brush("#B3FFFFFF");
            r["RowBorder"] = Brush("#0F000000");
            r["ControlBg"] = Brush("#B3FFFFFF");
            r["ControlBorder"] = Brush("#1A000000");
            r["AccentFg"] = Brush("#FFFFFFFF");
            r["FlyoutBg"] = Brush("#FFF9F9F9");
            r["ToggledBg"] = Brush("#B3FFFFFF");
            r["Danger"] = Brush("#FFC42B1C");
        }
        else
        {
            r["CardBg"] = Brush("#CC202020");
            r["CardBorder"] = Brush("#26FFFFFF");
            r["HoverBg"] = Brush("#18FFFFFF");
            r["Fg"] = Brush("#FFFFFFFF");
            r["SubFg"] = Brush("#FFC5C5C5");
            r["PopupBg"] = Brush(Environment.OSVersion.Version.Build >= 22621 ? "#B82C2C2C" : "#F7202020");
            r["Accent"] = Brush("#FF60CDFF");
            r["SectionBg"] = Brush("#0FFFFFFF");
            r["SectionLine"] = Brush("#14FFFFFF");
            r["SettingsBg"] = Brush(mica ? "#00000000" : "#FF202020");
            r["RowBg"] = Brush("#0DFFFFFF");
            r["RowBorder"] = Brush("#1A000000");
            r["ControlBg"] = Brush("#0FFFFFFF");
            r["ControlBorder"] = Brush("#17FFFFFF");
            r["AccentFg"] = Brush("#FF000000");
            r["FlyoutBg"] = Brush("#FF2C2C2C");
            r["ToggledBg"] = Brush("#15FFFFFF");
            r["Danger"] = Brush("#FFFF99A4");
        }
    }

    private static bool ReadAppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
