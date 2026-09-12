using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using PDFEditor.Models;
using PDFEditor.Pdf;

namespace PDFEditor.ViewModels;

/// <summary>Outils de la barre d'annotation.</summary>
public enum EditorTool
{
    Select, Hand, Marquee,
    TextBox, EditText,
    Pen, Highlighter,
    TextHighlight, TextUnderline, TextStrike, TextSquiggly,
    Rectangle, Ellipse, Line, Arrow,
    Note, Stamp, Signature, Image, Link,
    Redact, Whiteout
}

/// <summary>Une page du document et ses annotations.</summary>
public sealed class PageViewModel : ObservableObject
{
    private readonly HashSet<Annotation> _subscribed = new();
    private int _index;
    private double _width = 612;
    private double _height = 792;
    private ImageSource? _thumbnail;
    private bool _isSelected;
    private int _contentVersion;

    public PageViewModel()
    {
        Annotations.CollectionChanged += OnAnnotationsCollectionChanged;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public ObservableCollection<Annotation> Annotations { get; } = new();

    public int Index
    {
        get => _index;
        set
        {
            if (Set(ref _index, value))
            {
                Raise(nameof(Label));
            }
        }
    }

    public string Label => (_index + 1).ToString(CultureInfo.CurrentCulture);

    /// <summary>Largeur affichee (points).</summary>
    public double Width
    {
        get => _width;
        private set
        {
            if (Set(ref _width, value))
            {
                Raise(nameof(AspectRatio), nameof(SizeLabel));
            }
        }
    }

    /// <summary>Hauteur affichee (points).</summary>
    public double Height
    {
        get => _height;
        private set
        {
            if (Set(ref _height, value))
            {
                Raise(nameof(AspectRatio), nameof(SizeLabel));
            }
        }
    }

    public double AspectRatio => _width <= 0 ? 1.294 : _height / _width;

    public string SizeLabel => $"{_width * 25.4 / 72:0} × {_height * 25.4 / 72:0} mm";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => Set(ref _thumbnail, value);
    }

    /// <summary>Version du contenu pour laquelle la vignette a ete rendue.</summary>
    public int ThumbnailVersion { get; set; } = -1;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Incremente quand le contenu PDF de la page change.</summary>
    public int ContentVersion
    {
        get => _contentVersion;
        private set => Set(ref _contentVersion, value);
    }

    public bool HasAnnotations => Annotations.Count > 0;

    /// <summary>Une annotation a ete ajoutee, retiree ou modifiee (redessiner).</summary>
    public event EventHandler? AnnotationsInvalidated;

    /// <summary>La liste des annotations a change (liste de la barre laterale).</summary>
    public event EventHandler? AnnotationsListChanged;

    /// <summary>Le contenu PDF a change (nouveau rendu).</summary>
    public event EventHandler? ContentInvalidated;

    public void UpdateSize(Size size)
    {
        Width = size.Width;
        Height = size.Height;
    }

    public void InvalidateContent()
    {
        ContentVersion++;
        ContentInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void InvalidateAnnotations() => AnnotationsInvalidated?.Invoke(this, EventArgs.Empty);

    /// <summary>Lignes de texte d'origine a masquer au rendu de la page (textes modifies).</summary>
    public IReadOnlyList<Rect> GetHiddenTextRegions() =>
        Annotations.OfType<TextEditAnnotation>().Where(a => !a.UseCover).Select(a => a.OriginalRect).ToList();

    private void OnAnnotationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var current = new HashSet<Annotation>(Annotations);
        var textEditsChanged = false;

        foreach (var removed in _subscribed.Where(a => !current.Contains(a)).ToList())
        {
            removed.Changed -= OnAnnotationChanged;
            _subscribed.Remove(removed);
            textEditsChanged |= removed is TextEditAnnotation;
        }

        foreach (var annotation in current)
        {
            if (_subscribed.Add(annotation))
            {
                annotation.Changed += OnAnnotationChanged;
                textEditsChanged |= annotation is TextEditAnnotation;
            }
        }

        Raise(nameof(HasAnnotations));
        AnnotationsInvalidated?.Invoke(this, EventArgs.Empty);
        AnnotationsListChanged?.Invoke(this, EventArgs.Empty);

        // Le texte d'origine d'une ligne modifiee disparait (ou reapparait) : nouveau rendu.
        if (textEditsChanged)
        {
            InvalidateContent();
        }
    }

