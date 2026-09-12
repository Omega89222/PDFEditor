using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDFEditor.Models;
using PDFEditor.Rendering;
using PDFEditor.Services;
using PDFEditor.ViewModels;

namespace PDFEditor.Views.Document;

/// <summary>
/// Une page affichee : rendu PDFium (image complete, plus une tuile nette de la
/// zone visible aux forts zooms), puis calques dessines par-dessus : recherche et
/// selection de texte, annotations, poignees et apercus d'outils, editeurs en place.
/// </summary>
public sealed class PageControl : Grid
{
    private const double MaxFullPixels = 4_500_000;
    private const double LowResPixels = 1_400_000;

    private static readonly SolidColorBrush[] ShadowBrushes =
    {
        Frozen(Color.FromArgb(20, 0, 0, 0)),
        Frozen(Color.FromArgb(13, 0, 0, 0)),
        Frozen(Color.FromArgb(8, 0, 0, 0)),
        Frozen(Color.FromArgb(5, 0, 0, 0)),
        Frozen(Color.FromArgb(3, 0, 0, 0))
    };

    private static readonly SolidColorBrush NightPageBrush = Frozen(Color.FromRgb(28, 28, 30));

    private readonly Layer _surface;
    private readonly Layer _marks;
    private readonly Layer _annotations;
    private readonly Layer _overlay;

    private double _pendingFullScale = -1;
    private int _pendingFullVersion = -1;
    private bool _pendingFullNight;
    private Rect _pendingTileRect = Rect.Empty;
    private double _pendingTileScale = -1;
    private int _pendingTileVersion = -1;

    public PageControl(DocumentView host)
    {
        Host = host;
        SnapsToDevicePixels = true;

        _surface = new Layer(DrawSurface);
        _marks = new Layer(dc => Host.Tools.DrawMarks(dc, this));
        _annotations = new Layer(DrawAnnotations);
        _overlay = new Layer(dc => Host.Tools.DrawOverlay(dc, this));
        EditorHost = new Canvas { ClipToBounds = false };

        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.HighQuality);
        TextOptions.SetTextFormattingMode(_annotations, TextFormattingMode.Ideal);

