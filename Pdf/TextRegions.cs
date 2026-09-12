using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace PDFEditor.Pdf;

/// <summary>
/// Retrouve les objets texte PDF qui composent une ligne affichee. Sert a masquer le texte
/// d'origine d'une ligne modifiee a l'ecran, puis a le retirer du fichier a l'enregistrement :
/// seul le texte disparait, le fond (couleurs, images, filets) reste intact.
/// </summary>
internal static class TextRegions
{
    internal readonly struct Match
    {
        public Match(IntPtr obj, IntPtr parent)
        {
            Object = obj;
            Parent = parent;
        }

        public IntPtr Object { get; }

        /// <summary>Objet formulaire (XObject) contenant l'objet, ou zero pour un objet de la page.</summary>
        public IntPtr Parent { get; }
    }

    private const int MaxFormDepth = 4;

    public static List<Match> Find(IntPtr page, PdfPageInfo info, IReadOnlyList<Rect> displayRegions)
    {
        var result = new List<Match>();
        if (page == IntPtr.Zero || displayRegions.Count == 0)
        {
            return result;
        }

        var count = Native.FPDFPage_CountObjects(page);
        for (var i = 0; i < count; i++)
        {
            Visit(Native.FPDFPage_GetObject(page, i), IntPtr.Zero, Matrix.Identity, info, displayRegions, result, 0);
        }

        return result;
    }

    /// <summary>Mode de rendu visible (remplissage et/ou contour, sans role de detourage).</summary>
    public static bool IsVisible(IntPtr textObject) => Native.FPDFTextObj_GetTextRenderMode(textObject) is 0 or 1 or 2;

    private static void Visit(IntPtr obj, IntPtr parent, Matrix toPage, PdfPageInfo info, IReadOnlyList<Rect> regions, List<Match> result, int depth)
    {
        if (obj == IntPtr.Zero)
        {
            return;
        }

        var type = Native.FPDFPageObj_GetType(obj);
        if (type == Native.FPDF_PAGEOBJ_FORM)
        {
            if (depth >= MaxFormDepth)
            {
                return;
            }

            var formMatrix = Native.FPDFPageObj_GetMatrix(obj, out var m) != 0
                ? new Matrix(m.A, m.B, m.C, m.D, m.E, m.F)
                : Matrix.Identity;
            var childToPage = formMatrix * toPage;

            var count = Native.FPDFFormObj_CountObjects(obj);
            for (var i = 0; i < count; i++)
            {
                Visit(Native.FPDFFormObj_GetObject(obj, (uint)i), obj, childToPage, info, regions, result, depth + 1);
            }

            return;
        }

        if (type != Native.FPDF_PAGEOBJ_TEXT || Native.FPDFPageObj_GetBounds(obj, out var left, out var bottom, out var right, out var top) == 0)
        {
            return;
        }

        var user = TransformBounds(left, bottom, right, top, toPage);
        var display = info.ToDisplayRect(user.Left, user.Top, user.Right, user.Bottom);
        if (regions.Any(region => BelongsToLine(display, region)))
        {
            result.Add(new Match(obj, parent));
        }
    }

    /// <summary>
    /// Un objet appartient a la ligne si son centre vertical tombe dans la ligne, s'il n'est pas
    /// beaucoup plus haut qu'elle, et si la ligne recouvre au moins la moitie de sa largeur.
    /// Les lignes voisines (au-dessus, en dessous) ne sont donc jamais touchees.
    /// </summary>
    private static bool BelongsToLine(Rect obj, Rect line)
    {
        if (line.IsEmpty || obj.IsEmpty)
        {
            return false;
        }

        var slack = line.Height * 0.25;
        var centerY = obj.Top + obj.Height / 2;
        if (centerY < line.Top - slack || centerY > line.Bottom + slack || obj.Height > line.Height * 2.5 + 1)
        {
            return false;
        }

        var overlap = Math.Min(obj.Right, line.Right + 0.5) - Math.Max(obj.Left, line.Left - 0.5);
        return overlap > 0 && overlap >= Math.Max(obj.Width, 0.01) * 0.5;
    }

    /// <summary>Rectangle englobant (espace utilisateur de la page) ; Top/Bottom portent le haut et le bas PDF.</summary>
    private static (double Left, double Bottom, double Right, double Top) TransformBounds(double left, double bottom, double right, double top, Matrix matrix)
    {
        if (matrix.IsIdentity)
        {
            return (left, bottom, right, top);
        }

        var corners = new[]
        {
            matrix.Transform(new Point(left, bottom)),
            matrix.Transform(new Point(right, bottom)),
            matrix.Transform(new Point(left, top)),
            matrix.Transform(new Point(right, top))
        };

        return (corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y));
    }
}