    private void OnAnnotationChanged(object? sender, EventArgs e)
    {
        AnnotationsInvalidated?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Signet de la table des matieres.</summary>
public sealed class OutlineItemViewModel : ObservableObject
{
    private bool _isExpanded;

    public string Title { get; init; } = "";

    public int PageIndex { get; init; } = -1;

    public List<OutlineItemViewModel> Children { get; } = new();

    public string PageLabel => PageIndex >= 0 ? (PageIndex + 1).ToString(CultureInfo.CurrentCulture) : "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public static OutlineItemViewModel From(PdfOutlineItem item, int depth = 0)
    {
        var vm = new OutlineItemViewModel
        {
            Title = string.IsNullOrWhiteSpace(item.Title) ? "(Sans titre)" : item.Title,
            PageIndex = item.PageIndex,
            IsExpanded = depth == 0 && item.Children.Count <= 12
        };

        foreach (var child in item.Children)
        {
            vm.Children.Add(From(child, depth + 1));
        }

        return vm;
    }
}

/// <summary>Resultat de recherche : position et extrait.</summary>
public sealed class SearchResultViewModel
{
    public int PageIndex { get; init; }

    public TextSpan Span { get; init; }

    public List<Rect> Rects { get; init; } = new();

    public string Before { get; init; } = "";

    public string Match { get; init; } = "";

    public string After { get; init; } = "";

    public string PageLabel => $"Page {PageIndex + 1}";

    public Rect Bounds
    {
        get
        {
            var r = Rect.Empty;
            foreach (var rect in Rects)
            {
                r.Union(rect);
            }

            return r;
        }
    }

    public static SearchResultViewModel Create(int pageIndex, PdfTextPage text, TextSpan span)
    {
        var start = Math.Max(0, span.Start - 42);
        var end = Math.Min(text.Text.Length - 1, span.End + 60);

        return new SearchResultViewModel
        {
            PageIndex = pageIndex,
            Span = span,
            Rects = text.GetSpanRects(span),
            Before = (start > 0 ? "…" : "") + Collapse(text.Text[start..span.Start]).TrimStart(),
            Match = Collapse(text.GetSpanText(span)),
            After = Collapse(end > span.End ? text.Text[(span.End + 1)..(end + 1)] : "").TrimEnd() + (end < text.Text.Length - 1 ? "…" : "")
        };
    }

    private static string Collapse(string value)
    {
        var builder = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value)
        {
            // Glyphes sans correspondance Unicode (U+FFFD) et caracteres de controle : espace.
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c == (char)0xFFFD)
            {
                if (!space)
                {
                    builder.Append(' ');
                }

                space = true;
            }
            else
            {
                builder.Append(c);
                space = false;
            }
        }

        return builder.ToString();
    }
}

/// <summary>Selection de texte courante (une page).</summary>
public sealed class TextSelectionState
{
    public TextSelectionState(PageViewModel page, TextSpan span, List<Rect> rects, string text)
    {
        Page = page;
        Span = span;
        Rects = rects;
        Text = text;
    }

    public PageViewModel Page { get; }

    public TextSpan Span { get; }

    public List<Rect> Rects { get; }

    public string Text { get; }
}

/// <summary>Ligne de la liste des annotations (barre laterale).</summary>
public sealed class AnnotationListItem
{
    public AnnotationListItem(Annotation annotation, PageViewModel page)
    {
        Annotation = annotation;
        Page = page;
    }

    public Annotation Annotation { get; }

    public PageViewModel Page { get; }

    public string Title => Annotation.TypeName;

    public string Detail => Annotation.Summary == Annotation.TypeName ? "" : Annotation.Summary;

    public string PageLabel => $"Page {Page.Index + 1}";

    public string TimeLabel => Annotation.Modified.ToString("HH:mm", CultureInfo.CurrentCulture);

    public Color AccentColor => Annotation switch
    {
        WhiteoutAnnotation => Color.FromRgb(0xAE, 0xAE, 0xB2),
        RedactionAnnotation => Colors.Black,
        ImageAnnotation or LinkAnnotation => Color.FromRgb(0x00, 0x7A, 0xFF),
        TextBoxAnnotation text => text.TextColor,
        _ => Annotation.StrokeColor
    };

    public string IconKey => Annotation switch
    {
        InkAnnotation { IsSignature: true } => "Icon.Signature",
        InkAnnotation { IsHighlighter: true } => "Icon.Highlight",
        InkAnnotation => "Icon.Markup",
        ShapeAnnotation { Shape: ShapeKind.Ellipse } => "Icon.Ellipse",
        ShapeAnnotation => "Icon.Rectangle",
        LineAnnotation { Kind: AnnotationKind.Arrow } => "Icon.Arrow",
        LineAnnotation => "Icon.Line",
        TextEditAnnotation => "Icon.EditText",
        TextBoxAnnotation => "Icon.TextBox",
        MarkupAnnotation { Markup: MarkupKind.Underline } => "Icon.Underline",
        MarkupAnnotation { Markup: MarkupKind.StrikeOut } => "Icon.Strikethrough",
        MarkupAnnotation => "Icon.TextHighlight",
        NoteAnnotation => "Icon.Note",
        ImageAnnotation => "Icon.Photo",
        StampAnnotation => "Icon.Stamp",
        RedactionAnnotation => "Icon.Redact",
        WhiteoutAnnotation => "Icon.Whiteout",
        LinkAnnotation => "Icon.Link",
        _ => "Icon.Markup"
    };
}

/// <summary>Fichier recent (ecran d'accueil).</summary>
public sealed class RecentFileItem
{
    public RecentFileItem(RecentFile file)
    {
        Path = file.Path;
        Name = System.IO.Path.GetFileName(file.Path);
        Folder = System.IO.Path.GetDirectoryName(file.Path) ?? "";
        PageCount = file.PageCount;

        var date = file.OpenedAt;
        var today = DateTime.Today;
        DateLabel = date.Date == today
            ? "Aujourd’hui, " + date.ToString("HH:mm", CultureInfo.CurrentCulture)
            : date.Date == today.AddDays(-1)
                ? "Hier, " + date.ToString("HH:mm", CultureInfo.CurrentCulture)
                : date.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    public string Path { get; }

    public string Name { get; }

    public string Folder { get; }

    public int PageCount { get; }

    public string DateLabel { get; }

    public string Detail => PageCount > 0 ? $"{DateLabel} · {PageCount} page{(PageCount > 1 ? "s" : "")}" : DateLabel;
}