        Children.Add(_surface);
        Children.Add(_marks);
        Children.Add(_annotations);
        Children.Add(_overlay);
        Children.Add(EditorHost);
    }

    public DocumentView Host { get; }

    public PageViewModel? Page { get; private set; }

    /// <summary>Calque des editeurs en place (zone de texte, note).</summary>
    public Canvas EditorHost { get; }

    /// <summary>DIP par point PDF.</summary>
    public double Scale => Host.Canvas.LayoutScale;

    public BitmapSource? FullBitmap { get; private set; }

    public double FullScale { get; private set; }

    public int FullVersion { get; private set; } = -1;

    public bool FullNight { get; private set; }

    public BitmapSource? TileBitmap { get; private set; }

    public Rect TileRect { get; private set; }

    public double TileScale { get; private set; }

    public int TileVersion { get; private set; } = -1;

    public bool TileNight { get; private set; }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // =====================================================================
    // Cycle de vie
    // =====================================================================

    public void Attach(PageViewModel page)
    {
        Page = page;
        page.AnnotationsInvalidated += OnAnnotationsInvalidated;
        page.ContentInvalidated += OnContentInvalidated;
    }

    public void Detach()
    {
        var page = Page;
        if (page is not null)
        {
            page.AnnotationsInvalidated -= OnAnnotationsInvalidated;
            page.ContentInvalidated -= OnContentInvalidated;
        }

        var renderer = Host.Document?.Renderer;
        renderer?.Cancel((this, "full"));
        renderer?.Cancel((this, "tile"));

        Page = null;
        FullBitmap = null;
        TileBitmap = null;
        EditorHost.Children.Clear();
    }

    private void OnAnnotationsInvalidated(object? sender, EventArgs e)
    {
        _annotations.InvalidateVisual();
        _overlay.InvalidateVisual();
    }

    private void OnContentInvalidated(object? sender, EventArgs e)
    {
        _marks.InvalidateVisual();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Page is not null)
            {
                EnsureRendered(Host.ViewportRect, true);
            }
        }), DispatcherPriority.Background);
    }

    public void InvalidateMarks() => _marks.InvalidateVisual();

    public void InvalidateOverlay() => _overlay.InvalidateVisual();

    public void InvalidateAnnotations() => _annotations.InvalidateVisual();

    public void InvalidateLayers()
    {
        _surface.InvalidateVisual();
        _marks.InvalidateVisual();
        _annotations.InvalidateVisual();
        _overlay.InvalidateVisual();
    }

    /// <summary>Force un nouveau rendu (formulaire modifie, mode nuit...).</summary>
    public void ForceRerender()
    {
        _pendingFullScale = -1;
        _pendingTileScale = -1;
        FullVersion = -1;
        TileVersion = -1;
        EnsureRendered(Host.ViewportRect, true);
    }

    // =====================================================================
    // Rendu
    // =====================================================================

    public void EnsureRendered(Rect viewport, bool allowDetail)
    {
        var page = Page;
        var document = Host.Document;
        if (page is null || document is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(Host).DpiScaleX;
        var desired = Math.Max(0.05, Scale * dpi);
        var night = SettingsService.Current.NightMode;
        var version = page.ContentVersion;
        var area = Math.Max(1, page.Width * page.Height);

        var fullScale = area * desired * desired <= MaxFullPixels
            ? desired
            : Math.Min(desired, Math.Sqrt(LowResPixels / area));

        var fullReady = FullBitmap is not null && FullVersion == version && FullNight == night
                        && Math.Abs(FullScale - fullScale) <= fullScale * 0.03;
        var fullPending = _pendingFullVersion == version && _pendingFullNight == night
                          && Math.Abs(_pendingFullScale - fullScale) <= fullScale * 0.03;

        if (!fullReady && !fullPending)
        {
            RequestFull(page, document, fullScale, night, version);
        }

        if (fullScale >= desired * 0.97)
        {
            if (TileBitmap is not null)
            {
                TileBitmap = null;
                _surface.InvalidateVisual();
            }

            return;
        }

        if (!allowDetail)
        {
            return;
        }

        var pageRect = Host.Canvas.GetPageRect(page.Index);
        var visible = Rect.Intersect(pageRect, viewport);
        if (visible.IsEmpty || visible.Width < 1 || visible.Height < 1)
        {
            return;
        }

        var visiblePoints = new Rect(
            (visible.X - pageRect.X) / Scale,
            (visible.Y - pageRect.Y) / Scale,
            visible.Width / Scale,
            visible.Height / Scale);

        var tileReady = TileBitmap is not null && TileVersion == version && TileNight == night
                        && Math.Abs(TileScale - desired) <= desired * 0.03 && TileRect.Contains(visiblePoints);
        var tilePending = _pendingTileVersion == version && Math.Abs(_pendingTileScale - desired) <= desired * 0.03
                          && _pendingTileRect.Contains(visiblePoints);

        if (tileReady || tilePending)
        {
            return;
        }

        var tile = visiblePoints;
        tile.Inflate(visiblePoints.Width * 0.2, visiblePoints.Height * 0.2);
        tile.Intersect(new Rect(0, 0, page.Width, page.Height));
        if (!tile.IsEmpty)
        {
            RequestTile(page, document, tile, desired, night, version);
        }
    }

    private void RequestFull(PageViewModel page, DocumentViewModel document, double scale, bool night, int version)
    {
        _pendingFullScale = scale;
        _pendingFullVersion = version;
        _pendingFullNight = night;

        var hidden = page.GetHiddenTextRegions();
        document.Renderer.Submit((this, "full"), RenderScheduler.PriorityVisible, pdf =>
        {
            var index = page.Index;
            if (index < 0 || index >= pdf.PageCount)
            {
                return null;
            }

            var info = pdf.GetPageInfo(index);
            var width = Math.Max(1, (int)Math.Round(info.Width * scale));
            var height = Math.Max(1, (int)Math.Round(info.Height * scale));
            var pixels = pdf.Render(index, width, height, scale, hiddenText: hidden);
            if (night)
            {
                ImageTools.InvertForNightMode(pixels);
            }

            return ImageTools.FromBgra(pixels, width, height);
        }, bitmap =>
        {
            if (!ReferenceEquals(Page, page))
            {
                return;
            }

            FullBitmap = bitmap;
            FullScale = scale;
            FullVersion = version;
            FullNight = night;
            if (_pendingFullVersion == version && Math.Abs(_pendingFullScale - scale) < 1e-9)
            {
                _pendingFullScale = -1;
            }

            _surface.InvalidateVisual();
        });
    }

    private void RequestTile(PageViewModel page, DocumentViewModel document, Rect tile, double scale, bool night, int version)
    {
        var offsetX = (int)Math.Floor(tile.X * scale);
        var offsetY = (int)Math.Floor(tile.Y * scale);
        var width = Math.Max(1, (int)Math.Ceiling(tile.Right * scale) - offsetX);
        var height = Math.Max(1, (int)Math.Ceiling(tile.Bottom * scale) - offsetY);
        var actual = new Rect(offsetX / scale, offsetY / scale, width / scale, height / scale);

        _pendingTileRect = actual;
        _pendingTileScale = scale;
        _pendingTileVersion = version;

        var hidden = page.GetHiddenTextRegions();
        document.Renderer.Submit((this, "tile"), RenderScheduler.PriorityDetail, pdf =>
        {
            var index = page.Index;
            if (index < 0 || index >= pdf.PageCount)
            {
                return null;
            }

            var pixels = pdf.Render(index, width, height, scale, offsetX, offsetY, hiddenText: hidden);
            if (night)
            {
                ImageTools.InvertForNightMode(pixels);
            }

            return ImageTools.FromBgra(pixels, width, height);
        }, bitmap =>
        {
            if (!ReferenceEquals(Page, page))
            {
                return;
            }

            TileBitmap = bitmap;
            TileRect = actual;
            TileScale = scale;
            TileVersion = version;
            TileNight = night;
            _pendingTileScale = -1;
            _surface.InvalidateVisual();
        });
    }

    // =====================================================================
    // Dessin
    // =====================================================================

    private void DrawSurface(DrawingContext dc)
    {
        var page = Page;
        if (page is null)
        {
            return;
        }

        var scale = Scale;
        var rect = new Rect(0, 0, page.Width * scale, page.Height * scale);

        for (var i = ShadowBrushes.Length; i >= 1; i--)
        {
            var shadow = rect;
            shadow.Inflate(i, i);
            shadow.Offset(0, i * 0.45);
            dc.DrawRoundedRectangle(ShadowBrushes[i - 1], null, shadow, i, i);
        }

        dc.DrawRectangle(SettingsService.Current.NightMode ? NightPageBrush : Brushes.White, null, rect);

        if (FullBitmap is { } full)
        {
            dc.DrawImage(full, rect);
        }

        if (TileBitmap is { } tile && TileVersion == page.ContentVersion && TileNight == FullNight)
        {
            dc.DrawImage(tile, new Rect(TileRect.X * scale, TileRect.Y * scale, TileRect.Width * scale, TileRect.Height * scale));
        }

        if (Host.TryFindResource("PageBorderBrush") is Brush border)
        {
            var pen = new Pen(border, 1);
            pen.Freeze();
            var edge = rect;
            edge.Inflate(0.5, 0.5);
            dc.DrawRectangle(null, pen, edge);
        }
    }

    private void DrawAnnotations(DrawingContext dc)
    {
        var page = Page;
        if (page is null || page.Annotations.Count == 0)
        {
            return;
        }

        var scale = Scale;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, page.Width * scale, page.Height * scale)));
        dc.PushTransform(new ScaleTransform(scale, scale));

        foreach (var annotation in page.Annotations)
        {
            if (annotation is TextBoxAnnotation text && Host.Tools.IsInlineEditing(annotation))
            {
                // Le texte est affiche par l'editeur : on ne dessine que le fond.
                if (text is TextEditAnnotation { UseCover: true } edit)
                {
                    var cover = edit.OriginalRect;
                    cover.Inflate(0.6, 0.6);
                    dc.DrawRectangle(AnnotationRenderer.Brush(edit.CoverColor), null, cover);
                }

                if (text.FillColor.A > 0)
                {
                    dc.DrawRectangle(AnnotationRenderer.Brush(AnnotationRenderer.WithOpacity(text.FillColor, text.Opacity)), null, text.Rect);
                }

                continue;
            }

            AnnotationRenderer.Draw(dc, annotation, editorChrome: true);
        }

        dc.Pop();
        dc.Pop();
    }

    /// <summary>
    /// Couleur de fond autour d'une zone (bordure de la zone dans le rendu) :
    /// sert a masquer proprement un texte modifie sur un fond colore.
    /// </summary>
    public Color SampleBackground(Rect area)
    {
        var bitmap = FullBitmap;
        if (bitmap is null || FullNight)
        {
            return Colors.White;
        }

        var scale = FullScale;
        var left = Math.Clamp((int)Math.Floor((area.Left - 1.5) * scale), 0, bitmap.PixelWidth - 1);
        var top = Math.Clamp((int)Math.Floor((area.Top - 1.5) * scale), 0, bitmap.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling((area.Right + 1.5) * scale), left + 1, bitmap.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((area.Bottom + 1.5) * scale), top + 1, bitmap.PixelHeight);
        var width = right - left;
        var height = bottom - top;

        var pixels = new byte[width * height * 4];
        try
        {
            bitmap.CopyPixels(new Int32Rect(left, top, width, height), pixels, width * 4, 0);
        }
        catch
        {
            return Colors.White;
        }

        var buckets = new Dictionary<int, (int Count, long R, long G, long B)>();

        void Sample(int x, int y)
        {
            var i = (y * width + x) * 4;
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            var key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            buckets.TryGetValue(key, out var entry);
            buckets[key] = (entry.Count + 1, entry.R + r, entry.G + g, entry.B + b);
        }

        for (var x = 0; x < width; x++)
        {
            Sample(x, 0);
            Sample(x, height - 1);
        }

        for (var y = 1; y < height - 1; y++)
        {
            Sample(0, y);
            Sample(width - 1, y);
        }

        if (buckets.Count == 0)
        {
            return Colors.White;
        }

        var best = buckets.Values.MaxBy(v => v.Count);
        return Color.FromRgb((byte)(best.R / best.Count), (byte)(best.G / best.Count), (byte)(best.B / best.Count));
    }

    private sealed class Layer : FrameworkElement
    {
        private readonly Action<DrawingContext> _render;

        public Layer(Action<DrawingContext> render)
        {
            _render = render;
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
        }

        protected override void OnRender(DrawingContext drawingContext) => _render(drawingContext);
    }
}
