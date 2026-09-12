using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PDFEditor.Models;
using PDFEditor.ViewModels;

namespace PDFEditor.Views.Document;

/// <summary>
/// Surface defilante qui dispose les pages (continu, page unique, deux pages)
/// et ne cree de controle que pour les pages proches de la zone visible.
/// Coordonnees exprimees en pixels independants (DIP).
/// </summary>
public sealed class PagesCanvas : Panel
{
    public const double PageGap = 20;
    public const double OuterMargin = 32;

    private readonly List<Rect> _rects = new();
    private readonly Dictionary<PageViewModel, PageControl> _realized = new();
    private Size _extent;

    public PagesCanvas(DocumentView host)
    {
        Host = host;
        Background = Brushes.Transparent;
        ClipToBounds = false;
        Focusable = false;
    }

    public DocumentView Host { get; }

    /// <summary>Echelle (DIP par point) de la derniere mise en page.</summary>
    public double LayoutScale { get; private set; } = 96.0 / 72.0;

    public IEnumerable<PageControl> RealizedPages => _realized.Values;

    public Rect GetPageRect(int index) => index >= 0 && index < _rects.Count ? _rects[index] : Rect.Empty;

    public PageControl? GetControl(PageViewModel page) => _realized.TryGetValue(page, out var control) ? control : null;

    public void Reset()
    {
        foreach (var control in _realized.Values)
        {
            control.Detach();
        }

        _realized.Clear();
        Children.Clear();
        _rects.Clear();
        InvalidateMeasure();
    }

    // =====================================================================
    // Mise en page
    // =====================================================================

    protected override Size MeasureOverride(Size availableSize)
    {
        ComputeLayout();
        foreach (UIElement child in Children)
        {
            if (child is PageControl { Page: { } page })
            {
                var rect = GetPageRect(page.Index);
                child.Measure(rect.IsEmpty ? new Size(0, 0) : rect.Size);
            }
        }

        return _extent;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in Children)
        {
            if (child is PageControl { Page: { } page })
            {
                var rect = GetPageRect(page.Index);
                child.Arrange(rect.IsEmpty ? new Rect(0, 0, 0, 0) : rect);
            }
        }

