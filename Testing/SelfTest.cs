using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using PDFEditor.Services;
using PDFEditor.ViewModels;
using PDFEditor.Views;

namespace PDFEditor.Testing;

/// <summary>
/// Auto-test du moteur, sans interface :
/// <c>PDFEditor.exe --selftest rapport.txt --open document.pdf</c>.
/// Chaque etape est isolee ; le code de sortie est le nombre d'echecs.
/// </summary>
public static class SelfTest
{
    private sealed class NullProgress : IProgressReporter
    {
        public CancellationToken Token => CancellationToken.None;

        public void Report(double fraction, string? message = null)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Compte direct (la liste AnnotationItems est rafraichie en differe).</summary>
    private static int CountAnnotations(DocumentViewModel doc) => doc.Pages.Sum(p => p.Annotations.Count);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static async Task<int> RunAsync(string reportPath, string sourcePath)
    {
        var report = new StringBuilder();
        var failures = 0;
        var work = Path.Combine(Path.GetDirectoryName(reportPath) ?? ".", "selftest-output");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, true);
        }

        Directory.CreateDirectory(work);

        report.AppendLine($"Auto-test Éditeur PDF — {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        report.AppendLine($"Source : {sourcePath}");
        report.AppendLine();

        async Task Step(string name, Func<Task<string>> body)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var detail = await body();
                report.AppendLine($"[OK]     {name} ({watch.ElapsedMilliseconds} ms) {detail}");
            }
            catch (Exception ex)
            {
                failures++;
                report.AppendLine($"[ÉCHEC]  {name} ({watch.ElapsedMilliseconds} ms) {ex.GetType().Name} : {ex.Message}");
                report.AppendLine(ex.StackTrace);
            }
        }

        Task Sync(string name, Func<string> body) => Step(name, () => Task.FromResult(body()));

        var bytes = await File.ReadAllBytesAsync(sourcePath);
        DocumentViewModel? doc = null;

        await Sync("Ouverture", () =>
        {
            doc = DocumentViewModel.FromFile(PdfDoc.Open(bytes), sourcePath, null, bytes.Length);
            Check(doc.PageCount > 6, "le document de test doit avoir au moins 7 pages");
            Check(doc.PageCount == doc.Pdf.PageCount, "nombre de pages incoherent");
            return $"{doc.PageCount} pages, {doc.VersionLabel}";
        });

        if (doc is null)
        {
            await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);
            return 1;
        }

        PdfTextLine? firstLine = null;
        await Sync("Extraction du texte", () =>
        {
            var text = doc.Pdf.GetTextPage(0);
            Check(text.HasText, "aucun texte en page 1");
            firstLine = text.Lines.First(l => l.Text.Trim().Length > 5);
            return $"{text.Lines.Count} lignes, 1re : « {firstLine.Text.Trim()} »";
        });

        await Step("Recherche", async () =>
        {
            await doc.SearchAsync("CRC");
            var count = doc.SearchResults.Count;
            Check(count > 0, "aucun résultat");
            doc.ClearSearch();
            return $"{count} résultats pour « CRC »";
        });

        var page0 = doc.Pages[0];
        await Sync("Annotations (tous les types)", () =>
        {
            var w = page0.Width;
            var h = page0.Height;

            var rectangle = new ShapeAnnotation(ShapeKind.Rectangle) { Rect = new Rect(40, 40, 120, 60), StrokeColor = Colors.Red, StrokeWidth = 2 };
            var ellipse = new ShapeAnnotation(ShapeKind.Ellipse) { Rect = new Rect(200, 40, 100, 60), StrokeColor = Colors.Blue, FillColor = Color.FromArgb(80, 0, 120, 255), Dashed = true };
            var arrow = new LineAnnotation { Start = new Point(50, 150), End = new Point(250, 190), ArrowEnd = true, StrokeColor = Colors.Green, StrokeWidth = 3 };

            var ink = new InkAnnotation { StrokeColor = Colors.Purple, StrokeWidth = 2 };
            ink.AddStroke(Enumerable.Range(0, 40).Select(i => new Point(60 + i * 4, 230 + Math.Sin(i / 4.0) * 12)));

            var marker = new InkAnnotation(true) { StrokeColor = Colors.Yellow, StrokeWidth = 12, Opacity = 0.4 };
            marker.AddStroke(new[] { new Point(60, 270), new Point(260, 272) });

            var textBox = new TextBoxAnnotation { Rect = new Rect(320, 150, 230, 70) };
            textBox.Text = "Bonjour à tous — éèàç € «test»\nΩmega ✓ 你好";
            textBox.FontFamily = "Arial";
            textBox.FontSize = 14;
            textBox.TextColor = Colors.DarkBlue;

            var note = new NoteAnnotation { Location = new Point(w - 60, 40), Text = "Commentaire de test" };
            var stamp = new StampAnnotation("APPROUVÉ") { Rect = new Rect(320, 240, 160, 44), StrokeColor = Colors.Green };
            var whiteout = new WhiteoutAnnotation { Rect = new Rect(40, h - 120, 120, 30) };
            var link = new LinkAnnotation { Rect = new Rect(200, h - 120, 120, 20), Uri = "https://example.com" };
            var image = new ImageAnnotation(CreatePng()) { Rect = new Rect(w - 150, h - 170, 96, 96) };

            var items = new Annotation[] { rectangle, ellipse, arrow, ink, marker, textBox, note, stamp, whiteout, link, image };
            foreach (var annotation in items)
            {
                doc.AddAnnotation(page0, annotation, select: false);
            }

            Check(CountAnnotations(doc) == items.Length, $"compte {CountAnnotations(doc)}");
            return $"{items.Length} annotations";
        });

        await Sync("Surlignage d’une ligne de texte", () =>
        {
            var before = CountAnnotations(doc);
            var text = doc.Pdf.GetTextPage(0);
            var span = text.LineSpanAt(firstLine!.Start);
            doc.SetTextSelection(page0, span);
            var markup = doc.CreateMarkupFromSelection(MarkupKind.Highlight);
            Check(markup is not null, "aucun surlignage créé");
            if (doc.FindPage(markup!) is null)
            {
                doc.AddAnnotation(page0, markup!, select: false);
            }

            Check(CountAnnotations(doc) == before + 1, "surlignage non ajouté");
            return $"« {markup!.SelectedText.Trim()} »";
        });

        var editedLine = "";
        var editedBounds = Rect.Empty;
        await Sync("Modification d’un texte existant", () =>
        {
            var page = doc.Pages[1];
            var line = doc.Pdf.GetTextPage(1).Lines.First(l => l.Text.Trim().Length > 10);
            var edit = doc.CreateTextEdit(1, line, Colors.White);
            Check(!edit.UseCover, "le texte d’origine serait masqué par un aplat au lieu d’être retiré");
            editedLine = line.Text.Trim();
            edit.Text = "Texte remplacé par l’auto-test";
            if (doc.FindPage(edit) is null)
            {
                doc.AddAnnotation(page, edit, select: false);
            }

            // A l'ecran : le texte d'origine disparait du rendu de la page, sans aplat.
            editedBounds = line.Bounds;
            var hidden = page.GetHiddenTextRegions();
            var area = Rect.Inflate(line.Bounds, 1, 1);
            var inkBefore = CountInk(doc.Pdf, 1, area);
            var inkAfter = CountInk(doc.Pdf, 1, area, hidden);
            Check(inkBefore > 20 && inkAfter < inkBefore * 0.1, $"texte d’origine encore visible à l’écran ({inkAfter} px sur {inkBefore})");
            SaveRegionPng(doc.Pdf, 1, Rect.Inflate(line.Bounds, 40, 14), hidden, Path.Combine(work, "texte-modifie-ecran.png"));

            return $"« {line.Text.Trim()} » → « {edit.Text} » (encre {inkBefore} → {inkAfter} px)";
        });

        await Sync("Signatures (texte et tracé)", () =>
        {
            var page = doc.Pages[1];
            var textSignature = doc.CreateSignature(new SavedSignature { Kind = SignatureKind.Text, Text = "Jean Dupont", FontFamily = "Segoe Script" }, page, new Point(300, 520));
            Check(textSignature is not null, "signature texte");
            if (doc.FindPage(textSignature!) is null)
            {
                doc.AddAnnotation(page, textSignature!, select: false);
            }

            var inkSignature = new SavedSignature
            {
                Kind = SignatureKind.Ink,
                Width = 200,
                Height = 60,
                Strokes = { new List<double[]> { new[] { 0.0, 30 }, new[] { 50.0, 0 }, new[] { 100.0, 60 }, new[] { 200.0, 20 } } }
            };
            var ink = doc.CreateSignature(inkSignature, page, new Point(300, 620));
            Check(ink is not null, "signature tracée");
            if (doc.FindPage(ink!) is null)
            {
                doc.AddAnnotation(page, ink!, select: false);
            }

            return $"{CountAnnotations(doc)} annotations au total";
        });

        await Sync("Annuler / rétablir une annotation", () =>
        {
            var before = CountAnnotations(doc);
            doc.Undo.Undo();
            Check(CountAnnotations(doc) == before - 1, $"après annulation : {CountAnnotations(doc)}");
            doc.Undo.Redo();
            Check(CountAnnotations(doc) == before, $"après rétablissement : {CountAnnotations(doc)}");
            return $"{before} annotations";
        });

        var output1 = Path.Combine(work, "1-annotations.pdf");
        await Step("Enregistrement avec annotations", async () =>
        {
            await doc.SaveToAsync(output1);
            using var reopened = PdfDoc.Open(await File.ReadAllBytesAsync(output1));
            Check(reopened.PageCount == doc.PageCount, "nombre de pages");
            var text0 = reopened.GetTextPage(0).Text;
            Check(text0.Contains("Bonjour", StringComparison.Ordinal), "texte de la zone de texte absent");
            Check(text0.Contains("APPROUV", StringComparison.Ordinal), "tampon absent");
            Check(reopened.GetTextPage(1).Text.Contains("auto-test", StringComparison.Ordinal), "texte modifié absent");
            Check(!reopened.GetTextPage(1).Text.Contains(editedLine, StringComparison.Ordinal), "le texte d’origine de la ligne modifiée est toujours là");
            SaveRegionPng(reopened, 1, Rect.Inflate(editedBounds, 40, 14), null, Path.Combine(work, "texte-modifie-enregistre.png"));
            Check(!doc.IsModified, "le document est toujours marqué modifié");
            var ink = CountInk(reopened, 0, new Rect(38, 38, 124, 64));
            Check(ink > 50, $"rectangle invisible au rendu ({ink} px)");
            return $"{new FileInfo(output1).Length / 1024} Ko";
        });

        var pageCountBefore = doc.PageCount;
        var pageCountAfter = 0;
        await Sync("Pages : rotation, insertion, duplication, déplacement, suppression, recadrage", () =>
        {
            var count = doc.PageCount;
            doc.RotatePages(new[] { 1 }, 1);
            var rotation = doc.Pdf.GetRotation(1);
            Check(rotation is 1 or 90, $"rotation {rotation}");

            doc.InsertBlankPages(2, 595.28, 841.89, 2);
            Check(doc.PageCount == count + 2 && doc.Pdf.PageCount == count + 2, "insertion");

            var annotationsOnFirst = doc.Pages[0].Annotations.Count;
            doc.DuplicatePages(new[] { 0 });
            Check(doc.PageCount == count + 3, "duplication");
            Check(doc.Pages[1].Annotations.Count == annotationsOnFirst, $"annotations non dupliquées ({doc.Pages[1].Annotations.Count}/{annotationsOnFirst})");

            var moved = doc.Pages[0];
            doc.MovePages(new[] { 0 }, 4);
            Check(doc.Pages.IndexOf(moved) == 3, $"déplacement : index {doc.Pages.IndexOf(moved)}");
            Check(moved.Index == 3, "index de page non mis à jour");

            doc.DeletePages(new[] { doc.PageCount - 1 });
            Check(doc.PageCount == count + 2, "suppression");

            var target = doc.Pages[5];
            doc.CropPages(new[] { 5 }, new Rect(30, 30, target.Width - 60, target.Height - 60));

            for (var i = 0; i < doc.PageCount; i++)
            {
                var info = doc.Pdf.GetPageInfo(i);
                Check(Math.Abs(info.Width - doc.Pages[i].Width) < 1.5 && Math.Abs(info.Height - doc.Pages[i].Height) < 1.5,
                    $"taille de la page {i + 1} : pdf {info.Width:0}×{info.Height:0}, vue {doc.Pages[i].Width:0}×{doc.Pages[i].Height:0}");
                Check(doc.Pages[i].Index == i, $"index de la page {i + 1}");
            }

            pageCountAfter = doc.PageCount;
            return $"{doc.PageCount} pages";
        });

        await Sync("Annuler puis rétablir les opérations sur les pages", () =>
        {
            for (var i = 0; i < 6 && doc.Undo.CanUndo; i++)
            {
                doc.Undo.Undo();
            }

            Check(doc.PageCount == pageCountBefore && doc.Pdf.PageCount == pageCountBefore, $"après annulation : {doc.PageCount} pages");
            Check(doc.Pdf.GetRotation(1) == 0, "rotation non annulée");
            Check(doc.Pages[0].Annotations.Count > 0, "annotations perdues");

            for (var i = 0; i < 6 && doc.Undo.CanRedo; i++)
            {
                doc.Undo.Redo();
            }

            Check(doc.PageCount == pageCountAfter && doc.Pdf.PageCount == pageCountAfter, $"après rétablissement : {doc.PageCount} pages");
            return "ok";
        });

        await Sync("Filigrane et numérotation des pages", () =>
        {
            doc.ApplyWatermark(new WatermarkOptions("CONFIDENTIEL", "Arial", true, 72, Colors.Red, 0.2, 45), new[] { 0, 1, 2 });
            doc.ApplyHeaderFooter(new HeaderFooterOptions("Page {n} sur {total}", HeaderFooterPosition.BottomCenter, 1, "Arial", 10, Colors.Gray, 24, false),
                Enumerable.Range(0, doc.PageCount).ToList());
            var text = doc.Pdf.GetTextPage(0).Text;
            Check(text.Contains("CONFIDENTIEL", StringComparison.Ordinal), "filigrane absent");
            Check(text.Contains("Page 1 sur", StringComparison.Ordinal), "numéro de page absent");
            return "ok";
        });

        var output2 = Path.Combine(work, "2-protege.pdf");
        await Step("Métadonnées et mot de passe", async () =>
        {
            doc.MetaTitle = "Titre auto-test";
            doc.MetaAuthor = "Éditeur PDF";
            doc.SetProtection(new PdfProtection { UserPassword = "secret", OwnerPassword = "proprietaire" });
            await doc.SaveToAsync(output2);

            var data = await File.ReadAllBytesAsync(output2);
            var refused = false;
            try
            {
                using var _ = PdfDoc.Open(data);
            }
            catch (PdfPasswordException)
            {
                refused = true;
            }

            Check(refused, "le document s’ouvre sans mot de passe");
            using var opened = PdfDoc.Open(data, "secret");
            Check(opened.PageCount == doc.PageCount, "nombre de pages");
            var metadata = opened.GetMetadata();
            Check(metadata.Title == "Titre auto-test", $"titre lu : « {metadata.Title} »");
            doc.SetProtection(null);
            return "ok";
        });

        var output3 = Path.Combine(work, "3-caviarde.pdf");
        const int redactedPage = 3;
        await Step("Caviardage", async () =>
        {
            var page = doc.Pages[redactedPage];
            var line = doc.Pdf.GetTextPage(redactedPage).Lines.First(l => l.Text.Trim().Length > 12);
            doc.AddAnnotation(page, new RedactionAnnotation { Rect = Rect.Inflate(line.Bounds, 2, 2) }, select: false);
            await doc.SaveToAsync(output3);

            using var reopened = PdfDoc.Open(await File.ReadAllBytesAsync(output3));
            var after = reopened.GetTextPage(redactedPage).Text;
            Check(!after.Contains(line.Text.Trim(), StringComparison.Ordinal), "le texte caviardé est toujours présent");
            var area = Rect.Inflate(line.Bounds, 1, 1);
            var inked = CountInk(reopened, redactedPage, area);
            Check(inked > area.Width * area.Height * 0.8, $"zone caviardée non noircie ({inked} px sur {area.Width * area.Height:0})");
            return $"« {line.Text.Trim()} » supprimé";
        });

        await Sync("Intégrer les annotations au document", () =>
        {
            var before = CountAnnotations(doc);
            doc.FlattenAnnotations();
            Check(CountAnnotations(doc) == 0, $"il reste {CountAnnotations(doc)} annotations");
            Check(doc.Pdf.GetTextPage(0).Text.Contains("Bonjour", StringComparison.Ordinal), "texte intégré absent");
            doc.Undo.Undo();
            Check(CountAnnotations(doc) == before, $"annulation : {CountAnnotations(doc)}/{before}");
            doc.Undo.Redo();
            Check(CountAnnotations(doc) == 0, "rétablissement");
            return $"{before} annotations intégrées";
        });

        await Sync("Exports : images, texte, extraction, division, pages par feuille", () =>
        {
            var folder = Path.Combine(work, "images");
            DocumentViewModel.ExportImages(doc.Pdf, folder, "page", new[] { 0, 1 }, true, 72, null);
            DocumentViewModel.ExportImages(doc.Pdf, folder, "page", new[] { 2 }, false, 96, null);
            Check(Directory.GetFiles(folder).Length >= 3, "images manquantes");

            var text = DocumentViewModel.ExtractText(doc.Pdf);
            Check(text.Length > 1000, "texte extrait trop court");

            var extract = Path.Combine(work, "4-extrait.pdf");
            DocumentViewModel.SavePages(doc.Pdf, new[] { 0, 2 }, extract);
            using (var extracted = PdfDoc.Open(File.ReadAllBytes(extract)))
            {
                Check(extracted.PageCount == 2, "extraction de pages");
            }

            var parts = DocumentViewModel.ParseRanges("1-3, 4, 5-6", doc.PageCount);
            Check(parts is { Count: 3 }, "analyse des plages");
            var created = DocumentViewModel.Split(doc.Pdf, parts!, Path.Combine(work, "division"), "partie", null);
            Check(created == 3, $"division : {created} fichiers");

            using var nUp = doc.Pdf.CreateNUp(841.89, 595.28, 2, 1);
            Check(nUp is not null && nUp.PageCount == (doc.PageCount + 1) / 2, "pages par feuille");
            return $"{text.Length} caractères extraits";
        });

        await Step("Reconnaissance de texte (OCR)", async () =>
        {
            if (!OcrService.IsAvailable)
            {
                return "indisponible sur ce poste (ignoré)";
            }

            var data = await File.ReadAllBytesAsync(output3);
            using var scanned = DocumentViewModel.FromFile(PdfDoc.Open(data), output3, null, data.Length);
            // La page caviardee est devenue une image (plus les annotations integrees par-dessus).
            var before = scanned.Pdf.GetTextPage(redactedPage).Text.Length;
            var recognized = await scanned.RecognizeTextAsync(new[] { redactedPage }, null, false, new NullProgress());
            Check(recognized == 1, $"pages reconnues : {recognized}");
            var text = scanned.Pdf.GetTextPage(redactedPage);
            Check(text.Text.Length > before + 200, $"couche de texte trop courte ({before} → {text.Text.Length})");
            Check(text.Text.Contains("Abstract", StringComparison.OrdinalIgnoreCase), "« Abstract » non reconnu");
            return $"{text.Text.Length - before} caractères reconnus";
        });

        await Step("Nouveau document vierge", async () =>
        {
            using var blank = DocumentViewModel.FromUntitled(PdfDoc.CreateEmpty(), "Sans titre");
            if (blank.PageCount == 0)
            {
                blank.InsertBlankPages(0, 595.28, 841.89);
            }

            var textBox = new TextBoxAnnotation { Rect = new Rect(72, 72, 300, 40) };
            textBox.Text = "Document créé par l’auto-test";
            blank.AddAnnotation(blank.Pages[0], textBox, select: false);

            var path = Path.Combine(work, "5-nouveau.pdf");
            await blank.SaveToAsync(path);
            using var reopened = PdfDoc.Open(await File.ReadAllBytesAsync(path));
            Check(reopened.PageCount >= 1, "aucune page");
            Check(reopened.GetTextPage(0).Text.Contains("auto-test", StringComparison.Ordinal), "texte absent");
            return $"{reopened.PageCount} page(s)";
        });

        await Step("Rendu de toutes les pages", async () =>
        {
            var pdf = doc.Pdf;
            var count = pdf.PageCount;
            await Task.Run(() =>
            {
                for (var i = 0; i < count; i++)
                {
                    var info = pdf.GetPageInfo(i);
                    pdf.Render(i, Math.Max(1, (int)(info.Width / 2)), Math.Max(1, (int)(info.Height / 2)), 0.5);
                }
            });
            return $"{count} pages";
        });

        await Step("Enregistrement final", async () =>
        {
            var path = Path.Combine(work, "6-final.pdf");
            await doc.SaveToAsync(path);
            using var reopened = PdfDoc.Open(await File.ReadAllBytesAsync(path));
            Check(reopened.PageCount == doc.PageCount, "nombre de pages");
            return $"{new FileInfo(path).Length / 1024} Ko";
        });

        doc.Dispose();

        report.AppendLine();
        report.AppendLine(failures == 0 ? "Toutes les étapes ont réussi." : $"{failures} étape(s) en échec.");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);
        return failures;
    }

    private static byte[] CreatePng()
    {
        var element = AppIcon.Create(128);
        element.Measure(new Size(128, 128));
        element.Arrange(new Rect(0, 0, 128, 128));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return ImageTools.EncodePng(bitmap);
    }

    /// <summary>Image PNG d'une zone de page (a 3x), pour controle visuel.</summary>
    private static void SaveRegionPng(PdfDoc pdf, int index, Rect area, IReadOnlyList<Rect>? hidden, string path)
    {
        const double scale = 3;
        var width = Math.Max(1, (int)Math.Ceiling(area.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(area.Height * scale));
        var pixels = pdf.Render(index, width, height, scale, (int)Math.Floor(area.X * scale), (int)Math.Floor(area.Y * scale), hiddenText: hidden);
        File.WriteAllBytes(path, ImageTools.EncodePng(ImageTools.FromBgra(pixels, width, height)));
    }

    /// <summary>Nombre de pixels non blancs dans une zone (en points) de la page rendue a 72 dpi.</summary>
    private static int CountInk(PdfDoc pdf, int index, Rect area, IReadOnlyList<Rect>? hidden = null)
    {
        var info = pdf.GetPageInfo(index);
        var width = Math.Max(1, (int)Math.Round(info.Width));
        var height = Math.Max(1, (int)Math.Round(info.Height));
        var pixels = pdf.Render(index, width, height, 1.0, hiddenText: hidden);

        var count = 0;
        for (var y = Math.Max(0, (int)area.Top); y < Math.Min(height, (int)area.Bottom); y++)
        {
            for (var x = Math.Max(0, (int)area.Left); x < Math.Min(width, (int)area.Right); x++)
            {
                var offset = (y * width + x) * 4;
                if (pixels[offset] < 200 || pixels[offset + 1] < 200 || pixels[offset + 2] < 200)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
