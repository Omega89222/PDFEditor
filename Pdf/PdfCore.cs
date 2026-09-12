using System;
using System.Collections.Generic;
using System.Windows;

namespace PDFEditor.Pdf;

/// <summary>
/// Matrice affine 2D au format PDF : (x, y) -> (a x + c y + e, b x + d y + f).
/// </summary>
public readonly struct PdfMatrix
{
    public PdfMatrix(double a, double b, double c, double d, double e, double f)
    {
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    public double A { get; }
    public double B { get; }
    public double C { get; }
    public double D { get; }
    public double E { get; }
    public double F { get; }

    public static PdfMatrix Identity => new(1, 0, 0, 1, 0, 0);

    public Point Transform(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);

    public Point Transform(Point p) => Transform(p.X, p.Y);

    public PdfMatrix Invert()
    {
        var det = A * D - B * C;
        if (Math.Abs(det) < 1e-12)
        {
            return Identity;
        }

        var ia = D / det;
        var ib = -B / det;
        var ic = -C / det;
        var id = A / det;
        var ie = -(ia * E + ic * F);
        var iF = -(ib * E + id * F);
        return new PdfMatrix(ia, ib, ic, id, ie, iF);
    }

    /// <summary>Compose : applique <paramref name="first"/> puis <paramref name="second"/>.</summary>
    public static PdfMatrix Multiply(PdfMatrix first, PdfMatrix second)
    {
        return new PdfMatrix(
            second.A * first.A + second.C * first.B,
            second.B * first.A + second.D * first.B,
            second.A * first.C + second.C * first.D,
            second.B * first.C + second.D * first.D,
            second.A * first.E + second.C * first.F + second.E,
            second.B * first.E + second.D * first.F + second.F);
    }
}

/// <summary>
/// Geometrie d'une page, exprimee en points « d'affichage » : origine en haut
/// a gauche, axe Y vers le bas, rotation de la page deja appliquee. Toutes les
/// annotations de l'application sont stockees dans ce repere.
/// </summary>
public sealed class PdfPageInfo
{
    public int Index { get; init; }

    /// <summary>Largeur affichee (points PDF, rotation appliquee).</summary>
    public double Width { get; init; }

    /// <summary>Hauteur affichee (points PDF, rotation appliquee).</summary>
    public double Height { get; init; }

    /// <summary>Rotation de la page : 0, 1, 2 ou 3 quarts de tour horaires.</summary>
    public int Rotation { get; init; }

    /// <summary>Espace utilisateur PDF -> repere d'affichage.</summary>
    public PdfMatrix UserToDisplay { get; init; }

    /// <summary>Repere d'affichage -> espace utilisateur PDF.</summary>
    public PdfMatrix DisplayToUser { get; init; }

    public Size Size => new(Width, Height);

    /// <summary>Rectangle d'affichage englobant un rectangle de l'espace utilisateur.</summary>
    public Rect ToDisplayRect(double left, double bottom, double right, double top)
    {
        var p1 = UserToDisplay.Transform(left, bottom);
        var p2 = UserToDisplay.Transform(right, top);
        return new Rect(p1, p2);
    }

    /// <summary>Rectangle utilisateur (gauche, bas, droite, haut) d'un rectangle d'affichage.</summary>
    public (double Left, double Bottom, double Right, double Top) ToUserRect(Rect display)
    {
        var p1 = DisplayToUser.Transform(display.Left, display.Top);
        var p2 = DisplayToUser.Transform(display.Right, display.Bottom);
        return (Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y));
    }
}

/// <summary>Entree de la table des matieres (signets) du document.</summary>
public sealed class PdfOutlineItem
{
    public string Title { get; init; } = "";
    public int PageIndex { get; init; } = -1;
    public List<PdfOutlineItem> Children { get; } = new();
}

/// <summary>Cible d'un lien : page du document ou adresse web.</summary>
public sealed class PdfLinkTarget
{
    public int PageIndex { get; init; } = -1;
    public string? Uri { get; init; }
}

/// <summary>Metadonnees lues dans le dictionnaire /Info.</summary>
public sealed class PdfMetadata
{
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string Creator { get; set; } = "";
    public string Producer { get; set; } = "";
    public DateTime? Created { get; set; }
    public DateTime? Modified { get; set; }

    public PdfMetadata Clone() => (PdfMetadata)MemberwiseClone();

    public bool SameAs(PdfMetadata other) =>
        Title == other.Title && Author == other.Author && Subject == other.Subject && Keywords == other.Keywords;

    /// <summary>Convertit une date PDF « D:YYYYMMDDHHmmSS » en DateTime.</summary>
    public static DateTime? ParsePdfDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var s = value.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        int Part(int start, int length, int fallback)
        {
            if (s.Length < start + length)
            {
                return fallback;
            }

            return int.TryParse(s.AsSpan(start, length), out var v) ? v : fallback;
        }

        try
        {
            var year = Part(0, 4, 0);
            if (year < 1900)
            {
                return null;
            }

            return new DateTime(year, Math.Clamp(Part(4, 2, 1), 1, 12), Math.Clamp(Part(6, 2, 1), 1, 28 + 3),
                Math.Clamp(Part(8, 2, 0), 0, 23), Math.Clamp(Part(10, 2, 0), 0, 59), Math.Clamp(Part(12, 2, 0), 0, 59));
        }
        catch
        {
            return null;
        }
    }

    public static string ToPdfDate(DateTime date) => "D:" + date.ToString("yyyyMMddHHmmss");
}

/// <summary>Le document est protege par un mot de passe (absent ou incorrect).</summary>
public sealed class PdfPasswordException : Exception
{
    public PdfPasswordException(bool passwordWasSupplied)
        : base(passwordWasSupplied ? "Mot de passe incorrect." : "Ce document est protégé par un mot de passe.")
    {
        PasswordWasSupplied = passwordWasSupplied;
    }

    public bool PasswordWasSupplied { get; }
}

/// <summary>Le fichier n'a pas pu etre lu comme un PDF.</summary>
public sealed class PdfLoadException : Exception
{
    public PdfLoadException(string message)
        : base(message)
    {
    }

    public static PdfLoadException FromError(uint code) => new(code switch
    {
        Native.FPDF_ERR_FILE => "Le fichier est introuvable ou illisible.",
        Native.FPDF_ERR_FORMAT => "Le fichier n’est pas un PDF valide ou il est endommagé.",
        Native.FPDF_ERR_SECURITY => "Le gestionnaire de sécurité de ce PDF n’est pas pris en charge.",
        _ => "Le document n’a pas pu être ouvert."
    });
}
