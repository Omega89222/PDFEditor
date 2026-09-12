using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFEditor.Models;
using PDFEditor.Pdf;
using PDFEditor.Rendering;
using PDFEditor.Services;

namespace PDFEditor.ViewModels;

/// <summary>
/// Recherche, contenu ajoute au document (filigrane, numerotation, OCR),
/// enregistrement, exports et presse-papiers.
/// </summary>
public sealed partial class DocumentViewModel
{
    // =====================================================================
    // Recherche
    // =====================================================================

    private CancellationTokenSource? _searchCancellation;
    private string _searchQuery = "";
    private bool _searchMatchCase;
    private bool _searchWholeWord;
    private bool _isSearching;
    private string _searchStatus = "";
    private int _currentSearchIndex = -1;

    public ObservableCollection<SearchResultViewModel> SearchResults { get; } = new();

    /// <summary>Les resultats (ou le resultat courant) ont change : surlignages a redessiner.</summary>
    public event Action? SearchResultsChanged;

    public string SearchQuery
    {
        get => _searchQuery;
        set => Set(ref _searchQuery, value ?? "");
    }

    public bool SearchMatchCase
    {
        get => _searchMatchCase;
        set => Set(ref _searchMatchCase, value);
    }

    public bool SearchWholeWord
    {
        get => _searchWholeWord;
        set => Set(ref _searchWholeWord, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => Set(ref _isSearching, value);
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => Set(ref _searchStatus, value);
    }

    public int CurrentSearchIndex
    {
        get => _currentSearchIndex;
        private set
        {
            if (Set(ref _currentSearchIndex, value))
            {
                Raise(nameof(CurrentSearchResult), nameof(SearchPositionLabel));
            }
        }
    }

    public SearchResultViewModel? CurrentSearchResult =>
        _currentSearchIndex >= 0 && _currentSearchIndex < SearchResults.Count ? SearchResults[_currentSearchIndex] : null;

    public string SearchPositionLabel => SearchResults.Count == 0 ? "" : $"{_currentSearchIndex + 1} sur {SearchResults.Count}";

    public bool HasSearchResults => SearchResults.Count > 0;

    public async Task SearchAsync(string? query = null)
    {
        if (query is not null)
        {
            SearchQuery = query;
        }

        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;

        SearchResults.Clear();
        CurrentSearchIndex = -1;

        var needle = _searchQuery.Trim();
        if (needle.Length == 0)
        {
            IsSearching = false;
            SearchStatus = "";
            Raise(nameof(HasSearchResults), nameof(SearchPositionLabel));
            SearchResultsChanged?.Invoke();
            return;
        }

        IsSearching = true;
        SearchStatus = "Recherche…";

        var pdf = _pdf;
        var count = Pages.Count;
        var matchCase = _searchMatchCase;
        var wholeWord = _searchWholeWord;
        var token = cancellation.Token;

        try
        {
            for (var i = 0; i < count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var index = i;
                var found = await Task.Run(() =>
                {
                    var page = pdf.GetTextPage(index);
                    return page.Find(needle, matchCase, wholeWord)
                        .Select(span => SearchResultViewModel.Create(index, page, span))
                        .ToList();
                }, token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (found.Count == 0)
                {
                    continue;
                }

                foreach (var result in found)
                {
                    SearchResults.Add(result);
                }

                if (_currentSearchIndex < 0)
                {
                    CurrentSearchIndex = 0;
                    NavigateToCurrentResult();
                }

                Raise(nameof(HasSearchResults), nameof(SearchPositionLabel));
                SearchResultsChanged?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                IsSearching = false;
            }
        }

        SearchStatus = SearchResults.Count switch
        {
            0 => "Aucun résultat",
            1 => "1 résultat",
            var n => $"{n} résultats"
        };

        Raise(nameof(HasSearchResults), nameof(SearchPositionLabel));
        SearchResultsChanged?.Invoke();
    }

    public void ClearSearchResults()
    {
        _searchCancellation?.Cancel();
        SearchResults.Clear();
        _currentSearchIndex = -1;
        IsSearching = false;
        SearchStatus = "";
        Raise(nameof(CurrentSearchIndex), nameof(CurrentSearchResult), nameof(HasSearchResults), nameof(SearchPositionLabel));
        SearchResultsChanged?.Invoke();
    }

    public void ClearSearch()
    {
        SearchQuery = "";
        ClearSearchResults();
    }

    public void MoveSearch(int direction)
    {
        if (SearchResults.Count == 0)
        {
            return;
        }

        var count = SearchResults.Count;
        CurrentSearchIndex = ((_currentSearchIndex + direction) % count + count) % count;
        NavigateToCurrentResult();
    }

    public void ShowSearchResult(SearchResultViewModel result)
    {
        var index = SearchResults.IndexOf(result);
        if (index < 0)
        {
            return;
        }

        CurrentSearchIndex = index;
        NavigateToCurrentResult();
    }

    private void NavigateToCurrentResult()
    {
        if (CurrentSearchResult is { } result)
        {
            GoToPage(result.PageIndex, result.Bounds);
            SearchResultsChanged?.Invoke();
        }
    }

    // =====================================================================
    // Filigrane, numerotation
    // =====================================================================

    public void ApplyWatermark(WatermarkOptions options, IReadOnlyList<int> pages)
    {
        var targets = ValidIndices(pages);
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(options.Text))
        {
            return;
        }

        ExecuteDocumentChange("Ajouter un filigrane", pdf =>
        {
            var font = new PdfFont(options.FontFamily, options.Bold);
            var alpha = (byte)Math.Round(255 * Math.Clamp(options.Opacity, 0.02, 1));
            var color = Color.FromArgb(alpha, options.Color.R, options.Color.G, options.Color.B);

            foreach (var index in targets)
            {
                pdf.EditPage(index, editor =>
                {
                    var info = editor.Info;
                    var size = options.FontSize;
                    var width = editor.MeasureText(options.Text, font, size);

                    var radians = options.Angle * Math.PI / 180;
                    var cos = Math.Abs(Math.Cos(radians));
                    var sin = Math.Abs(Math.Sin(radians));
                    var available = cos > 0.02 && sin > 0.02
                        ? Math.Min(info.Width / cos, info.Height / sin)
                        : cos > 0.02 ? info.Width : info.Height;
                    available *= 0.88;

                    if (width > available && width > 0)
                    {
                        size *= available / width;
                        width = available;
                    }

                    var direction = new Vector(Math.Cos(radians), -Math.Sin(radians));
                    var up = new Vector(-Math.Sin(radians), -Math.Cos(radians));
                    var center = new Point(info.Width / 2, info.Height / 2);
                    var origin = center - direction * (width / 2) - up * (size * 0.35);
                    editor.AddText(options.Text, origin, font, size, color, options.Angle);
                });
            }
        });
    }

    public void ApplyHeaderFooter(HeaderFooterOptions options, IReadOnlyList<int> pages)
    {
        var targets = ValidIndices(pages);
        if (targets.Count == 0 || string.IsNullOrWhiteSpace(options.Format))
        {
            return;
        }

        var total = Pages.Count;
        var fileName = Path.GetFileNameWithoutExtension(DisplayName);
        var date = DateTime.Now.ToString("d", CultureInfo.CurrentCulture);

        ExecuteDocumentChange("Numéroter les pages", pdf =>
        {
            var font = new PdfFont(options.FontFamily);
            foreach (var index in targets)
            {
                if (options.SkipFirstPage && index == 0)
                {
                    continue;
                }

                var number = options.StartNumber + index;
                var text = options.Format
                    .Replace("{n}", number.ToString(CultureInfo.CurrentCulture))
                    .Replace("{total}", (options.StartNumber + total - 1).ToString(CultureInfo.CurrentCulture))
                    .Replace("{date}", date)
                    .Replace("{fichier}", fileName);

                pdf.EditPage(index, editor =>
                {
                    var info = editor.Info;
                    var width = editor.MeasureText(text, font, options.FontSize);
                    var x = options.Position switch
                    {
                        HeaderFooterPosition.TopLeft or HeaderFooterPosition.BottomLeft => options.Margin,
                        HeaderFooterPosition.TopCenter or HeaderFooterPosition.BottomCenter => (info.Width - width) / 2,
                        _ => info.Width - options.Margin - width
                    };

                    var atTop = options.Position is HeaderFooterPosition.TopLeft or HeaderFooterPosition.TopCenter or HeaderFooterPosition.TopRight;
                    var baseline = atTop ? options.Margin + options.FontSize * 0.75 : info.Height - options.Margin;
                    editor.AddText(text, new Point(x, baseline), font, options.FontSize, options.Color);
                });
            }
        });
    }

    // =====================================================================
    // Reconnaissance de texte (OCR)
    // =====================================================================

    /// <summary>
    /// Ajoute une couche de texte invisible sur les pages scannees : le texte
    /// devient selectionnable et recherchable. Retourne le nombre de pages traitees.
    /// </summary>
    public async Task<int> RecognizeTextAsync(IReadOnlyList<int> pages, string? language, bool skipPagesWithText, IProgressReporter progress)
    {
        var targets = ValidIndices(pages);
        if (targets.Count == 0)
        {
            return 0;
        }

        CommitPendingEdit();
        ClearTextSelection();
        Select(null, null);

        var pdf = _pdf;
        var beforeBytes = await Task.Run(() => pdf.Save());
        var maxDimension = Math.Clamp(OcrService.MaxImageDimension, 1000, 4200);
        var recognized = 0;

        for (var k = 0; k < targets.Count; k++)
        {
            if (progress.Token.IsCancellationRequested)
            {
                break;
            }

            var index = targets[k];
            progress.Report((double)k / targets.Count, $"Page {index + 1} ({k + 1} sur {targets.Count})…");

            if (skipPagesWithText && await Task.Run(() => pdf.GetTextPage(index).HasText))
            {
                continue;
            }

            var info = await Task.Run(() => pdf.GetPageInfo(index));
            var scale = Math.Min(300.0 / 72.0, maxDimension / Math.Max(info.Width, info.Height));
            var width = Math.Max(1, (int)Math.Round(info.Width * scale));
            var height = Math.Max(1, (int)Math.Round(info.Height * scale));
            var pixels = await Task.Run(() => pdf.Render(index, width, height, scale, 0, 0, true));

            var words = await OcrService.RecognizeAsync(pixels, width, height, language);
            if (words.Count == 0)
            {
                continue;
            }

            await Task.Run(() => pdf.EditPage(index, editor =>
            {
                var font = new PdfFont("Arial");
                foreach (var word in words)
                {
                    var r = word.PixelRect;
                    var box = new Rect(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);
                    editor.AddTextFitted(word.Text, box, font, Colors.Black, invisible: true);
                }
            }));

            recognized++;
            if (index < Pages.Count)
            {
                Pages[index].InvalidateContent();
            }
        }

        progress.Report(1);

        if (recognized > 0)
        {
            var pageList = Pages.ToList();
            byte[]? afterBytes = null;
            Undo.Push("Reconnaissance de texte",
                () =>
                {
                    Select(null, null);
                    afterBytes ??= _pdf.Save();
                    ReplacePdf(beforeBytes);
                    ApplyPageList(pageList);
                },
                () =>
                {
                    if (afterBytes is null)
                    {
                        return;
                    }

                    ReplacePdf(afterBytes);
                    ApplyPageList(pageList);
                });
        }

        return recognized;
    }

    // =====================================================================
    // Enregistrement
    // =====================================================================

    private List<(int Index, IReadOnlyList<Annotation> Items)> SnapshotAnnotations()
    {
        CommitPendingEdit();
        return Pages
            .Where(p => p.Annotations.Count > 0)
            .Select(p => (p.Index, (IReadOnlyList<Annotation>)p.Annotations.Select(a => a.Snapshot()).ToList()))
            .ToList();
    }

    /// <summary>Document final : contenu + annotations integrees + metadonnees + chiffrement.</summary>
    private static byte[] Compose(PdfDoc pdf, List<(int Index, IReadOnlyList<Annotation> Items)> jobs, string author, PdfMetadata? metadata, PdfProtection? protection)
    {
        var bytes = pdf.Save();

        if (jobs.Count > 0)
        {
            using var copy = PdfDoc.Open(bytes);
            foreach (var (index, items) in jobs)
            {
                AnnotationExporter.ApplyToPage(copy, index, items, author);
            }

            bytes = copy.Save();
        }

        if (metadata is null && protection is null)
        {
            return bytes;
        }

        try
        {
            return PdfPostProcessor.Apply(bytes, metadata, protection);
        }
        catch (Exception ex) when (protection is null)
        {
            // Les metadonnees ne justifient pas de perdre l'enregistrement.
            Debug.WriteLine($"[Document] Metadonnees non ecrites : {ex.Message}");
            return bytes;
        }
    }

    public byte[] BuildOutput()
    {
        var jobs = SnapshotAnnotations();
        return Compose(_pdf, jobs, SettingsService.Current.AuthorName,
            _metadataChanged ? _metadata.Clone() : null,
            _protection is { IsEnabled: true } ? _protection.Clone() : null);
    }

    public Task<byte[]> BuildOutputAsync()
    {
        var jobs = SnapshotAnnotations();
        var pdf = _pdf;
        var author = SettingsService.Current.AuthorName;
        var metadata = _metadataChanged ? _metadata.Clone() : null;
        var protection = _protection is { IsEnabled: true } ? _protection.Clone() : null;
        return Task.Run(() => Compose(pdf, jobs, author, metadata, protection));
    }

    /// <summary>Copie du document avec les annotations integrees (impression, exports).</summary>
    public PdfDoc CreateFlattenedCopy()
    {
        var jobs = SnapshotAnnotations();
        return PdfDoc.Open(Compose(_pdf, jobs, SettingsService.Current.AuthorName, null, null));
    }

    public Task<PdfDoc> CreateFlattenedCopyAsync()
    {
        var jobs = SnapshotAnnotations();
        var pdf = _pdf;
        var author = SettingsService.Current.AuthorName;
        return Task.Run(() => PdfDoc.Open(Compose(pdf, jobs, author, null, null)));
    }

    public void SaveTo(string path)
    {
        var bytes = BuildOutput();
        WriteAtomically(path, bytes);
        OnSaved(path, bytes.Length);
    }

    public async Task SaveToAsync(string path)
    {
        var bytes = await BuildOutputAsync();
        await Task.Run(() => WriteAtomically(path, bytes));
        OnSaved(path, bytes.Length);
    }

    private void OnSaved(string path, long size)
    {
        FilePath = path;
        FileSize = size;
        Password = _protection is { IsEnabled: true } ? _protection.UserPassword : null;

        if (_metadataChanged)
        {
            _metadata.Modified = DateTime.Now;
        }

        _formsDirty = false;
        _metadataChanged = false;
        _protectionChanged = false;
        Undo.MarkSaved();

        Raise(nameof(IsModified), nameof(ModifiedLabel));
        SettingsService.AddRecent(path, Pages.Count);
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full) ?? ".";
        var temp = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");

        File.WriteAllBytes(temp, bytes);
        try
        {
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // Fichier temporaire orphelin : sans consequence.
            }

            throw;
        }
    }

    // =====================================================================
    // Exports (sur une copie aplatie, depuis n'importe quel fil)
    // =====================================================================

    public static void ExportImages(PdfDoc pdf, string folder, string baseName, IReadOnlyList<int> pages, bool png, double dpi, IProgressReporter? progress)
    {
        Directory.CreateDirectory(folder);
        var safeName = SafeFileName(baseName);

        for (var k = 0; k < pages.Count; k++)
        {
            if (progress?.Token.IsCancellationRequested == true)
            {
                break;
            }

            var index = pages[k];
            progress?.Report((double)k / pages.Count, $"Page {index + 1} ({k + 1} sur {pages.Count})…");

            var info = pdf.GetPageInfo(index);
            var scale = Math.Min(dpi / 72.0, 12000.0 / Math.Max(info.Width, info.Height));
            var width = Math.Max(1, (int)Math.Ceiling(info.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(info.Height * scale));
            var pixels = pdf.Render(index, width, height, scale, 0, 0, printing: true);
            var bitmap = ImageTools.FromBgra(pixels, width, height);
            var data = png ? ImageTools.EncodePng(bitmap) : ImageTools.EncodeJpeg(bitmap, 92);
            File.WriteAllBytes(Path.Combine(folder, $"{safeName} - page {index + 1:000}.{(png ? "png" : "jpg")}"), data);
        }

        progress?.Report(1);
    }

    public static string ExtractText(PdfDoc pdf)
    {
        var builder = new StringBuilder();
        var count = pdf.PageCount;
        for (var i = 0; i < count; i++)
        {
            var text = pdf.GetTextPage(i).Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
            if (i > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(text);
        }

        return builder.ToString().Replace("\n", Environment.NewLine);
    }

    public static void SavePages(PdfDoc pdf, IReadOnlyList<int> pages, string path)
    {
        using var part = pdf.ExtractPages(pages);
        WriteAtomically(path, part.Save());
    }

    /// <summary>Ecrit un fichier par groupe de pages ; retourne le nombre de fichiers.</summary>
    public static int Split(PdfDoc pdf, IReadOnlyList<IReadOnlyList<int>> parts, string folder, string baseName, IProgressReporter? progress)
    {
        Directory.CreateDirectory(folder);
        var safeName = SafeFileName(baseName);
        var written = 0;

        for (var k = 0; k < parts.Count; k++)
        {
            if (progress?.Token.IsCancellationRequested == true)
            {
                break;
            }

            var part = parts[k];
            if (part.Count == 0)
            {
                continue;
            }

            progress?.Report((double)k / parts.Count, $"Fichier {k + 1} sur {parts.Count}…");
            var label = part.Count == 1 ? $"page {part[0] + 1}" : $"pages {part[0] + 1}-{part[^1] + 1}";
            SavePages(pdf, part, Path.Combine(folder, $"{safeName} - {label}.pdf"));
            written++;
        }

        progress?.Report(1);
        return written;
    }

    /// <summary>
    /// « 1-3, 5, 8-10 » -> groupes d'indices (base 0). Chaque element separe par
    /// une virgule forme un groupe. Retourne null si la saisie est invalide.
    /// </summary>
    public static List<List<int>>? ParseRanges(string text, int pageCount)
    {
        var groups = new List<List<int>>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            var dash = part.IndexOfAny(new[] { '-', '–' });
            int start, end;
            if (dash > 0)
            {
                if (!int.TryParse(part[..dash].Trim(), out start) || !int.TryParse(part[(dash + 1)..].Trim(), out end))
                {
                    return null;
                }
            }
            else if (int.TryParse(part, out start))
            {
                end = start;
            }
            else
            {
                return null;
            }

            if (start < 1 || end < 1 || start > pageCount || end > pageCount)
            {
                return null;
            }

            if (end < start)
            {
                (start, end) = (end, start);
            }

            groups.Add(Enumerable.Range(start - 1, end - start + 1).ToList());
        }

        return groups.Count == 0 ? null : groups;
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "Document" : result;
    }

    // =====================================================================
    // Zones de page
    // =====================================================================

    /// <summary>Rendu d'une zone de page (copier en tant qu'image).</summary>
    public BitmapSource RenderArea(PageViewModel page, Rect area, double dpi = 200)
    {
        var scale = Math.Min(dpi / 72.0, 6000.0 / Math.Max(1, Math.Max(area.Width, area.Height)));
        var width = Math.Max(1, (int)Math.Ceiling(area.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(area.Height * scale));
        var pixels = _pdf.Render(page.Index, width, height, scale, (int)Math.Round(area.X * scale), (int)Math.Round(area.Y * scale));
        return ImageTools.FromBgra(pixels, width, height);
    }

    /// <summary>Texte dont les caracteres tombent dans la zone.</summary>
    public string GetTextInArea(PageViewModel page, Rect area)
    {
        var text = _pdf.GetTextPage(page.Index);
        var builder = new StringBuilder();

        foreach (var line in text.Lines)
        {
            if (!line.Bounds.IntersectsWith(area))
            {
                continue;
            }

            var lineBuilder = new StringBuilder();
            for (var i = line.Start; i <= line.End; i++)
            {
                var c = text.Chars[i];
                if (c.HasBox)
                {
                    var center = new Point(c.Box.X + c.Box.Width / 2, c.Box.Y + c.Box.Height / 2);
                    if (area.Contains(center))
                    {
                        lineBuilder.Append(c.Value);
                    }
                }
                else if (c.Value == ' ' && lineBuilder.Length > 0)
                {
                    lineBuilder.Append(' ');
                }
            }

            var content = lineBuilder.ToString().Trim();
            if (content.Length > 0)
            {
                builder.AppendLine(content);
            }
        }

        return builder.ToString().TrimEnd();
    }

    public void SelectAllText(PageViewModel page)
    {
        var text = _pdf.GetTextPage(page.Index);
        if (text.Chars.Length > 0)
        {
            SetTextSelection(page, new TextSpan(0, text.Chars.Length - 1));
        }
    }

    // =====================================================================
    // Creation d'annotations « metier »
    // =====================================================================

    public MarkupAnnotation? CreateMarkupFromSelection(MarkupKind kind)
    {
        if (_textSelection is not { } selection || selection.Rects.Count == 0)
        {
            return null;
        }

        var markup = new MarkupAnnotation(kind, selection.Rects, selection.Text);
        ApplyToolStyle(markup, kind switch
        {
            MarkupKind.Highlight => ToolKeys.Highlight,
            MarkupKind.Underline => ToolKeys.Underline,
            MarkupKind.StrikeOut => ToolKeys.Strike,
            _ => ToolKeys.Squiggly
        });

        AddAnnotation(selection.Page, markup, select: false);
        ClearTextSelection();
        return markup;
    }

    /// <summary>Remplacement modifiable d'une ligne de texte existante.</summary>
    public TextEditAnnotation CreateTextEdit(int pageIndex, PdfTextLine line, Color coverColor)
    {
        var family = FontCatalog.MatchFamily(line.FontName, line.Serif, line.Monospace);
        var fontSize = line.FontSize > 1 ? Math.Round(line.FontSize * 2) / 2 : Math.Max(4, line.Bounds.Height * 0.75);

        var edit = new TextEditAnnotation
        {
            OriginalText = line.Text,
            CoverColor = coverColor,
            OriginalRect = line.Bounds,
            // Vrai texte PDF : il sera retire, sans aplat. Page numerisee : aplat de la couleur du fond.
            UseCover = !_pdf.HasVisibleText(pageIndex, line.Bounds),
            Padding = 0,
            FillColor = Colors.Transparent,
            StrokeWidth = 0
        };

        edit.FontFamily = family;
        edit.Bold = line.Bold;
        edit.Italic = line.Italic;
        edit.FontSize = fontSize;
        edit.TextColor = line.Color;

        // La ligne de base du nouveau texte coincide avec celle d'origine.
        var baselineOffset = TextLayout.BaselineFor(edit.Font, fontSize);
        var measured = TextLayout.MeasureWidth(line.Text, edit.Font, fontSize);
        var width = Math.Max(line.Bounds.Width, measured) + fontSize;
        edit.Rect = new Rect(line.Bounds.Left, line.Baseline - baselineOffset, width, line.Bounds.Height);
        edit.Text = line.Text;
        return edit;
    }

    public ImageAnnotation? CreateImage(PageViewModel page, byte[] data, Point? center = null)
    {
        Size pixels;
        try
        {
            pixels = ImageTools.GetPixelSize(data);
        }
        catch
        {
            return null;
        }

        if (pixels.Width < 1 || pixels.Height < 1)
        {
            return null;
        }

        var width = pixels.Width * 0.75;
        var height = pixels.Height * 0.75;
        var fit = Math.Min(1, Math.Min(page.Width * 0.6 / width, page.Height * 0.6 / height));
        width *= fit;
        height *= fit;

        var c = center ?? new Point(page.Width / 2, page.Height / 2);
        return new ImageAnnotation(data) { Rect = new Rect(c.X - width / 2, c.Y - height / 2, width, height) };
    }

    public Annotation? CreateSignature(SavedSignature signature, PageViewModel page, Point center)
    {
        var color = ColorUtil.Parse(signature.Color, Color.FromRgb(0x1C, 0x2A, 0x6B));

        switch (signature.Kind)
        {
            case SignatureKind.Ink when signature.Strokes.Count > 0 && signature.Width > 0:
            {
                var targetWidth = Math.Min(170, page.Width * 0.32);
                var factor = targetWidth / signature.Width;
                var height = signature.Height * factor;
                var ink = new InkAnnotation { IsSignature = true };
                ink.StrokeColor = color;
                ink.StrokeWidth = Math.Max(0.8, signature.StrokeWidth * Math.Clamp(factor, 0.5, 1.2));

                foreach (var stroke in signature.Strokes)
                {
                    ink.AddStroke(stroke
                        .Where(p => p.Length >= 2)
                        .Select(p => new Point(center.X - targetWidth / 2 + p[0] * factor, center.Y - height / 2 + p[1] * factor)));
                }

                return ink;
            }

            case SignatureKind.Text when !string.IsNullOrWhiteSpace(signature.Text):
            {
                var text = new TextBoxAnnotation
                {
                    Padding = 2,
                    FillColor = Colors.Transparent,
                    StrokeWidth = 0
                };

                text.FontFamily = signature.FontFamily ?? "Segoe Script";
                text.FontSize = 26;
                text.TextColor = color;
                text.Rect = new Rect(center.X, center.Y, 200, 40);
                text.Text = signature.Text!;
                text.FitWidth(page.Width * 0.8);
                text.Rect = new Rect(center.X - text.Rect.Width / 2, center.Y - text.Rect.Height / 2, text.Rect.Width, text.Rect.Height);
                return text;
            }

            case SignatureKind.Image when !string.IsNullOrEmpty(signature.ImageBase64):
            {
                try
                {
                    var data = Convert.FromBase64String(signature.ImageBase64!);
                    var size = ImageTools.GetPixelSize(data);
                    var width = Math.Min(180, page.Width * 0.35);
                    var height = size.Width > 0 ? width * size.Height / size.Width : width / 3;
                    return new ImageAnnotation(data) { Rect = new Rect(center.X - width / 2, center.Y - height / 2, width, height) };
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }

    // =====================================================================
    // Presse-papiers
    // =====================================================================

    private const string ClipboardFormat = "PDFEditor.Annotation";
    private static Annotation? _clipboardAnnotation;
    private static string? _clipboardToken;

    public bool CopySelection()
    {
        if (_selectedAnnotation is { } annotation)
        {
            CommitPendingEdit();
            _clipboardAnnotation = annotation.Snapshot();
            _clipboardToken = Guid.NewGuid().ToString("N");

            var data = new DataObject();
            data.SetData(ClipboardFormat, _clipboardToken);
            if (annotation is TextBoxAnnotation { Text.Length: > 0 } text)
            {
                data.SetText(text.Text);
            }

            TrySetClipboard(data);
            return true;
        }

        if (_textSelection is { Text.Length: > 0 } selection)
        {
            TrySetClipboard(new DataObject(DataFormats.UnicodeText, NormalizeNewLines(selection.Text)));
            return true;
        }

        return false;
    }

    public bool CutSelection()
    {
        if (_selectedAnnotation is null || !CopySelection())
        {
            return false;
        }

        DeleteSelectedAnnotation();
        return true;
    }

    public bool Paste(PageViewModel page, Point? location)
    {
        try
        {
            var data = Clipboard.GetDataObject();
            var target = location ?? new Point(page.Width / 2, page.Height / 2);

            if (data?.GetDataPresent(ClipboardFormat) == true
                && data.GetData(ClipboardFormat) as string == _clipboardToken
                && _clipboardAnnotation is not null)
            {
                var copy = _clipboardAnnotation.Duplicate(new Vector());
                if (copy.CanMove)
                {
                    var bounds = copy.Bounds;
                    var offset = location is null ? new Vector(12, 12) : new Vector();
                    var destination = location is null
                        ? new Point(Math.Min(bounds.X + 12, page.Width - bounds.Width), Math.Min(bounds.Y + 12, page.Height - bounds.Height))
                        : new Point(target.X - bounds.Width / 2, target.Y - bounds.Height / 2);
                    copy.Translate(destination - bounds.TopLeft);
                    _clipboardAnnotation = copy.Snapshot();
                    _ = offset;
                }

                AddAnnotation(page, copy);
                return true;
            }

            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            {
                var image = CreateImage(page, ImageTools.EncodePng(NormalizeClipboardImage(bitmap)), target);
                if (image is not null)
                {
                    AddAnnotation(page, image);
                    return true;
                }
            }

            if (Clipboard.ContainsText())
            {
                var content = Clipboard.GetText().Trim();
                if (content.Length > 0)
                {
                    var box = new TextBoxAnnotation();
                    ApplyToolStyle(box, ToolKeys.Text);
                    box.Rect = new Rect(target.X, target.Y, Math.Min(360, page.Width * 0.6), 20);
                    box.Text = content;
                    box.FitWidth(Math.Min(420, page.Width * 0.8));
                    box.Rect = new Rect(
                        Math.Clamp(target.X - box.Rect.Width / 2, 0, Math.Max(0, page.Width - box.Rect.Width)),
                        Math.Clamp(target.Y - box.Rect.Height / 2, 0, Math.Max(0, page.Height - box.Rect.Height)),
                        box.Rect.Width,
                        box.Rect.Height);
                    AddAnnotation(page, box);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Document] Collage impossible : {ex.Message}");
        }

        return false;
    }

    private static void TrySetClipboard(DataObject data)
    {
        try
        {
            Clipboard.SetDataObject(data, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Document] Presse-papiers indisponible : {ex.Message}");
        }
    }

    private static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);

    /// <summary>
    /// Les captures d'ecran collees arrivent souvent avec un canal alpha nul :
    /// on les rend opaques pour ne pas inserer une image invisible.
    /// </summary>
    private static BitmapSource NormalizeClipboardImage(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        var allTransparent = true;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                allTransparent = false;
                break;
            }
        }

        if (allTransparent)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        return ImageTools.FromBgra(pixels, width, height);
    }
}
