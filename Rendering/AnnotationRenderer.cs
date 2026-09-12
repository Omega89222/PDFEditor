using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using PDFEditor.Models;
using PDFEditor.Pdf;

namespace PDFEditor.Rendering;

/// <summary>
/// Dessin WPF des annotations, dans le repere de la page (points).
/// L'appelant pousse la mise a l'echelle voulue avant d'appeler Draw.
/// </summary>
public static class AnnotationRenderer
{
    private static readonly Dictionary<Color, SolidColorBrush> BrushCache = new();
    private static readonly ConditionalWeakTable<InkAnnotation, InkCacheEntry> InkCache = new();

    private sealed class InkCacheEntry
    {
        public int Revision = -1;
        public Geometry? Geometry;
    }

    public static SolidColorBrush Brush(Color color)
    {
        lock (BrushCache)
        {
            if (!BrushCache.TryGetValue(color, out var brush))
            {
                brush = new SolidColorBrush(color);
                brush.Freeze();
                if (BrushCache.Count > 512)
                {
                    BrushCache.Clear();
                }

                BrushCache[color] = brush;
            }

            return brush;
        }
    }

    public static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);

    public static Pen CreatePen(Color color, double thickness, bool dashed = false, bool round = true)
    {
        var pen = new Pen(Brush(color), Math.Max(0.1, thickness))
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = round && !dashed ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = round && !dashed ? PenLineCap.Round : PenLineCap.Flat,
            DashCap = PenLineCap.Flat
        };

        if (dashed)
        {
            pen.DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0);
        }

        pen.Freeze();
        return pen;
    }

    public static void Draw(DrawingContext dc, Annotation annotation, bool editorChrome)
    {
        switch (annotation)
        {
            case InkAnnotation ink:
                DrawInk(dc, ink);
                break;
            case ShapeAnnotation shape:
                DrawShape(dc, shape);
                break;
            case LineAnnotation line:
                DrawLine(dc, line);
                break;
            case TextEditAnnotation edit:
                DrawTextEdit(dc, edit);
                break;
            case TextBoxAnnotation text:
                DrawTextBox(dc, text);
                break;
            case MarkupAnnotation markup:
                DrawMarkup(dc, markup);
                break;
            case NoteAnnotation note:
                DrawNote(dc, note);
                break;
            case ImageAnnotation image:
                DrawImage(dc, image);
                break;
            case StampAnnotation stamp:
                DrawStamp(dc, stamp);
                break;
            case RedactionAnnotation redaction:
                DrawRedaction(dc, redaction, editorChrome);
                break;
            case WhiteoutAnnotation whiteout:
                dc.DrawRectangle(Brush(whiteout.FillColor), null, whiteout.Rect);
                break;
            case LinkAnnotation link:
                if (editorChrome)
                {
                    DrawLink(dc, link);
                }

                break;
        }
    }

    // ------------------------------------------------------------------ dessin

    public static Geometry BuildInkGeometry(IEnumerable<IReadOnlyList<Point>> strokes)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var stroke in strokes)
            {
                if (stroke.Count == 0)
                {
                    continue;
                }

                ctx.BeginFigure(stroke[0], false, false);
                if (stroke.Count == 1)
                {
                    ctx.LineTo(stroke[0] + new Vector(0.01, 0.01), true, true);
                    continue;
                }

                if (stroke.Count == 2)
                {
                    ctx.LineTo(stroke[1], true, true);
                    continue;
                }

                foreach (var segment in InkGeometry.Smooth(stroke))
                {
                    ctx.BezierTo(segment.Control1, segment.Control2, segment.End, true, true);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static void DrawInk(DrawingContext dc, InkAnnotation ink)
    {
        var entry = InkCache.GetOrCreateValue(ink);
        if (entry.Geometry is null || entry.Revision != ink.Revision)
        {
            entry.Geometry = BuildInkGeometry(ink.Strokes);
            entry.Revision = ink.Revision;
        }

        dc.DrawGeometry(null, CreatePen(WithOpacity(ink.StrokeColor, ink.Opacity), ink.StrokeWidth), entry.Geometry);
    }

    private static void DrawShape(DrawingContext dc, ShapeAnnotation shape)
    {
        var fill = shape.FillColor.A > 0 ? Brush(WithOpacity(shape.FillColor, shape.Opacity)) : null;
        var pen = shape.StrokeWidth > 0 && shape.StrokeColor.A > 0
            ? CreatePen(WithOpacity(shape.StrokeColor, shape.Opacity), shape.StrokeWidth, shape.Dashed)
            : null;

        var r = shape.Rect;
        if (shape.Shape == ShapeKind.Ellipse)
        {
            dc.DrawEllipse(fill, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
        }
        else if (shape.CornerRadius > 0)
        {
            dc.DrawRoundedRectangle(fill, pen, r, shape.CornerRadius, shape.CornerRadius);
        }
        else
        {
            dc.DrawRectangle(fill, pen, r);
        }
    }

    private static void DrawLine(DrawingContext dc, LineAnnotation line)
    {
        var color = WithOpacity(line.StrokeColor, line.Opacity);
        var pen = CreatePen(color, line.StrokeWidth, line.Dashed);
        var brush = Brush(color);

        var start = line.Start;
        var end = line.End;

        if (line.ArrowEnd)
        {
            var (shaftEnd, head) = ShapeGeometry.ArrowHead(line.Start, line.End, line.StrokeWidth);
            end = shaftEnd;
            DrawPolygon(dc, head, brush);
        }

        if (line.ArrowStart)
        {
            var (shaftStart, head) = ShapeGeometry.ArrowHead(line.End, line.Start, line.StrokeWidth);
            start = shaftStart;
            DrawPolygon(dc, head, brush);
        }

        dc.DrawLine(pen, start, end);
    }

    public static void DrawPolygon(DrawingContext dc, IReadOnlyList<Point> points, Brush brush)
    {
        if (points.Count < 3)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            for (var i = 1; i < points.Count; i++)
            {
                ctx.LineTo(points[i], true, true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    private static void DrawTextBox(DrawingContext dc, TextBoxAnnotation text)
    {
        var r = text.Rect;
        if (text.FillColor.A > 0)
        {
            dc.DrawRectangle(Brush(WithOpacity(text.FillColor, text.Opacity)), null, r);
        }

        if (text.StrokeWidth > 0 && text.StrokeColor.A > 0)
        {
            dc.DrawRectangle(null, CreatePen(WithOpacity(text.StrokeColor, text.Opacity), text.StrokeWidth, text.Dashed, false), r);
        }

        DrawTextContent(dc, text, r);
    }

    private static void DrawTextEdit(DrawingContext dc, TextEditAnnotation edit)
    {
        // Le texte d'origine est retire du rendu de la page : aucun aplat, le fond reste intact.
        if (edit.UseCover)
        {
            var cover = edit.OriginalRect;
            cover.Inflate(0.6, 0.6);
            dc.DrawRectangle(Brush(edit.CoverColor), null, cover);
        }

        if (edit.FillColor.A > 0)
        {
            dc.DrawRectangle(Brush(WithOpacity(edit.FillColor, edit.Opacity)), null, edit.Rect);
        }

        DrawTextContent(dc, edit, edit.Rect);
    }

    public static void DrawTextContent(DrawingContext dc, TextBoxAnnotation text, Rect r)
    {
        var layout = text.GetLayout();
        var typeface = FontCatalog.GetTypeface(text.Font);
        var brush = Brush(WithOpacity(text.TextColor, text.Opacity));

        foreach (var line in layout.Lines)
        {
            if (line.Text.Length == 0)
            {
                continue;
            }

            var formatted = new FormattedText(
                line.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                text.FontSize,
                brush,
                null,
                TextFormattingMode.Ideal,
                1.0);

            if (text.Underline)
            {
                formatted.SetTextDecorations(TextDecorations.Underline);
            }

            dc.DrawText(formatted, new Point(r.Left + text.Padding + line.X, r.Top + text.Padding + line.Top));
        }
    }

    private static void DrawMarkup(DrawingContext dc, MarkupAnnotation markup)
    {
        var color = WithOpacity(markup.StrokeColor, markup.Opacity);
        var brush = Brush(color);

        foreach (var rect in markup.Rects)
        {
            switch (markup.Markup)
            {
                case MarkupKind.Highlight:
                    dc.DrawRectangle(brush, null, rect);
                    break;
                case MarkupKind.Underline:
                    dc.DrawRectangle(brush, null, ShapeGeometry.UnderlineRect(rect));
                    break;
                case MarkupKind.StrikeOut:
                    dc.DrawRectangle(brush, null, ShapeGeometry.StrikeRect(rect));
                    break;
                case MarkupKind.Squiggly:
                    var points = ShapeGeometry.SquigglePoints(rect);
                    if (points.Count > 1)
                    {
                        var geometry = new StreamGeometry();
                        using (var ctx = geometry.Open())
                        {
                            ctx.BeginFigure(points[0], false, false);
                            for (var i = 1; i < points.Count; i++)
                            {
                                ctx.LineTo(points[i], true, true);
                            }
                        }

                        geometry.Freeze();
                        dc.DrawGeometry(null, CreatePen(color, ShapeGeometry.SquiggleThickness(rect)), geometry);
                    }

                    break;
            }
        }
    }

    public static void DrawNote(DrawingContext dc, NoteAnnotation note)
    {
        DrawNoteIcon(dc, note.Bounds, note.StrokeColor);
    }

    public static void DrawNoteIcon(DrawingContext dc, Rect r, Color color)
    {
        var fold = r.Width * 0.3;
        var darker = Color.FromRgb((byte)(color.R * 0.72), (byte)(color.G * 0.72), (byte)(color.B * 0.72));

        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(r.Left, r.Top), true, true);
            ctx.LineTo(new Point(r.Right - fold, r.Top), true, true);
            ctx.LineTo(new Point(r.Right, r.Top + fold), true, true);
            ctx.LineTo(new Point(r.Right, r.Bottom), true, true);
            ctx.LineTo(new Point(r.Left, r.Bottom), true, true);
        }

        body.Freeze();
        dc.DrawGeometry(Brush(color), CreatePen(darker, 0.8), body);

        DrawPolygon(dc, new[]
        {
            new Point(r.Right - fold, r.Top),
            new Point(r.Right - fold, r.Top + fold),
            new Point(r.Right, r.Top + fold)
        }, Brush(darker));

        var linePen = CreatePen(Color.FromArgb(150, 60, 45, 0), Math.Max(0.6, r.Height * 0.06));
        for (var i = 0; i < 3; i++)
        {
            var y = r.Top + r.Height * (0.42 + i * 0.18);
            var right = i == 2 ? r.Left + r.Width * 0.55 : r.Right - r.Width * 0.2;
            dc.DrawLine(linePen, new Point(r.Left + r.Width * 0.2, y), new Point(right, y));
        }
    }

    private static void DrawImage(DrawingContext dc, ImageAnnotation image)
    {
        var bitmap = image.Bitmap;
        if (bitmap is null)
        {
            dc.DrawRectangle(Brush(Color.FromArgb(60, 128, 128, 128)), CreatePen(Color.FromArgb(140, 128, 128, 128), 1, true), image.Rect);
            return;
        }

        dc.PushOpacity(image.Opacity);
        dc.DrawImage(bitmap, image.Rect);
        dc.Pop();
    }

    private static void DrawStamp(DrawingContext dc, StampAnnotation stamp)
    {
        var color = WithOpacity(stamp.StrokeColor, stamp.Opacity);
        var layout = TextLayout.LayoutStamp(stamp.Label, stamp.Rect, stamp.StrokeWidth);

        if (stamp.FillColor.A > 0)
        {
            dc.DrawRoundedRectangle(Brush(WithOpacity(stamp.FillColor, stamp.Opacity)), null, layout.BorderRect, layout.Radius, layout.Radius);
        }

        if (stamp.StrokeWidth > 0)
        {
            dc.DrawRoundedRectangle(null, CreatePen(color, stamp.StrokeWidth, stamp.Dashed), layout.BorderRect, layout.Radius, layout.Radius);
        }

        var formatted = new FormattedText(
            stamp.Label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            FontCatalog.GetTypeface(TextLayout.StampFont),
            layout.FontSize,
            Brush(color),
            null,
            TextFormattingMode.Ideal,
            1.0);

        dc.DrawText(formatted, new Point(layout.TextX, layout.TextTop));
    }

    private static void DrawRedaction(DrawingContext dc, RedactionAnnotation redaction, bool editorChrome)
    {
        dc.DrawRectangle(Brush(Colors.Black), null, redaction.Rect);
        if (editorChrome)
        {
            // Bordure rouge en pointilles : zone qui sera detruite a l'enregistrement.
            dc.DrawRectangle(null, CreatePen(Color.FromRgb(0xFF, 0x45, 0x3A), 1, true), redaction.Rect);
        }
    }

    private static void DrawLink(DrawingContext dc, LinkAnnotation link)
    {
        dc.DrawRectangle(
            Brush(Color.FromArgb(28, 0x0A, 0x84, 0xFF)),
            CreatePen(Color.FromRgb(0x0A, 0x84, 0xFF), 1, true),
            link.Rect);
    }
}
