using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PDFEditor.Models;
using PDFEditor.ViewModels;

namespace PDFEditor.Views.Document;

/// <summary>
/// Zone du document : defilement, zoom (molette, ajustements), navigation,
/// HUD de page et depot de fichiers. Les interactions sur les pages sont
/// deleguees au <see cref="ToolController"/>.
/// </summary>
public partial class DocumentView : UserControl
{
    private static readonly string[] PdfExtensions = { ".pdf" };
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    private readonly DispatcherTimer _detailTimer;
    private readonly DispatcherTimer _hudTimer;
    private DocumentViewModel? _document;
    private MainViewModel? _main;
    private Point? _zoomAnchor;
    private bool _suppressPageTracking;
    private bool _hudVisible;

    public DocumentView()
    {
        InitializeComponent();

        Canvas = new PagesCanvas(this);
        Scroller.Content = Canvas;
        Tools = new ToolController(this);

        _detailTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _detailTimer.Tick += (_, _) =>
        {
            _detailTimer.Stop();
            Canvas.UpdateRealization(ViewportRect, true);
        };

        _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _hudTimer.Tick += (_, _) =>
        {
            _hudTimer.Stop();
            if (!Hud.IsMouseOver && !PageNumberBox.IsKeyboardFocusWithin)
            {
                SetHudVisible(false);
            }
        };

        Scroller.ScrollChanged += OnScrollChanged;
        SizeChanged += OnSizeChanged;
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += (_, e) => Tools.OnPreviewKeyDown(e);
        PreviewTextInput += (_, e) => Tools.OnPreviewTextInput(e);
        MouseMove += (_, _) => ShowHud();
        DataContextChanged += OnDataContextChanged;

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;

        PageNumberBox.KeyDown += OnPageNumberKeyDown;
        PageNumberBox.GotKeyboardFocus += (_, _) => PageNumberBox.SelectAll();
        PageNumberBox.LostKeyboardFocus += (_, _) => UpdateHud();

        Loaded += (_, _) => RefreshLayout();
    }

    public PagesCanvas Canvas { get; }

    public ToolController Tools { get; }

    public IDialogService? Dialogs { get; set; }

    public MainViewModel? Main => DataContext as MainViewModel;

    public Size ViewportSize => new(
        Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : ActualWidth,
        Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : ActualHeight);

    public Rect ViewportRect => new(Scroller.HorizontalOffset, Scroller.VerticalOffset, ViewportSize.Width, ViewportSize.Height);

