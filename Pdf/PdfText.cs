using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace PDFEditor.Pdf;

/// <summary>Un caractere extrait de la page, avec sa boite en points d'affichage.</summary>
public readonly struct PdfChar
{
    public PdfChar(char value, Rect box, double fontSize, bool generated)
    {
        Value = value;
        Box = box;
        FontSize = fontSize;
        Generated = generated;
    }

    public char Value { get; }

    /// <summary>Boite « large » du caractere (hauteur de ligne), Rect.Empty si aucune.</summary>
    public Rect Box { get; }

    public double FontSize { get; }

    /// <summary>Caractere ajoute par PDFium (espace ou saut de ligne deduit).</summary>
    public bool Generated { get; }

    public bool HasBox => !Box.IsEmpty && Box.Width > 0.01 && Box.Height > 0.01;
}

/// <summary>Ligne de texte reconstituee (sert a l'outil « Modifier le texte »).</summary>
public sealed class PdfTextLine
{
    public int Start { get; init; }
    public int End { get; init; }
    public Rect Bounds { get; init; }
    public string Text { get; init; } = "";
    public double FontSize { get; init; }
    public string FontName { get; init; } = "";
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Serif { get; init; }
    public bool Monospace { get; init; }
    public Color Color { get; init; } = Colors.Black;

    /// <summary>Ordonnee de la ligne de base (points d'affichage).</summary>
    public double Baseline { get; init; }
}

/// <summary>Intervalle de caracteres (bornes incluses).</summary>
public readonly record struct TextSpan(int Start, int End)
{
    public int Length => End - Start + 1;
}

/// <summary>
/// Couche texte d'une page : caracteres, lignes, recherche et selection.
/// Les coordonnees sont en points d'affichage (origine en haut a gauche).
/// </summary>
public sealed class PdfTextPage
{
    private static readonly Dictionary<char, char> FoldCache = new();

    private string? _foldedText;
    private List<int>? _foldedMap;
    private bool _foldedMatchCase;

    private PdfTextPage(int pageIndex, PdfChar[] chars, string text, List<PdfTextLine> lines)
    {
        PageIndex = pageIndex;
        Chars = chars;
        Text = text;
        Lines = lines;
    }

    public int PageIndex { get; }

    public PdfChar[] Chars { get; }

    /// <summary>Texte complet de la page, meme indexation que <see cref="Chars"/>.</summary>
    public string Text { get; }

    public IReadOnlyList<PdfTextLine> Lines { get; }

    public bool HasText => Lines.Count > 0;

    public static PdfTextPage Empty(int pageIndex) =>
        new(pageIndex, Array.Empty<PdfChar>(), "", new List<PdfTextLine>());

    // =====================================================================
    // Construction (sous PdfDoc.Sync)
    // =====================================================================

    internal static unsafe PdfTextPage Build(int pageIndex, IntPtr textPage, PdfPageInfo info)
    {
        var count = Native.FPDFText_CountChars(textPage);
        if (count <= 0)
        {
            return Empty(pageIndex);
        }

        var chars = new PdfChar[count];
        var builder = new StringBuilder(count);

        for (var i = 0; i < count; i++)
        {
            var code = Native.FPDFText_GetUnicode(textPage, i);
            var value = code is > 0 and <= 0xFFFF ? (char)code : '�';
            var generated = Native.FPDFText_IsGenerated(textPage, i) == 1;

            var box = Rect.Empty;
            if (Native.FPDFText_GetLooseCharBox(textPage, i, out var r) != 0)
            {
                var display = info.ToDisplayRect(r.Left, r.Bottom, r.Right, r.Top);
                if (display.Width > 0.01 && display.Height > 0.01)
                {
                    box = display;
                }
            }

            chars[i] = new PdfChar(value, box, Native.FPDFText_GetFontSize(textPage, i), generated);
            builder.Append(value);
        }

        var text = builder.ToString();
        var lines = BuildLines(textPage, info, chars, text);
        return new PdfTextPage(pageIndex, chars, text, lines);
    }

