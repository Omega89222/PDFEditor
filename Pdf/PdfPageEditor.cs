using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using PDFEditor.Services;

namespace PDFEditor.Pdf;

/// <summary>
/// Ecriture dans le contenu d'une page. Toutes les coordonnees recues sont
/// en points d'affichage (origine en haut a gauche) ; la conversion vers
/// l'espace utilisateur PDF (rotation, CropBox) est faite ici.
/// Instance obtenue uniquement via <see cref="PdfDoc.EditPage"/> (sous verrou).
/// </summary>
public sealed unsafe class PdfPageEditor : IDisposable
{
    private const double Kappa = 0.5522847498;

    private readonly IntPtr _doc;
    private readonly IntPtr _page;
    private readonly Dictionary<string, IntPtr> _fonts;
    private bool _contentChanged;

    internal PdfPageEditor(IntPtr doc, IntPtr page, PdfPageInfo info, Dictionary<string, IntPtr> fonts)
    {
        _doc = doc;
        _page = page;
        _fonts = fonts;
        Info = info;
    }

    public PdfPageInfo Info { get; }

    // =====================================================================
    // Formes
    // =====================================================================

    public void AddRectangle(Rect rect, Color? fill, Color? stroke, double strokeWidth, double cornerRadius = 0, bool dashed = false, string? blendMode = null)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var radius = Math.Max(0, Math.Min(cornerRadius, Math.Min(rect.Width, rect.Height) / 2));
        double l = rect.Left, t = rect.Top, r = rect.Right, b = rect.Bottom;

        if (radius < 0.01)
        {
            var path = NewPath(new Point(l, t));
            LineTo(path, new Point(r, t));
            LineTo(path, new Point(r, b));
            LineTo(path, new Point(l, b));
            Native.FPDFPath_Close(path);
            Finish(path, fill, stroke, strokeWidth, dashed, blendMode);
            return;
        }

