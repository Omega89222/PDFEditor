using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PDFEditor.Pdf;

namespace PDFEditor.Rendering;

public sealed record LaidOutLine(string Text, double X, double Top, double Width);

public sealed class TextLayoutResult
{
    public required List<LaidOutLine> Lines { get; init; }

    /// <summary>Interligne (points).</summary>
    public double LineHeight { get; init; }

    /// <summary>Distance du haut d'une ligne a sa ligne de base.</summary>
    public double Baseline { get; init; }

    public double ContentHeight => Lines.Count * LineHeight;

    public double MaxLineWidth => Lines.Count == 0 ? 0 : Lines.Max(l => l.Width);
}

public readonly record struct StampLayoutResult(Rect BorderRect, double Radius, double FontSize, double TextX, double TextTop, double Baseline);

/// <summary>
/// Mise en page du texte des annotations. Utilisee a l'identique pour l'affichage
/// et pour l'ecriture dans le PDF, afin que les retours a la ligne correspondent.
/// Les unites sont des points PDF.
/// </summary>
public static class TextLayout
{
    private const int CacheLimit = 4000;

    private static readonly Dictionary<(string Text, PdfFont Font), double> WidthCache = new();

    /// <summary>Largeur d'une chaine, en points.</summary>
    public static double MeasureWidth(string text, PdfFont font, double fontSize)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
        {
            return 0;
        }

        // On mesure a la taille 100 puis on met a l'echelle : cache independant de la taille.
        double unit;
        lock (WidthCache)
        {
            if (!WidthCache.TryGetValue((text, font), out unit))
            {
                var formatted = new FormattedText(
                    text,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    FontCatalog.GetTypeface(font),
                    100,
                    Brushes.Black,
                    null,
                    TextFormattingMode.Ideal,
                    1.0);

                unit = formatted.WidthIncludingTrailingWhitespace / 100;
                if (WidthCache.Count > CacheLimit)
                {
                    WidthCache.Clear();
                }

                WidthCache[(text, font)] = unit;
            }
        }

        return unit * fontSize;
    }

    public static double LineHeightFor(PdfFont font, double fontSize) =>
        FontCatalog.GetWpfFamily(font.Family).LineSpacing * fontSize;

    public static double BaselineFor(PdfFont font, double fontSize) =>
        FontCatalog.GetWpfFamily(font.Family).Baseline * fontSize;

    public static TextLayoutResult Layout(string text, PdfFont font, double fontSize, double maxWidth, TextAlignment alignment)
    {
        var lineHeight = LineHeightFor(font, fontSize);
        var baseline = BaselineFor(font, fontSize);
        var lines = new List<LaidOutLine>();
        var top = 0.0;

        var paragraphs = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var paragraph in paragraphs)
        {
            foreach (var raw in Wrap(paragraph, font, fontSize, maxWidth))
            {
                var content = raw.TrimEnd();
                var width = MeasureWidth(content, font, fontSize);
                var x = 0.0;
                if (!double.IsInfinity(maxWidth))
                {
                    x = alignment switch
                    {
                        TextAlignment.Center => Math.Max(0, (maxWidth - width) / 2),
                        TextAlignment.Right => Math.Max(0, maxWidth - width),
                        _ => 0
                    };
                }

                lines.Add(new LaidOutLine(content, x, top, width));
                top += lineHeight;
            }
        }

        return new TextLayoutResult { Lines = lines, LineHeight = lineHeight, Baseline = baseline };
    }

    private static List<string> Wrap(string paragraph, PdfFont font, double fontSize, double maxWidth)
    {
        var result = new List<string>();
        if (paragraph.Length == 0 || double.IsInfinity(maxWidth) || maxWidth <= 0)
        {
            result.Add(paragraph);
            return result;
        }

        var current = "";
        foreach (var token in Tokenize(paragraph))
        {
            var candidate = current + token;
            if (MeasureWidth(candidate.TrimEnd(), font, fontSize) <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                result.Add(current);
                current = "";
            }

            // Mot plus long que la largeur : coupure au caractere.
            if (MeasureWidth(token.TrimEnd(), font, fontSize) > maxWidth)
            {
                foreach (var ch in token)
                {
                    if (current.Length > 0 && MeasureWidth((current + ch).TrimEnd(), font, fontSize) > maxWidth)
                    {
                        result.Add(current);
                        current = "";
                    }

                    current += ch;
                }
            }
            else
            {
                current = token;
            }
        }

        if (current.Length > 0 || result.Count == 0)
        {
            result.Add(current);
        }

        return result;
    }

    /// <summary>Decoupe en mots, chacun suivi de ses espaces.</summary>
    private static IEnumerable<string> Tokenize(string paragraph)
    {
        var start = 0;
        var i = 0;
        while (i < paragraph.Length)
        {
            while (i < paragraph.Length && paragraph[i] != ' ')
            {
                i++;
            }

            while (i < paragraph.Length && paragraph[i] == ' ')
            {
                i++;
            }

            yield return paragraph[start..i];
            start = i;
        }
    }

    /// <summary>Disposition d'un tampon : cadre arrondi et libelle centre, ajuste.</summary>
    public static StampLayoutResult LayoutStamp(string label, Rect rect, double strokeWidth)
    {
        var inset = Math.Max(0, strokeWidth) / 2 + 1;
        var border = new Rect(rect.X + inset, rect.Y + inset, Math.Max(1, rect.Width - 2 * inset), Math.Max(1, rect.Height - 2 * inset));
        var radius = Math.Min(border.Height * 0.2, 9);
        var font = StampFont;

        var unit = MeasureWidth(label, font, 1);
        var byHeight = border.Height * 0.55;
        var byWidth = unit > 0 ? (border.Width - border.Height * 0.45) / unit : byHeight;
        var size = Math.Max(2, Math.Min(byHeight, byWidth));

        var textWidth = unit * size;
        var lineHeight = LineHeightFor(font, size);
        var textX = border.X + (border.Width - textWidth) / 2;
        var textTop = border.Y + (border.Height - lineHeight) / 2;
        return new StampLayoutResult(border, radius, size, textX, textTop, textTop + BaselineFor(font, size));
    }

    public static PdfFont StampFont => new("Arial", true, false);
}

