using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PDFEditor.Rendering;
using PDFEditor.Services;
using PDFEditor.ViewModels;

namespace PDFEditor.Views;

/// <summary>Vignette d'une page (rendu PDFium en arriere-plan + annotations).</summary>
public sealed class PageThumbnail : FrameworkElement
{
    public static readonly DependencyProperty PageProperty =
        DependencyProperty.Register(nameof(Page), typeof(PageViewModel), typeof(PageThumbnail),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, OnPageChanged));

    public static readonly DependencyProperty DocumentProperty =
        DependencyProperty.Register(nameof(Document), typeof(DocumentViewModel), typeof(PageThumbnail),
            new FrameworkPropertyMetadata(null, (d, _) => ((PageThumbnail)d).Request()));

    public static readonly DependencyProperty ThumbnailWidthProperty =
        DependencyProperty.Register(nameof(ThumbnailWidth), typeof(double), typeof(PageThumbnail),
            new FrameworkPropertyMetadata(120d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly SolidColorBrush[] Shadows =
    {
        Frozen(Color.FromArgb(22, 0, 0, 0)),
        Frozen(Color.FromArgb(12, 0, 0, 0)),
        Frozen(Color.FromArgb(6, 0, 0, 0))
    };

    private PageViewModel? _subscribed;

    public PageThumbnail()
    {
        Loaded += (_, _) =>
        {
            Subscribe(Page);
            Request();
        };
        Unloaded += (_, _) => Subscribe(null);
    }

    public PageViewModel? Page
    {
        get => (PageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public DocumentViewModel? Document
    {
        get => (DocumentViewModel?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public double ThumbnailWidth
    {
        get => (double)GetValue(ThumbnailWidthProperty);
        set => SetValue(ThumbnailWidthProperty, value);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var thumbnail = (PageThumbnail)d;
        if (thumbnail.IsLoaded)
        {
            thumbnail.Subscribe(e.NewValue as PageViewModel);
            thumbnail.Request();
        }
    }

    private void Subscribe(PageViewModel? page)
    {
        if (_subscribed is not null)
        {
            _subscribed.AnnotationsInvalidated -= OnAnnotationsInvalidated;
            _subscribed.ContentInvalidated -= OnContentInvalidated;
            _subscribed.PropertyChanged -= OnPagePropertyChanged;
        }

        _subscribed = page;
        if (page is not null)
        {
            page.AnnotationsInvalidated += OnAnnotationsInvalidated;
            page.ContentInvalidated += OnContentInvalidated;
            page.PropertyChanged += OnPagePropertyChanged;
        }
    }

    private void OnAnnotationsInvalidated(object? sender, EventArgs e) => InvalidateVisual();

    private void OnContentInvalidated(object? sender, EventArgs e) => Request();

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PageViewModel.Thumbnail):
                InvalidateVisual();
                break;
            case nameof(PageViewModel.Width):
            case nameof(PageViewModel.Height):
                InvalidateMeasure();
                InvalidateVisual();
                break;
        }
    }

    private void Request()
    {
        var page = Page;
        var document = Document;
        if (page is null || document is null || !IsLoaded)
        {
            return;
        }

        if (page.Thumbnail is not null && page.ThumbnailVersion == page.ContentVersion)
        {
            return;
        }

        var version = page.ContentVersion;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var scale = ThumbnailWidth * dpi / Math.Max(1, page.Width);

        var hidden = page.GetHiddenTextRegions();
        document.Renderer.Submit((page, "thumb"), RenderScheduler.PriorityThumbnail, pdf =>
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
            return ImageTools.FromBgra(pixels, width, height);
        }, bitmap =>
        {
            page.Thumbnail = bitmap;
            page.ThumbnailVersion = version;
            InvalidateVisual();
        });
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = ThumbnailWidth;
        var ratio = Page?.AspectRatio ?? 1.294;
        return new Size(width, Math.Max(20, width * ratio));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var page = Page;
        var rect = new Rect(RenderSize);
        if (rect.Width < 1 || rect.Height < 1)
        {
            return;
        }

        for (var i = Shadows.Length; i >= 1; i--)
        {
            var shadow = rect;
            shadow.Inflate(i, i);
            shadow.Offset(0, i * 0.5);
            dc.DrawRoundedRectangle(Shadows[i - 1], null, shadow, i + 1, i + 1);
        }

        dc.DrawRectangle(Brushes.White, null, rect);

        if (page?.Thumbnail is { } image)
        {
            dc.DrawImage(image, rect);
        }

        if (page is not null && page.Annotations.Count > 0)
        {
            var scale = rect.Width / Math.Max(1, page.Width);
            dc.PushClip(new RectangleGeometry(rect));
            dc.PushTransform(new ScaleTransform(scale, scale));
            foreach (var annotation in page.Annotations)
            {
                AnnotationRenderer.Draw(dc, annotation, editorChrome: false);
            }

            dc.Pop();
            dc.Pop();
        }

        if (TryFindResource("PageBorderBrush") is Brush border)
        {
            dc.DrawRectangle(null, new Pen(border, 1), rect);
        }
    }
}
