using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DeskFolders;

internal static class Theme
{
    public static bool IsLight { get; private set; } = true;

    public static void Apply()
    {
        IsLight = ReadAppsUseLightTheme();
        var r = Application.Current.Resources;
        if (IsLight)
        {
            r["CardBg"] = Brush("#D9F3F3F3");
            r["CardBorder"] = Brush("#1F000000");
            r["HoverBg"] = Brush("#14000000");
            r["Fg"] = Brush("#FF1B1B1B");
            r["SubFg"] = Brush("#FF5F5F5F");
            r["PopupBg"] = Brush(Environment.OSVersion.Version.Build >= 22621 ? "#00FFFFFF" : "#F7F3F3F3");
            r["Accent"] = Brush("#FF005FB8");
        }
        else
        {
            r["CardBg"] = Brush("#CC202020");
            r["CardBorder"] = Brush("#26FFFFFF");
            r["HoverBg"] = Brush("#18FFFFFF");
            r["Fg"] = Brush("#FFFFFFFF");
            r["SubFg"] = Brush("#FFC5C5C5");
            r["PopupBg"] = Brush(Environment.OSVersion.Version.Build >= 22621 ? "#00000000" : "#F7202020");
            r["Accent"] = Brush("#FF60CDFF");
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
