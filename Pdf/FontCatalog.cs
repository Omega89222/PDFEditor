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
        "Arial", "Helvetica", "Times New Roman", "Courier New", "Segoe UI", "Calibri", "Cambria", "Aptos",
        "Georgia", "Verdana", "Tahoma", "Trebuchet MS", "Garamond", "Century Gothic", "Book Antiqua",
        "Palatino Linotype", "Franklin Gothic Medium", "Candara", "Corbel", "Constantia", "Arial Narrow",
        "Bahnschrift", "Impact", "Rockwell", "Comic Sans MS", "Consolas", "Cascadia Mono", "Lucida Console",
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

    /// <summary>Familles installees, indexees par nom compact (« timesnewroman »).</summary>
    private static Dictionary<string, string>? _installedByCompact;

    private static Dictionary<string, string> InstalledByCompact
    {
        get
        {
            if (_installedByCompact is null)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var family in Fonts.SystemFontFamilies)
                {
                    var key = Compact(family.Source);
                    if (key.Length > 0 && !map.ContainsKey(key))
                    {
                        map[key] = family.Source;
                    }
                }

                _installedByCompact = map;
            }

            return _installedByCompact;
        }
    }

    /// <summary>Suffixes de style a retirer d'un nom de police PDF (« Arial-BoldMT »).</summary>
    private static readonly string[] StyleSuffixes =
    {
        "psmt", "ps", "mt", "std", "pro", "lt", "ce", "wgl4", "identityh", "identityv",
        "bolditalic", "boldoblique", "semibold", "demibold", "extrabold", "ultrabold", "bold",
        "italic", "oblique", "regular", "roman", "book", "light", "medium", "black", "heavy", "bd", "it", "rg"
    };

    /// <summary>Equivalences vers une police installee : clones metriques, polices libres et polices web.</summary>
    private static readonly (string Contains, string Family)[] Aliases =
    {
        ("liberationsans", "Arial"), ("liberationserif", "Times New Roman"), ("liberationmono", "Courier New"),
        ("nimbussan", "Arial"), ("nimbusrom", "Times New Roman"), ("nimbusmon", "Courier New"),
        ("freesans", "Arial"), ("freeserif", "Times New Roman"), ("freemono", "Courier New"),
        ("dejavusansmono", "Consolas"), ("dejavusans", "Verdana"), ("dejavuserif", "Georgia"),
        ("bitstreamvera", "Verdana"), ("carlito", "Calibri"), ("caladea", "Cambria"), ("aptos", "Calibri"),
        ("helvetica", "Arial"), ("arialnarrow", "Arial Narrow"), ("arial", "Arial"),
        ("timesnewroman", "Times New Roman"), ("times", "Times New Roman"), ("courier", "Courier New"),
        ("cmtt", "Courier New"), ("cmss", "Arial"), ("cmr", "Times New Roman"), ("cmbx", "Times New Roman"),
        ("cmti", "Times New Roman"), ("lmmono", "Courier New"), ("lmsans", "Arial"), ("lmroman", "Times New Roman"),
        ("latinmodern", "Times New Roman"), ("computermodern", "Times New Roman"),
        ("segoe", "Segoe UI"), ("calibri", "Calibri"), ("cambria", "Cambria"), ("candara", "Candara"),
        ("corbel", "Corbel"), ("constantia", "Constantia"), ("georgia", "Georgia"), ("verdana", "Verdana"),
        ("tahoma", "Tahoma"), ("trebuchet", "Trebuchet MS"), ("consolas", "Consolas"), ("garamond", "Garamond"),
        ("palatino", "Palatino Linotype"), ("bookantiqua", "Book Antiqua"), ("centurygothic", "Century Gothic"),
        ("futura", "Century Gothic"), ("avenir", "Century Gothic"), ("franklin", "Franklin Gothic Medium"),
        ("comicsans", "Comic Sans MS"), ("impact", "Impact"), ("rockwell", "Rockwell"),
        ("roboto", "Segoe UI"), ("opensans", "Segoe UI"), ("notosansmono", "Consolas"), ("notosans", "Segoe UI"),
        ("sourcesans", "Segoe UI"), ("lato", "Segoe UI"), ("inter", "Segoe UI"), ("ptsans", "Segoe UI"),
        ("montserrat", "Century Gothic"), ("poppins", "Century Gothic"), ("ptserif", "Georgia"),
        ("notoserif", "Georgia"), ("sourceserif", "Georgia"), ("merriweather", "Georgia"), ("lora", "Georgia"),
        ("minion", "Times New Roman"), ("myriad", "Arial"), ("thorndale", "Times New Roman"), ("albany", "Arial"),
        ("cumberland", "Courier New")
    };

    /// <summary>
    /// Familles resolues par Windows mais absentes de la liste des familles
    /// (« Arial Black » est range sous « Arial » par WPF).
    /// </summary>
    private static readonly Dictionary<string, bool> Probed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vrai si Windows sait dessiner cette famille sous ce nom exact.</summary>
    public static bool IsInstalled(string family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return false;
        }

        if (InstalledByCompact.ContainsKey(Compact(family)))
        {
            return true;
        }

        lock (Probed)
        {
            if (Probed.TryGetValue(family, out var known))
            {
                return known;
            }

            var found = false;
            try
            {
                var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (typeface.TryGetGlyphTypeface(out var glyphs))
                {
                    var wanted = Compact(family);
                    found = glyphs.Win32FamilyNames.Values.Concat(glyphs.FamilyNames.Values)
                        .Any(name => Compact(name) == wanted);
                }
            }
            catch
            {
                found = false;
            }

            Probed[family] = found;
            return found;
        }
    }

    /// <summary>Vrai si le nom de la famille porte deja la graisse (« Arial Black »).</summary>
    public static bool FamilyCarriesWeight(string family)
    {
        var compact = Compact(family);
        return compact.EndsWith("black", StringComparison.Ordinal) || compact.EndsWith("bold", StringComparison.Ordinal)
            || compact.EndsWith("heavy", StringComparison.Ordinal) || compact.EndsWith("light", StringComparison.Ordinal)
            || compact.EndsWith("semibold", StringComparison.Ordinal) || compact.EndsWith("medium", StringComparison.Ordinal);
    }

    /// <summary>Vrai si le nom de la famille porte deja l'italique.</summary>
    public static bool FamilyCarriesItalic(string family)
    {
        var compact = Compact(family);
        return compact.EndsWith("italic", StringComparison.Ordinal) || compact.EndsWith("oblique", StringComparison.Ordinal);
    }

    /// <summary>
    /// Famille la plus proche d'une police trouvee dans un PDF. Le nom est nettoye
    /// (prefixe de sous-ensemble « ABCDEF+ », variante « ,Bold », suffixes « -BoldMT »), puis
    /// compare aux polices du systeme, aux equivalences connues, enfin aux indices du document.
    /// </summary>
    public static string MatchFamily(string pdfFontName, bool serif, bool monospace)
    {
        var raw = (pdfFontName ?? "").Trim();

        var plus = raw.IndexOf('+');
        if (plus is > 0 and < 8)
        {
            raw = raw[(plus + 1)..];
        }

        var comma = raw.IndexOf(',');
        if (comma > 0)
        {
            raw = raw[..comma];
        }

        // 1. Le nom exact (« Arial Black »), puis le meme nom sans ses suffixes de style.
        foreach (var candidate in NameCandidates(raw))
        {
            if (InstalledByCompact.TryGetValue(Compact(candidate), out var installed))
            {
                return installed;
            }

            if (IsInstalled(candidate))
            {
                return candidate;
            }
        }

        // 2. Equivalences connues.
        var compact = Compact(raw);
        foreach (var (contains, family) in Aliases)
        {
            if (compact.Contains(contains, StringComparison.Ordinal) && IsInstalled(family))
            {
                return InstalledByCompact.TryGetValue(Compact(family), out var installed) ? installed : family;
            }
        }

        // 3. Indices donnes par le document.
        if (monospace || compact.Contains("mono", StringComparison.Ordinal) || compact.Contains("consol", StringComparison.Ordinal))
        {
            return "Courier New";
        }

        if (compact.Contains("script", StringComparison.Ordinal) || compact.Contains("hand", StringComparison.Ordinal))
        {
            return HandwritingFamilies[0];
        }

        if (serif && !compact.Contains("sans", StringComparison.Ordinal))
        {
            return "Times New Roman";
        }

        return "Arial";
    }

    /// <summary>Gras et italique annonces par le nom de la police (« Arial-BoldItalicMT »).</summary>
    public static (bool Bold, bool Italic) StyleFromName(string pdfFontName)
    {
        var compact = Compact(pdfFontName ?? "");
        var bold = compact.Contains("bold", StringComparison.Ordinal)
                   || compact.Contains("black", StringComparison.Ordinal)
                   || compact.Contains("heavy", StringComparison.Ordinal);
        var italic = compact.Contains("italic", StringComparison.Ordinal)
                     || compact.Contains("oblique", StringComparison.Ordinal);
        return (bold, italic);
    }

    /// <summary>« Arial-BoldMT » -> « Arial Bold MT », « Arial Bold », « Arial ».</summary>
    private static IEnumerable<string> NameCandidates(string raw)
    {
        var name = Normalize(raw);
        if (name.Length == 0)
        {
            yield break;
        }

        yield return name;

        for (var round = 0; round < 5; round++)
        {
            var compact = Compact(name);
            var shorter = name;

            foreach (var suffix in StyleSuffixes)
            {
                if (compact.Length > suffix.Length + 2 && compact.EndsWith(suffix, StringComparison.Ordinal))
                {
                    shorter = TrimLetters(name, suffix.Length);
                    break;
                }
            }

            if (shorter.Length == 0 || shorter == name)
            {
                yield break;
            }

            name = shorter;
            yield return name;
        }
    }

    /// <summary>Nom lisible : separateurs remplaces par des espaces.</summary>
    private static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c is '-' or '_' or '.' or '#' ? ' ' : c);
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Retire les <paramref name="count"/> dernieres lettres ou chiffres du nom.</summary>
    private static string TrimLetters(string name, int count)
    {
        var index = name.Length;
        var removed = 0;
        while (index > 0 && removed < count)
        {
            index--;
            if (char.IsLetterOrDigit(name[index]))
            {
                removed++;
            }
        }

        return name[..index].TrimEnd(' ', '-', '_', '.');
    }

    /// <summary>Nom reduit aux lettres et chiffres, en minuscules.</summary>
    private static string Compact(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
