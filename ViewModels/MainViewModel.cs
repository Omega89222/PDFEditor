using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PDFEditor.Models;
using PDFEditor.Pdf;
using PDFEditor.Services;

namespace PDFEditor.ViewModels;

public sealed record ColorChoice(Color Color, string Name);

public sealed record StampChoice(string Label, Color Color);

/// <summary>
/// Modele de vue d'une fenetre : document courant, commandes, dialogues,
/// etat de l'interface (panneaux, theme...). Chaque fenetre affiche au plus un
/// document ; ouvrir un second fichier ouvre une nouvelle fenetre, comme sur macOS.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string PdfFilter = "Documents PDF (*.pdf)|*.pdf|Tous les fichiers (*.*)|*.*";
    private const string ImageFilter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Tous les fichiers (*.*)|*.*";

    private static readonly double[] ZoomSteps = { 0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6.5, 8 };

    private static readonly string[] PageScopes =
    {
        "Toutes les pages",
        "Page actuelle",
        "Pages sélectionnées dans la barre latérale"
    };

    public static readonly (string Name, double Width, double Height)[] PageSizes =
    {
        ("A4 (210 × 297 mm)", 595.28, 841.89),
        ("A3 (297 × 420 mm)", 841.89, 1190.55),
        ("A5 (148 × 210 mm)", 419.53, 595.28),
        ("Lettre US (8,5 × 11 po)", 612, 792),
        ("Légal US (8,5 × 14 po)", 612, 1008)
    };

    private readonly IDialogService _dialogs;
    private DocumentViewModel? _document;
    private bool _isBusy;
    private string _busyMessage = "";

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;

        ReloadRecentFiles();
        ReloadSignatures();
        SettingsService.RecentFilesChanged += ReloadRecentFiles;
        SettingsService.SignaturesChanged += ReloadSignatures;
        ThemeManager.Changed += OnThemeChanged;

        PaletteColors = ColorUtil.Palette.Select(p => new ColorChoice(p.Color, p.Name)).ToList();
        StampChoices = Models.StampPresets.All.Select(s => new StampChoice(s.Label, s.Color)).ToList();

        CreateCommands();
    }

    /// <summary>Le document affiche a change (ancien, nouveau).</summary>
    public event Action<DocumentViewModel?, DocumentViewModel?>? DocumentChanged;

    // =====================================================================
    // Etat
    // =====================================================================

    public DocumentViewModel? Document
    {
        get => _document;
        private set
        {
            if (ReferenceEquals(_document, value))
            {
                return;
            }

            var old = _document;
            if (old is not null)
            {
                old.PropertyChanged -= OnDocumentPropertyChanged;
            }

            _document = value;
            if (value is not null)
            {
                value.PropertyChanged += OnDocumentPropertyChanged;
            }

            Raise(nameof(Document), nameof(HasDocument), nameof(WindowTitle));
            DocumentChanged?.Invoke(old, value);
            old?.Dispose();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasDocument => _document is not null;

    public string WindowTitle => _document is null
        ? "Éditeur PDF"
        : _document.DisplayName + (_document.IsModified ? " — Modifié" : "");

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string BusyMessage
    {
        get => _busyMessage;
        private set => Set(ref _busyMessage, value);
    }

    public ObservableCollection<RecentFileItem> RecentFiles { get; } = new();

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public ObservableCollection<SavedSignature> Signatures { get; } = new();

    public IReadOnlyList<ColorChoice> PaletteColors { get; }

    public IReadOnlyList<StampChoice> StampChoices { get; }

    public IReadOnlyList<string> FontFamilies => FontCatalog.Families;

    public IReadOnlyList<double> FontSizes { get; } = new double[] { 8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72, 96 };

    public IReadOnlyList<double> StrokeWidths { get; } = new[] { 0.5, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 24 };

    public bool IsOcrAvailable => OcrService.IsAvailable;

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.IsModified) or nameof(DocumentViewModel.DisplayName))
        {
            Raise(nameof(WindowTitle));
        }
    }

    private void ReloadRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var file in SettingsService.Current.RecentFiles)
        {
            RecentFiles.Add(new RecentFileItem(file));
        }

        Raise(nameof(HasRecentFiles));
    }

    private void ReloadSignatures()
    {
        Signatures.Clear();
        foreach (var signature in SettingsService.Current.Signatures)
        {
            Signatures.Add(signature);
        }
    }

    // ------------------------------------------------------------ interface

    public bool IsSidebarVisible
    {
        get => SettingsService.Current.IsSidebarVisible;
        set => SetSetting(value, SettingsService.Current.IsSidebarVisible, v => SettingsService.Current.IsSidebarVisible = v);
    }

    public bool IsInspectorVisible
    {
        get => SettingsService.Current.IsInspectorVisible;
        set => SetSetting(value, SettingsService.Current.IsInspectorVisible, v => SettingsService.Current.IsInspectorVisible = v);
    }

    public bool IsMarkupBarVisible
    {
        get => SettingsService.Current.IsMarkupBarVisible;
        set => SetSetting(value, SettingsService.Current.IsMarkupBarVisible, v => SettingsService.Current.IsMarkupBarVisible = v);
    }

    public SidebarModeKind SidebarMode
    {
        get => SettingsService.Current.SidebarMode;
        set => SetSetting(value, SettingsService.Current.SidebarMode, v => SettingsService.Current.SidebarMode = v);
    }

    public bool NightMode
    {
        get => SettingsService.Current.NightMode;
        set => SetSetting(value, SettingsService.Current.NightMode, v => SettingsService.Current.NightMode = v);
    }

    public bool HighlightFormFields
    {
        get => SettingsService.Current.HighlightFormFields;
        set
        {
            SetSetting(value, SettingsService.Current.HighlightFormFields, v => SettingsService.Current.HighlightFormFields = v);
            if (_document is not null)
            {
                _document.Pdf.SetFormHighlight(value);
                foreach (var page in _document.Pages)
                {
                    page.InvalidateContent();
                }
            }
        }
    }

    public string AuthorName
    {
        get => SettingsService.Current.AuthorName;
        set => SetSetting(value ?? "", SettingsService.Current.AuthorName, v => SettingsService.Current.AuthorName = v);
    }

    public AppTheme Theme
    {
        get => SettingsService.Current.Theme;
        set
        {
            if (SettingsService.Current.Theme == value)
            {
                return;
            }

            SettingsService.Current.Theme = value;
            SettingsService.SaveSoon();
            ThemeManager.Apply(value);
            Raise(nameof(Theme), nameof(IsDarkTheme));
        }
    }

    public bool IsDarkTheme => ThemeManager.IsDark;

    private void OnThemeChanged() => Raise(nameof(Theme), nameof(IsDarkTheme));

    private void SetSetting<T>(T value, T current, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
        {
            return;
        }

        assign(value);
        SettingsService.SaveSoon();
        Raise(name ?? "");
    }

    private void SetBusy(bool busy, string message = "")
    {
        IsBusy = busy;
        BusyMessage = message;
        Mouse.OverrideCursor = busy ? Cursors.AppStarting : null;
    }

    // =====================================================================
    // Commandes
    // =====================================================================

    public ICommand NewWindowCommand { get; private set; } = null!;
    public ICommand OpenCommand { get; private set; } = null!;
    public ICommand OpenRecentCommand { get; private set; } = null!;
    public ICommand RemoveRecentCommand { get; private set; } = null!;
    public ICommand ClearRecentCommand { get; private set; } = null!;
    public ICommand SaveCommand { get; private set; } = null!;
    public ICommand SaveAsCommand { get; private set; } = null!;
    public ICommand CloseDocumentCommand { get; private set; } = null!;
    public ICommand PrintCommand { get; private set; } = null!;
    public ICommand ExportImagesCommand { get; private set; } = null!;
    public ICommand ExportTextCommand { get; private set; } = null!;
    public ICommand ExtractPagesCommand { get; private set; } = null!;
    public ICommand SplitCommand { get; private set; } = null!;
    public ICommand CreateBlankCommand { get; private set; } = null!;
    public ICommand CreateFromImagesCommand { get; private set; } = null!;
    public ICommand MergeCommand { get; private set; } = null!;
    public ICommand RevealInExplorerCommand { get; private set; } = null!;
    public ICommand CopyPathCommand { get; private set; } = null!;

    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand RedoCommand { get; private set; } = null!;
    public ICommand CopyCommand { get; private set; } = null!;
    public ICommand CutCommand { get; private set; } = null!;
    public ICommand PasteCommand { get; private set; } = null!;
    public ICommand DeleteCommand { get; private set; } = null!;
    public ICommand DuplicateCommand { get; private set; } = null!;
    public ICommand BringToFrontCommand { get; private set; } = null!;
    public ICommand SendToBackCommand { get; private set; } = null!;
    public ICommand SelectAllCommand { get; private set; } = null!;
    public ICommand DeselectCommand { get; private set; } = null!;

    public ICommand ZoomInCommand { get; private set; } = null!;
    public ICommand ZoomOutCommand { get; private set; } = null!;
    public ICommand ActualSizeCommand { get; private set; } = null!;
    public ICommand FitWidthCommand { get; private set; } = null!;
    public ICommand FitPageCommand { get; private set; } = null!;
    public ICommand SetZoomCommand { get; private set; } = null!;
    public ICommand SetScrollModeCommand { get; private set; } = null!;
    public ICommand ToggleSidebarCommand { get; private set; } = null!;
    public ICommand ToggleInspectorCommand { get; private set; } = null!;
    public ICommand ToggleMarkupBarCommand { get; private set; } = null!;
    public ICommand SetSidebarModeCommand { get; private set; } = null!;
    public ICommand ToggleThemeCommand { get; private set; } = null!;
    public ICommand SetThemeCommand { get; private set; } = null!;
    public ICommand ToggleNightModeCommand { get; private set; } = null!;
    public ICommand ToggleFormHighlightCommand { get; private set; } = null!;
    public ICommand NextPageCommand { get; private set; } = null!;
    public ICommand PreviousPageCommand { get; private set; } = null!;
    public ICommand FirstPageCommand { get; private set; } = null!;
    public ICommand LastPageCommand { get; private set; } = null!;
    public ICommand GoToPageCommand { get; private set; } = null!;
    public ICommand ShowPageCommand { get; private set; } = null!;
    public ICommand ShowShortcutsCommand { get; private set; } = null!;
    public ICommand ShowAboutCommand { get; private set; } = null!;
    public ICommand FocusSearchCommand { get; private set; } = null!;
    public ICommand PreferencesCommand { get; private set; } = null!;

    public ICommand SetToolCommand { get; private set; } = null!;
    public ICommand SelectStampCommand { get; private set; } = null!;
    public ICommand CustomStampCommand { get; private set; } = null!;
    public ICommand SelectSignatureCommand { get; private set; } = null!;
    public ICommand CreateSignatureCommand { get; private set; } = null!;
    public ICommand DeleteSignatureCommand { get; private set; } = null!;
    public ICommand InsertImageCommand { get; private set; } = null!;
    public ICommand ApplyMarkupCommand { get; private set; } = null!;
    public ICommand StrokeColorCommand { get; private set; } = null!;
    public ICommand FillColorCommand { get; private set; } = null!;
    public ICommand TextColorCommand { get; private set; } = null!;
    public ICommand StrokeWidthCommand { get; private set; } = null!;
    public ICommand FlattenAnnotationsCommand { get; private set; } = null!;

    public ICommand RotateLeftCommand { get; private set; } = null!;
    public ICommand RotateRightCommand { get; private set; } = null!;
    public ICommand RotateAllCommand { get; private set; } = null!;
    public ICommand DeletePagesCommand { get; private set; } = null!;
    public ICommand InsertBlankPageCommand { get; private set; } = null!;
    public ICommand InsertPagesFromFileCommand { get; private set; } = null!;
    public ICommand InsertImagePagesCommand { get; private set; } = null!;
    public ICommand DuplicatePagesCommand { get; private set; } = null!;
    public ICommand MovePagesUpCommand { get; private set; } = null!;
    public ICommand MovePagesDownCommand { get; private set; } = null!;
    public ICommand CropPagesCommand { get; private set; } = null!;
    public ICommand NUpCommand { get; private set; } = null!;

    public ICommand WatermarkCommand { get; private set; } = null!;
    public ICommand HeaderFooterCommand { get; private set; } = null!;
    public ICommand OcrCommand { get; private set; } = null!;
    public ICommand ProtectCommand { get; private set; } = null!;
    public ICommand RemoveProtectionCommand { get; private set; } = null!;
    public ICommand PropertiesCommand { get; private set; } = null!;

    public ICommand SearchCommand { get; private set; } = null!;
    public ICommand NextResultCommand { get; private set; } = null!;
    public ICommand PreviousResultCommand { get; private set; } = null!;
    public ICommand ClearSearchCommand { get; private set; } = null!;
    public ICommand ShowSearchResultCommand { get; private set; } = null!;
    public ICommand ShowOutlineItemCommand { get; private set; } = null!;
    public ICommand ShowAnnotationCommand { get; private set; } = null!;

    private void CreateCommands()
    {
        bool Doc() => _document is not null && !_isBusy;

        // Fichier
        NewWindowCommand = new RelayCommand(() => _dialogs.OpenNewWindow());
        OpenCommand = new RelayCommand(Open, () => !_isBusy);
        OpenRecentCommand = new RelayCommand(p => { if (p is string path) { _ = OpenPathAsync(path); } });
        RemoveRecentCommand = new RelayCommand(p => { if (p is string path) { SettingsService.RemoveRecent(path); } });
        ClearRecentCommand = new RelayCommand(SettingsService.ClearRecent, () => HasRecentFiles);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(false), Doc);
        SaveAsCommand = new RelayCommand(() => _ = SaveAsync(true), Doc);
        CloseDocumentCommand = new RelayCommand(() => { if (ConfirmDiscardChanges()) { Document = null; } }, Doc);
        PrintCommand = new RelayCommand(() => _dialogs.Print(_document!), Doc);
        ExportImagesCommand = new RelayCommand(() => _ = ExportImagesAsync(), Doc);
        ExportTextCommand = new RelayCommand(ExportText, Doc);
        ExtractPagesCommand = new RelayCommand(() => _ = ExtractPagesAsync(), Doc);
        SplitCommand = new RelayCommand(() => _ = SplitAsync(), () => Doc() && _document!.PageCount > 1);
        CreateBlankCommand = new RelayCommand(CreateBlankDocument);
        CreateFromImagesCommand = new RelayCommand(() => _ = CreateFromImagesAsync());
        MergeCommand = new RelayCommand(MergePdfs, () => !_isBusy);
        RevealInExplorerCommand = new RelayCommand(RevealInExplorer, () => _document?.FilePath is not null);
        CopyPathCommand = new RelayCommand(() => TryClipboard(_document!.FilePath!), () => _document?.FilePath is not null);

        // Edition
        UndoCommand = new RelayCommand(() => _document!.Undo.Undo(), () => Doc() && _document!.Undo.CanUndo);
        RedoCommand = new RelayCommand(() => _document!.Undo.Redo(), () => Doc() && _document!.Undo.CanRedo);
        CopyCommand = new RelayCommand(() => _document!.CopySelection(), () => Doc() && (_document!.HasSelection || _document.HasTextSelection));
        CutCommand = new RelayCommand(() => _document!.CutSelection(), () => Doc() && _document!.HasSelection);
        PasteCommand = new RelayCommand(() => { if (_document?.CurrentPage is { } page) { _document.Paste(page, null); } }, Doc);
        DeleteCommand = new RelayCommand(() => _document!.DeleteSelectedAnnotation(), () => Doc() && _document!.HasSelection);
        DuplicateCommand = new RelayCommand(() => _document!.DuplicateSelectedAnnotation(), () => Doc() && _document!.HasSelection);
        BringToFrontCommand = new RelayCommand(() => _document!.ReorderSelectedAnnotation(true), () => Doc() && _document!.HasSelection);
        SendToBackCommand = new RelayCommand(() => _document!.ReorderSelectedAnnotation(false), () => Doc() && _document!.HasSelection);
        SelectAllCommand = new RelayCommand(() => { if (_document?.CurrentPage is { } page) { _document.SelectAllText(page); } }, Doc);
        DeselectCommand = new RelayCommand(Deselect, Doc);

        // Affichage
        ZoomInCommand = new RelayCommand(() => StepZoom(1), Doc);
        ZoomOutCommand = new RelayCommand(() => StepZoom(-1), Doc);
        ActualSizeCommand = new RelayCommand(() => SetZoom(1), Doc);
        FitWidthCommand = new RelayCommand(() => _document!.ZoomMode = ZoomModeKind.FitWidth, Doc);
        FitPageCommand = new RelayCommand(() => _document!.ZoomMode = ZoomModeKind.FitPage, Doc);
        SetZoomCommand = new RelayCommand(p => SetZoom(ToDouble(p, 1)), _ => Doc());
        SetScrollModeCommand = new RelayCommand(p =>
        {
            if (_document is not null && TryParseEnum<ScrollModeKind>(p, out var mode))
            {
                _document.ScrollMode = mode;
            }
        }, _ => Doc());
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarVisible = !IsSidebarVisible);
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorVisible = !IsInspectorVisible);
        ToggleMarkupBarCommand = new RelayCommand(() => IsMarkupBarVisible = !IsMarkupBarVisible);
        SetSidebarModeCommand = new RelayCommand(p =>
        {
            if (TryParseEnum<SidebarModeKind>(p, out var mode))
            {
                SidebarMode = mode;
                IsSidebarVisible = true;
            }
        });
        ToggleThemeCommand = new RelayCommand(() => Theme = IsDarkTheme ? AppTheme.Light : AppTheme.Dark);
        SetThemeCommand = new RelayCommand(p => { if (TryParseEnum<AppTheme>(p, out var theme)) { Theme = theme; } });
        ToggleNightModeCommand = new RelayCommand(() => NightMode = !NightMode);
        ToggleFormHighlightCommand = new RelayCommand(() => HighlightFormFields = !HighlightFormFields);
        NextPageCommand = new RelayCommand(() => _document!.GoToPage(_document.CurrentPageIndex + 1), () => Doc() && _document!.CurrentPageIndex < _document.PageCount - 1);
        PreviousPageCommand = new RelayCommand(() => _document!.GoToPage(_document.CurrentPageIndex - 1), () => Doc() && _document!.CurrentPageIndex > 0);
        FirstPageCommand = new RelayCommand(() => _document!.GoToPage(0), Doc);
        LastPageCommand = new RelayCommand(() => _document!.GoToPage(_document.PageCount - 1), Doc);
        GoToPageCommand = new RelayCommand(GoToPagePrompt, Doc);
        ShowPageCommand = new RelayCommand(p =>
        {
            switch (p)
            {
                case PageViewModel page:
                    _document?.GoToPage(page.Index);
                    break;
                case int index:
                    _document?.GoToPage(index);
                    break;
            }
        });
        ShowShortcutsCommand = new RelayCommand(() => _dialogs.ShowShortcuts());
        ShowAboutCommand = new RelayCommand(() => _dialogs.ShowAbout());
        FocusSearchCommand = new RelayCommand(() =>
        {
            SidebarMode = SidebarModeKind.Search;
            IsSidebarVisible = true;
            _dialogs.FocusSearch();
        }, Doc);
        PreferencesCommand = new RelayCommand(ShowPreferences);

        // Outils et styles
        SetToolCommand = new RelayCommand(p => SetTool(p), _ => Doc());
        SelectStampCommand = new RelayCommand(p =>
        {
            if (_document is not null && p is string label)
            {
                _document.StampLabel = label;
                _document.Tool = EditorTool.Stamp;
            }
        }, _ => Doc());
        CustomStampCommand = new RelayCommand(CustomStamp, Doc);
        SelectSignatureCommand = new RelayCommand(p =>
        {
            if (_document is not null && p is SavedSignature signature)
            {
                _document.Signature = signature;
                _document.Tool = EditorTool.Signature;
            }
        }, _ => Doc());
        CreateSignatureCommand = new RelayCommand(CreateSignature);
        DeleteSignatureCommand = new RelayCommand(p =>
        {
            if (p is SavedSignature signature)
            {
                SettingsService.RemoveSignature(signature.Id);
                if (ReferenceEquals(_document?.Signature, signature))
                {
                    _document!.Signature = null;
                }
            }
        });
        InsertImageCommand = new RelayCommand(InsertImage, Doc);
        ApplyMarkupCommand = new RelayCommand(p =>
        {
            if (_document is not null && TryParseEnum<MarkupKind>(p, out var kind))
            {
                _document.CreateMarkupFromSelection(kind);
            }
        }, _ => Doc() && _document!.HasTextSelection);
        StrokeColorCommand = new RelayCommand(p => ApplyColor(p, c => _document!.StyleStrokeColor = c), _ => Doc());
        FillColorCommand = new RelayCommand(p => ApplyColor(p, c => _document!.StyleFillColor = c), _ => Doc());
        TextColorCommand = new RelayCommand(p => ApplyColor(p, c => _document!.StyleTextColor = c), _ => Doc());
        StrokeWidthCommand = new RelayCommand(p =>
        {
            if (_document is not null)
            {
                _document.StyleStrokeWidth = ToDouble(p, 2);
            }
        }, _ => Doc());
        FlattenAnnotationsCommand = new RelayCommand(FlattenAnnotations, () => Doc() && _document!.AnnotationCount > 0);

        // Pages
        RotateLeftCommand = new RelayCommand(() => _document!.RotatePages(_document.GetTargetPages(), -1), Doc);
        RotateRightCommand = new RelayCommand(() => _document!.RotatePages(_document.GetTargetPages(), 1), Doc);
        RotateAllCommand = new RelayCommand(p => _document!.RotatePages(Enumerable.Range(0, _document.PageCount).ToList(), (int)ToDouble(p, 1)), _ => Doc());
        DeletePagesCommand = new RelayCommand(DeletePages, () => Doc() && _document!.PageCount > 1);
        InsertBlankPageCommand = new RelayCommand(InsertBlankPages, Doc);
        InsertPagesFromFileCommand = new RelayCommand(InsertPagesFromFile, Doc);
        InsertImagePagesCommand = new RelayCommand(() => _ = InsertImagePagesAsync(), Doc);
        DuplicatePagesCommand = new RelayCommand(() => _document!.DuplicatePages(_document.GetTargetPages()), Doc);
        MovePagesUpCommand = new RelayCommand(() => MoveTargetPages(-1), Doc);
        MovePagesDownCommand = new RelayCommand(() => MoveTargetPages(1), Doc);
        CropPagesCommand = new RelayCommand(CropPages, Doc);
        NUpCommand = new RelayCommand(() => _ = CreateNUpAsync(), Doc);

        // Document
        WatermarkCommand = new RelayCommand(AddWatermark, Doc);
        HeaderFooterCommand = new RelayCommand(AddHeaderFooter, Doc);
        OcrCommand = new RelayCommand(() => _ = RecognizeTextAsync(), Doc);
        ProtectCommand = new RelayCommand(Protect, Doc);
        RemoveProtectionCommand = new RelayCommand(RemoveProtection, () => Doc() && _document!.IsProtected);
        PropertiesCommand = new RelayCommand(() =>
        {
            _document?.Select(null, null);
            IsInspectorVisible = true;
        }, Doc);

        // Recherche et navigation
        SearchCommand = new RelayCommand(p =>
        {
            if (_document is not null)
            {
                _ = _document.SearchAsync(p as string);
            }
        }, _ => Doc());
        NextResultCommand = new RelayCommand(() => _document!.MoveSearch(1), () => Doc() && _document!.HasSearchResults);
        PreviousResultCommand = new RelayCommand(() => _document!.MoveSearch(-1), () => Doc() && _document!.HasSearchResults);
        ClearSearchCommand = new RelayCommand(() => _document?.ClearSearch());
        ShowSearchResultCommand = new RelayCommand(p =>
        {
            if (p is SearchResultViewModel result)
            {
                _document?.ShowSearchResult(result);
            }
        });
        ShowOutlineItemCommand = new RelayCommand(p =>
        {
            if (p is OutlineItemViewModel { PageIndex: >= 0 } item)
            {
                _document?.GoToPage(item.PageIndex);
            }
        });
        ShowAnnotationCommand = new RelayCommand(p =>
        {
            if (_document is not null && p is AnnotationListItem item)
            {
                _document.Tool = EditorTool.Select;
                _document.GoToPage(item.Page.Index, item.Annotation.Bounds);
                _document.Select(item.Page, item.Annotation);
            }
        });
    }

    private static bool TryParseEnum<T>(object? value, out T result)
        where T : struct, Enum
    {
        switch (value)
        {
            case T typed:
                result = typed;
                return true;
            case string text when Enum.TryParse(text, true, out T parsed):
                result = parsed;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static double ToDouble(object? value, double fallback) => value switch
    {
        double d => d,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => fallback
    };

    private void ShowError(string message, Exception ex)
    {
        Debug.WriteLine($"[MainViewModel] {message} {ex}");
        _dialogs.Alert(message, ex.Message, new[] { "OK" }, icon: AlertIcon.Error);
    }

    private static void TryClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Presse-papiers occupe : sans gravite.
        }
    }

    // =====================================================================
    // Fichiers
    // =====================================================================

    private void Open()
    {
        var path = _dialogs.OpenFile("Ouvrir un document PDF", PdfFilter);
        if (path is not null)
        {
            _ = OpenPathAsync(path);
        }
    }

    /// <summary>Ouvre un fichier ; si un document est deja affiche, dans une nouvelle fenetre.</summary>
    public async Task<bool> OpenPathAsync(string path, bool inThisWindow = false)
    {
        if (_document is not null && !inThisWindow)
        {
            if (!string.Equals(_document.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _dialogs.OpenPathInNewWindow(path);
            }

            return true;
        }

        if (!File.Exists(path))
        {
            _dialogs.Alert("Fichier introuvable", $"« {Path.GetFileName(path)} » n’existe plus ou a été déplacé.", new[] { "OK" }, icon: AlertIcon.Warning);
            SettingsService.RemoveRecent(path);
            return false;
        }

        string? password = null;
        while (true)
        {
            try
            {
                SetBusy(true, "Ouverture…");
                var attempt = password;
                var (pdf, size) = await Task.Run(() =>
                {
                    var bytes = File.ReadAllBytes(path);
                    return (PdfDoc.Open(bytes, attempt), (long)bytes.Length);
                });

                var document = DocumentViewModel.FromFile(pdf, path, password, size);
                Document = document;
                SettingsService.AddRecent(path, document.PageCount);
                return true;
            }
            catch (PdfPasswordException ex)
            {
                SetBusy(false);
                password = _dialogs.Prompt(
                    ex.PasswordWasSupplied ? "Mot de passe incorrect" : "Document protégé",
                    $"« {Path.GetFileName(path)} » est protégé par un mot de passe. Saisissez-le pour ouvrir le document.",
                    "",
                    "Mot de passe",
                    password: true,
                    confirm: "Ouvrir");

                if (password is null)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                SetBusy(false);
                ShowError($"Impossible d’ouvrir « {Path.GetFileName(path)} ».", ex);
                return false;
            }
            finally
            {
                SetBusy(false);
            }
        }
    }

    /// <summary>Affiche un document deja construit (fenetre neuve).</summary>
    public void AttachDocument(DocumentViewModel document) => Document = document;

    private void ShowDocument(DocumentViewModel document)
    {
        if (_document is null)
        {
            Document = document;
        }
        else
        {
            _dialogs.OpenInNewWindow(document);
        }
    }

    private PdfDoc? LoadPdfWithPassword(string path)
    {
        string? password = null;
        while (true)
        {
            try
            {
                return PdfDoc.Open(File.ReadAllBytes(path), password);
            }
            catch (PdfPasswordException ex)
            {
                password = _dialogs.Prompt(
                    ex.PasswordWasSupplied ? "Mot de passe incorrect" : "Document protégé",
                    $"« {Path.GetFileName(path)} » est protégé par un mot de passe.",
                    "",
                    "Mot de passe",
                    password: true,
                    confirm: "Ouvrir");

                if (password is null)
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                ShowError($"Impossible de lire « {Path.GetFileName(path)} ».", ex);
                return null;
            }
        }
    }

    /// <summary>Insere les pages de plusieurs PDF (glisser-deposer) a partir de la position donnee.</summary>
    public void InsertPdfFiles(IReadOnlyList<string> files, int at)
    {
        var document = _document;
        if (document is null)
        {
            return;
        }

        foreach (var file in files)
        {
            using var source = LoadPdfWithPassword(file);
            if (source is null)
            {
                continue;
            }

            try
            {
                at += document.InsertPagesFrom(source, Math.Clamp(at, 0, document.PageCount));
            }
            catch (Exception ex)
            {
                ShowError($"« {Path.GetFileName(file)} » n’a pas pu être inséré.", ex);
            }
        }
    }

    /// <summary>Demande quoi faire des modifications non enregistrees. Vrai si l'on peut fermer.</summary>
    public bool ConfirmDiscardChanges()
    {
        var document = _document;
        if (document is null || !document.IsModified)
        {
            return true;
        }

        var choice = _dialogs.Alert(
            $"Voulez-vous enregistrer les modifications apportées à « {document.DisplayName} » ?",
            "Vos modifications seront perdues si vous ne les enregistrez pas.",
            new[] { "Enregistrer", "Ne pas enregistrer", "Annuler" },
            defaultButton: 0,
            cancelButton: 2,
            destructiveButton: 1,
            icon: AlertIcon.Warning);

        return choice switch
        {
            0 => SaveSync(),
            1 => true,
            _ => false
        };
    }

    private string? ResolveSavePath(DocumentViewModel document, bool saveAs)
    {
        if (!saveAs && document.FilePath is not null)
        {
            return document.FilePath;
        }

        return _dialogs.SaveFile(
            saveAs ? "Enregistrer une copie sous" : "Enregistrer le document",
            "Document PDF (*.pdf)|*.pdf",
            document.DisplayName,
            document.FolderPath.Length > 0 ? document.FolderPath : null);
    }

    private bool SaveSync()
    {
        var document = _document;
        if (document is null)
        {
            return false;
        }

        var path = ResolveSavePath(document, false);
        if (path is null)
        {
            return false;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            document.SaveTo(path);
            return true;
        }
        catch (Exception ex)
        {
            ShowError("L’enregistrement a échoué.", ex);
            return false;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    public async Task<bool> SaveAsync(bool saveAs)
    {
        var document = _document;
        if (document is null || _isBusy)
        {
            return false;
        }

        var path = ResolveSavePath(document, saveAs);
        if (path is null)
        {
            return false;
        }

        try
        {
            SetBusy(true, "Enregistrement…");
            await document.SaveToAsync(path);
            return true;
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowError("L’enregistrement a échoué.", ex);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RevealInExplorer()
    {
        if (_document?.FilePath is not { } path)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("Impossible d’afficher le fichier dans l’Explorateur.", ex);
        }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch
        {
            // Sans gravite.
        }
    }

    private void CreateBlankDocument()
    {
        var spec = new FormSpec("Nouveau document")
            {
                ConfirmLabel = "Créer",
                Width = 400
            }
            .Choice("size", "Format", PageSizes.Select(s => s.Name).ToList())
            .Choice("orientation", "Orientation", new[] { "Portrait", "Paysage" })
            .Number("count", "Nombre de pages", 1, 1, 500);

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        var (_, width, height) = PageSizes[Math.Clamp(result.Index("size"), 0, PageSizes.Length - 1)];
        if (result.Index("orientation") == 1)
        {
            (width, height) = (height, width);
        }

        var pdf = PdfDoc.CreateEmpty();
        var count = (int)Math.Clamp(result.Number("count"), 1, 500);
        for (var i = 0; i < count; i++)
        {
            pdf.InsertBlankPage(i, width, height);
        }

        ShowDocument(DocumentViewModel.FromUntitled(pdf, "Sans titre.pdf"));
    }

    private FormResult? AskImageLayout(int count)
    {
        var spec = new FormSpec(count == 1 ? "Créer un PDF à partir d’une image" : $"Créer un PDF à partir de {count} images")
            {
                ConfirmLabel = "Créer",
                Width = 420
            }
            .Choice("layout", "Mise en page", new[] { "Ajuster sur une page A4 (marges de 1 cm)", "Page à la taille de l’image (150 ppp)" });

        return _dialogs.ShowForm(spec);
    }

    private static PdfDoc BuildPdfFromImages(IReadOnlyList<string> paths, bool fitA4, IProgressReporter? progress)
    {
        var pdf = PdfDoc.CreateEmpty();
        var index = 0;

        for (var k = 0; k < paths.Count; k++)
        {
            if (progress?.Token.IsCancellationRequested == true)
            {
                break;
            }

            progress?.Report((double)k / paths.Count, $"Image {k + 1} sur {paths.Count}…");

            DecodedImage image;
            try
            {
                image = ImageTools.Decode(File.ReadAllBytes(paths[k]), 4000);
            }
            catch
            {
                continue;
            }

            double pageWidth, pageHeight;
            Rect area;
            if (fitA4)
            {
                var landscape = image.Width > image.Height;
                pageWidth = landscape ? 841.89 : 595.28;
                pageHeight = landscape ? 595.28 : 841.89;
                const double margin = 28.35;
                var scale = Math.Min((pageWidth - 2 * margin) / image.Width, (pageHeight - 2 * margin) / image.Height);
                var w = image.Width * scale;
                var h = image.Height * scale;
                area = new Rect((pageWidth - w) / 2, (pageHeight - h) / 2, w, h);
            }
            else
            {
                pageWidth = Math.Clamp(image.Width * 72.0 / 150, 36, 14400);
                pageHeight = Math.Clamp(image.Height * 72.0 / 150, 36, 14400);
                area = new Rect(0, 0, pageWidth, pageHeight);
            }

            pdf.InsertBlankPage(index, pageWidth, pageHeight);
            var captured = image;
            var target = area;
            pdf.EditPage(index, editor => editor.AddImage(captured, target));
            index++;
        }

        progress?.Report(1);
        return pdf;
    }

    private async Task CreateFromImagesAsync()
    {
        var files = _dialogs.OpenFiles("Choisir des images", ImageFilter);
        if (files.Length == 0)
        {
            return;
        }

        var layout = AskImageLayout(files.Length);
        if (layout is null)
        {
            return;
        }

        var fitA4 = layout.Index("layout") == 0;
        PdfDoc pdf;
        using (var progress = _dialogs.BeginProgress("Création du document", "Préparation…", true))
        {
            pdf = await Task.Run(() => BuildPdfFromImages(files, fitA4, progress));
        }

        if (pdf.PageCount == 0)
        {
            pdf.Dispose();
            _dialogs.Alert("Aucune image lisible", "Les fichiers choisis n’ont pas pu être lus comme des images.", new[] { "OK" }, icon: AlertIcon.Warning);
            return;
        }

        ShowDocument(DocumentViewModel.FromUntitled(pdf, files.Length == 1 ? Path.GetFileNameWithoutExtension(files[0]) + ".pdf" : "Images.pdf"));
    }

    private void MergePdfs()
    {
        var files = _dialogs.OpenFiles("Fusionner des documents PDF", PdfFilter);
        if (files.Length == 0)
        {
            return;
        }

        if (_document is null)
        {
            var merged = PdfDoc.CreateEmpty();
            foreach (var file in files)
            {
                using var source = LoadPdfWithPassword(file);
                if (source is not null)
                {
                    merged.ImportPages(source, Enumerable.Range(0, source.PageCount).ToList(), merged.PageCount);
                }
            }

            if (merged.PageCount == 0)
            {
                merged.Dispose();
                return;
            }

            Document = DocumentViewModel.FromUntitled(merged, "Fusion.pdf");
            return;
        }

        var at = _document.PageCount;
        foreach (var file in files)
        {
            using var source = LoadPdfWithPassword(file);
            if (source is null)
            {
                continue;
            }

            try
            {
                at += _document.InsertPagesFrom(source, at);
            }
            catch (Exception ex)
            {
                ShowError($"« {Path.GetFileName(file)} » n’a pas pu être ajouté.", ex);
            }
        }
    }

    // =====================================================================
    // Exports
    // =====================================================================

    private List<int>? AskPageScope(FormResult result, DocumentViewModel document, string key = "pages") =>
        result.Index(key) switch
        {
            1 => new List<int> { document.CurrentPageIndex },
            2 => document.GetTargetPages(),
            _ => Enumerable.Range(0, document.PageCount).ToList()
        };

    private async Task ExportImagesAsync()
    {
        var document = _document!;
        var spec = new FormSpec("Exporter en images")
            {
                ConfirmLabel = "Exporter…",
                Width = 420
            }
            .Choice("format", "Format", new[] { "PNG (sans perte)", "JPEG (plus léger)" })
            .Choice("dpi", "Résolution", new[] { "72 ppp (écran)", "150 ppp", "200 ppp", "300 ppp (impression)" }, 1)
            .Choice("pages", "Pages", PageScopes);

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        var folder = _dialogs.PickFolder("Choisir le dossier d’export");
        if (folder is null)
        {
            return;
        }

        var pages = AskPageScope(result, document)!;
        var png = result.Index("format") == 0;
        var dpi = result.Index("dpi") switch { 0 => 72, 2 => 200, 3 => 300, _ => 150 };
        var baseName = Path.GetFileNameWithoutExtension(document.DisplayName);

        try
        {
            using var flat = await document.CreateFlattenedCopyAsync();
            using (var progress = _dialogs.BeginProgress("Export en images", "Préparation…", true))
            {
                await Task.Run(() => DocumentViewModel.ExportImages(flat, folder, baseName, pages, png, dpi, progress));
            }

            var choice = _dialogs.Alert("Export terminé", $"{pages.Count} image{(pages.Count > 1 ? "s" : "")} enregistrée{(pages.Count > 1 ? "s" : "")} dans « {Path.GetFileName(folder)} ».", new[] { "Afficher le dossier", "OK" }, 1, 1);
            if (choice == 0)
            {
                OpenFolder(folder);
            }
        }
        catch (Exception ex)
        {
            ShowError("L’export en images a échoué.", ex);
        }
    }

    private void ExportText()
    {
        var document = _document!;
        var path = _dialogs.SaveFile("Exporter le texte", "Texte brut (*.txt)|*.txt", Path.GetFileNameWithoutExtension(document.DisplayName) + ".txt", document.FolderPath.Length > 0 ? document.FolderPath : null);
        if (path is null)
        {
            return;
        }

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var text = DocumentViewModel.ExtractText(document.Pdf);
            File.WriteAllText(path, text, new UTF8Encoding(true));
            if (text.Trim().Length == 0)
            {
                _dialogs.Alert("Aucun texte trouvé", "Ce document ne contient pas de texte sélectionnable. S’il s’agit d’un scan, utilisez d’abord la reconnaissance de texte (OCR).", new[] { "OK" }, icon: AlertIcon.Info);
            }
        }
        catch (Exception ex)
        {
            ShowError("L’export du texte a échoué.", ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async Task ExtractPagesAsync()
    {
        var document = _document!;
        var targets = document.GetTargetPages();
        var defaultRange = targets.Count == 1 ? $"{targets[0] + 1}" : string.Join(", ", targets.Select(i => (i + 1).ToString(CultureInfo.CurrentCulture)));

        var spec = new FormSpec("Extraire des pages")
            {
                ConfirmLabel = "Extraire…",
                Message = "Les pages choisies sont enregistrées dans un nouveau document PDF, avec leurs annotations.",
                Validate = r => DocumentViewModel.ParseRanges(r.String("pages"), document.PageCount) is null
                    ? $"Indiquez des pages entre 1 et {document.PageCount} (ex. : 1-3, 5)."
                    : null
            }
            .Text("pages", "Pages", defaultRange, "Ex. : 1-3, 5");

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        var pages = DocumentViewModel.ParseRanges(result.String("pages"), document.PageCount)!.SelectMany(g => g).Distinct().ToList();
        var path = _dialogs.SaveFile("Enregistrer les pages extraites", "Document PDF (*.pdf)|*.pdf", Path.GetFileNameWithoutExtension(document.DisplayName) + " (extrait).pdf", document.FolderPath.Length > 0 ? document.FolderPath : null);
        if (path is null)
        {
            return;
        }

        try
        {
            SetBusy(true, "Extraction…");
            using var flat = await document.CreateFlattenedCopyAsync();
            await Task.Run(() => DocumentViewModel.SavePages(flat, pages, path));
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowError("L’extraction a échoué.", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SplitAsync()
    {
        var document = _document!;
        var spec = new FormSpec("Diviser le document")
            {
                ConfirmLabel = "Diviser…",
                Width = 460,
                Validate = r => r.Index("mode") == 1 && DocumentViewModel.ParseRanges(r.String("ranges"), document.PageCount) is null
                    ? $"Plages invalides : utilisez des numéros entre 1 et {document.PageCount}."
                    : null
            }
            .Choice("mode", "Méthode", new[] { "Toutes les N pages", "Selon des plages (un fichier par plage)", "Une page par fichier" })
            .Number("every", "Pages par fichier", Math.Min(2, document.PageCount), 1, Math.Max(1, document.PageCount))
            .Text("ranges", "Plages", $"1-{Math.Max(1, document.PageCount / 2)}, {Math.Min(document.PageCount, document.PageCount / 2 + 1)}-{document.PageCount}", "Ex. : 1-3, 4-10, 11");

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        var groups = new List<IReadOnlyList<int>>();
        switch (result.Index("mode"))
        {
            case 1:
                groups.AddRange(DocumentViewModel.ParseRanges(result.String("ranges"), document.PageCount)!);
                break;
            case 2:
                groups.AddRange(Enumerable.Range(0, document.PageCount).Select(i => (IReadOnlyList<int>)new List<int> { i }));
                break;
            default:
                var every = (int)Math.Max(1, result.Number("every"));
                for (var start = 0; start < document.PageCount; start += every)
                {
                    groups.Add(Enumerable.Range(start, Math.Min(every, document.PageCount - start)).ToList());
                }

                break;
        }

        var folder = _dialogs.PickFolder("Choisir le dossier de destination");
        if (folder is null)
        {
            return;
        }

        try
        {
            using var flat = await document.CreateFlattenedCopyAsync();
            int written;
            using (var progress = _dialogs.BeginProgress("Division du document", "Préparation…", true))
            {
                written = await Task.Run(() => DocumentViewModel.Split(flat, groups, folder, Path.GetFileNameWithoutExtension(document.DisplayName), progress));
            }

            var choice = _dialogs.Alert("Division terminée", $"{written} fichier{(written > 1 ? "s" : "")} PDF créé{(written > 1 ? "s" : "")}.", new[] { "Afficher le dossier", "OK" }, 1, 1);
            if (choice == 0)
            {
                OpenFolder(folder);
            }
        }
        catch (Exception ex)
        {
            ShowError("La division a échoué.", ex);
        }
    }

    // =====================================================================
    // Affichage
    // =====================================================================

    private void StepZoom(int direction)
    {
        if (_document is null)
        {
            return;
        }

        var zoom = _document.Zoom;
        var next = direction > 0
            ? ZoomSteps.FirstOrDefault(s => s > zoom * 1.01, DocumentViewModel.MaxZoom)
            : ZoomSteps.LastOrDefault(s => s < zoom / 1.01, DocumentViewModel.MinZoom);
        SetZoom(next);
    }

    private void SetZoom(double zoom)
    {
        if (_document is null)
        {
            return;
        }

        _document.ZoomMode = ZoomModeKind.Custom;
        _document.Zoom = zoom;
    }

    private void GoToPagePrompt()
    {
        var document = _document!;
        var answer = _dialogs.Prompt("Aller à la page", $"Saisissez un numéro de page entre 1 et {document.PageCount}.", document.CurrentPageNumber.ToString(CultureInfo.CurrentCulture), confirm: "Aller");
        if (answer is not null && int.TryParse(answer.Trim(), out var number))
        {
            document.GoToPage(number - 1);
        }
    }

    private void ShowPreferences()
    {
        var spec = new FormSpec("Réglages")
            {
                ConfirmLabel = "Enregistrer",
                Width = 440
            }
            .Choice("theme", "Apparence", new[] { "Claire", "Sombre", "Selon Windows" }, (int)Theme)
            .Text("author", "Nom de l’auteur des notes", AuthorName)
            .Choice("zoom", "Zoom à l’ouverture", new[] { "Ajuster à la largeur", "Page entière", "Taille réelle" },
                SettingsService.Current.DefaultZoomMode switch { ZoomModeKind.FitPage => 1, ZoomModeKind.Custom => 2, _ => 0 })
            .Switch("highlight", "Surligner les champs de formulaire", HighlightFormFields)
            .Switch("night", "Mode lecture nuit (pages inversées)", NightMode)
            .Separator()
            .Check("resetStyles", "Rétablir les styles d’annotation par défaut", false);

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        Theme = (AppTheme)Math.Clamp(result.Index("theme"), 0, 2);
        AuthorName = result.String("author").Trim();
        SettingsService.Current.DefaultZoomMode = result.Index("zoom") switch { 1 => ZoomModeKind.FitPage, 2 => ZoomModeKind.Custom, _ => ZoomModeKind.FitWidth };
        HighlightFormFields = result.Bool("highlight");
        NightMode = result.Bool("night");
        if (result.Bool("resetStyles"))
        {
            SettingsService.ResetToolStyles();
            _document?.RaiseStyle();
        }

        SettingsService.SaveSoon();
    }

    // =====================================================================
    // Outils
    // =====================================================================

    private void SetTool(object? parameter)
    {
        var document = _document;
        if (document is null || !TryParseEnum<EditorTool>(parameter, out var tool))
        {
            return;
        }

        switch (tool)
        {
            case EditorTool.Image:
                InsertImage();
                return;

            case EditorTool.Signature when document.Signature is null:
                if (Signatures.Count > 0)
                {
                    document.Signature = Signatures[0];
                }
                else
                {
                    CreateSignature();
                    if (document.Signature is null)
                    {
                        return;
                    }
                }

                break;

            case EditorTool.TextHighlight or EditorTool.TextUnderline or EditorTool.TextStrike or EditorTool.TextSquiggly
                when document.HasTextSelection:
                // Texte deja selectionne : on applique directement.
                document.CreateMarkupFromSelection(tool switch
                {
                    EditorTool.TextUnderline => MarkupKind.Underline,
                    EditorTool.TextStrike => MarkupKind.StrikeOut,
                    EditorTool.TextSquiggly => MarkupKind.Squiggly,
                    _ => MarkupKind.Highlight
                });
                break;
        }

        document.Tool = document.Tool == tool && tool != EditorTool.Select ? EditorTool.Select : tool;
    }

    private void Deselect()
    {
        if (_document is null)
        {
            return;
        }

        _document.Select(null, null);
        _document.ClearTextSelection();
        _document.Tool = EditorTool.Select;
    }

    private void CustomStamp()
    {
        var label = _dialogs.Prompt("Tampon personnalisé", "Texte du tampon :", "", $"Ex. : REÇU LE {DateTime.Now:dd/MM/yyyy}", confirm: "Utiliser");
        if (string.IsNullOrWhiteSpace(label) || _document is null)
        {
            return;
        }

        _document.StampLabel = label.Trim().ToUpper(CultureInfo.CurrentCulture);
        _document.Tool = EditorTool.Stamp;
    }

    private void CreateSignature()
    {
        var signature = _dialogs.CreateSignature();
        if (signature is null)
        {
            return;
        }

        SettingsService.AddSignature(signature);
        if (_document is not null)
        {
            _document.Signature = signature;
            _document.Tool = EditorTool.Signature;
        }
    }

    private void InsertImage()
    {
        var document = _document;
        if (document?.CurrentPage is not { } page)
        {
            return;
        }

        var path = _dialogs.OpenFile("Insérer une image", ImageFilter);
        if (path is null)
        {
            return;
        }

        try
        {
            var image = document.CreateImage(page, File.ReadAllBytes(path));
            if (image is null)
            {
                _dialogs.Alert("Image illisible", $"« {Path.GetFileName(path)} » n’a pas pu être lue.", new[] { "OK" }, icon: AlertIcon.Warning);
                return;
            }

            document.Tool = EditorTool.Select;
            document.AddAnnotation(page, image);
        }
        catch (Exception ex)
        {
            ShowError("L’image n’a pas pu être insérée.", ex);
        }
    }

    private void ApplyColor(object? parameter, Action<Color> apply)
    {
        if (_document is null)
        {
            return;
        }

        Color? color = parameter switch
        {
            Color c => c,
            ColorChoice choice => choice.Color,
            string text when ColorUtil.TryParse(text, out var parsed) => parsed,
            _ => null
        };

        if (color is { } value)
        {
            apply(value);
            if (value.A > 0)
            {
                SettingsService.AddRecentColor(value);
            }
        }
    }

    private void FlattenAnnotations()
    {
        var choice = _dialogs.Alert(
            "Intégrer les annotations au document ?",
            "Elles deviendront une partie des pages et ne pourront plus être déplacées ni modifiées. L’opération peut être annulée tant que le document est ouvert.",
            new[] { "Intégrer", "Annuler" },
            0,
            1,
            icon: AlertIcon.Question);

        if (choice == 0)
        {
            RunSafely("L’intégration des annotations a échoué.", () => _document!.FlattenAnnotations());
        }
    }

    private void RunSafely(string failure, Action action)
    {
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            action();
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            ShowError(failure, ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // =====================================================================
    // Pages
    // =====================================================================

    private void DeletePages()
    {
        var document = _document!;
        var pages = document.GetTargetPages();
        if (pages.Count == 0)
        {
            return;
        }

        if (pages.Count >= document.PageCount)
        {
            _dialogs.Alert("Impossible de supprimer toutes les pages", "Un document PDF doit contenir au moins une page.", new[] { "OK" }, icon: AlertIcon.Warning);
            return;
        }

        if (pages.Count > 1 || document.Pages[pages[0]].HasAnnotations)
        {
            var choice = _dialogs.Alert(
                pages.Count == 1 ? $"Supprimer la page {pages[0] + 1} ?" : $"Supprimer {pages.Count} pages ?",
                "Vous pourrez annuler cette opération avec Ctrl + Z.",
                new[] { "Supprimer", "Annuler" },
                1,
                1,
                0,
                AlertIcon.Warning);

            if (choice != 0)
            {
                return;
            }
        }

        RunSafely("La suppression a échoué.", () => document.DeletePages(pages));
    }

    private void InsertBlankPages()
    {
        var document = _document!;
        var current = document.CurrentPage;
        var sizeNames = new List<string> { current is null ? "Identique à la page actuelle" : $"Identique à la page actuelle ({current.SizeLabel})" };
        sizeNames.AddRange(PageSizes.Select(s => s.Name));

        var spec = new FormSpec("Insérer des pages vierges")
            {
                ConfirmLabel = "Insérer",
                Width = 440
            }
            .Choice("position", "Position", new[] { "Après la page actuelle", "Avant la page actuelle", "Au début du document", "À la fin du document" })
            .Choice("size", "Format", sizeNames)
            .Choice("orientation", "Orientation", new[] { "Portrait", "Paysage" })
            .Number("count", "Nombre de pages", 1, 1, 500);

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        double width, height;
        var sizeIndex = result.Index("size");
        if (sizeIndex == 0 && current is not null)
        {
            (width, height) = (current.Width, current.Height);
        }
        else
        {
            var size = PageSizes[Math.Clamp(sizeIndex - 1, 0, PageSizes.Length - 1)];
            (width, height) = (size.Width, size.Height);
            if (result.Index("orientation") == 1)
            {
                (width, height) = (height, width);
            }
        }

        var at = result.Index("position") switch
        {
            1 => document.CurrentPageIndex,
            2 => 0,
            3 => document.PageCount,
            _ => document.CurrentPageIndex + 1
        };

        RunSafely("L’insertion a échoué.", () => document.InsertBlankPages(at, width, height, (int)result.Number("count")));
    }

    private void InsertPagesFromFile()
    {
        var document = _document!;
        var path = _dialogs.OpenFile("Insérer des pages depuis un PDF", PdfFilter);
        if (path is null)
        {
            return;
        }

        using var source = LoadPdfWithPassword(path);
        if (source is null)
        {
            return;
        }

        var count = source.PageCount;
        var spec = new FormSpec("Insérer des pages")
            {
                ConfirmLabel = "Insérer",
                Message = $"« {Path.GetFileName(path)} » contient {count} page{(count > 1 ? "s" : "")}.",
                Validate = r => DocumentViewModel.ParseRanges(r.String("pages"), count) is null
                    ? $"Indiquez des pages entre 1 et {count}."
                    : null
            }
            .Text("pages", "Pages à insérer", count == 1 ? "1" : $"1-{count}", "Ex. : 1-3, 5")
            .Choice("position", "Position", new[] { "Après la page actuelle", "Avant la page actuelle", "Au début du document", "À la fin du document" });

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        var indices = DocumentViewModel.ParseRanges(result.String("pages"), count)!.SelectMany(g => g).ToList();
        var at = result.Index("position") switch
        {
            1 => document.CurrentPageIndex,
            2 => 0,
            3 => document.PageCount,
            _ => document.CurrentPageIndex + 1
        };

        RunSafely("L’insertion a échoué.", () => document.InsertPagesFrom(source, at, indices));
    }

    private async Task InsertImagePagesAsync()
    {
        var document = _document!;
        var files = _dialogs.OpenFiles("Insérer des images comme pages", ImageFilter);
        if (files.Length == 0)
        {
            return;
        }

        var layout = AskImageLayout(files.Length);
        if (layout is null)
        {
            return;
        }

        var fitA4 = layout.Index("layout") == 0;
        try
        {
            PdfDoc pdf;
            using (var progress = _dialogs.BeginProgress("Insertion d’images", "Préparation…", true))
            {
                pdf = await Task.Run(() => BuildPdfFromImages(files, fitA4, progress));
            }

            using (pdf)
            {
                if (pdf.PageCount > 0)
                {
                    document.InsertPagesFrom(pdf, document.CurrentPageIndex + 1);
                }
            }
        }
        catch (Exception ex)
        {
            ShowError("L’insertion des images a échoué.", ex);
        }
    }

    private void MoveTargetPages(int direction)
    {
        var document = _document!;
        var pages = document.GetTargetPages();
        if (pages.Count == 0)
        {
            return;
        }

        var insertBefore = direction < 0 ? pages.Min() - 1 : pages.Max() + 2;
        if (insertBefore < 0 || insertBefore > document.PageCount)
        {
            return;
        }

        var selected = pages.Select(i => document.Pages[i]).ToList();
        RunSafely("Le déplacement a échoué.", () => document.MovePages(pages, insertBefore));
        foreach (var page in selected)
        {
            page.IsSelected = true;
        }
    }

    private void CropPages()
    {
        var document = _document!;
        var spec = new FormSpec("Recadrer les pages")
            {
                ConfirmLabel = "Recadrer",
                Width = 440,
                Message = "Retire les marges indiquées. Pour recadrer sur une zone précise, utilisez l’outil Sélection rectangulaire."
            }
            .Number("left", "Marge gauche", 10, 0, 300, 1, 0, "mm")
            .Number("top", "Marge haute", 10, 0, 300, 1, 0, "mm")
            .Number("right", "Marge droite", 10, 0, 300, 1, 0, "mm")
            .Number("bottom", "Marge basse", 10, 0, 300, 1, 0, "mm")
            .Choice("pages", "Pages", PageScopes);

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        const double mm = 72 / 25.4;
        var pages = AskPageScope(result, document)!;
        RunSafely("Le recadrage a échoué.", () => document.TrimMargins(pages,
            result.Number("left") * mm, result.Number("top") * mm, result.Number("right") * mm, result.Number("bottom") * mm));
    }

    private async Task CreateNUpAsync()
    {
        var document = _document!;
        var spec = new FormSpec("Pages par feuille")
            {
                ConfirmLabel = "Créer",
                Width = 420,
                Message = "Crée un nouveau document qui regroupe plusieurs pages sur chaque feuille (idéal pour imprimer)."
            }
            .Choice("layout", "Disposition", new[] { "2 pages par feuille", "4 pages par feuille", "6 pages par feuille", "9 pages par feuille", "16 pages par feuille" })
            .Choice("size", "Format de la feuille", PageSizes.Select(s => s.Name).ToList());

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        var (columns, rows, landscape) = result.Index("layout") switch
        {
            1 => (2, 2, false),
            2 => (3, 2, true),
            3 => (3, 3, false),
            4 => (4, 4, false),
            _ => (2, 1, true)
        };

        var size = PageSizes[Math.Clamp(result.Index("size"), 0, PageSizes.Length - 1)];
        var (width, height) = landscape ? (size.Height, size.Width) : (size.Width, size.Height);

        try
        {
            SetBusy(true, "Création…");
            using var flat = await document.CreateFlattenedCopyAsync();
            var nup = await Task.Run(() => flat.CreateNUp(width, height, columns, rows));
            SetBusy(false);

            if (nup is null)
            {
                _dialogs.Alert("Création impossible", "Le document n’a pas pu être assemblé.", new[] { "OK" }, icon: AlertIcon.Warning);
                return;
            }

            ShowDocument(DocumentViewModel.FromUntitled(nup, Path.GetFileNameWithoutExtension(document.DisplayName) + $" ({columns * rows} par feuille).pdf"));
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowError("La création a échoué.", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // =====================================================================
    // Document : filigrane, numerotation, OCR, protection
    // =====================================================================

    private int FamilyIndex(string family)
    {
        var index = FontFamilies.ToList().FindIndex(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }

    private void AddWatermark()
    {
        var document = _document!;
        var spec = new FormSpec("Ajouter un filigrane")
            {
                ConfirmLabel = "Appliquer",
                Width = 460,
                Message = "Le texte est ajouté au contenu des pages. L’opération peut être annulée."
            }
            .Text("text", "Texte", "CONFIDENTIEL")
            .Choice("font", "Police", FontFamilies, FamilyIndex("Arial"))
            .Check("bold", "Gras", true)
            .Number("size", "Taille", 80, 8, 400, 1, 0, "pt")
            .ColorPick("color", "Couleur", Color.FromRgb(0xFF, 0x3B, 0x30))
            .Slider("opacity", "Opacité", 18, 2, 100, "%")
            .Choice("angle", "Orientation", new[] { "Diagonale montante (45°)", "Horizontale", "Diagonale descendante (−45°)", "Verticale (90°)" })
            .Choice("pages", "Pages", PageScopes);

        var result = _dialogs.ShowForm(spec);
        if (result is null || string.IsNullOrWhiteSpace(result.String("text")))
        {
            return;
        }

        var options = new WatermarkOptions(
            result.String("text").Trim(),
            FontFamilies[Math.Clamp(result.Index("font"), 0, FontFamilies.Count - 1)],
            result.Bool("bold"),
            result.Number("size"),
            result.Color("color"),
            result.Number("opacity") / 100.0,
            result.Index("angle") switch { 1 => 0, 2 => -45, 3 => 90, _ => 45 });

        var pages = AskPageScope(result, document)!;
        RunSafely("Le filigrane n’a pas pu être ajouté.", () => document.ApplyWatermark(options, pages));
    }

    private void AddHeaderFooter()
    {
        var document = _document!;
        var spec = new FormSpec("Numéroter les pages")
            {
                ConfirmLabel = "Appliquer",
                Width = 480,
                Message = "Variables disponibles : {n} numéro de page, {total} nombre de pages, {date} date du jour, {fichier} nom du document."
            }
            .Text("format", "Texte", "Page {n} sur {total}")
            .Choice("position", "Position", new[] { "En haut à gauche", "En haut au centre", "En haut à droite", "En bas à gauche", "En bas au centre", "En bas à droite" }, 4)
            .Number("start", "Premier numéro", 1, 0, 100000)
            .Choice("font", "Police", FontFamilies, FamilyIndex("Arial"))
            .Number("size", "Taille", 10, 5, 72, 0.5, 1, "pt")
            .ColorPick("color", "Couleur", Color.FromRgb(0x3A, 0x3A, 0x3C))
            .Number("margin", "Marge", 12, 0, 80, 1, 0, "mm")
            .Check("skipFirst", "Ne pas numéroter la première page", false)
            .Choice("pages", "Pages", PageScopes);

        var result = _dialogs.ShowForm(spec);
        if (result is null || string.IsNullOrWhiteSpace(result.String("format")))
        {
            return;
        }

        var options = new HeaderFooterOptions(
            result.String("format"),
            (HeaderFooterPosition)Math.Clamp(result.Index("position"), 0, 5),
            (int)result.Number("start"),
            FontFamilies[Math.Clamp(result.Index("font"), 0, FontFamilies.Count - 1)],
            result.Number("size"),
            result.Color("color"),
            result.Number("margin") * 72 / 25.4,
            result.Bool("skipFirst"));

        var pages = AskPageScope(result, document)!;
        RunSafely("La numérotation n’a pas pu être ajoutée.", () => document.ApplyHeaderFooter(options, pages));
    }

    private async Task RecognizeTextAsync()
    {
        var document = _document!;
        var languages = OcrService.Languages;
        if (languages.Count == 0)
        {
            _dialogs.Alert("Reconnaissance de texte indisponible", "Aucune langue de reconnaissance n’est installée. Ajoutez une langue (avec la reconnaissance optique de caractères) dans Paramètres › Heure et langue › Langue et région.", new[] { "OK" }, icon: AlertIcon.Warning);
            return;
        }

        var culture = CultureInfo.CurrentUICulture.Name;
        var preferred = languages.ToList().FindIndex(l => l.Tag.StartsWith(culture[..2], StringComparison.OrdinalIgnoreCase));

        var spec = new FormSpec("Reconnaissance de texte (OCR)")
            {
                ConfirmLabel = "Lancer",
                Width = 460,
                Message = "Rend le texte des pages scannées sélectionnable et recherchable, grâce au moteur intégré à Windows. L’apparence des pages ne change pas."
            }
            .Choice("language", "Langue du document", languages.Select(l => l.Name).ToList(), Math.Max(0, preferred))
            .Choice("pages", "Pages", PageScopes)
            .Check("skip", "Ignorer les pages qui contiennent déjà du texte", true);

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        var pages = AskPageScope(result, document)!;
        var language = languages[Math.Clamp(result.Index("language"), 0, languages.Count - 1)].Tag;

        try
        {
            int count;
            using (var progress = _dialogs.BeginProgress("Reconnaissance de texte", "Préparation…", true))
            {
                count = await document.RecognizeTextAsync(pages, language, result.Bool("skip"), progress);
            }

            _dialogs.Alert(
                count == 0 ? "Aucune page traitée" : "Reconnaissance terminée",
                count == 0
                    ? "Aucun texte n’a été ajouté : les pages contiennent déjà du texte ou aucun mot n’a été reconnu."
                    : $"Le texte de {count} page{(count > 1 ? "s" : "")} est maintenant sélectionnable et recherchable.",
                new[] { "OK" },
                icon: AlertIcon.Info);
        }
        catch (Exception ex)
        {
            ShowError("La reconnaissance de texte a échoué.", ex);
        }
    }

    private void Protect()
    {
        var document = _document!;
        var current = document.Protection;

        var spec = new FormSpec("Protéger le document")
            {
                ConfirmLabel = "Protéger",
                Width = 470,
                Icon = AlertIcon.Lock,
                Message = "Le document sera chiffré en AES 256 bits lors de l’enregistrement.",
                Validate = r =>
                {
                    if (r.String("password") != r.String("confirm"))
                    {
                        return "Les deux mots de passe ne correspondent pas.";
                    }

                    var restricted = !r.Bool("print") || !r.Bool("copy") || !r.Bool("modify") || !r.Bool("annotate") || !r.Bool("forms");
                    return r.String("password").Length == 0 && !restricted
                        ? "Saisissez un mot de passe ou retirez au moins une autorisation."
                        : null;
                }
            }
            .Password("password", "Mot de passe d’ouverture", "Laisser vide pour une ouverture libre")
            .Password("confirm", "Confirmation")
            .Separator()
            .Check("print", "Autoriser l’impression", current?.AllowPrint ?? true)
            .Check("copy", "Autoriser la copie du texte et des images", current?.AllowCopy ?? true)
            .Check("modify", "Autoriser la modification et l’assemblage", current?.AllowModify ?? true)
            .Check("annotate", "Autoriser les annotations", current?.AllowAnnotations ?? true)
            .Check("forms", "Autoriser le remplissage des formulaires", current?.AllowForms ?? true)
            .Password("owner", "Mot de passe propriétaire", "Facultatif", "Permet de lever les restrictions dans d’autres logiciels.");

        var result = _dialogs.ShowForm(spec);
        if (result is null)
        {
            return;
        }

        document.SetProtection(new PdfProtection
        {
            UserPassword = result.String("password"),
            OwnerPassword = result.String("owner"),
            AllowPrint = result.Bool("print"),
            AllowCopy = result.Bool("copy"),
            AllowModify = result.Bool("modify"),
            AllowAnnotations = result.Bool("annotate"),
            AllowForms = result.Bool("forms")
        });

        var choice = _dialogs.Alert("Protection prête", "Elle sera appliquée au prochain enregistrement du document.", new[] { "Enregistrer maintenant", "Plus tard" }, 0, 1, icon: AlertIcon.Lock);
        if (choice == 0)
        {
            _ = SaveAsync(false);
        }
    }

    private void RemoveProtection()
    {
        var choice = _dialogs.Alert(
            "Retirer la protection ?",
            "Le document ne sera plus chiffré ni restreint après le prochain enregistrement.",
            new[] { "Retirer", "Annuler" },
            1,
            1,
            0,
            AlertIcon.Lock);

        if (choice == 0)
        {
            _document!.SetProtection(null);
        }
    }

    // =====================================================================

    public void Dispose()
    {
        SettingsService.RecentFilesChanged -= ReloadRecentFiles;
        SettingsService.SignaturesChanged -= ReloadSignatures;
        ThemeManager.Changed -= OnThemeChanged;

        var document = _document;
        _document = null;
        document?.Dispose();
    }
}
