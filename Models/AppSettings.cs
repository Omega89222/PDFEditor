using System;
using System.Collections.Generic;

namespace PDFEditor.Models;

public enum AppTheme { Light, Dark, System }

/// <summary>Disposition des pages dans la vue du document.</summary>
public enum ScrollModeKind { Continuous, SinglePage, TwoPages }

/// <summary>Mode de zoom : libre, ajuste a la largeur ou a la page entiere.</summary>
public enum ZoomModeKind { Custom, FitWidth, FitPage }

/// <summary>Onglet actif de la barre laterale.</summary>
public enum SidebarModeKind { Pages, Outline, Annotations, Search }

/// <summary>Fichier recemment ouvert (ecran d'accueil).</summary>
public sealed class RecentFile
{
    public string Path { get; set; } = "";
    public DateTime OpenedAt { get; set; }
    public int PageCount { get; set; }
}

/// <summary>
/// Style memorise pour un outil d'annotation. Les couleurs sont stockees
/// au format « #AARRGGBB » pour rester lisibles dans le fichier JSON.
/// </summary>
public sealed class ToolStyle
{
    public string StrokeColor { get; set; } = "#FFFF3B30";
    public string FillColor { get; set; } = "#00000000";
    public double StrokeWidth { get; set; } = 2;
    public double Opacity { get; set; } = 1;
    public string FontFamily { get; set; } = "Arial";
    public double FontSize { get; set; } = 14;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public string TextColor { get; set; } = "#FF1C1C1E";
    public bool Dashed { get; set; }

    public ToolStyle Clone() => (ToolStyle)MemberwiseClone();
}

/// <summary>Signature enregistree : trace a la main, texte manuscrit ou image.</summary>
public sealed class SavedSignature
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public SignatureKind Kind { get; set; } = SignatureKind.Ink;

    /// <summary>Traces normalises : chaque point est [x, y] dans la boite Width x Height.</summary>
    public List<List<double[]>> Strokes { get; set; } = new();
    public double Width { get; set; }
    public double Height { get; set; }
    public double StrokeWidth { get; set; } = 2.2;

    public string? Text { get; set; }
    public string? FontFamily { get; set; }

    public string? ImageBase64 { get; set; }

    public string Color { get; set; } = "#FF1C2A6B";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public enum SignatureKind { Ink, Text, Image }

public sealed class AppSettings
{
    public double WindowWidth { get; set; } = 1320;
    public double WindowHeight { get; set; } = 860;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool IsMaximized { get; set; }

    public double SidebarWidth { get; set; } = 212;
    public double InspectorWidth { get; set; } = 276;
    public bool IsSidebarVisible { get; set; } = true;
    public bool IsInspectorVisible { get; set; } = true;
    public bool IsMarkupBarVisible { get; set; } = true;
    public SidebarModeKind SidebarMode { get; set; } = SidebarModeKind.Pages;

    public AppTheme Theme { get; set; } = AppTheme.Light;
    public ScrollModeKind ScrollMode { get; set; } = ScrollModeKind.Continuous;
    public ZoomModeKind DefaultZoomMode { get; set; } = ZoomModeKind.FitWidth;
    public bool NightMode { get; set; }
    public bool HighlightFormFields { get; set; } = true;

    public string AuthorName { get; set; } = Environment.UserName;

    public List<RecentFile> RecentFiles { get; set; } = new();
    public Dictionary<string, ToolStyle> ToolStyles { get; set; } = new();
    public List<SavedSignature> Signatures { get; set; } = new();
    public List<string> RecentColors { get; set; } = new();
}
