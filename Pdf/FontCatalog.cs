using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace PDFEditor.Pdf;

/// <summary>Police demandee pour un texte : famille, gras, italique.</summary>
public readonly record struct PdfFont(string Family, bool Bold = false, bool Italic = false);

/// <summary>
/// Catalogue des polices proposees dans l'application.
///
/// A l'ecran, le texte est dessine par WPF avec la police systeme ; dans le PDF,
/// Arial / Times New Roman / Courier New sont remplacees par les polices standard
/// Helvetica / Times / Courier (metriques identiques, aucun fichier embarque) tant
/// que le texte reste dans le jeu WinAnsi. Toute autre police est incorporee.
/// </summary>
public static class FontCatalog
{
    private static readonly string[] Preferred =
    {
        "Arial", "Helvetica", "Times New Roman", "Courier New", "Segoe UI", "Calibri", "Georgia",
        "Verdana", "Tahoma", "Trebuchet MS", "Garamond", "Century Gothic", "Book Antiqua",
        "Palatino Linotype", "Franklin Gothic Medium", "Comic Sans MS", "Consolas", "Lucida Console",
        "Segoe Print", "Segoe Script", "Ink Free", "Lucida Handwriting", "Bradley Hand ITC", "Brush Script MT",
        "Freestyle Script", "Mistral", "Kristen ITC"
    };

    private static readonly string[] Handwriting =
    {
        "Segoe Script", "Ink Free", "Segoe Print", "Lucida Handwriting", "Bradley Hand ITC",
        "Brush Script MT", "Freestyle Script", "Mistral", "Kristen ITC"
    };

    private static readonly HashSet<char> WinAnsiExtras = new("€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ");

    private static readonly Dictionary<string, FontFamily> WpfFamilies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<PdfFont, string?> FileCache = new();
    private static List<string>? _families;

    /// <summary>Familles installees et incorporables, dans un ordre agreable.</summary>
    public static IReadOnlyList<string> Families
    {
        get
        {
            if (_families is not null)
            {
                return _families;
            }

            var installed = new HashSet<string>(
                Fonts.SystemFontFamilies.Select(f => f.Source),
                StringComparer.OrdinalIgnoreCase);

            var list = new List<string>();
            foreach (var name in Preferred)
            {
                if (installed.Contains(name) && ResolveFile(new PdfFont(name)) is not null)
                {
                    list.Add(name);
                }
            }

            if (!list.Contains("Arial"))
            {
                list.Insert(0, "Arial");
            }

            _families = list;
            return _families;
        }
    }

    /// <summary>Polices manuscrites installees (signatures tapees).</summary>
    public static IReadOnlyList<string> HandwritingFamilies =>
        Families.Where(f => Handwriting.Contains(f, StringComparer.OrdinalIgnoreCase)).DefaultIfEmpty("Segoe Script").ToList();

    public static FontFamily GetWpfFamily(string family)
    {
        lock (WpfFamilies)
        {
            if (!WpfFamilies.TryGetValue(family, out var result))
            {
                result = new FontFamily(string.IsNullOrWhiteSpace(family) ? "Arial" : family);
                WpfFamilies[family] = result;
            }

            return result;
        }
    }

    public static Typeface GetTypeface(PdfFont font) =>
        new(GetWpfFamily(font.Family),
            font.Italic ? FontStyles.Italic : FontStyles.Normal,
            font.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

    /// <summary>Nom de police standard PDF equivalente, ou null s'il faut incorporer.</summary>
    public static string? GetStandardFontName(PdfFont font, string text)
    {
        if (!IsWinAnsi(text))
        {
            return null;
        }

        var family = font.Family.Trim().ToLowerInvariant();
        return family switch
        {
            "arial" or "helvetica" => (font.Bold, font.Italic) switch
            {
                (false, false) => "Helvetica",
                (true, false) => "Helvetica-Bold",
                (false, true) => "Helvetica-Oblique",
                _ => "Helvetica-BoldOblique"
            },
            "times new roman" or "times" => (font.Bold, font.Italic) switch
            {
                (false, false) => "Times-Roman",
                (true, false) => "Times-Bold",
                (false, true) => "Times-Italic",
                _ => "Times-BoldItalic"
            },
            "courier new" or "courier" => (font.Bold, font.Italic) switch
            {
                (false, false) => "Courier",
                (true, false) => "Courier-Bold",
                (false, true) => "Courier-Oblique",
                _ => "Courier-BoldOblique"
            },
            _ => null
        };
    }

    /// <summary>Fichier TrueType a incorporer pour cette police (repli sur Arial).</summary>
    public static string? GetFontFile(PdfFont font)
    {
        return ResolveFile(font) ?? ResolveFile(new PdfFont("Arial", font.Bold, font.Italic)) ?? ResolveFile(new PdfFont("Arial"));
    }

    private static string? ResolveFile(PdfFont font)
    {
        lock (FileCache)
        {
            if (FileCache.TryGetValue(font, out var cached))
            {
                return cached;
            }

            string? result = null;
            try
            {
                if (GetTypeface(font).TryGetGlyphTypeface(out var glyphs) && glyphs.FontUri.IsFile)
                {
                    var path = glyphs.FontUri.LocalPath;
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    if (extension is ".ttf" or ".otf" && File.Exists(path))
                    {
                        result = path;
                    }
                }
            }
            catch
            {
                result = null;
            }

            FileCache[font] = result;
            return result;
        }
    }

    /// <summary>Vrai si tous les caracteres existent dans l'encodage WinAnsi (cp1252).</summary>
    public static bool IsWinAnsi(string text)
    {
        foreach (var c in text)
        {
            if (c < 0x7F)
            {
                continue;
            }

            if (c >= '\u00A0' && c <= '\u00FF')
            {
                continue;
            }

            if (!WinAnsiExtras.Contains(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Famille d'affichage la plus proche d'une police trouvee dans un PDF.</summary>
    public static string MatchFamily(string pdfFontName, bool serif, bool monospace)
    {
        var name = pdfFontName.ToLowerInvariant();
        var plus = name.IndexOf('+');
        if (plus is > 0 and < 8)
        {
            name = name[(plus + 1)..];
        }

        foreach (var family in Families)
        {
            var compact = family.Replace(" ", "").ToLowerInvariant();
            if (name.Replace(" ", "").Replace("-", "").StartsWith(compact, StringComparison.Ordinal))
            {
                return family;
            }
        }

        if (name.Contains("courier") || name.Contains("mono") || name.Contains("consol") || monospace)
        {
            return "Courier New";
        }

        if (name.Contains("times") || name.Contains("serif") && !name.Contains("sans") || name.Contains("garamond")
            || name.Contains("georgia") || name.Contains("cambria") || name.Contains("minion") || serif)
        {
            return Families.Contains("Georgia") && name.Contains("georgia") ? "Georgia" : "Times New Roman";
        }

        if (name.Contains("calibri") && Families.Contains("Calibri"))
        {
            return "Calibri";
        }

        if (name.Contains("segoe") && Families.Contains("Segoe UI"))
        {
            return "Segoe UI";
        }

        if (name.Contains("verdana") && Families.Contains("Verdana"))
        {
            return "Verdana";
        }

        return "Arial";
    }
}