    private static List<PdfTextLine> BuildLines(IntPtr textPage, PdfPageInfo info, PdfChar[] chars, string text)
    {
        var lines = new List<PdfTextLine>();
        var start = -1;
        var last = -1;
        var bounds = Rect.Empty;

        void Flush()
        {
            if (start >= 0 && last >= start && !bounds.IsEmpty)
            {
                var line = MakeLine(textPage, info, chars, text, start, last, bounds);
                if (line is not null)
                {
                    lines.Add(line);
                }
            }

            start = -1;
            last = -1;
            bounds = Rect.Empty;
        }

        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c.Value is '\r' or '\n')
            {
                Flush();
                continue;
            }

            if (!c.HasBox)
            {
                if (start >= 0)
                {
                    last = i;
                }

                continue;
            }

            if (start >= 0)
            {
                var height = Math.Max(bounds.Height, c.Box.Height);
                var centerLine = bounds.Top + bounds.Height / 2;
                var centerChar = c.Box.Top + c.Box.Height / 2;
                var sameRow = Math.Abs(centerLine - centerChar) < height * 0.5;
                var closeEnough = c.Box.Left - bounds.Right < height * 2.5 && c.Box.Right > bounds.Left - height;

                if (!sameRow || !closeEnough)
                {
                    Flush();
                }
            }

            if (start < 0)
            {
                start = i;
                bounds = c.Box;
            }
            else
            {
                bounds.Union(c.Box);
            }