/// <summary>Geometries partagees entre l'affichage et l'export.</summary>
public static class ShapeGeometry
{
    /// <summary>Pointe de fleche : extremite du trait raccourcie et triangle.</summary>
    public static (Point ShaftEnd, Point[] Head) ArrowHead(Point from, Point to, double strokeWidth)
    {
        var v = to - from;
        var length = v.Length;
        if (length < 0.01)
        {
            return (to, Array.Empty<Point>());
        }

        v /= length;
        var headLength = Math.Min(Math.Max(7, strokeWidth * 4.2), length * 0.6);
        var halfWidth = headLength * 0.58;
        var basePoint = to - v * headLength;
        var normal = new Vector(-v.Y, v.X);
        var head = new[] { to, basePoint + normal * halfWidth, basePoint - normal * halfWidth };
        return (to - v * headLength * 0.8, head);
    }

    public static Rect UnderlineRect(Rect line)
    {
        var thickness = Math.Max(0.8, line.Height * 0.075);
        var y = line.Bottom - line.Height * 0.1;
        return new Rect(line.Left, y - thickness / 2, line.Width, thickness);
    }

    public static Rect StrikeRect(Rect line)
    {
        var thickness = Math.Max(0.8, line.Height * 0.075);
        var y = line.Top + line.Height * 0.54;
        return new Rect(line.Left, y - thickness / 2, line.Width, thickness);
    }

    public static List<Point> SquigglePoints(Rect line)
    {
        var amplitude = Math.Max(0.8, line.Height * 0.06);
        var step = Math.Max(1.5, line.Height * 0.16);
        var y = line.Bottom - line.Height * 0.08;
        var points = new List<Point>();
        var up = true;
        for (var x = line.Left; x <= line.Right + 0.01; x += step)
        {
            points.Add(new Point(Math.Min(x, line.Right), y + (up ? -amplitude : amplitude)));
            up = !up;
        }

        return points;
    }

    public static double SquiggleThickness(Rect line) => Math.Max(0.7, line.Height * 0.05);
}
