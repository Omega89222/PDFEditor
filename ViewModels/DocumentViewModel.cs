using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PDFEditor.Models;
using PDFEditor.Pdf;
using PDFEditor.Services;

namespace PDFEditor.ViewModels;

/// <summary>
/// Un document ouvert : fichier PDF (PDFium), pages, annotations, selection,
/// outil courant, historique. Ne montre jamais de dialogue : c'est le role
/// de <see cref="MainViewModel"/>.
/// </summary>
public sealed partial class DocumentViewModel : ObservableObject, IDisposable
{
    public const double MinZoom = 0.1;
    public const double MaxZoom = 8;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _editCommitTimer;
    private readonly DispatcherTimer _annotationListTimer;

    private PdfDoc _pdf;
    private string? _filePath;
    private string _untitledName = "Sans titre.pdf";
    private long _fileSize;
    private int _currentPageIndex;
    private double _zoom = 1;
    private ZoomModeKind _zoomMode;
    private ScrollModeKind _scrollMode;
    private EditorTool _tool = EditorTool.Select;
    private Annotation? _selectedAnnotation;
    private PageViewModel? _selectedAnnotationPage;
    private Annotation? _editSnapshot;
    private int _editSnapshotRevision;
    private TextSelectionState? _textSelection;
    private bool _formsDirty;
    private bool _metadataChanged;
    private bool _protectionChanged;
    private PdfMetadata _metadata;
    private PdfProtection? _protection;
    private string _stampLabel = "APPROUVÉ";
    private SavedSignature? _signature;
    private bool _disposed;

    private DocumentViewModel(PdfDoc pdf, string? filePath, string? password, long fileSize)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _pdf = pdf;
        _filePath = filePath;
        _fileSize = fileSize;
        Password = password;
        _metadata = pdf.GetMetadata();
        _scrollMode = SettingsService.Current.ScrollMode;
        _zoomMode = SettingsService.Current.DefaultZoomMode;

        pdf.SetFormHighlight(SettingsService.Current.HighlightFormFields);
        Renderer = new RenderScheduler(_dispatcher) { Document = pdf };

        _editCommitTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _editCommitTimer.Tick += (_, _) => CommitPendingEdit("Modifier l’annotation");

        _annotationListTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _annotationListTimer.Tick += (_, _) =>
        {
            _annotationListTimer.Stop();
            RebuildAnnotationItems();
        };

        Undo.StateChanged += OnUndoStateChanged;

        if (pdf.IsEncrypted)
        {
            InitializeProtection(password);
        }

