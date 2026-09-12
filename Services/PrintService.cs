using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PDFEditor.Pdf;
using PDFEditor.ViewModels;

namespace PDFEditor.Services;

/// <summary>Impression via la boite de dialogue Windows ; chaque page est rendue a 300 ppp.</summary>
public static class PrintService
{
    public static void Print(Window owner, DocumentViewModel document, IDialogService dialogs)
    {
        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            MinPage = 1,
            MaxPage = (uint)Math.Max(1, document.PageCount),
            PageRange = new PageRange(1, Math.Max(1, document.PageCount))
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var (from, to) = dialog.PageRangeSelection switch
        {
            PageRangeSelection.UserPages => (dialog.PageRange.PageFrom, dialog.PageRange.PageTo),
            PageRangeSelection.CurrentPage => (document.CurrentPageIndex + 1, document.CurrentPageIndex + 1),
            _ => (1, document.PageCount)
        };

        from = Math.Clamp(from, 1, document.PageCount);
        to = Math.Clamp(Math.Max(from, to), 1, document.PageCount);

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            using var flat = document.CreateFlattenedCopy();
            var paginator = new PdfPaginator(flat, from - 1, to - 1, new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight));
            dialog.PrintDocument(paginator, document.DisplayName);
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            dialogs.Alert("L’impression a échoué.", ex.Message, new[] { "OK" }, icon: AlertIcon.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private sealed class PdfPaginator : DocumentPaginator
    {
        private readonly PdfDoc _document;
        private readonly int _first;
        private readonly int _last;
        private Size _pageSize;

        public PdfPaginator(PdfDoc document, int first, int last, Size pageSize)
        {
            _document = document;
            _first = first;
            _last = last;
            _pageSize = pageSize;
        }

        public override bool IsPageCountValid => true;

        public override int PageCount => _last - _first + 1;

        public override Size PageSize
        {
            get => _pageSize;
            set => _pageSize = value;
        }

        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            var index = _first + pageNumber;
            var info = _document.GetPageInfo(index);
            var paper = _pageSize;

            // Page paysage sur papier portrait (ou l'inverse) : rotation automatique.
            var rotate = (info.Width > info.Height) != (paper.Width > paper.Height);
            var contentWidth = (rotate ? info.Height : info.Width) * 96 / 72;
            var contentHeight = (rotate ? info.Width : info.Height) * 96 / 72;
            var fit = Math.Min(paper.Width / contentWidth, paper.Height / contentHeight);

            var renderScale = Math.Min(300.0 / 72.0, 4800.0 / Math.Max(info.Width, info.Height));
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(info.Width * renderScale));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(info.Height * renderScale));
            var pixels = _document.Render(index, pixelWidth, pixelHeight, renderScale, 0, 0, printing: true);
            var bitmap = ImageTools.FromBgra(pixels, pixelWidth, pixelHeight);

            var drawWidth = info.Width * 96 / 72 * fit;
            var drawHeight = info.Height * 96 / 72 * fit;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (rotate)
                {
                    dc.PushTransform(new TranslateTransform(paper.Width / 2, paper.Height / 2));
                    dc.PushTransform(new RotateTransform(90));
                    dc.DrawImage(bitmap, new Rect(-drawWidth / 2, -drawHeight / 2, drawWidth, drawHeight));
                    dc.Pop();
                    dc.Pop();
                }
                else
                {
                    dc.DrawImage(bitmap, new Rect((paper.Width - drawWidth) / 2, (paper.Height - drawHeight) / 2, drawWidth, drawHeight));
                }
            }

            return new DocumentPage(visual, paper, new Rect(paper), new Rect(paper));
        }
    }
}
