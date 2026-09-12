using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using PDFEditor.Models;
using PDFEditor.Rendering;
using PDFEditor.Services;

namespace PDFEditor.Pdf;

/// <summary>
/// Ecrit les annotations de l'application dans le contenu d'une page PDF.
/// Tout est « aplati » (visible dans n'importe quel lecteur), sauf les notes
/// et les liens qui deviennent de vraies annotations PDF.
/// </summary>
public static class AnnotationExporter
{
    public static void ApplyToPage(PdfDoc doc, int pageIndex, IReadOnlyList<Annotation> annotations, string author)
    {
        if (annotations.Count == 0)
        {
            return;
        }

        // 1. Caviardage : la page est rasterisee, le texte et les images d'origine
        //    disparaissent reellement du fichier.
        var redactions = annotations.OfType<RedactionAnnotation>().Select(r => r.Rect).ToList();
        if (redactions.Count > 0)
        {
            Redact(doc, pageIndex, redactions);
        }

        doc.EditPage(pageIndex, editor =>
        {
            // 2. Modifications de texte : on retire d'abord TOUS les textes d'origine (le fond
            //    reste intact), pour ne jamais effacer un texte que l'on vient d'ecrire.
            //    Seul le texte d'une page numerisee, qui n'est pas du vrai texte, est masque.
            var edits = annotations.OfType<TextEditAnnotation>().ToList();
            var lines = edits.Where(e => !e.UseCover).Select(e => e.OriginalRect).ToList();
            if (lines.Count > 0)
            {
                editor.RemoveTextLines(lines);
            }

            foreach (var edit in edits.Where(e => e.UseCover))
            {
                var region = edit.OriginalRect;
                region.Inflate(0.5, 0.5);
                editor.AddRectangle(region, edit.CoverColor, null, 0);
            }

            // 3. Les annotations, dans leur ordre de superposition.
            foreach (var annotation in annotations)
            {
                try
                {
                    Export(editor, annotation, author);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Export] {annotation.TypeName} ignoree : {ex.Message}");
                }
            }
        });
    }

    private static Color Alpha(Color color, double opacity) => AnnotationRenderer.WithOpacity(color, opacity);

    private static void Export(PdfPageEditor editor, Annotation annotation, string author)
    {
        switch (annotation)
        {
            case InkAnnotation ink:
                var blend = ink.IsHighlighter ? "Multiply" : null;
                foreach (var stroke in ink.Strokes)
                {
                    editor.AddInk(stroke, Alpha(ink.StrokeColor, ink.Opacity), ink.StrokeWidth, blend);
                }

                break;

            case ShapeAnnotation shape:
                Color? fill = shape.FillColor.A > 0 ? Alpha(shape.FillColor, shape.Opacity) : null;
                Color? stroke2 = shape.StrokeWidth > 0 && shape.StrokeColor.A > 0 ? Alpha(shape.StrokeColor, shape.Opacity) : null;
                if (shape.Shape == ShapeKind.Ellipse)
                {
                    editor.AddEllipse(shape.Rect, fill, stroke2, shape.StrokeWidth, shape.Dashed);
                }
                else
                {
                    editor.AddRectangle(shape.Rect, fill, stroke2, shape.StrokeWidth, shape.CornerRadius, shape.Dashed);
                }

                break;

            case LineAnnotation line:
                ExportLine(editor, line);
                break;

            case TextEditAnnotation edit:
                if (edit.FillColor.A > 0)
                {
                    editor.AddRectangle(edit.Rect, Alpha(edit.FillColor, edit.Opacity), null, 0);
                }

                ExportText(editor, edit, edit.Rect);
                break;

            case TextBoxAnnotation text:
                if (text.FillColor.A > 0)
                {
                    editor.AddRectangle(text.Rect, Alpha(text.FillColor, text.Opacity), null, 0);
                }

                if (text.StrokeWidth > 0 && text.StrokeColor.A > 0)
                {
                    editor.AddRectangle(text.Rect, null, Alpha(text.StrokeColor, text.Opacity), text.StrokeWidth, 0, text.Dashed);
                }

                ExportText(editor, text, text.Rect);
                break;

            case MarkupAnnotation markup:
                ExportMarkup(editor, markup);
                break;

            case NoteAnnotation note:
                editor.AddNoteAnnotation(note.Bounds, note.Text, string.IsNullOrWhiteSpace(note.Author) ? author : note.Author, note.StrokeColor);
                break;

            case ImageAnnotation image:
                editor.AddImage(ImageTools.Decode(image.Data), image.Rect);
                break;

            case StampAnnotation stamp:
                ExportStamp(editor, stamp);
                break;

            case RedactionAnnotation redaction:
                editor.AddRectangle(redaction.Rect, Colors.Black, null, 0);
                break;

            case WhiteoutAnnotation whiteout:
                editor.AddRectangle(whiteout.Rect, whiteout.FillColor, null, 0);
                break;

            case LinkAnnotation link:
                editor.AddLinkAnnotation(link.Rect, link.Uri);
                break;
        }
    }

    private static void ExportLine(PdfPageEditor editor, LineAnnotation line)
    {
        var color = Alpha(line.StrokeColor, line.Opacity);
        var start = line.Start;
        var end = line.End;

        if (line.ArrowEnd)
        {
            var (shaftEnd, head) = ShapeGeometry.ArrowHead(line.Start, line.End, line.StrokeWidth);
            end = shaftEnd;
            editor.AddPolygon(head, color, null, 0);
        }

        if (line.ArrowStart)
        {
            var (shaftStart, head) = ShapeGeometry.ArrowHead(line.End, line.Start, line.StrokeWidth);
            start = shaftStart;
            editor.AddPolygon(head, color, null, 0);
        }

        editor.AddLine(start, end, color, line.StrokeWidth, line.Dashed);
    }

    private static void ExportText(PdfPageEditor editor, TextBoxAnnotation text, Rect rect)
    {
        var layout = text.GetLayout();
        var color = Alpha(text.TextColor, text.Opacity);

        foreach (var line in layout.Lines)
        {
            if (line.Text.Length == 0)
            {
                continue;
            }

            var x = rect.Left + text.Padding + line.X;
            var baseline = rect.Top + text.Padding + line.Top + layout.Baseline;
            editor.AddText(line.Text, new Point(x, baseline), text.Font, text.FontSize, color);

            if (text.Underline)
            {
                var y = baseline + text.FontSize * 0.12;
                editor.AddLine(new Point(x, y), new Point(x + line.Width, y), color, Math.Max(0.5, text.FontSize * 0.06));
            }
        }
    }

    private static void ExportMarkup(PdfPageEditor editor, MarkupAnnotation markup)
    {
        var color = Alpha(markup.StrokeColor, markup.Opacity);
        foreach (var rect in markup.Rects)
        {
            switch (markup.Markup)
            {
                case MarkupKind.Highlight:
                    editor.AddRectangle(rect, color, null, 0, 0, false, "Multiply");
                    break;
                case MarkupKind.Underline:
                    editor.AddRectangle(ShapeGeometry.UnderlineRect(rect), color, null, 0);
                    break;
                case MarkupKind.StrikeOut:
                    editor.AddRectangle(ShapeGeometry.StrikeRect(rect), color, null, 0);
                    break;
                case MarkupKind.Squiggly:
                    editor.AddInk(ShapeGeometry.SquigglePoints(rect), color, ShapeGeometry.SquiggleThickness(rect), null, smooth: false);
                    break;
            }
        }
    }

    private static void ExportStamp(PdfPageEditor editor, StampAnnotation stamp)
    {
        var color = Alpha(stamp.StrokeColor, stamp.Opacity);
        var layout = TextLayout.LayoutStamp(stamp.Label, stamp.Rect, stamp.StrokeWidth);

        Color? fill = stamp.FillColor.A > 0 ? Alpha(stamp.FillColor, stamp.Opacity) : null;
        editor.AddRectangle(layout.BorderRect, fill, stamp.StrokeWidth > 0 ? color : null, stamp.StrokeWidth, layout.Radius, stamp.Dashed);

        // Centrage avec les metriques de PDFium (Helvetica-Bold).
        var width = editor.MeasureText(stamp.Label, TextLayout.StampFont, layout.FontSize);
        var x = width > 0 ? layout.BorderRect.X + (layout.BorderRect.Width - width) / 2 : layout.TextX;
        editor.AddText(stamp.Label, new Point(x, layout.Baseline), TextLayout.StampFont, layout.FontSize, color);
    }

    /// <summary>
    /// Caviardage securise : rendu de la page en image (220 dpi), zones
    /// peintes en noir dans les pixels, puis remplacement integral du contenu.
    /// </summary>
    public static void Redact(PdfDoc doc, int pageIndex, IReadOnlyList<Rect> areas)
    {
        var info = doc.GetPageInfo(pageIndex);
        var scale = Math.Min(220.0 / 72.0, 5200.0 / Math.Max(info.Width, info.Height));
        var width = Math.Max(1, (int)Math.Ceiling(info.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(info.Height * scale));
        var pixels = doc.Render(pageIndex, width, height, scale, 0, 0, printing: true);

        foreach (var area in areas)
        {
            var left = Math.Clamp((int)Math.Floor(area.Left * scale) - 1, 0, width);
            var top = Math.Clamp((int)Math.Floor(area.Top * scale) - 1, 0, height);
            var right = Math.Clamp((int)Math.Ceiling(area.Right * scale) + 1, 0, width);
            var bottom = Math.Clamp((int)Math.Ceiling(area.Bottom * scale) + 1, 0, height);

            for (var y = top; y < bottom; y++)
            {
                var row = y * width * 4;
                for (var x = left; x < right; x++)
                {
                    var i = row + x * 4;
                    pixels[i] = 0;
                    pixels[i + 1] = 0;
                    pixels[i + 2] = 0;
                    pixels[i + 3] = 255;
                }
            }
        }

        var jpeg = ImageTools.EncodeJpeg(ImageTools.FromBgra(pixels, width, height), 92);
        var image = new DecodedImage { Bgra = pixels, Width = width, Height = height, HasAlpha = false, JpegData = jpeg };

        doc.EditPage(pageIndex, editor =>
        {
            editor.RemoveAllObjects();
            editor.RemoveAllAnnotations();
            editor.AddImage(image, new Rect(0, 0, info.Width, info.Height));
        });
    }
}
