using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DeskCards;

internal static class Theme
{
    public static bool IsLight { get; private set; } = true;

    /// <summary>창 배경에 Mica·아크릴을 쓸 수 있는지(22H2부터).</summary>
    public static bool HasBackdrop => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>테마(밝게·어둡게)를 다시 적용했을 때. .dard 화면에 새 색을 알려 준다.</summary>
    public static event Action? Changed;

    public static void Apply()
    {
        IsLight = ReadAppsUseLightTheme();
        var r = Application.Current.Resources;
        // 시스템 배경을 쓸 수 있으면 설정 창·펼친 창 바탕을 비치게 둔다.
        bool mica = HasBackdrop;
        if (IsLight)
        {
            r["CardBg"] = Brush("#D9F3F3F3");
            r["CardBorder"] = Brush("#1F000000");
            r["HoverBg"] = Brush("#14000000");
            r["Fg"] = Brush("#FF1B1B1B");
            r["SubFg"] = Brush("#FF5F5F5F");
            r["PopupBg"] = Brush(mica ? "#B8FAFAFA" : "#F7F3F3F3");
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
            r["MenuLine"] = Brush("#14000000");
            r["MonitorBg"] = Brush("#FFD6D6D6");
            r["ScrollThumb"] = Brush("#FF8A8A8A");
            r["ScrollTrack"] = Brush("#F2F9F9F9");
        }
        else
        {
            r["CardBg"] = Brush("#CC202020");
            r["CardBorder"] = Brush("#26FFFFFF");
            r["HoverBg"] = Brush("#18FFFFFF");
            r["Fg"] = Brush("#FFFFFFFF");
            r["SubFg"] = Brush("#FFC5C5C5");
            r["PopupBg"] = Brush(mica ? "#B82C2C2C" : "#F7202020");
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
            r["MenuLine"] = Brush("#1FFFFFFF");
            r["MonitorBg"] = Brush("#FF3D3D3D");
            r["ScrollThumb"] = Brush("#FF9F9F9F");
            r["ScrollTrack"] = Brush("#F22C2C2C");
        }
        Changed?.Invoke();
    }

    /// <summary>.dard 화면에 CSS 변수(--desk-이름)로 넘겨 주는 색. #RRGGBBAA 형식.</summary>
    public static System.Collections.Generic.Dictionary<string, string> CssColors()
    {
        var r = Application.Current.Resources;
        string Css(string key) => r[key] is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}{b.Color.A:X2}" : "transparent";
        return new()
        {
            ["fg"] = Css("Fg"),
            ["subfg"] = Css("SubFg"),
            ["accent"] = Css("Accent"),
            ["accent-fg"] = Css("AccentFg"),
            ["card-bg"] = Css("CardBg"),
            ["border"] = Css("CardBorder"),
            ["hover"] = Css("HoverBg"),
            ["control-bg"] = Css("ControlBg"),
            ["control-border"] = Css("ControlBorder"),
            ["danger"] = Css("Danger"),
        };
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
