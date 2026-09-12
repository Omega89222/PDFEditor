using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using PDFEditor.Models;
using PDFEditor.Pdf;
using PDFEditor.Services;

namespace PDFEditor.ViewModels;

/// <summary>
/// Operations qui modifient le fichier PDF lui-meme (pages, rotation, recadrage,
/// contenu ajoute). Chacune est annulable : l'etat du fichier est capture avant
/// l'operation, et apres l'operation au moment ou l'on annule.
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>La liste des pages a ete reconstruite (la vue doit se recalculer).</summary>
    public event Action? PagesChanged;

    private void ExecuteDocumentChange(
        string name,
        Action<PdfDoc> mutate,
        Func<List<PageViewModel>, List<PageViewModel>>? rearrange = null,
        IReadOnlyCollection<PageViewModel>? transformedPages = null,
        Action? transformAnnotations = null)
    {
        CommitPendingEdit();
        ClearTextSelection();
        Select(null, null);

        var beforeBytes = _pdf.Save();
        var beforePages = Pages.ToList();
        var annotationsBefore = transformedPages?
            .SelectMany(p => p.Annotations)
            .Select(a => (Annotation: a, State: a.Snapshot()))
            .ToList();

        try
        {
            mutate(_pdf);
        }
        catch
        {
            ReplacePdf(beforeBytes);
            ApplyPageList(beforePages);
            throw;
        }

        transformAnnotations?.Invoke();
        var afterPages = rearrange?.Invoke(beforePages) ?? beforePages;
        var annotationsAfter = annotationsBefore?
            .Select(x => (x.Annotation, State: x.Annotation.Snapshot()))
            .ToList();

        ApplyPageList(afterPages);

        byte[]? afterBytes = null;
        Undo.Push(name,
            () =>
            {
                Select(null, null);
                afterBytes ??= _pdf.Save();
                ReplacePdf(beforeBytes);
                if (annotationsBefore is not null)
                {
                    foreach (var (annotation, state) in annotationsBefore)
                    {
                        annotation.Restore(state);
                    }
                }

                ApplyPageList(beforePages);
            },
            () =>
            {
                if (afterBytes is null)
                {
                    return;
                }

                Select(null, null);
                ReplacePdf(afterBytes);
                if (annotationsAfter is not null)
                {
                    foreach (var (annotation, state) in annotationsAfter)
                    {
                        annotation.Restore(state);
                    }
                }

                ApplyPageList(afterPages);
            });
    }

    private void ReplacePdf(byte[] bytes)
    {
        var fresh = PdfDoc.Open(bytes);
        fresh.SetFormHighlight(SettingsService.Current.HighlightFormFields);

        var old = _pdf;
        _pdf = fresh;
        Renderer.Document = fresh;
        old.Dispose();

        Raise(nameof(Pdf), nameof(HasForms));
    }

    private void ApplyPageList(List<PageViewModel> pages)
    {
        ClearSearchResults();

        var count = _pdf.PageCount;
        if (count != pages.Count)
        {
            Debug.WriteLine($"[Document] Incoherence : {count} pages PDF pour {pages.Count} pages affichees.");
        }

        Pages.Clear();
        for (var i = 0; i < Math.Min(count, pages.Count); i++)
        {
            var page = pages[i];
            page.Index = i;
            page.IsSelected = false;
            page.UpdateSize(_pdf.GetPageSize(i));
            page.InvalidateContent();
            Pages.Add(page);
        }

        for (var i = pages.Count; i < count; i++)
        {
            var page = CreatePage();
            page.Index = i;
            page.UpdateSize(_pdf.GetPageSize(i));
            Pages.Add(page);
        }

        _currentPageIndex = Pages.Count == 0 ? 0 : Math.Clamp(_currentPageIndex, 0, Pages.Count - 1);
        LoadOutline();
        RebuildAnnotationItems();

        Raise(nameof(PageCount), nameof(PageStatus), nameof(CurrentPage), nameof(CurrentPageIndex), nameof(CurrentPageNumber));
        PagesChanged?.Invoke();
    }

    private List<int> ValidIndices(IEnumerable<int> indices) =>
        indices.Where(i => i >= 0 && i < Pages.Count).Distinct().OrderBy(i => i).ToList();

    // =====================================================================
    // Rotation
    // =====================================================================

    public void RotatePages(IReadOnlyList<int> indices, int quarterTurns)
    {
        var turns = ((quarterTurns % 4) + 4) % 4;
        var targets = ValidIndices(indices);
        if (turns == 0 || targets.Count == 0)
        {
            return;
        }

        var pages = targets.Select(i => Pages[i]).ToList();
        var sizes = pages.ToDictionary(p => p, p => new Size(p.Width, p.Height));
        var name = turns switch
        {
            1 => "Pivoter à droite",
            3 => "Pivoter à gauche",
            _ => "Pivoter de 180°"
        };

        ExecuteDocumentChange(name,
            pdf =>
            {
                foreach (var index in targets)
                {
                    pdf.SetRotation(index, pdf.GetRotation(index) + turns);
                }
            },
            null,
            pages,
            () =>
            {
                foreach (var page in pages)
                {
                    var map = RotationMap(sizes[page], turns);
                    foreach (var annotation in page.Annotations)
                    {
                        annotation.TransformFrame(map);
                    }
                }
            });
    }

    /// <summary>Repere d'une page WxH tournee de 1, 2 ou 3 quarts de tour horaires.</summary>
    private static Func<Point, Point> RotationMap(Size size, int turns)
    {
        var w = size.Width;
        var h = size.Height;
        if (turns == 1)
        {
            return p => new Point(h - p.Y, p.X);
        }

        if (turns == 2)
        {
            return p => new Point(w - p.X, h - p.Y);
        }

        return p => new Point(p.Y, w - p.X);
    }

    // =====================================================================
    // Suppression, insertion, duplication, deplacement
    // =====================================================================

    public void DeletePages(IReadOnlyList<int> indices)
    {
        var targets = ValidIndices(indices);
        if (targets.Count == 0 || targets.Count >= Pages.Count)
        {
            return;
        }

        var removed = targets.Select(i => Pages[i]).ToHashSet();
        ExecuteDocumentChange(targets.Count == 1 ? "Supprimer la page" : "Supprimer les pages",
            pdf =>
            {
                foreach (var index in targets.OrderByDescending(i => i))
                {
                    pdf.DeletePage(index);
                }
            },
            list => list.Where(p => !removed.Contains(p)).ToList());
    }

    public void InsertBlankPages(int at, double width, double height, int count = 1)
    {
        at = Math.Clamp(at, 0, Pages.Count);
        count = Math.Clamp(count, 1, 500);
        var created = Enumerable.Range(0, count).Select(_ => CreatePage()).ToList();

        ExecuteDocumentChange(count == 1 ? "Insérer une page vierge" : "Insérer des pages vierges",
            pdf =>
            {
                for (var k = 0; k < count; k++)
                {
                    pdf.InsertBlankPage(at + k, width, height);
                }
            },
            list =>
            {
                var result = list.ToList();
                result.InsertRange(at, created);
                return result;
            });

        GoToPage(at);
    }

    /// <summary>Insere tout ou partie d'un autre PDF ; retourne le nombre de pages inserees.</summary>
    public int InsertPagesFrom(PdfDoc source, int at, IReadOnlyList<int>? sourceIndices = null)
    {
        var indices = sourceIndices?.ToList() ?? Enumerable.Range(0, source.PageCount).ToList();
        if (indices.Count == 0)
        {
            return 0;
        }

        at = Math.Clamp(at, 0, Pages.Count);
        var created = indices.Select(_ => CreatePage()).ToList();

        ExecuteDocumentChange(indices.Count == 1 ? "Insérer une page" : "Insérer des pages",
            pdf =>
            {
                if (!pdf.ImportPages(source, indices, at))
                {
                    throw new InvalidOperationException("Les pages n’ont pas pu être importées.");
                }
            },
            list =>
            {
                var result = list.ToList();
                result.InsertRange(at, created);
                return result;
            });

        GoToPage(at);
        return indices.Count;
    }

    public void DuplicatePages(IReadOnlyList<int> indices)
    {
        var targets = ValidIndices(indices);
        if (targets.Count == 0)
        {
            return;
        }

        var originals = targets.Select(i => Pages[i]).ToList();
        var copies = originals.Select(original =>
        {
            var copy = CreatePage();
            foreach (var annotation in original.Annotations)
            {
                copy.Annotations.Add(annotation.Duplicate(new Vector()));
            }

            return copy;
        }).ToList();

        ExecuteDocumentChange(targets.Count == 1 ? "Dupliquer la page" : "Dupliquer les pages",
            pdf =>
            {
                using var clone = pdf.Clone();
                for (var k = targets.Count - 1; k >= 0; k--)
                {
                    pdf.ImportPages(clone, new[] { targets[k] }, targets[k] + 1);
                }
            },
            list =>
            {
                var result = new List<PageViewModel>(list.Count + copies.Count);
                foreach (var page in list)
                {
                    result.Add(page);
                    var position = originals.IndexOf(page);
                    if (position >= 0)
                    {
                        result.Add(copies[position]);
                    }
                }

                return result;
            });
    }

    /// <summary>Deplace des pages avant la page d'indice <paramref name="insertBefore"/> (indices actuels).</summary>
    public void MovePages(IReadOnlyList<int> indices, int insertBefore)
    {
        var targets = ValidIndices(indices);
        if (targets.Count == 0)
        {
            return;
        }

        insertBefore = Math.Clamp(insertBefore, 0, Pages.Count);
        var destination = insertBefore - targets.Count(i => i < insertBefore);

        var contiguous = targets[^1] - targets[0] == targets.Count - 1;
        if (contiguous && destination == targets[0])
        {
            return;
        }

        var moving = targets.Select(i => Pages[i]).ToList();
        ExecuteDocumentChange(targets.Count == 1 ? "Déplacer la page" : "Déplacer les pages",
            pdf =>
            {
                if (!pdf.MovePages(targets, destination))
                {
                    throw new InvalidOperationException("Les pages n’ont pas pu être déplacées.");
                }
            },
            list =>
            {
                var rest = list.Where(p => !moving.Contains(p)).ToList();
                rest.InsertRange(Math.Clamp(destination, 0, rest.Count), moving);
                return rest;
            });

        GoToPage(destination);
    }

    // =====================================================================
    // Recadrage
    // =====================================================================

    /// <summary>Recadre les pages sur une zone exprimee dans le repere de chacune.</summary>
    public void CropPages(IReadOnlyList<int> indices, Rect area)
    {
        var targets = ValidIndices(indices);
        var rects = new Dictionary<PageViewModel, Rect>();
        foreach (var index in targets)
        {
            var page = Pages[index];
            var r = Rect.Intersect(area, new Rect(0, 0, page.Width, page.Height));
            if (!r.IsEmpty && r.Width >= 18 && r.Height >= 18)
            {
                rects[page] = r;
            }
        }

        if (rects.Count == 0)
        {
            return;
        }

        var pages = rects.Keys.ToList();
        ExecuteDocumentChange(pages.Count == 1 ? "Recadrer la page" : "Recadrer les pages",
            pdf =>
            {
                foreach (var (page, rect) in rects)
                {
                    pdf.SetCropBox(page.Index, rect);
                }
            },
            null,
            pages,
            () =>
            {
                foreach (var (page, rect) in rects)
                {
                    foreach (var annotation in page.Annotations)
                    {
                        annotation.TransformFrame(p => new Point(p.X - rect.X, p.Y - rect.Y));
                    }
                }
            });
    }

    /// <summary>Retire des marges (en points) sur toutes les pages visees.</summary>
    public void TrimMargins(IReadOnlyList<int> indices, double left, double top, double right, double bottom)
    {
        var targets = ValidIndices(indices);
        var rects = new Dictionary<PageViewModel, Rect>();
        foreach (var index in targets)
        {
            var page = Pages[index];
            var width = page.Width - left - right;
            var height = page.Height - top - bottom;
            if (width >= 18 && height >= 18)
            {
                rects[page] = new Rect(left, top, width, height);
            }
        }

        if (rects.Count == 0)
        {
            return;
        }

        var pages = rects.Keys.ToList();
        ExecuteDocumentChange("Recadrer les marges",
            pdf =>
            {
                foreach (var (page, rect) in rects)
                {
                    pdf.SetCropBox(page.Index, rect);
                }
            },
            null,
            pages,
            () =>
            {
                foreach (var (page, rect) in rects)
                {
                    foreach (var annotation in page.Annotations)
                    {
                        annotation.TransformFrame(p => new Point(p.X - rect.X, p.Y - rect.Y));
                    }
                }
            });
    }

    // =====================================================================
    // Integration definitive des annotations dans le document
    // =====================================================================

    /// <summary>
    /// Ecrit les annotations dans les pages (elles ne sont alors plus modifiables),
    /// par exemple avant de reordonner un document tres annote.
    /// </summary>
    public void FlattenAnnotations()
    {
        var removed = Pages.Where(p => p.Annotations.Count > 0).ToDictionary(p => p, p => p.Annotations.ToList());
        if (removed.Count == 0)
        {
            return;
        }

        CommitPendingEdit();
        ClearTextSelection();
        Select(null, null);

        var author = SettingsService.Current.AuthorName;
        var beforeBytes = _pdf.Save();
        var pageList = Pages.ToList();

        foreach (var (page, items) in removed)
        {
            AnnotationExporter.ApplyToPage(_pdf, page.Index, items, author);
        }

        foreach (var page in removed.Keys)
        {
            page.Annotations.Clear();
        }

        ApplyPageList(pageList);

        byte[]? afterBytes = null;
        Undo.Push("Intégrer les annotations",
            () =>
            {
                Select(null, null);
                afterBytes ??= _pdf.Save();
                ReplacePdf(beforeBytes);
                foreach (var (page, items) in removed)
                {
                    foreach (var item in items)
                    {
                        page.Annotations.Add(item);
                    }
                }

                ApplyPageList(pageList);
            },
            () =>
            {
                if (afterBytes is null)
                {
                    return;
                }

                Select(null, null);
                ReplacePdf(afterBytes);
                foreach (var page in removed.Keys)
                {
                    page.Annotations.Clear();
                }

                ApplyPageList(pageList);
            });
    }
}