        return finalSize;
    }

    private void ComputeLayout()
    {
        _rects.Clear();
        var document = Host.Document;
        if (document is null || document.Pages.Count == 0)
        {
            _extent = new Size(0, 0);
            return;
        }

        var scale = document.Zoom * 96.0 / 72.0;
        LayoutScale = scale;
        var viewport = Host.ViewportSize;
        var pages = document.Pages;

        switch (document.ScrollMode)
        {
            case ScrollModeKind.SinglePage:
            {
                var index = Math.Clamp(document.CurrentPageIndex, 0, pages.Count - 1);
                var width = pages[index].Width * scale;
                var height = pages[index].Height * scale;
                var contentWidth = Math.Max(viewport.Width, width + 2 * OuterMargin);
                var contentHeight = Math.Max(viewport.Height, height + 2 * OuterMargin);

                for (var i = 0; i < pages.Count; i++)
                {
                    _rects.Add(Rect.Empty);
                }

                _rects[index] = new Rect((contentWidth - width) / 2, Math.Max(OuterMargin, (contentHeight - height) / 2), width, height);
                _extent = new Size(contentWidth, contentHeight);
                break;
            }

            case ScrollModeKind.TwoPages:
            {
                var widest = 0.0;
                for (var i = 0; i < pages.Count; i += 2)
                {
                    var row = pages[i].Width * scale + (i + 1 < pages.Count ? PageGap + pages[i + 1].Width * scale : 0);
                    widest = Math.Max(widest, row);
                }

                var contentWidth = Math.Max(viewport.Width, widest + 2 * OuterMargin);
                var y = OuterMargin;
                for (var i = 0; i < pages.Count; i += 2)
                {
                    var w0 = pages[i].Width * scale;
                    var h0 = pages[i].Height * scale;
                    var hasSecond = i + 1 < pages.Count;
                    var w1 = hasSecond ? pages[i + 1].Width * scale : 0;
                    var h1 = hasSecond ? pages[i + 1].Height * scale : 0;
                    var rowWidth = w0 + (hasSecond ? PageGap + w1 : 0);
                    var rowHeight = Math.Max(h0, h1);
                    var x = (contentWidth - rowWidth) / 2;

                    _rects.Add(new Rect(x, y + (rowHeight - h0) / 2, w0, h0));
                    if (hasSecond)
                    {
                        _rects.Add(new Rect(x + w0 + PageGap, y + (rowHeight - h1) / 2, w1, h1));
                    }

                    y += rowHeight + PageGap;
                }

                _extent = new Size(contentWidth, y - PageGap + OuterMargin);
                break;
            }

            default:
            {
                var widest = pages.Max(p => p.Width) * scale;
                var contentWidth = Math.Max(viewport.Width, widest + 2 * OuterMargin);
                var y = OuterMargin;
                foreach (var page in pages)
                {
                    var width = page.Width * scale;
                    var height = page.Height * scale;
                    _rects.Add(new Rect((contentWidth - width) / 2, y, width, height));
                    y += height + PageGap;
                }

                _extent = new Size(contentWidth, y - PageGap + OuterMargin);
                break;
            }
        }
    }

    // =====================================================================
    // Virtualisation
    // =====================================================================

    public void UpdateRealization(Rect viewport, bool allowDetail)
    {
        var document = Host.Document;
        if (document is null)
        {
            return;
        }

        if (_rects.Count != document.Pages.Count)
        {
            InvalidateMeasure();
            UpdateLayout();
        }

        var area = viewport;
        area.Inflate(Math.Max(200, viewport.Width * 0.25), Math.Max(400, viewport.Height * 0.8));

        var wanted = new HashSet<PageViewModel>();
        for (var i = 0; i < document.Pages.Count && i < _rects.Count; i++)
        {
            var rect = _rects[i];
            if (!rect.IsEmpty && rect.IntersectsWith(area))
            {
                wanted.Add(document.Pages[i]);
            }
        }

        foreach (var (page, control) in _realized.ToList())
        {
            if (!wanted.Contains(page))
            {
                Host.Tools.OnPageDetaching(control);
                control.Detach();
                Children.Remove(control);
                _realized.Remove(page);
            }
        }

        foreach (var page in wanted)
        {
            if (!_realized.ContainsKey(page))
            {
                var control = new PageControl(Host);
                control.Attach(page);
                _realized[page] = control;
                Children.Add(control);
            }
        }

        foreach (var control in _realized.Values)
        {
            control.EnsureRendered(viewport, allowDetail);
        }
    }

    // =====================================================================
    // Positions
    // =====================================================================

    /// <summary>Page sous un point du canevas (ou la plus proche si demande).</summary>
    public (PageControl? Control, PageViewModel? Page, Point PagePoint) HitTest(Point canvasPoint, bool nearest = false)
    {
        var document = Host.Document;
        if (document is null)
        {
            return (null, null, default);
        }

        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _rects.Count && i < document.Pages.Count; i++)
        {
            var rect = _rects[i];
            if (rect.IsEmpty)
            {
                continue;
            }

            if (rect.Contains(canvasPoint))
            {
                best = i;
                bestDistance = 0;
                break;
            }

            if (nearest)
            {
                var dx = canvasPoint.X < rect.Left ? rect.Left - canvasPoint.X : canvasPoint.X > rect.Right ? canvasPoint.X - rect.Right : 0;
                var dy = canvasPoint.Y < rect.Top ? rect.Top - canvasPoint.Y : canvasPoint.Y > rect.Bottom ? canvasPoint.Y - rect.Bottom : 0;
                var distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }
        }

        if (best < 0)
        {
            return (null, null, default);
        }

        var page = document.Pages[best];
        return (GetControl(page), page, ToPagePoint(page, canvasPoint));
    }

    public Point ToPagePoint(PageViewModel page, Point canvasPoint)
    {
        var rect = GetPageRect(page.Index);
        return rect.IsEmpty
            ? new Point(0, 0)
            : new Point((canvasPoint.X - rect.X) / LayoutScale, (canvasPoint.Y - rect.Y) / LayoutScale);
    }

    /// <summary>Page qui occupe le plus de hauteur visible.</summary>
    public int FindCurrentPage(Rect viewport)
    {
        var best = -1;
        var bestHeight = 0.0;
        for (var i = 0; i < _rects.Count; i++)
        {
            var rect = _rects[i];
            if (rect.IsEmpty || rect.Bottom < viewport.Top || rect.Top > viewport.Bottom)
            {
                continue;
            }

            var visible = Math.Min(rect.Bottom, viewport.Bottom) - Math.Max(rect.Top, viewport.Top);
            if (visible > bestHeight + 0.5)
            {
                bestHeight = visible;
                best = i;
            }
        }

        return best;
    }
}
