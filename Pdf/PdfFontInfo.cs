namespace PDFEditor.Pdf;

/// <summary>
/// Police d'une ligne de texte telle que le PDF la declare : nom de base
/// (souvent « ABCDEF+Calibri-Bold »), nom de famille, indicateurs du descripteur,
/// graisse, inclinaison et corps.
/// </summary>
public sealed class PdfFontInfo
{
    // Indicateurs du descripteur de police PDF (table 122 de la specification).
    private const int FixedPitchFlag = 1;
    private const int SerifFlag = 2;
    private const int ItalicFlag = 64;
    private const int ForceBoldFlag = 1 << 18;

    public string BaseName { get; init; } = "";

    public string FamilyName { get; init; } = "";

    public int Flags { get; init; }

    public int Weight { get; init; }

    public int ItalicAngle { get; init; }

    /// <summary>Vrai si le fichier de police est incorpore au document.</summary>
    public bool IsEmbedded { get; init; }

    /// <summary>Corps declare par l'objet texte (points).</summary>
    public double Size { get; init; }

    /// <summary>Nom le plus parlant pour retrouver une police installee.</summary>
    public string Name => !string.IsNullOrWhiteSpace(BaseName) ? BaseName : FamilyName;

    public bool Monospace => (Flags & FixedPitchFlag) != 0;

    public bool Serif => (Flags & SerifFlag) != 0;

    public bool Bold => Weight >= 600 || (Flags & ForceBoldFlag) != 0;

    public bool Italic => ItalicAngle != 0 || (Flags & ItalicFlag) != 0;
}