        var k = Kappa * radius;
        var p = NewPath(new Point(l + radius, t));
        LineTo(p, new Point(r - radius, t));
        BezierTo(p, new Point(r - radius + k, t), new Point(r, t + radius - k), new Point(r, t + radius));
        LineTo(p, new Point(r, b - radius));
        BezierTo(p, new Point(r, b - radius + k), new Point(r - radius + k, b), new Point(r - radius, b));
        LineTo(p, new Point(l + radius, b));
        BezierTo(p, new Point(l + radius - k, b), new Point(l, b - radius + k), new Point(l, b - radius));
        LineTo(p, new Point(l, t + radius));
        BezierTo(p, new Point(l, t + radius - k), new Point(l + radius - k, t), new Point(l + radius, t));
        Native.FPDFPath_Close(p);
        Finish(p, fill, stroke, strokeWidth, dashed, blendMode);
    }

    public void AddEllipse(Rect rect, Color? fill, Color? stroke, double strokeWidth, bool dashed = false)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var cx = rect.Left + rect.Width / 2;
        var cy = rect.Top + rect.Height / 2;
        var rx = rect.Width / 2;
        var ry = rect.Height / 2;
        var kx = Kappa * rx;
        var ky = Kappa * ry;

        var p = NewPath(new Point(cx + rx, cy));
        BezierTo(p, new Point(cx + rx, cy + ky), new Point(cx + kx, cy + ry), new Point(cx, cy + ry));
        BezierTo(p, new Point(cx - kx, cy + ry), new Point(cx - rx, cy + ky), new Point(cx - rx, cy));
        BezierTo(p, new Point(cx - rx, cy - ky), new Point(cx - kx, cy - ry), new Point(cx, cy - ry));
        BezierTo(p, new Point(cx + kx, cy - ry), new Point(cx + rx, cy - ky), new Point(cx + rx, cy));
        Native.FPDFPath_Close(p);
        Finish(p, fill, stroke, strokeWidth, dashed, null);
    }

    public void AddLine(Point from, Point to, Color stroke, double strokeWidth, bool dashed = false)
    {
        var p = NewPath(from);
        LineTo(p, to);
        Finish(p, null, stroke, strokeWidth, dashed, null);
    }

    public void AddPolygon(IReadOnlyList<Point> points, Color? fill, Color? stroke, double strokeWidth)
    {
        if (points.Count < 3)
        {
            return;
        }

        var p = NewPath(points[0]);
        for (var i = 1; i < points.Count; i++)
        {
            LineTo(p, points[i]);
        }

        Native.FPDFPath_Close(p);
        Finish(p, fill, stroke, strokeWidth, false, null);
    }

    /// <summary>Trait a main levee, lisse par courbes de Bezier (Catmull-Rom).</summary>
    public void AddInk(IReadOnlyList<Point> points, Color stroke, double strokeWidth, string? blendMode = null, bool smooth = true)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (points.Count == 1)
        {
            var d = Math.Max(0.5, strokeWidth);
            AddEllipse(new Rect(points[0].X - d / 2, points[0].Y - d / 2, d, d), stroke, null, 0);
            return;
        }

        var path = NewPath(points[0]);
        if (smooth && points.Count > 2)
        {
            foreach (var segment in InkGeometry.Smooth(points))
            {
                BezierTo(path, segment.Control1, segment.Control2, segment.End);
            }
        }
        else
        {
            for (var i = 1; i < points.Count; i++)
            {
                LineTo(path, points[i]);
            }
        }

        Finish(path, null, stroke, strokeWidth, false, blendMode);
    }

    // =====================================================================
    // Texte
    // =====================================================================

    /// <summary>
    /// Ajoute une ligne de texte. <paramref name="baselineOrigin"/> est le point
    /// de depart de la ligne de base ; <paramref name="angleDegrees"/> tourne le
    /// texte dans le sens inverse des aiguilles d'une montre, a l'ecran.
    /// </summary>
    public void AddText(string text, Point baselineOrigin, PdfFont font, double fontSize, Color color,
        double angleDegrees = 0, double horizontalScale = 1, bool invisible = false)
    {
        if (string.IsNullOrEmpty(text) || fontSize <= 0)
        {
            return;
        }

        var obj = CreateTextObject(font, text, fontSize);
        if (obj == IntPtr.Zero)
        {
            return;
        }

        Native.FPDFText_SetText(obj, text);
        Native.FPDFPageObj_SetFillColor(obj, color.R, color.G, color.B, color.A);
        if (invisible)
        {
            Native.FPDFTextObj_SetTextRenderMode(obj, Native.FPDF_TEXTRENDERMODE_INVISIBLE);
        }

        var radians = angleDegrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        // Espace texte (Y vers le haut) -> affichage (Y vers le bas).
        var toDisplay = new PdfMatrix(horizontalScale * cos, -horizontalScale * sin, -sin, -cos, baselineOrigin.X, baselineOrigin.Y);
        var m = PdfMatrix.Multiply(toDisplay, Info.DisplayToUser);
        Native.FPDFPageObj_Transform(obj, m.A, m.B, m.C, m.D, m.E, m.F);
        Native.FPDFPage_InsertObject(_page, obj);
        _contentChanged = true;
    }

    /// <summary>Largeur du texte telle que PDFium la calculera (points).</summary>
    public double MeasureText(string text, PdfFont font, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var obj = CreateTextObject(font, text, fontSize);
        if (obj == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            Native.FPDFText_SetText(obj, text);
            return Native.FPDFPageObj_GetBounds(obj, out var left, out _, out var right, out _) != 0
                ? Math.Max(0, right - left)
                : 0;
        }
        finally
        {
            Native.FPDFPageObj_Destroy(obj);
        }
    }

    /// <summary>Texte etire pour remplir exactement une boite (couche OCR).</summary>
    public void AddTextFitted(string text, Rect box, PdfFont font, Color color, bool invisible)
    {
        if (string.IsNullOrWhiteSpace(text) || box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        var size = Math.Max(1, box.Height * 0.9);
        var width = MeasureText(text, font, size);
        var scale = width > 0.01 ? Math.Clamp(box.Width / width, 0.1, 10) : 1;
        AddText(text, new Point(box.Left, box.Bottom - box.Height * 0.18), font, size, color, 0, scale, invisible);
    }

    private IntPtr CreateTextObject(PdfFont font, string text, double fontSize)
    {
        var standard = FontCatalog.GetStandardFontName(font, text);
        if (standard is not null)
        {
            return Native.FPDFPageObj_NewTextObj(_doc, standard, (float)fontSize);
        }

        var handle = LoadEmbeddedFont(font);
        return handle != IntPtr.Zero
            ? Native.FPDFPageObj_CreateTextObj(_doc, handle, (float)fontSize)
            : Native.FPDFPageObj_NewTextObj(_doc, "Helvetica", (float)fontSize);
    }

    private IntPtr LoadEmbeddedFont(PdfFont font)
    {
        var file = FontCatalog.GetFontFile(font);
        if (file is null)
        {
            return IntPtr.Zero;
        }

        if (_fonts.TryGetValue(file, out var cached))
        {
            return cached;
        }

        var handle = IntPtr.Zero;
        try
        {
            var data = File.ReadAllBytes(file);
            fixed (byte* p = data)
            {
                handle = Native.FPDFText_LoadFont(_doc, p, (uint)data.Length, Native.FPDF_FONT_TRUETYPE, 1);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PdfPageEditor] Police {file} illisible : {ex.Message}");
        }

        _fonts[file] = handle;
        return handle;
    }

    // =====================================================================
    // Images
    // =====================================================================

    public bool AddImage(DecodedImage image, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || image.Width <= 0 || image.Height <= 0)
        {
            return false;
        }

        var obj = Native.FPDFPageObj_NewImageObj(_doc);
        if (obj == IntPtr.Zero)
        {
            return false;
        }

        var page = _page;
        var ok = false;

        if (image.JpegData is { Length: > 0 } jpeg && !image.HasAlpha)
        {
            ok = LoadJpeg(obj, jpeg, &page);
        }

        if (!ok)
        {
            fixed (byte* pixels = image.Bgra)
            {
                var format = image.HasAlpha ? Native.FPDFBitmap_BGRA : Native.FPDFBitmap_BGRx;
                var bitmap = Native.FPDFBitmap_CreateEx(image.Width, image.Height, format, (IntPtr)pixels, image.Width * 4);
                if (bitmap != IntPtr.Zero)
                {
                    ok = Native.FPDFImageObj_SetBitmap(&page, 1, obj, bitmap) != 0;
                    Native.FPDFBitmap_Destroy(bitmap);
                }
            }
        }

        if (!ok)
        {
            Native.FPDFPageObj_Destroy(obj);
            return false;
        }

        // Carre unite de l'image (origine en bas a gauche) -> rectangle d'affichage.
        var placement = new PdfMatrix(rect.Width, 0, 0, -rect.Height, rect.Left, rect.Bottom);
        var m = PdfMatrix.Multiply(placement, Info.DisplayToUser);
        Native.FPDFImageObj_SetMatrix(obj, m.A, m.B, m.C, m.D, m.E, m.F);
        Native.FPDFPage_InsertObject(_page, obj);
        _contentChanged = true;
        return true;
    }

    [UnmanagedCallersOnly]
    private static int ReadJpegBlock(IntPtr param, uint position, byte* buffer, uint size)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(param);
            if (handle.Target is not byte[] data || position + (long)size > data.Length)
            {
                return 0;
            }

            new ReadOnlySpan<byte>(data, (int)position, (int)size).CopyTo(new Span<byte>(buffer, (int)size));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    private static bool LoadJpeg(IntPtr imageObject, byte[] jpeg, IntPtr* page)
    {
        var handle = GCHandle.Alloc(jpeg);
        try
        {
            var access = new FpdfFileAccess
            {
                FileLen = (uint)jpeg.Length,
                GetBlock = (IntPtr)(delegate* unmanaged<IntPtr, uint, byte*, uint, int>)&ReadJpegBlock,
                Param = GCHandle.ToIntPtr(handle)
            };

            return Native.FPDFImageObj_LoadJpegFileInline(page, 1, imageObject, &access) != 0;
        }
        finally
        {
            handle.Free();
        }
    }

    // =====================================================================
    // Suppression de contenu
    // =====================================================================

    /// <summary>
    /// Retire les objets texte contenus (a <paramref name="coverage"/> pres) dans
    /// la zone. <c>Partial</c> signale un contenu qui deborde de la zone ou qui ne
    /// peut pas etre retire (image, formulaire) : il faudra alors le masquer.
    /// </summary>
    public (int Removed, bool Partial) RemoveTextInside(Rect display, double coverage = 0.8)
    {
        var (l, b, r, t) = Info.ToUserRect(display);
        var zone = new Rect(l, b, Math.Max(0, r - l), Math.Max(0, t - b));
        var victims = new List<IntPtr>();
        var partial = false;

        var count = Native.FPDFPage_CountObjects(_page);
        for (var i = 0; i < count; i++)
        {
            var obj = Native.FPDFPage_GetObject(_page, i);
            if (obj == IntPtr.Zero || Native.FPDFPageObj_GetBounds(obj, out var ol, out var ob, out var or, out var ot) == 0)
            {
                continue;
            }

            var bounds = new Rect(ol, ob, Math.Max(0.01, or - ol), Math.Max(0.01, ot - ob));
            var intersection = Rect.Intersect(bounds, zone);
            if (intersection.IsEmpty || intersection.Width * intersection.Height <= 0.0001)
            {
                continue;
            }

            var type = Native.FPDFPageObj_GetType(obj);
            if (type == Native.FPDF_PAGEOBJ_TEXT)
            {
                var ratio = intersection.Width * intersection.Height / (bounds.Width * bounds.Height);
                if (ratio >= coverage)
                {
                    victims.Add(obj);
                }
                else
                {
                    partial = true;
                }
            }
            else if (type is Native.FPDF_PAGEOBJ_FORM or Native.FPDF_PAGEOBJ_IMAGE or Native.FPDF_PAGEOBJ_SHADING)
            {
                partial = true;
            }
        }

        foreach (var obj in victims)
        {
            if (Native.FPDFPage_RemoveObject(_page, obj) != 0)
            {
                Native.FPDFPageObj_Destroy(obj);
            }
        }

        if (victims.Count > 0)
        {
            _contentChanged = true;
        }

        return (victims.Count, partial);
    }

    /// <summary>
    /// Retire les objets texte qui composent ces lignes (textes modifies) : seul le texte
    /// disparait, le fond reste intact. Retourne le nombre d'objets retires.
    /// </summary>
    public int RemoveTextLines(IReadOnlyList<Rect> displayRegions)
    {
        var removed = 0;
        foreach (var match in TextRegions.Find(_page, Info, displayRegions))
        {
            var detached = match.Parent == IntPtr.Zero
                ? Native.FPDFPage_RemoveObject(_page, match.Object) != 0
                : Native.FPDFFormObj_RemoveObject(match.Parent, match.Object) != 0;

            if (detached)
            {
                Native.FPDFPageObj_Destroy(match.Object);
                removed++;
            }
        }

        if (removed > 0)
        {
            _contentChanged = true;
        }

        return removed;
    }

    /// <summary>Vide entierement le contenu graphique de la page.</summary>
    public void RemoveAllObjects()
    {
        for (var i = Native.FPDFPage_CountObjects(_page) - 1; i >= 0; i--)
        {
            var obj = Native.FPDFPage_GetObject(_page, i);
            if (obj != IntPtr.Zero && Native.FPDFPage_RemoveObject(_page, obj) != 0)
            {
                Native.FPDFPageObj_Destroy(obj);
            }
        }

        _contentChanged = true;
    }

    public void RemoveAllAnnotations()
    {
        for (var i = Native.FPDFPage_GetAnnotCount(_page) - 1; i >= 0; i--)
        {
            Native.FPDFPage_RemoveAnnot(_page, i);
        }
    }

    // =====================================================================
    // Annotations PDF natives
    // =====================================================================

    /// <summary>Note repositionnable (annotation /Text) visible dans tous les lecteurs.</summary>
    public void AddNoteAnnotation(Rect iconRect, string text, string author, Color color)
    {
        var annot = Native.FPDFPage_CreateAnnot(_page, Native.FPDF_ANNOT_TEXT);
        if (annot == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var rect = ToFsRect(iconRect);
            Native.FPDFAnnot_SetRect(annot, ref rect);
            Native.FPDFAnnot_SetColor(annot, Native.FPDFANNOT_COLORTYPE_Color, color.R, color.G, color.B, 255);
            Native.FPDFAnnot_SetStringValue(annot, "Contents", text ?? "");
            Native.FPDFAnnot_SetStringValue(annot, "T", string.IsNullOrWhiteSpace(author) ? "Éditeur PDF" : author);
            Native.FPDFAnnot_SetStringValue(annot, "M", PdfMetadata.ToPdfDate(DateTime.Now));
            Native.FPDFAnnot_SetFlags(annot, Native.FPDF_ANNOT_FLAG_PRINT);
        }
        finally
        {
            Native.FPDFPage_CloseAnnot(annot);
        }
    }

    /// <summary>Zone cliquable ouvrant une adresse web.</summary>
    public void AddLinkAnnotation(Rect area, string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        var annot = Native.FPDFPage_CreateAnnot(_page, Native.FPDF_ANNOT_LINK);
        if (annot == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var rect = ToFsRect(area);
            Native.FPDFAnnot_SetRect(annot, ref rect);
            Native.FPDFAnnot_SetBorder(annot, 0, 0, 0);
            Native.FPDFAnnot_SetURI(annot, EscapeUri(uri.Trim()));
            Native.FPDFAnnot_SetFlags(annot, Native.FPDF_ANNOT_FLAG_PRINT);
        }
        finally
        {
            Native.FPDFPage_CloseAnnot(annot);
        }
    }

    private static string EscapeUri(string uri)
    {
        // FPDFAnnot_SetURI n'accepte que de l'ASCII : on encode le reste.
        var builder = new System.Text.StringBuilder(uri.Length);
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(uri))
        {
            if (b is > 32 and < 127)
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2"));
            }
        }

        return builder.ToString();
    }

    private FsRectF ToFsRect(Rect display)
    {
        var (l, b, r, t) = Info.ToUserRect(display);
        return new FsRectF { Left = (float)l, Bottom = (float)b, Right = (float)r, Top = (float)t };
    }

    // =====================================================================
    // Chemins (espace utilisateur)
    // =====================================================================

    private IntPtr NewPath(Point start)
    {
        var u = Info.DisplayToUser.Transform(start);
        return Native.FPDFPageObj_CreateNewPath((float)u.X, (float)u.Y);
    }

    private void LineTo(IntPtr path, Point p)
    {
        var u = Info.DisplayToUser.Transform(p);
        Native.FPDFPath_LineTo(path, (float)u.X, (float)u.Y);
    }

    private void BezierTo(IntPtr path, Point c1, Point c2, Point end)
    {
        var u1 = Info.DisplayToUser.Transform(c1);
        var u2 = Info.DisplayToUser.Transform(c2);
        var u3 = Info.DisplayToUser.Transform(end);
        Native.FPDFPath_BezierTo(path, (float)u1.X, (float)u1.Y, (float)u2.X, (float)u2.Y, (float)u3.X, (float)u3.Y);
    }

    private void Finish(IntPtr path, Color? fill, Color? stroke, double strokeWidth, bool dashed, string? blendMode)
    {
        var hasFill = fill is { A: > 0 };
        var hasStroke = stroke is { A: > 0 } && strokeWidth > 0;

        if (!hasFill && !hasStroke)
        {
            Native.FPDFPageObj_Destroy(path);
            return;
        }

        if (hasFill)
        {
            var f = fill!.Value;
            Native.FPDFPageObj_SetFillColor(path, f.R, f.G, f.B, f.A);
        }

        if (hasStroke)
        {
            var s = stroke!.Value;
            Native.FPDFPageObj_SetStrokeColor(path, s.R, s.G, s.B, s.A);
            Native.FPDFPageObj_SetStrokeWidth(path, (float)strokeWidth);
            Native.FPDFPageObj_SetLineJoin(path, Native.FPDF_LINEJOIN_ROUND);

            if (dashed)
            {
                var dash = stackalloc float[2];
                dash[0] = (float)Math.Max(1, strokeWidth * 3);
                dash[1] = (float)Math.Max(1, strokeWidth * 2);
                Native.FPDFPageObj_SetDashArray(path, dash, 2, 0);
            }
            else
            {
                Native.FPDFPageObj_SetLineCap(path, Native.FPDF_LINECAP_ROUND);
            }
        }

        Native.FPDFPath_SetDrawMode(path, hasFill ? Native.FPDF_FILLMODE_WINDING : Native.FPDF_FILLMODE_NONE, hasStroke ? 1 : 0);

        if (!string.IsNullOrEmpty(blendMode))
        {
            Native.FPDFPageObj_SetBlendMode(path, blendMode);
        }

        Native.FPDFPage_InsertObject(_page, path);
        _contentChanged = true;
    }

    // =====================================================================

    internal void Commit()
    {
        if (_contentChanged)
        {
            Native.FPDFPage_GenerateContent(_page);
        }
    }

    public void Dispose()
    {
        // Les polices appartiennent au document (voir PdfDoc) : rien a liberer ici.
    }
}