        LoadPages();
        LoadOutline();
    }

    /// <summary>Document ouvert depuis un fichier (a appeler sur le fil de l'interface).</summary>
    public static DocumentViewModel FromFile(PdfDoc pdf, string path, string? password, long fileSize) =>
        new(pdf, path, password, fileSize);

    /// <summary>Nouveau document non enregistre.</summary>
    public static DocumentViewModel FromUntitled(PdfDoc pdf, string name)
    {
        var vm = new DocumentViewModel(pdf, null, null, 0) { _untitledName = name };
        vm.Undo.MarkDirty();
        return vm;
    }

    // =====================================================================
    // Fichier
    // =====================================================================

    public PdfDoc Pdf => _pdf;

    public RenderScheduler Renderer { get; }

    public UndoManager Undo { get; } = new();

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    public ObservableCollection<OutlineItemViewModel> Outline { get; } = new();

    public ObservableCollection<AnnotationListItem> AnnotationItems { get; } = new();

    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (Set(ref _filePath, value))
            {
                Raise(nameof(DisplayName), nameof(IsUntitled), nameof(FolderPath));
            }
        }
    }

    public bool IsUntitled => _filePath is null;

    public string DisplayName => _filePath is not null ? Path.GetFileName(_filePath) : _untitledName;

    public string FolderPath => _filePath is not null ? Path.GetDirectoryName(_filePath) ?? "" : "";

    public string? Password { get; private set; }

    public long FileSize
    {
        get => _fileSize;
        private set
        {
            if (Set(ref _fileSize, value))
            {
                Raise(nameof(FileSizeLabel));
            }
        }
    }

    public string FileSizeLabel => FormatSize(_fileSize);

    public string VersionLabel => _pdf.FileVersion > 0
        ? string.Format(CultureInfo.InvariantCulture, "PDF {0}.{1}", _pdf.FileVersion / 10, _pdf.FileVersion % 10)
        : "PDF";

    public bool HasForms => _pdf.HasForms;

    public int PageCount => Pages.Count;

    public int AnnotationCount => AnnotationItems.Count;

    public bool HasOutline => Outline.Count > 0;

    public bool IsModified => !Undo.IsAtSavedState || _formsDirty || _metadataChanged || _protectionChanged;

    public void MarkFormsDirty()
    {
        if (!_formsDirty)
        {
            _formsDirty = true;
            Raise(nameof(IsModified));
        }
    }

    private void OnUndoStateChanged()
    {
        Raise(nameof(IsModified));
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "—";
        }

        string[] units = { "octets", "Ko", "Mo", "Go" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} octets"
            : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    // =====================================================================
    // Pages, signets
    // =====================================================================

    private PageViewModel CreatePage()
    {
        var page = new PageViewModel();
        page.AnnotationsListChanged += (_, _) => ScheduleAnnotationListRebuild();
        return page;
    }

    private void LoadPages()
    {
        Pages.Clear();
        var count = _pdf.PageCount;
        for (var i = 0; i < count; i++)
        {
            var page = CreatePage();
            page.Index = i;
            page.UpdateSize(_pdf.GetPageSize(i));
            Pages.Add(page);
        }

        Raise(nameof(PageCount), nameof(PageStatus));
    }

    private void LoadOutline()
    {
        Outline.Clear();
        try
        {
            foreach (var item in _pdf.GetOutline())
            {
                Outline.Add(OutlineItemViewModel.From(item));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Document] Signets illisibles : {ex.Message}");
        }

        Raise(nameof(HasOutline));
    }

    private void ScheduleAnnotationListRebuild()
    {
        _annotationListTimer.Stop();
        _annotationListTimer.Start();
    }

    private void RebuildAnnotationItems()
    {
        AnnotationItems.Clear();
        foreach (var page in Pages)
        {
            foreach (var annotation in page.Annotations)
            {
                AnnotationItems.Add(new AnnotationListItem(annotation, page));
            }
        }

        Raise(nameof(AnnotationCount));
    }

    public PageViewModel? FindPage(Annotation annotation) =>
        Pages.FirstOrDefault(p => p.Annotations.Contains(annotation));

    /// <summary>Pages visees par une commande : selection de vignettes, sinon page courante.</summary>
    public List<int> GetTargetPages()
    {
        var selected = Pages.Where(p => p.IsSelected).Select(p => p.Index).ToList();
        if (selected.Count == 0 && CurrentPage is not null)
        {
            selected.Add(CurrentPageIndex);
        }

        return selected;
    }

    // =====================================================================
    // Navigation et affichage
    // =====================================================================

    /// <summary>La vue doit faire defiler jusqu'a une page (et une zone eventuelle).</summary>
    public event Action<int, Rect?>? NavigationRequested;

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            var clamped = Pages.Count == 0 ? 0 : Math.Clamp(value, 0, Pages.Count - 1);
            if (Set(ref _currentPageIndex, clamped))
            {
                Raise(nameof(CurrentPage), nameof(PageStatus), nameof(CurrentPageNumber));
            }
        }
    }

    public PageViewModel? CurrentPage =>
        _currentPageIndex >= 0 && _currentPageIndex < Pages.Count ? Pages[_currentPageIndex] : null;

    /// <summary>Numero de page (base 1), modifiable depuis le HUD.</summary>
    public int CurrentPageNumber
    {
        get => _currentPageIndex + 1;
        set => GoToPage(value - 1);
    }

    public string PageStatus => Pages.Count == 0 ? "" : $"Page {_currentPageIndex + 1} sur {Pages.Count}";

    public double Zoom
    {
        get => _zoom;
        set
        {
            if (Set(ref _zoom, Math.Clamp(value, MinZoom, MaxZoom)))
            {
                Raise(nameof(ZoomLabel));
            }
        }
    }

    public string ZoomLabel => $"{Math.Round(_zoom * 100)} %";

    public ZoomModeKind ZoomMode
    {
        get => _zoomMode;
        set => Set(ref _zoomMode, value);
    }

    public ScrollModeKind ScrollMode
    {
        get => _scrollMode;
        set
        {
            if (Set(ref _scrollMode, value))
            {
                SettingsService.Current.ScrollMode = value;
                SettingsService.SaveSoon();
            }
        }
    }

    public void GoToPage(int index, Rect? area = null)
    {
        if (Pages.Count == 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, Pages.Count - 1);
        CurrentPageIndex = index;
        NavigationRequested?.Invoke(index, area);
    }

    // =====================================================================
    // Outil courant
    // =====================================================================

    public event Action? ToolChanged;

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (!Set(ref _tool, value))
            {
                return;
            }

            if (value != EditorTool.Select && _selectedAnnotation is not null && !IsCompatibleSelection(value, _selectedAnnotation))
            {
                Select(null, null);
            }

            if (value is not (EditorTool.Select or EditorTool.TextHighlight or EditorTool.TextUnderline or EditorTool.TextStrike or EditorTool.TextSquiggly))
            {
                ClearTextSelection();
            }

            RaiseStyle();
            ToolChanged?.Invoke();
        }
    }

    private static bool IsCompatibleSelection(EditorTool tool, Annotation annotation) => tool switch
    {
        EditorTool.Pen => annotation.Kind == AnnotationKind.Ink,
        EditorTool.Highlighter => annotation.Kind == AnnotationKind.Highlighter,
        _ => false
    };

    public string StampLabel
    {
        get => _stampLabel;
        set => Set(ref _stampLabel, string.IsNullOrWhiteSpace(value) ? "APPROUVÉ" : value);
    }

    public SavedSignature? Signature
    {
        get => _signature;
        set => Set(ref _signature, value);
    }

    // =====================================================================
    // Selection d'annotation et suivi des modifications
    // =====================================================================

    public event Action? SelectionChanged;

    public Annotation? SelectedAnnotation => _selectedAnnotation;

    public PageViewModel? SelectedAnnotationPage => _selectedAnnotationPage;

    public bool HasSelection => _selectedAnnotation is not null;

    public void Select(PageViewModel? page, Annotation? annotation)
    {
        if (ReferenceEquals(annotation, _selectedAnnotation) && ReferenceEquals(page, _selectedAnnotationPage))
        {
            return;
        }

        CommitPendingEdit();

        if (_selectedAnnotation is not null)
        {
            _selectedAnnotation.Changed -= OnSelectedAnnotationChanged;
        }

        _selectedAnnotation = annotation;
        _selectedAnnotationPage = annotation is null ? null : page ?? FindPage(annotation);
        TakeEditSnapshot();

        if (annotation is not null)
        {
            annotation.Changed += OnSelectedAnnotationChanged;
        }

        Raise(nameof(SelectedAnnotation), nameof(SelectedAnnotationPage), nameof(HasSelection));
        RaiseStyle();
        SelectionChanged?.Invoke();
    }

    private void TakeEditSnapshot()
    {
        _editSnapshot = _selectedAnnotation?.Snapshot();
        _editSnapshotRevision = _selectedAnnotation?.Revision ?? 0;
    }

    /// <summary>Suspend l'enregistrement automatique (edition de texte en place).</summary>
    public bool SuspendEditTracking { get; set; }

    /// <summary>Oublie les modifications en cours sans les inscrire dans l'historique.</summary>
    public void DiscardPendingEdit()
    {
        _editCommitTimer.Stop();
        TakeEditSnapshot();
    }

    private void OnSelectedAnnotationChanged(object? sender, EventArgs e)
    {
        if (Undo.IsApplying || _editSnapshot is null || SuspendEditTracking)
        {
            RaiseStyle();
            return;
        }

        _editCommitTimer.Stop();
        _editCommitTimer.Start();
        RaiseStyle();
    }

    /// <summary>
    /// Enregistre dans l'historique les modifications en cours de l'annotation
    /// selectionnee (deplacement, redimensionnement, style...).
    /// </summary>
    public void CommitPendingEdit(string name = "Modifier l’annotation")
    {
        _editCommitTimer.Stop();

        var annotation = _selectedAnnotation;
        var before = _editSnapshot;
        if (annotation is null || before is null || annotation.Revision == _editSnapshotRevision)
        {
            return;
        }

        var after = annotation.Snapshot();
        Undo.Push(name,
            () =>
            {
                annotation.Restore(before);
                if (ReferenceEquals(_selectedAnnotation, annotation))
                {
                    TakeEditSnapshot();
                }

                RaiseStyle();
            },
            () =>
            {
                annotation.Restore(after);
                if (ReferenceEquals(_selectedAnnotation, annotation))
                {
                    TakeEditSnapshot();
                }

                RaiseStyle();
            });

        TakeEditSnapshot();
    }

    // =====================================================================
    // Ajout, suppression, ordre des annotations
    // =====================================================================

    public void AddAnnotation(PageViewModel page, Annotation annotation, bool select = true)
    {
        CommitPendingEdit();

        if (string.IsNullOrWhiteSpace(annotation.Author))
        {
            annotation.Author = SettingsService.Current.AuthorName;
        }

        page.Annotations.Add(annotation);
        Undo.Push("Ajouter : " + annotation.TypeName.ToLowerInvariant(),
            () =>
            {
                if (ReferenceEquals(_selectedAnnotation, annotation))
                {
                    Select(null, null);
                }

                page.Annotations.Remove(annotation);
            },
            () => page.Annotations.Add(annotation));

        if (select)
        {
            Select(page, annotation);
        }
    }

    public void RemoveAnnotation(PageViewModel page, Annotation annotation)
    {
        CommitPendingEdit();

        var index = page.Annotations.IndexOf(annotation);
        if (index < 0)
        {
            return;
        }

        if (ReferenceEquals(_selectedAnnotation, annotation))
        {
            Select(null, null);
        }

        page.Annotations.RemoveAt(index);
        Undo.Push("Supprimer : " + annotation.TypeName.ToLowerInvariant(),
            () => page.Annotations.Insert(Math.Min(index, page.Annotations.Count), annotation),
            () =>
            {
                if (ReferenceEquals(_selectedAnnotation, annotation))
                {
                    Select(null, null);
                }

                page.Annotations.Remove(annotation);
            });
    }

    public void DeleteSelectedAnnotation()
    {
        if (_selectedAnnotation is { } annotation && _selectedAnnotationPage is { } page)
        {
            RemoveAnnotation(page, annotation);
        }
    }

    public void DuplicateSelectedAnnotation()
    {
        if (_selectedAnnotation is not { } annotation || _selectedAnnotationPage is not { } page)
        {
            return;
        }

        CommitPendingEdit();
        var copy = annotation.Duplicate(annotation.CanMove ? new Vector(12, 12) : new Vector());
        AddAnnotation(page, copy);
    }

    public void ReorderSelectedAnnotation(bool toFront)
    {
        if (_selectedAnnotation is not { } annotation || _selectedAnnotationPage is not { } page)
        {
            return;
        }

        var from = page.Annotations.IndexOf(annotation);
        var to = toFront ? page.Annotations.Count - 1 : 0;
        if (from < 0 || from == to)
        {
            return;
        }

        CommitPendingEdit();
        page.Annotations.Move(from, to);
        Undo.Push(toFront ? "Mettre au premier plan" : "Mettre à l’arrière-plan",
            () => page.Annotations.Move(page.Annotations.IndexOf(annotation), from),
            () => page.Annotations.Move(page.Annotations.IndexOf(annotation), to));
        page.InvalidateAnnotations();
    }

    /// <summary>Applique le style memorise d'un outil a une nouvelle annotation.</summary>
    public static void ApplyToolStyle(Annotation annotation, string key)
    {
        var style = SettingsService.GetToolStyle(key);
        annotation.StrokeColor = ColorUtil.Parse(style.StrokeColor, annotation.StrokeColor);
        annotation.StrokeWidth = style.StrokeWidth;
        annotation.Opacity = style.Opacity;
        annotation.Dashed = style.Dashed;

        if (annotation.HasFillStyle)
        {
            annotation.FillColor = ColorUtil.Parse(style.FillColor, annotation.FillColor);
        }

        if (annotation is TextBoxAnnotation text)
        {
            text.FontFamily = style.FontFamily;
            text.FontSize = style.FontSize;
            text.Bold = style.Bold;
            text.Italic = style.Italic;
            text.TextColor = ColorUtil.Parse(style.TextColor, text.TextColor);
        }
    }

    // =====================================================================
    // Selection de texte
    // =====================================================================

    public event Action? TextSelectionChanged;

    public TextSelectionState? TextSelection => _textSelection;

    public bool HasTextSelection => _textSelection is not null;

    public void SetTextSelection(PageViewModel page, TextSpan span)
    {
        var text = _pdf.GetTextPage(page.Index);
        var rects = text.GetSpanRects(span);
        _textSelection = rects.Count == 0 ? null : new TextSelectionState(page, span, rects, text.GetSpanText(span));
        Raise(nameof(TextSelection), nameof(HasTextSelection));
        TextSelectionChanged?.Invoke();
    }

    public void ClearTextSelection()
    {
        if (_textSelection is null)
        {
            return;
        }

        _textSelection = null;
        Raise(nameof(TextSelection), nameof(HasTextSelection));
        TextSelectionChanged?.Invoke();
    }

    // =====================================================================
    // Styles (barre d'annotation et inspecteur)
    // =====================================================================

    /// <summary>Cle du style edite : celui de la selection, sinon celui de l'outil.</summary>
    public string StyleKey => _selectedAnnotation is not null ? KeyFor(_selectedAnnotation) : KeyFor(_tool);

    private ToolStyle CurrentStyle => SettingsService.GetToolStyle(StyleKey);

    public static string KeyFor(Annotation annotation) => annotation switch
    {
        InkAnnotation { IsHighlighter: true } => ToolKeys.Highlighter,
        InkAnnotation => ToolKeys.Pen,
        ShapeAnnotation => ToolKeys.Shape,
        LineAnnotation => ToolKeys.Line,
        TextBoxAnnotation => ToolKeys.Text,
        MarkupAnnotation { Markup: MarkupKind.Highlight } => ToolKeys.Highlight,
        MarkupAnnotation { Markup: MarkupKind.Underline } => ToolKeys.Underline,
        MarkupAnnotation { Markup: MarkupKind.StrikeOut } => ToolKeys.Strike,
        MarkupAnnotation => ToolKeys.Squiggly,
        NoteAnnotation => ToolKeys.Note,
        StampAnnotation => ToolKeys.Stamp,
        WhiteoutAnnotation => ToolKeys.Whiteout,
        _ => ToolKeys.Pen
    };

    public static string KeyFor(EditorTool tool) => tool switch
    {
        EditorTool.Highlighter => ToolKeys.Highlighter,
        EditorTool.Rectangle or EditorTool.Ellipse => ToolKeys.Shape,
        EditorTool.Line or EditorTool.Arrow => ToolKeys.Line,
        EditorTool.TextBox or EditorTool.EditText => ToolKeys.Text,
        EditorTool.TextHighlight => ToolKeys.Highlight,
        EditorTool.TextUnderline => ToolKeys.Underline,
        EditorTool.TextStrike => ToolKeys.Strike,
        EditorTool.TextSquiggly => ToolKeys.Squiggly,
        EditorTool.Note => ToolKeys.Note,
        EditorTool.Stamp => ToolKeys.Stamp,
        EditorTool.Whiteout => ToolKeys.Whiteout,
        _ => ToolKeys.Pen
    };

    public Color StyleStrokeColor
    {
        get => _selectedAnnotation is { HasStrokeStyle: true } a ? a.StrokeColor : ColorUtil.Parse(CurrentStyle.StrokeColor, Colors.Red);
        set
        {
            if (_selectedAnnotation is { HasStrokeStyle: true } a)
            {
                a.StrokeColor = value;
            }

            CurrentStyle.StrokeColor = ColorUtil.ToHex(value);
            SaveStyle();
        }
    }

    public Color StyleFillColor
    {
        get => _selectedAnnotation is { HasFillStyle: true } a ? a.FillColor : ColorUtil.Parse(CurrentStyle.FillColor, Colors.Transparent);
        set
        {
            if (_selectedAnnotation is { HasFillStyle: true } a)
            {
                a.FillColor = value;
            }

            CurrentStyle.FillColor = ColorUtil.ToHex(value);
            SaveStyle();
        }
    }

    public double StyleStrokeWidth
    {
        get => _selectedAnnotation is { HasWidthStyle: true } a ? a.StrokeWidth : CurrentStyle.StrokeWidth;
        set
        {
            if (_selectedAnnotation is { HasWidthStyle: true } a)
            {
                a.StrokeWidth = value;
            }

            CurrentStyle.StrokeWidth = Math.Clamp(value, 0, 72);
            SaveStyle();
        }
    }

    public double StyleOpacity
    {
        get => _selectedAnnotation is { HasOpacityStyle: true } a ? a.Opacity : CurrentStyle.Opacity;
        set
        {
            if (_selectedAnnotation is { HasOpacityStyle: true } a)
            {
                a.Opacity = value;
            }

            CurrentStyle.Opacity = Math.Clamp(value, 0.05, 1);
            SaveStyle();
        }
    }

    public bool StyleDashed
    {
        get => _selectedAnnotation is { HasDashStyle: true } a ? a.Dashed : CurrentStyle.Dashed;
        set
        {
            if (_selectedAnnotation is { HasDashStyle: true } a)
            {
                a.Dashed = value;
            }

            CurrentStyle.Dashed = value;
            SaveStyle();
        }
    }

    public string StyleFontFamily
    {
        get => _selectedAnnotation is TextBoxAnnotation t ? t.FontFamily : SettingsService.GetToolStyle(ToolKeys.Text).FontFamily;
        set
        {
            if (_selectedAnnotation is TextBoxAnnotation t)
            {
                t.FontFamily = value;
            }

            SettingsService.GetToolStyle(ToolKeys.Text).FontFamily = value;
            SaveStyle();
        }
    }

    public double StyleFontSize
    {
        get => _selectedAnnotation is TextBoxAnnotation t ? t.FontSize : SettingsService.GetToolStyle(ToolKeys.Text).FontSize;
        set
        {
            if (_selectedAnnotation is TextBoxAnnotation t)
            {
                t.FontSize = value;
            }

            SettingsService.GetToolStyle(ToolKeys.Text).FontSize = Math.Clamp(value, 2, 400);
            SaveStyle();
        }
    }

    public bool StyleBold
    {
        get => _selectedAnnotation is TextBoxAnnotation t ? t.Bold : SettingsService.GetToolStyle(ToolKeys.Text).Bold;
        set
        {
            if (_selectedAnnotation is TextBoxAnnotation t)
            {
                t.Bold = value;
            }

            SettingsService.GetToolStyle(ToolKeys.Text).Bold = value;
            SaveStyle();
        }
    }

    public bool StyleItalic
    {
        get => _selectedAnnotation is TextBoxAnnotation t ? t.Italic : SettingsService.GetToolStyle(ToolKeys.Text).Italic;
        set
        {
            if (_selectedAnnotation is TextBoxAnnotation t)
            {
                t.Italic = value;
            }

            SettingsService.GetToolStyle(ToolKeys.Text).Italic = value;
            SaveStyle();
        }
    }

    public bool StyleUnderline
    {
        get => _selectedAnnotation is TextBoxAnnotation t && t.Underline;
        set
        {
            if (_selectedAnnotation is TextBoxAnnotation t)
            {
                t.Underline = value;
            }

            RaiseStyle();
        }
    }

    public TextAlignment StyleAlignment
    {
        get => _selectedAnnotation is TextBoxAnnotation t ? t.Alignment : TextAlignment.Left;
        set
        {
            if (_selectedAnnotation is TextBoxAnnotation t)
            {
                t.Alignment = value;
            }

            RaiseStyle();
        }
    }

    public Color StyleTextColor
    {
        get => _selectedAnnotation is TextBoxAnnotation t ? t.TextColor : ColorUtil.Parse(SettingsService.GetToolStyle(ToolKeys.Text).TextColor, Colors.Black);
        set
        {
            if (_selectedAnnotation is TextBoxAnnotation t)
            {
                t.TextColor = value;
            }

            SettingsService.GetToolStyle(ToolKeys.Text).TextColor = ColorUtil.ToHex(value);
            SaveStyle();
        }
    }

    private void SaveStyle()
    {
        SettingsService.SaveSoon();
        RaiseStyle();
    }

    public void RaiseStyle()
    {
        Raise(nameof(StyleKey), nameof(StyleStrokeColor), nameof(StyleFillColor), nameof(StyleStrokeWidth),
            nameof(StyleOpacity), nameof(StyleDashed), nameof(StyleFontFamily), nameof(StyleFontSize),
            nameof(StyleBold), nameof(StyleItalic), nameof(StyleUnderline), nameof(StyleAlignment), nameof(StyleTextColor));
    }

    // =====================================================================
    // Metadonnees et protection
    // =====================================================================

    public string MetaTitle
    {
        get => _metadata.Title;
        set => SetMeta(value, _metadata.Title, v => _metadata.Title = v);
    }

    public string MetaAuthor
    {
        get => _metadata.Author;
        set => SetMeta(value, _metadata.Author, v => _metadata.Author = v);
    }

    public string MetaSubject
    {
        get => _metadata.Subject;
        set => SetMeta(value, _metadata.Subject, v => _metadata.Subject = v);
    }

    public string MetaKeywords
    {
        get => _metadata.Keywords;
        set => SetMeta(value, _metadata.Keywords, v => _metadata.Keywords = v);
    }

    public string MetaCreator => string.IsNullOrWhiteSpace(_metadata.Creator) ? "—" : _metadata.Creator;

    public string MetaProducer => string.IsNullOrWhiteSpace(_metadata.Producer) ? "—" : _metadata.Producer;

    public string CreatedLabel => _metadata.Created?.ToString("g", CultureInfo.CurrentCulture) ?? "—";

    public string ModifiedLabel => _metadata.Modified?.ToString("g", CultureInfo.CurrentCulture) ?? "—";

    private void SetMeta(string? value, string current, Action<string> assign)
    {
        value ??= "";
        if (value == current)
        {
            return;
        }

        assign(value);
        _metadataChanged = true;
        Raise(nameof(MetaTitle), nameof(MetaAuthor), nameof(MetaSubject), nameof(MetaKeywords), nameof(IsModified));
    }

    public PdfProtection? Protection => _protection;

    public bool IsProtected => _protection is { IsEnabled: true };

    public string ProtectionLabel => !IsProtected
        ? "Aucune protection"
        : !string.IsNullOrEmpty(_protection!.UserPassword)
            ? "Mot de passe à l’ouverture (AES 256 bits)"
            : "Restrictions d’utilisation";

    public void SetProtection(PdfProtection? protection)
    {
        _protection = protection;
        _protectionChanged = true;
        Raise(nameof(Protection), nameof(IsProtected), nameof(ProtectionLabel), nameof(IsModified));
    }

    private void InitializeProtection(string? password)
    {
        var permissions = _pdf.Permissions;
        _protection = new PdfProtection
        {
            UserPassword = password ?? "",
            AllowPrint = (permissions & 4) != 0,
            AllowModify = (permissions & 8) != 0,
            AllowCopy = (permissions & 16) != 0,
            AllowAnnotations = (permissions & 32) != 0,
            AllowForms = (permissions & 256) != 0
        };
    }

    // =====================================================================

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _editCommitTimer.Stop();
        _annotationListTimer.Stop();
        _searchCancellation?.Cancel();
        Renderer.Dispose();
        _pdf.Dispose();
    }
}