    public DocumentViewModel? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value))
            {
                return;
            }

            Tools.Reset();
            if (_document is not null)
            {
                Unsubscribe(_document);
            }

            _document = value;
            Canvas.Reset();

            if (value is not null)
            {
                Subscribe(value);
            }

            UpdateHud();
            Scroller.ScrollToHome();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyFitZoom();
                RefreshLayout();
            }), DispatcherPriority.Loaded);
        }
    }

    public void FocusDocument()
    {
        if (!IsKeyboardFocused)
        {
            Focus();
            Keyboard.Focus(this);
        }
    }

    // =====================================================================
    // Abonnements
    // =====================================================================

    private void Subscribe(DocumentViewModel document)
    {
        document.PropertyChanged += OnDocumentPropertyChanged;
        document.PagesChanged += OnPagesChanged;
        document.NavigationRequested += OnNavigationRequested;
        document.SelectionChanged += OnSelectionChanged;
        document.TextSelectionChanged += OnMarksChanged;
        document.SearchResultsChanged += OnMarksChanged;
        document.ToolChanged += OnToolChanged;
    }

    private void Unsubscribe(DocumentViewModel document)
    {
        document.PropertyChanged -= OnDocumentPropertyChanged;
        document.PagesChanged -= OnPagesChanged;
        document.NavigationRequested -= OnNavigationRequested;
        document.SelectionChanged -= OnSelectionChanged;
        document.TextSelectionChanged -= OnMarksChanged;
        document.SearchResultsChanged -= OnMarksChanged;
        document.ToolChanged -= OnToolChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_main is not null)
        {
            _main.PropertyChanged -= OnMainPropertyChanged;
        }

        _main = DataContext as MainViewModel;
        if (_main is not null)
        {
            _main.PropertyChanged += OnMainPropertyChanged;
        }
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.NightMode) or nameof(MainViewModel.HighlightFormFields))
        {
            foreach (var control in Canvas.RealizedPages.ToList())
            {
                control.ForceRerender();
                control.InvalidateLayers();
            }
        }
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.Zoom):
                OnZoomChanged();
                break;

            case nameof(DocumentViewModel.ZoomMode):
                ApplyFitZoom();
                break;

            case nameof(DocumentViewModel.ScrollMode):
                Canvas.Reset();
                ApplyFitZoom();
                RefreshLayout();
                if (_document is not null)
                {
                    OnNavigationRequested(_document.CurrentPageIndex, null);
                }

                break;

            case nameof(DocumentViewModel.CurrentPageIndex):
                UpdateHud();
                if (_document?.ScrollMode == ScrollModeKind.SinglePage)
                {
                    Canvas.Reset();
                    if (_document.ZoomMode == ZoomModeKind.FitPage)
                    {
                        ApplyFitZoom();
                    }

                    RefreshLayout();
                    Scroller.ScrollToHome();
                }

                break;

            case nameof(DocumentViewModel.PageCount):
                UpdateHud();
                break;
        }
    }

    private void OnPagesChanged()
    {
        Tools.Reset();
        Canvas.Reset();
        ApplyFitZoom();
        RefreshLayout();
        UpdateHud();
    }

    private void OnSelectionChanged()
    {
        foreach (var control in Canvas.RealizedPages)
        {
            control.InvalidateOverlay();
        }
    }

    private void OnMarksChanged()
    {
        foreach (var control in Canvas.RealizedPages)
        {
            control.InvalidateMarks();
        }
    }

    private void OnToolChanged()
    {
        Tools.OnToolChanged();
        foreach (var control in Canvas.RealizedPages)
        {
            control.InvalidateOverlay();
            control.InvalidateMarks();
        }
    }

    // =====================================================================
    // Mise en page, zoom, defilement
    // =====================================================================

    public void RefreshLayout()
    {
        if (_document is null)
        {
            return;
        }

        Canvas.InvalidateMeasure();
        Scroller.UpdateLayout();
        Canvas.UpdateRealization(ViewportRect, false);
        _detailTimer.Stop();
        _detailTimer.Start();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyFitZoom();
        RefreshLayout();
    }

    private void ApplyFitZoom()
    {
        var document = _document;
        if (document is null || document.ZoomMode == ZoomModeKind.Custom || document.Pages.Count == 0 || ActualWidth < 50)
        {
            return;
        }

        const double pointToDip = 96.0 / 72.0;
        var availableWidth = ActualWidth - 2 * PagesCanvas.OuterMargin - 16;
        var availableHeight = ActualHeight - 2 * PagesCanvas.OuterMargin;
        var widest = document.Pages.Max(p => p.Width);

        var zoom = document.ScrollMode == ScrollModeKind.TwoPages
            ? (availableWidth - PagesCanvas.PageGap) / (2 * widest * pointToDip)
            : availableWidth / (widest * pointToDip);

        if (document.ZoomMode == ZoomModeKind.FitPage)
        {
            var page = document.CurrentPage ?? document.Pages[0];
            zoom = Math.Min(zoom, availableHeight / (page.Height * pointToDip));
        }

        zoom = Math.Clamp(zoom, DocumentViewModel.MinZoom, DocumentViewModel.MaxZoom);
        if (Math.Abs(zoom - document.Zoom) > 0.001)
        {
            _zoomAnchor = null;
            document.Zoom = zoom;
            if (document.ZoomMode == ZoomModeKind.FitPage)
            {
                OnNavigationRequested(document.CurrentPageIndex, null);
            }
        }
    }

    private void OnZoomChanged()
    {
        var document = _document;
        if (document is null)
        {
            return;
        }

        var viewport = ViewportSize;
        var anchor = _zoomAnchor ?? new Point(viewport.Width / 2, viewport.Height / 2);
        _zoomAnchor = null;

        var canvasPoint = new Point(Scroller.HorizontalOffset + anchor.X, Scroller.VerticalOffset + anchor.Y);
        var hit = Canvas.HitTest(canvasPoint, nearest: true);

        Canvas.InvalidateMeasure();
        Scroller.UpdateLayout();

        if (hit.Page is not null)
        {
            var rect = Canvas.GetPageRect(hit.Page.Index);
            if (!rect.IsEmpty)
            {
                var target = new Point(rect.X + hit.PagePoint.X * Canvas.LayoutScale, rect.Y + hit.PagePoint.Y * Canvas.LayoutScale);
                _suppressPageTracking = true;
                Scroller.ScrollToHorizontalOffset(Math.Max(0, target.X - anchor.X));
                Scroller.ScrollToVerticalOffset(Math.Max(0, target.Y - anchor.Y));
                Dispatcher.BeginInvoke(new Action(() => _suppressPageTracking = false), DispatcherPriority.Background);
            }
        }

        foreach (var control in Canvas.RealizedPages)
        {
            control.InvalidateLayers();
        }

        Tools.OnZoomChanged();
        RefreshLayout();
        UpdateHud();
        ShowHud();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var document = _document;
        if (document is null)
        {
            return;
        }

        Canvas.UpdateRealization(ViewportRect, false);
        _detailTimer.Stop();
        _detailTimer.Start();

        if (!_suppressPageTracking && document.ScrollMode != ScrollModeKind.SinglePage
            && (e.VerticalChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0))
        {
            var index = Canvas.FindCurrentPage(ViewportRect);
            if (index >= 0)
            {
                document.CurrentPageIndex = index;
            }
        }

        if (e.VerticalChange != 0 || e.HorizontalChange != 0)
        {
            ShowHud();
        }

        Tools.OnScrolled();
    }

    private void OnNavigationRequested(int index, Rect? area)
    {
        var document = _document;
        if (document is null)
        {
            return;
        }

        if (document.ScrollMode == ScrollModeKind.SinglePage)
        {
            Canvas.InvalidateMeasure();
            Scroller.UpdateLayout();
        }

        var rect = Canvas.GetPageRect(index);
        if (rect.IsEmpty)
        {
            return;
        }

        var viewport = ViewportRect;
        var scale = Canvas.LayoutScale;
        var y = rect.Top - 16;
        double? x = null;

        if (area is { IsEmpty: false } zone)
        {
            var top = rect.Top + zone.Top * scale;
            var height = zone.Height * scale;
            y = top >= viewport.Top + 20 && top + height <= viewport.Bottom - 20
                ? viewport.Top
                : top - viewport.Height * 0.35;

            var left = rect.Left + zone.Left * scale;
            var width = zone.Width * scale;
            if (left < viewport.Left || left + width > viewport.Right)
            {
                x = left - viewport.Width * 0.3;
            }
        }

        _suppressPageTracking = true;
        Scroller.ScrollToVerticalOffset(Math.Max(0, y));
        if (x is { } horizontal)
        {
            Scroller.ScrollToHorizontalOffset(Math.Max(0, horizontal));
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _suppressPageTracking = false;
            Canvas.UpdateRealization(ViewportRect, true);
        }), DispatcherPriority.Background);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var document = _document;
        if (document is null)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            _zoomAnchor = e.GetPosition(Scroller);
            document.ZoomMode = ZoomModeKind.Custom;
            document.Zoom *= Math.Pow(1.0016, e.Delta);
            e.Handled = true;
            return;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        if (document.ScrollMode == ScrollModeKind.SinglePage)
        {
            var atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1;
            var atTop = Scroller.VerticalOffset <= 1;
            if (e.Delta < 0 && atBottom && document.CurrentPageIndex < document.PageCount - 1)
            {
                document.GoToPage(document.CurrentPageIndex + 1);
                e.Handled = true;
            }
            else if (e.Delta > 0 && atTop && document.CurrentPageIndex > 0)
            {
                document.GoToPage(document.CurrentPageIndex - 1);
                e.Handled = true;
            }
        }
    }

    // =====================================================================
    // HUD
    // =====================================================================

    private void ShowHud()
    {
        if (_document is null)
        {
            return;
        }

        SetHudVisible(true);
        _hudTimer.Stop();
        _hudTimer.Start();
    }

    private void SetHudVisible(bool visible)
    {
        if (_hudVisible == visible)
        {
            return;
        }

        _hudVisible = visible;
        Hud.IsHitTestVisible = visible;
        Hud.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(visible ? 120 : 450)));
    }

    private void UpdateHud()
    {
        var document = _document;
        if (!PageNumberBox.IsKeyboardFocusWithin)
        {
            PageNumberBox.Text = document is null ? "" : document.CurrentPageNumber.ToString();
        }

        PageCountText.Text = document is null ? "" : $"sur {document.PageCount}";
        ZoomText.Text = document?.ZoomLabel ?? "";
    }

    private void OnPageNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (_document is not null && int.TryParse(PageNumberBox.Text.Trim(), out var number))
            {
                _document.GoToPage(number - 1);
            }

            FocusDocument();
            UpdateHud();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FocusDocument();
            UpdateHud();
            e.Handled = true;
        }
    }

    // =====================================================================
    // Depot de fichiers
    // =====================================================================

    private static string[] GetFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : Array.Empty<string>();

    private static bool HasExtension(string path, string[] extensions) =>
        extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var files = GetFiles(e);
        var images = files.Any(f => HasExtension(f, ImageExtensions));
        var pdfs = files.Any(f => HasExtension(f, PdfExtensions));

        if (_document is null || (!images && !pdfs))
        {
            e.Effects = DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            e.Effects = DragDropEffects.Copy;
            DropText.Text = images && !pdfs ? "Déposer pour ajouter l’image sur la page" : "Déposer pour ouvrir ou insérer les pages";
            DropOverlay.Visibility = Visibility.Visible;
        }

        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var document = _document;
        var main = Main;
        var files = GetFiles(e);
        if (document is null || main is null || files.Length == 0)
        {
            return;
        }

        e.Handled = true;
        var images = files.Where(f => HasExtension(f, ImageExtensions)).ToList();
        var pdfs = files.Where(f => HasExtension(f, PdfExtensions)).ToList();
        var dropPoint = e.GetPosition(Canvas);

        if (images.Count > 0)
        {
            Tools.InsertImages(images, dropPoint);
        }

        if (pdfs.Count == 0)
        {
            return;
        }

        var title = pdfs.Count == 1
            ? $"Que faire de « {System.IO.Path.GetFileName(pdfs[0])} » ?"
            : $"Que faire de ces {pdfs.Count} documents ?";

        var choice = Dialogs?.Alert(title, "Vous pouvez insérer leurs pages dans ce document ou les ouvrir dans de nouvelles fenêtres.",
            new[] { "Insérer les pages", "Ouvrir", "Annuler" }, 0, 2, icon: AlertIcon.Question) ?? 2;

        if (choice == 1)
        {
            foreach (var path in pdfs)
            {
                _ = main.OpenPathAsync(path);
            }
        }
        else if (choice == 0)
        {
            var hit = Canvas.HitTest(dropPoint, nearest: true);
            var at = hit.Page is null ? document.PageCount : hit.Page.Index + 1;
            main.InsertPdfFiles(pdfs, at);
        }
    }
}
