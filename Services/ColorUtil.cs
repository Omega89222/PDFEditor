using System;
using System.Windows.Media;

namespace PDFEditor.Services;

/// <summary>Couleurs : palette systeme Apple, conversions hexadecimales.</summary>
public static class ColorUtil
{
    public static readonly (Color Color, string Name)[] Palette =
    {
        (Color.FromRgb(0xFF, 0x3B, 0x30), "Rouge"),
        (Color.FromRgb(0xFF, 0x95, 0x00), "Orange"),
        (Color.FromRgb(0xFF, 0xCC, 0x00), "Jaune"),
        (Color.FromRgb(0x34, 0xC7, 0x59), "Vert"),
        (Color.FromRgb(0x00, 0xC7, 0xBE), "Menthe"),
        (Color.FromRgb(0x32, 0xAD, 0xE6), "Cyan"),
        (Color.FromRgb(0x00, 0x7A, 0xFF), "Bleu"),
        (Color.FromRgb(0x58, 0x56, 0xD6), "Indigo"),
        (Color.FromRgb(0xAF, 0x52, 0xDE), "Violet"),
        (Color.FromRgb(0xFF, 0x2D, 0x55), "Rose"),
        (Color.FromRgb(0xA2, 0x84, 0x5E), "Marron"),
        (Color.FromRgb(0x1C, 0x2A, 0x6B), "Bleu encre"),
        (Color.FromRgb(0x1C, 0x1C, 0x1E), "Noir"),
        (Color.FromRgb(0x8E, 0x8E, 0x93), "Gris"),
        (Color.FromRgb(0xD1, 0xD1, 0xD6), "Gris clair"),
        (Color.FromRgb(0xFF, 0xFF, 0xFF), "Blanc")
    };

    public static Color Parse(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            var text = value.Trim();
            if (!text.StartsWith('#') && text.Length is 6 or 8)
            {
                text = "#" + text;
            }

            return ColorConverter.ConvertFromString(text) is Color color ? color : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static bool TryParse(string? value, out Color color)
    {
        color = Parse(value, Color.FromArgb(1, 1, 2, 3));
        return color != Color.FromArgb(1, 1, 2, 3);
    }

    public static string ToHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    public static string ToRgbHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static string NameOf(Color color)
    {
        if (color.A == 0)
        {
            return "Aucune";
        }

        foreach (var (c, name) in Palette)
        {
            if (c.R == color.R && c.G == color.G && c.B == color.B)
            {
                return name;
            }
        }

        return ToRgbHex(color);
    }

    public static Color Darken(Color color, double factor) =>
        Color.FromArgb(color.A, (byte)(color.R * (1 - factor)), (byte)(color.G * (1 - factor)), (byte)(color.B * (1 - factor)));

    public static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    public static bool SameRgb(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;
}
