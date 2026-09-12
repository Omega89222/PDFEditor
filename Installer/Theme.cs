using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PDFEditorSetup;

/// <summary>Palettes claire et sombre, reprises de l'Editeur PDF (et de Notes).</summary>
internal static class Theme
{
    public static bool IsSystemDark()
    {
        try
        {
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return value is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(ResourceDictionary resources, bool dark)
    {
        void Set(string key, string light, string darkColor)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : light));
            brush.Freeze();
            resources[key] = brush;
        }

        Set("WindowBg", "#FFFFFFFF", "#FF1E1E1E");
        Set("SidebarBg", "#FFF1EFEC", "#FF262625");
        Set("CardBg", "#FFFBFAF9", "#FF252524");
        Set("CardBorderBrush", "#FFE4E1DD", "#FF3A3A3C");
        Set("SeparatorBrush", "#FFDFDCD8", "#FF3A3A3C");

        Set("TextPrimary", "#FF1C1C1E", "#FFF5F5F7");
        Set("TextSecondary", "#FF6E6E73", "#FF98989D");
        Set("TextTertiary", "#FFAEAEB2", "#FF6E6E73");

        Set("AccentYellow", "#FFFFCC00", "#FFFFD60A");
        Set("AccentDeep", "#FFB98A00", "#FFFFD60A");
        Set("StepCurrentBg", "#FFF8E4A4", "#FF4A3F18");

        Set("ButtonBg", "#FFFFFFFF", "#FF3A3A3C");
        Set("FieldBg", "#FFFFFFFF", "#FF1C1C1E");
        Set("FieldBorderBrush", "#FFD9D6D2", "#FF48484A");
        Set("HoverBg", "#0D000000", "#14FFFFFF");
        Set("PressedBg", "#16000000", "#20FFFFFF");
        Set("TrackBg", "#FFE6E3DF", "#FF3A3A3C");

        Set("DangerBrush", "#FFFF3B30", "#FFFF453A");
        Set("SuccessBrush", "#FF34C759", "#FF30D158");

        Set("TrafficRed", "#FFFF5F57", "#FFFF5F57");
        Set("TrafficYellow", "#FFFEBC2E", "#FFFEBC2E");
        Set("TrafficGreen", "#FF28C840", "#FF28C840");
        Set("TrafficInactive", "#FFD5D2CD", "#FF4A4A4C");
        Set("TrafficGlyph", "#80000000", "#90000000");
    }
}