            last = i;
        }

        Flush();
        return lines;
    }

    private static unsafe PdfTextLine? MakeLine(IntPtr textPage, PdfPageInfo info, PdfChar[] chars, string text, int start, int end, Rect bounds)
    {
        var content = text.Substring(start, end - start + 1).Trim();
        if (content.Length == 0)
        {
            return null;
        }

        // Caractere representatif : le premier non blanc muni d'une boite.
        var probe = start;
        for (var i = start; i <= end; i++)
        {
            if (chars[i].HasBox && !char.IsWhiteSpace(chars[i].Value))
            {
                probe = i;
                break;
            }
        }

        var sizes = new List<double>();
        for (var i = start; i <= end; i++)
        {
            if (chars[i].HasBox && chars[i].FontSize > 0)
            {
                sizes.Add(chars[i].FontSize);
            }
        }

        sizes.Sort();
        var fontSize = sizes.Count > 0 ? sizes[sizes.Count / 2] : 12;

        var name = "";
        var flags = 0;
        byte* buffer = stackalloc byte[256];
        var length = Native.FPDFText_GetFontInfo(textPage, probe, buffer, 256, out flags);
        if (length > 0 && length <= 256)
        {
            var n = (int)length;
            if (buffer[n - 1] == 0)
            {
                n--;
            }

            name = Encoding.UTF8.GetString(buffer, n);
        }

        var weight = Native.FPDFText_GetFontWeight(textPage, probe);
        var color = Colors.Black;
        if (Native.FPDFText_GetFillColor(textPage, probe, out var r, out var g, out var b, out _) != 0)
        {
            color = Color.FromRgb((byte)r, (byte)g, (byte)b);
        }

        var baseline = bounds.Bottom;
        if (Native.FPDFText_GetCharOrigin(textPage, probe, out var ox, out var oy) != 0)
        {
            baseline = info.UserToDisplay.Transform(ox, oy).Y;
        }

        var lowerName = name.ToLowerInvariant();

        return new PdfTextLine
        {
            Start = start,
            End = end,
            Bounds = bounds,
            Text = content,
            FontSize = fontSize,
            FontName = name,
            Bold = weight >= 600 || lowerName.Contains("bold") || lowerName.Contains("black") || lowerName.Contains("heavy") || lowerName.Contains("semibold"),
            Italic = (flags & 64) != 0 || lowerName.Contains("italic") || lowerName.Contains("oblique"),
            Serif = (flags & 2) != 0,
            Monospace = (flags & 1) != 0,
            Color = color,
            Baseline = baseline
        };
    }

    // =====================================================================
    // Recherche de position
    // =====================================================================

    /// <summary>Caractere dont la boite contient le point, ou -1.</summary>
    public int HitTestChar(Point point, double tolerance = 1.5)
    {
        for (var i = 0; i < Chars.Length; i++)
        {
            var c = Chars[i];
            if (!c.HasBox)
            {
                continue;
            }

            var box = c.Box;
            box.Inflate(tolerance, tolerance);
            if (box.Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Vrai si le point survole du texte (curseur en I).</summary>
    public bool IsOverText(Point point) => LineAt(point, 1.5) is not null;

    /// <summary>Ligne sous le point, ou null.</summary>
    public PdfTextLine? LineAt(Point point, double tolerance = 2)
    {
        foreach (var line in Lines)
        {
            var box = line.Bounds;
            box.Inflate(tolerance, tolerance);
            if (box.Contains(point))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// Caractere le plus proche d'un point, pour la selection a la souris :
    /// on choisit d'abord la ligne la plus proche verticalement, puis le
    /// caractere le plus proche horizontalement dans cette ligne.
    /// </summary>
    public int NearestChar(Point point)
    {
        if (Lines.Count == 0)
        {
            return -1;
        }

        PdfTextLine? best = null;
        var bestDistance = double.MaxValue;
        foreach (var line in Lines)
        {
            double dy = point.Y < line.Bounds.Top ? line.Bounds.Top - point.Y
                : point.Y > line.Bounds.Bottom ? point.Y - line.Bounds.Bottom
                : 0;
            double dx = point.X < line.Bounds.Left ? line.Bounds.Left - point.X
                : point.X > line.Bounds.Right ? point.X - line.Bounds.Right
                : 0;
            var distance = dy * 4 + dx;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = line;
            }
        }

        if (best is null)
        {
            return -1;
        }

        var result = best.Start;
        var nearest = double.MaxValue;
        for (var i = best.Start; i <= best.End; i++)
        {
            var c = Chars[i];
            if (!c.HasBox)
            {
                continue;
            }

            var center = c.Box.Left + c.Box.Width / 2;
            var d = Math.Abs(center - point.X);
            if (d < nearest)
            {
                nearest = d;
                result = i;
            }
        }

        return result;
    }

    /// <summary>Mot contenant le caractere (double-clic).</summary>
    public TextSpan WordAt(int index)
    {
        if (index < 0 || index >= Text.Length)
        {
            return new TextSpan(index, index);
        }

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '\'' or '’' or '_' or '@' or '.';

        if (!IsWordChar(Text[index]))
        {
            return new TextSpan(index, index);
        }

        var start = index;
        var end = index;
        while (start > 0 && IsWordChar(Text[start - 1]))
        {
            start--;
        }

        while (end < Text.Length - 1 && IsWordChar(Text[end + 1]))
        {
            end++;
        }

        // Ponctuation finale exclue.
        while (end > start && Text[end] == '.')
        {
            end--;
        }

        return new TextSpan(start, end);
    }

    /// <summary>Ligne complete contenant le caractere (triple-clic).</summary>
    public TextSpan LineSpanAt(int index)
    {
        foreach (var line in Lines)
        {
            if (index >= line.Start && index <= line.End)
            {
                return new TextSpan(line.Start, line.End);
            }
        }

        return new TextSpan(index, index);
    }

    /// <summary>Rectangles de surlignage d'une plage, fusionnes ligne par ligne.</summary>
    public List<Rect> GetSpanRects(TextSpan span)
    {
        var rects = new List<Rect>();
        var start = Math.Max(0, Math.Min(span.Start, span.End));
        var end = Math.Min(Chars.Length - 1, Math.Max(span.Start, span.End));
        var current = Rect.Empty;

        for (var i = start; i <= end; i++)
        {
            var c = Chars[i];
            if (!c.HasBox)
            {
                continue;
            }

            if (current.IsEmpty)
            {
                current = c.Box;
                continue;
            }

            var height = Math.Max(current.Height, c.Box.Height);
            var sameRow = Math.Abs((current.Top + current.Height / 2) - (c.Box.Top + c.Box.Height / 2)) < height * 0.5
                          && c.Box.Left > current.Left - height;
            if (sameRow)
            {
                current.Union(c.Box);
            }
            else
            {
                rects.Add(current);
                current = c.Box;
            }
        }

        if (!current.IsEmpty)
        {
            rects.Add(current);
        }

        return rects;
    }

    public string GetSpanText(TextSpan span)
    {
        var start = Math.Max(0, Math.Min(span.Start, span.End));
        var end = Math.Min(Text.Length - 1, Math.Max(span.Start, span.End));
        return end < start ? "" : Text.Substring(start, end - start + 1);
    }

    // =====================================================================
    // Recherche plein texte
    // =====================================================================

    /// <summary>
    /// Toutes les occurrences de <paramref name="query"/>. Sans respect de la casse,
    /// les accents sont ignores (« ete » trouve « Été ») ; les espaces et sauts
    /// de ligne consecutifs comptent pour un seul espace.
    /// </summary>
    public List<TextSpan> Find(string query, bool matchCase, bool wholeWord)
    {
        var results = new List<TextSpan>();
        if (string.IsNullOrWhiteSpace(query) || Text.Length == 0)
        {
            return results;
        }

        EnsureFolded(matchCase);
        var haystack = _foldedText!;
        var map = _foldedMap!;
        var needle = Fold(query.Trim(), matchCase);
        if (needle.Length == 0)
        {
            return results;
        }

        var position = 0;
        while (position <= haystack.Length - needle.Length)
        {
            var found = haystack.IndexOf(needle, position, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            var endFolded = found + needle.Length - 1;
            var ok = true;
            if (wholeWord)
            {
                var before = found > 0 ? haystack[found - 1] : ' ';
                var after = endFolded + 1 < haystack.Length ? haystack[endFolded + 1] : ' ';
                ok = !char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after);
            }

            if (ok)
            {
                results.Add(new TextSpan(map[found], map[endFolded]));
            }

            position = found + Math.Max(1, needle.Length);
        }

        return results;
    }

    private void EnsureFolded(bool matchCase)
    {
        if (_foldedText is not null && _foldedMatchCase == matchCase)
        {
            return;
        }

        var builder = new StringBuilder(Text.Length);
        var map = new List<int>(Text.Length);
        var lastWasSpace = true;

        for (var i = 0; i < Text.Length; i++)
        {
            var c = Text[i];
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    map.Add(i);
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(matchCase ? c : FoldChar(c));
            map.Add(i);
            lastWasSpace = false;
        }

        _foldedText = builder.ToString();
        _foldedMap = map;
        _foldedMatchCase = matchCase;
    }

    private static string Fold(string value, bool matchCase)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(matchCase ? c : FoldChar(c));
            lastWasSpace = false;
        }

        return builder.ToString();
    }

    /// <summary>Minuscule sans diacritique, toujours un seul caractere.</summary>
    public static char FoldChar(char c)
    {
        if (c < 128)
        {
            return char.ToLowerInvariant(c);
        }

        lock (FoldCache)
        {
            if (FoldCache.TryGetValue(c, out var cached))
            {
                return cached;
            }

            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            var result = char.ToLowerInvariant(c);
            foreach (var d in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark)
                {
                    result = char.ToLowerInvariant(d);
                    break;
                }
            }

            FoldCache[c] = result;
            return result;
        }
    }
}
