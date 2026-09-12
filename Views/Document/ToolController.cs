using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using PDFEditor.Models;
using PDFEditor.Pdf;
using PDFEditor.Rendering;
using PDFEditor.Services;
using PDFEditor.ViewModels;

namespace PDFEditor.Views.Document;

public enum HandleKind { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Start, End }

/// <summary>
/// Interactions souris et clavier sur les pages : selection et manipulation des
/// annotations, selection de texte, dessin, creation d'objets, formulaires,
/// liens, edition de texte en place, menus contextuels.
/// </summary>
public sealed class ToolController
{
    private enum DragKind { None, Pan, Move, Resize, Endpoint, TextSelect, Ink, Box, Line, Marquee, FormField }

    private const double HandleRadius = 4.5;
    private const double HandleHitDistance = 8;
    private const double HitTolerance = 4;

    private readonly DocumentView _view;

    private DragKind _drag;
    private PageControl? _dragControl;
    private PageViewModel? _dragPage;
    private Point _startCanvas;
    private Point _startViewport;
    private Point _startOffset;
    private Point _startPage;
    private HandleKind _handle;
    private Rect _startBounds;
    private int _anchorChar = -1;
    private bool _moved;
    private List<Point>? _stroke;
    private Rect? _previewRect;
    private (Point Start, Point End)? _previewLine;
    private PdfLinkTarget? _pendingLink;
    private int _formFieldType = -1;

    private Rect? _marquee;
    private PageViewModel? _marqueePage;

    private PdfTextLine? _hoverLine;
    private PageViewModel? _hoverPage;
    private DateTime _lastHover = DateTime.MinValue;

    private TextBox? _editor;
    private TextBoxAnnotation? _editing;
    private PageViewModel? _editingPage;
    private bool _editingIsNew;
    private string _editingOriginalText = "";

    private Border? _noteCard;
    private NoteAnnotation? _openNote;
    private PageViewModel? _notePage;
    private bool _noteIsNew;

    private PageViewModel? _formPage;

    public ToolController(DocumentView view)
    {
        _view = view;
        var canvas = view.Canvas;
        canvas.MouseLeftButtonDown += OnMouseLeftButtonDown;
        canvas.MouseLeftButtonUp += OnMouseLeftButtonUp;
        canvas.MouseRightButtonUp += OnMouseRightButtonUp;
        canvas.MouseDown += OnMouseDown;
        canvas.MouseUp += OnMouseUp;
        canvas.MouseMove += OnMouseMove;
        canvas.MouseLeave += (_, _) => ClearHover();
        canvas.LostMouseCapture += (_, _) =>
        {
            if (_drag is DragKind.Pan)
            {
                _drag = DragKind.None;
            }
        };
    }

    private DocumentViewModel? Document => _view.Document;

    // =====================================================================
    // Etat
    // =====================================================================

    public void Reset()
    {
        CommitInlineEdit();
        CloseNote();
        _drag = DragKind.None;
        _stroke = null;
        _previewRect = null;
        _previewLine = null;
        _marquee = null;
        _marqueePage = null;
        _hoverLine = null;
        _hoverPage = null;
        _formPage = null;
        if (_view.Canvas.IsMouseCaptured)
        {
            _view.Canvas.ReleaseMouseCapture();
        }
    }

    public void OnToolChanged()
    {
        _previewRect = null;
        _previewLine = null;
        _stroke = null;
        ClearMarquee();
        ClearHover();
        _view.Canvas.Cursor = Document?.Tool switch
        {
            EditorTool.Hand => Cursors.Hand,
            EditorTool.Pen or EditorTool.Highlighter => Cursors.Pen,
            EditorTool.Select or EditorTool.EditText or EditorTool.TextHighlight or EditorTool.TextUnderline
                or EditorTool.TextStrike or EditorTool.TextSquiggly or null => Cursors.Arrow,
            _ => Cursors.Cross
        };
    }

    public void OnZoomChanged()
    {
        if (_editor is not null && _editing is not null)
        {
            ApplyEditorStyle(_editor, _editing, _view.Canvas.LayoutScale);
        }

        if (_noteCard is not null && _openNote is not null)
        {
            PositionNoteCard(_noteCard, _openNote, _view.Canvas.LayoutScale);
        }
    }

    public void OnScrolled()
    {
    }

    public void OnPageDetaching(PageControl control)
    {
        if (control.Page is null)
        {
            return;
        }

        if (ReferenceEquals(_editingPage, control.Page))
        {
            CommitInlineEdit();
        }

        if (ReferenceEquals(_notePage, control.Page))
        {
            CloseNote();
        }
    }

    public bool IsInlineEditing(Annotation annotation) => _editor is not null && ReferenceEquals(_editing, annotation);

    private Brush Resource(string key) => _view.TryFindResource(key) as Brush ?? Brushes.Transparent;

    // =====================================================================
    // Souris
    // =====================================================================

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || Document is null)
        {
            return;
        }

        BeginPan(e);
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _drag != DragKind.Pan)
        {
            return;
        }

        _drag = DragKind.None;
        _view.Canvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void BeginPan(MouseEventArgs e)
    {
        _drag = DragKind.Pan;
        _startViewport = e.GetPosition(_view.Scroller);
        _startOffset = new Point(_view.Scroller.HorizontalOffset, _view.Scroller.VerticalOffset);
        _view.Canvas.CaptureMouse();
        _view.Canvas.Cursor = Cursors.SizeAll;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var doc = Document;
        if (doc is null || IsFromEditor(e.OriginalSource))
        {
            return;
        }

        CommitInlineEdit();
        CloseNote();
        _view.FocusDocument();

        var canvasPoint = e.GetPosition(_view.Canvas);
        var hit = _view.Canvas.HitTest(canvasPoint);
        _startCanvas = canvasPoint;
        _moved = false;
        _pendingLink = null;

        if (doc.Tool == EditorTool.Hand || (hit.Page is null && doc.Tool is EditorTool.Select or EditorTool.Marquee))
        {
            if (hit.Page is null)
            {
                doc.Select(null, null);
                doc.ClearTextSelection();
                ClearMarquee();
            }

            BeginPan(e);
            e.Handled = true;
            return;
        }

        if (hit.Page is null || hit.Control is null)
        {
            return;
        }

        var page = hit.Page;
        var control = hit.Control;
        var point = hit.PagePoint;
        _dragControl = control;
        _dragPage = page;
        _startPage = point;

        switch (doc.Tool)
        {
            case EditorTool.Select:
                SelectDown(doc, control, page, point, e);
                break;

            case EditorTool.Marquee:
                ClearMarquee();
                _drag = DragKind.Marquee;
                _previewRect = new Rect(point, point);
                break;

            case EditorTool.Pen:
            case EditorTool.Highlighter:
                doc.Select(null, null);
                _drag = DragKind.Ink;
                _stroke = new List<Point> { point };
                break;

            case EditorTool.TextBox:
            case EditorTool.Rectangle:
            case EditorTool.Ellipse:
            case EditorTool.Redact:
            case EditorTool.Whiteout:
            case EditorTool.Link:
                doc.Select(null, null);
                _drag = DragKind.Box;
                _previewRect = new Rect(point, point);
                break;

            case EditorTool.Line:
            case EditorTool.Arrow:
                doc.Select(null, null);
                _drag = DragKind.Line;
                _previewLine = (point, point);
                break;

            case EditorTool.TextHighlight:
            case EditorTool.TextUnderline:
            case EditorTool.TextStrike:
            case EditorTool.TextSquiggly:
                BeginTextSelection(doc, page, point, e.ClickCount, markup: true);
                break;

            case EditorTool.EditText:
                EditTextAt(doc, control, page, point);
                e.Handled = true;
                return;

            case EditorTool.Note:
                PlaceNote(doc, control, page, point);
                e.Handled = true;
                return;

            case EditorTool.Stamp:
                PlaceStamp(doc, page, point);
                e.Handled = true;
                return;

            case EditorTool.Signature:
                PlaceSignature(doc, page, point);
                e.Handled = true;
                return;

            default:
                return;
        }

        if (_drag != DragKind.None)
        {
            _view.Canvas.CaptureMouse();
            control.InvalidateOverlay();
        }

        e.Handled = true;
    }

    private void SelectDown(DocumentViewModel doc, PageControl control, PageViewModel page, Point point, MouseButtonEventArgs e)
    {
        var scale = control.Scale;

        // 1. Poignee de l'annotation selectionnee.
        if (doc.SelectedAnnotation is { } selected && ReferenceEquals(doc.SelectedAnnotationPage, page))
        {
            var handle = HitHandle(selected, point, scale);
            if (handle != HandleKind.None)
            {
                _handle = handle;
                _startBounds = selected.Bounds;
                _drag = handle is HandleKind.Start or HandleKind.End ? DragKind.Endpoint : DragKind.Resize;
                return;
            }
        }

        // 2. Annotation sous le pointeur (de la plus haute a la plus basse).
        var annotation = page.Annotations.Reverse().FirstOrDefault(a => a.HitTest(point, HitTolerance / scale));
        if (annotation is not null)
        {
            doc.ClearTextSelection();
            doc.Select(page, annotation);

            if (e.ClickCount >= 2)
            {
                OpenAnnotation(doc, control, page, annotation);
                return;
            }

            if (annotation.CanMove)
            {
                _drag = DragKind.Move;
                _startBounds = annotation.Bounds;
            }

            return;
        }

        // 3. Champ de formulaire.
        var fieldType = doc.Pdf.GetFormFieldTypeAt(page.Index, point);
        if (fieldType >= 0)
        {
            doc.Select(null, null);
            doc.ClearTextSelection();
            _formPage = page;
            _formFieldType = fieldType;
            if (e.ClickCount >= 2)
            {
                doc.Pdf.FormDoubleClick(page.Index, point, FormModifiers());
            }
            else
            {
                doc.Pdf.FormMouseDown(page.Index, point, FormModifiers());
            }

            _drag = DragKind.FormField;
            control.ForceRerender();
            return;
        }

        if (doc.Pdf.FocusedFormPage >= 0)
        {
            doc.Pdf.FormKillFocus();
            if (_formPage is not null)
            {
                RefreshPage(_formPage);
            }

            _formPage = null;
        }

        // 4. Lien : suivi au relachement si la souris n'a pas bouge.
        _pendingLink = doc.Pdf.GetLinkAt(page.Index, point);

        // 5. Texte.
        var text = doc.Pdf.GetTextPage(page.Index);
        if (_pendingLink is null && text.HasText && (text.IsOverText(point) || e.ClickCount > 1))
        {
            doc.Select(null, null);
            BeginTextSelection(doc, page, point, e.ClickCount, markup: false);
            return;
        }

        // 6. Zone vide : deselection et defilement a la main.
        doc.Select(null, null);
        doc.ClearTextSelection();
        BeginPan(e);
    }

    private void BeginTextSelection(DocumentViewModel doc, PageViewModel page, Point point, int clickCount, bool markup)
    {
        var text = doc.Pdf.GetTextPage(page.Index);
        var index = text.NearestChar(point);
        if (index < 0)
        {
            return;
        }

        if (clickCount >= 2)
        {
            doc.SetTextSelection(page, clickCount >= 3 ? text.LineSpanAt(index) : text.WordAt(index));
            if (markup)
            {
                doc.CreateMarkupFromSelection(MarkupFor(doc.Tool));
            }

            return;
        }

        doc.ClearTextSelection();
        _anchorChar = index;
        _drag = DragKind.TextSelect;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var doc = Document;
        if (doc is null)
        {
            return;
        }

        var canvasPoint = e.GetPosition(_view.Canvas);
        if (_drag == DragKind.None)
        {
            UpdateHover(doc, canvasPoint);
            return;
        }

        if (!_moved && (canvasPoint - _startCanvas).Length >= 3)
        {
            _moved = true;
        }

        if (_drag == DragKind.Pan)
        {
            var current = e.GetPosition(_view.Scroller);
            _view.Scroller.ScrollToHorizontalOffset(_startOffset.X - (current.X - _startViewport.X));
            _view.Scroller.ScrollToVerticalOffset(_startOffset.Y - (current.Y - _startViewport.Y));
            return;
        }

        var page = _dragPage;
        var control = _dragControl;
        if (page is null)
        {
            return;
        }

        var point = _view.Canvas.ToPagePoint(page, canvasPoint);
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var selected = doc.SelectedAnnotation;

        switch (_drag)
        {
            case DragKind.Move when selected is not null && _moved:
            {
                var target = point - _startPage;
                if (shift)
                {
                    target = Math.Abs(target.X) >= Math.Abs(target.Y) ? new Vector(target.X, 0) : new Vector(0, target.Y);
                }

                var current = selected.Bounds.TopLeft - _startBounds.TopLeft;
                selected.Translate(target - current);
                break;
            }

            case DragKind.Resize when selected is not null:
                selected.Resize(ComputeResize(_startBounds, _handle, point, selected.KeepAspectRatio || shift));
                if (selected is TextBoxAnnotation box && _handle is HandleKind.Top or HandleKind.Bottom or HandleKind.TopLeft or HandleKind.TopRight or HandleKind.BottomLeft or HandleKind.BottomRight)
                {
                    box.AutoHeight = false;
                }

                break;

            case DragKind.Endpoint when selected is LineAnnotation line:
            {
                var anchor = _handle == HandleKind.Start ? line.End : line.Start;
                line.SetEndpoint(_handle == HandleKind.Start, shift ? Snap45(anchor, point) : point);
                break;
            }

            case DragKind.TextSelect:
            {
                var text = doc.Pdf.GetTextPage(page.Index);
                var index = text.NearestChar(point);
                if (index >= 0 && _anchorChar >= 0 && _moved)
                {
                    doc.SetTextSelection(page, new TextSpan(Math.Min(index, _anchorChar), Math.Max(index, _anchorChar)));
                }

                break;
            }

            case DragKind.Ink when _stroke is not null && control is not null:
                if ((point - _stroke[^1]).Length >= 0.7 / control.Scale)
                {
                    _stroke.Add(point);
                    control.InvalidateOverlay();
                }

                break;

            case DragKind.Box:
            case DragKind.Marquee:
            {
                var end = point;
                if (shift && _drag == DragKind.Box)
                {
                    var dx = point.X - _startPage.X;
                    var dy = point.Y - _startPage.Y;
                    var size = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    end = new Point(_startPage.X + (dx < 0 ? -size : size), _startPage.Y + (dy < 0 ? -size : size));
                }

                _previewRect = new Rect(_startPage, end);
                control?.InvalidateOverlay();
                break;
            }

            case DragKind.Line:
                _previewLine = (_startPage, shift ? Snap45(_startPage, point) : point);
                control?.InvalidateOverlay();
                break;

            case DragKind.FormField:
                doc.Pdf.FormMouseMove(page.Index, point, FormModifiers() | (1 << 6));
                break;
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var doc = Document;
        var drag = _drag;
        _drag = DragKind.None;

        if (_view.Canvas.IsMouseCaptured)
        {
            _view.Canvas.ReleaseMouseCapture();
        }

        if (doc is null)
        {
            return;
        }

        var page = _dragPage;
        var control = _dragControl;
        var canvasPoint = e.GetPosition(_view.Canvas);
        var point = page is null ? default : _view.Canvas.ToPagePoint(page, canvasPoint);

        try
        {
            switch (drag)
            {
                case DragKind.Pan:
                    OnToolChanged();
                    if (!_moved && _pendingLink is not null)
                    {
                        FollowLink(doc, _pendingLink);
                    }

                    break;

                case DragKind.Move:
                    if (_moved)
                    {
                        doc.CommitPendingEdit("Déplacer");
                    }

                    break;

                case DragKind.Resize:
                    doc.CommitPendingEdit("Redimensionner");
                    break;

                case DragKind.Endpoint:
                    doc.CommitPendingEdit("Modifier la ligne");
                    break;

                case DragKind.TextSelect:
                    if (!_moved)
                    {
                        doc.ClearTextSelection();
                    }
                    else if (IsMarkupTool(doc.Tool) && doc.HasTextSelection)
                    {
                        doc.CreateMarkupFromSelection(MarkupFor(doc.Tool));
                    }

                    break;

                case DragKind.Ink when page is not null && control is not null:
                    FinishInk(doc, page, control);
                    break;

                case DragKind.Box when page is not null && control is not null:
                    FinishBox(doc, page, control);
                    break;

                case DragKind.Line when page is not null:
                    FinishLine(doc, page);
                    break;

                case DragKind.Marquee when page is not null && control is not null:
                    FinishMarquee(doc, page, control);
                    break;

                case DragKind.FormField when page is not null:
                    doc.Pdf.FormMouseUp(page.Index, point, FormModifiers());
                    if (_formFieldType is 2 or 3 or 4 or 5)
                    {
                        doc.MarkFormsDirty();
                    }

                    RefreshPage(page);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Outils] {ex}");
            _view.Dialogs?.Alert("L’opération a échoué.", ex.Message, new[] { "OK" }, icon: AlertIcon.Error);
        }
        finally
        {
            _previewRect = null;
            _previewLine = null;
            _stroke = null;
            control?.InvalidateOverlay();
        }
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var doc = Document;
        var main = _view.Main;
        if (doc is null || main is null || IsFromEditor(e.OriginalSource))
        {
            return;
        }

        CommitInlineEdit();
        var hit = _view.Canvas.HitTest(e.GetPosition(_view.Canvas));
        if (hit.Page is null || hit.Control is null)
        {
            return;
        }

        var page = hit.Page;
        var control = hit.Control;
        var point = hit.PagePoint;
        var menu = new ContextMenu();

        var annotation = page.Annotations.Reverse().FirstOrDefault(a => a.HitTest(point, HitTolerance / control.Scale));
        if (annotation is not null)
        {
            doc.Tool = EditorTool.Select;
            doc.Select(page, annotation);
            var specific = 0;

            if (annotation is TextBoxAnnotation box)
            {
                menu.Items.Add(Item("Modifier le texte", () => BeginInlineEdit(control, page, box)));
                specific++;
            }

            if (annotation is NoteAnnotation note)
            {
                menu.Items.Add(Item("Ouvrir la note", () => OpenNote(control, page, note, false)));
                specific++;
            }

            if (annotation is LinkAnnotation link)
            {
                menu.Items.Add(Item("Ouvrir le lien", () => OpenUri(link.Uri)));
                menu.Items.Add(Item("Modifier l’adresse…", () => EditLink(doc, link)));
                specific += 2;
            }

            if (annotation is StampAnnotation stamp)
            {
                menu.Items.Add(Item("Modifier le libellé…", () => EditStamp(doc, stamp)));
                specific++;
            }

            if (specific > 0)
            {
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(CommandItem("Couper", main.CutCommand, "Ctrl+X"));
            menu.Items.Add(CommandItem("Copier", main.CopyCommand, "Ctrl+C"));
            menu.Items.Add(CommandItem("Dupliquer", main.DuplicateCommand, "Ctrl+D"));
            menu.Items.Add(new Separator());
            menu.Items.Add(CommandItem("Mettre au premier plan", main.BringToFrontCommand));
            menu.Items.Add(CommandItem("Mettre à l’arrière-plan", main.SendToBackCommand));
            menu.Items.Add(new Separator());
            menu.Items.Add(CommandItem("Supprimer", main.DeleteCommand, "Suppr"));
        }
        else if (doc.TextSelection is { } selection && ReferenceEquals(selection.Page, page))
        {
            menu.Items.Add(CommandItem("Copier", main.CopyCommand, "Ctrl+C"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Surligner", () => doc.CreateMarkupFromSelection(MarkupKind.Highlight)));
            menu.Items.Add(Item("Souligner", () => doc.CreateMarkupFromSelection(MarkupKind.Underline)));
            menu.Items.Add(Item("Barrer", () => doc.CreateMarkupFromSelection(MarkupKind.StrikeOut)));
            menu.Items.Add(Item("Souligner (ondulé)", () => doc.CreateMarkupFromSelection(MarkupKind.Squiggly)));
            menu.Items.Add(new Separator());

            var query = string.Join(' ', selection.Text.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (query.Length > 0)
            {
                var label = query.Length > 28 ? query[..28] + "…" : query;
                menu.Items.Add(Item($"Rechercher « {label} »", () =>
                {
                    main.SidebarMode = SidebarModeKind.Search;
                    main.IsSidebarVisible = true;
                    _ = doc.SearchAsync(query);
                }));
            }
        }
        else
        {
            menu.Items.Add(Item("Coller ici", () => doc.Paste(page, point)));
            menu.Items.Add(Item("Sélectionner tout le texte de la page", () => doc.SelectAllText(page)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Pivoter la page à gauche", () => doc.RotatePages(new[] { page.Index }, -1)));
            menu.Items.Add(Item("Pivoter la page à droite", () => doc.RotatePages(new[] { page.Index }, 1)));
            menu.Items.Add(Item("Dupliquer la page", () => doc.DuplicatePages(new[] { page.Index })));
            menu.Items.Add(Item("Insérer une page vierge après", () => doc.InsertBlankPages(page.Index + 1, page.Width, page.Height)));
            menu.Items.Add(Item("Supprimer la page", () => doc.DeletePages(new[] { page.Index }), enabled: doc.PageCount > 1));
            menu.Items.Add(new Separator());
            menu.Items.Add(CommandItem("Ajuster à la largeur", main.FitWidthCommand, "Ctrl+1"));
            menu.Items.Add(CommandItem("Page entière", main.FitPageCommand, "Ctrl+2"));
        }

        menu.PlacementTarget = _view.Canvas;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // =====================================================================
    // Survol
    // =====================================================================

    private void UpdateHover(DocumentViewModel doc, Point canvasPoint)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHover).TotalMilliseconds < 30)
        {
            return;
        }

        _lastHover = now;

        // Ne jamais bloquer l'interface derriere un rendu en cours.
        if (!Monitor.TryEnter(PdfDoc.Sync))
        {
            return;
        }

        try
        {
            var hit = _view.Canvas.HitTest(canvasPoint);
            var cursor = Cursors.Arrow;
            PdfTextLine? hoverLine = null;

            if (hit.Page is null || hit.Control is null)
            {
                cursor = doc.Tool == EditorTool.Hand ? Cursors.Hand : Cursors.Arrow;
            }
            else
            {
                var page = hit.Page;
                var point = hit.PagePoint;
                var scale = hit.Control.Scale;

                switch (doc.Tool)
                {
                    case EditorTool.Select:
                        if (doc.SelectedAnnotation is { } selected && ReferenceEquals(doc.SelectedAnnotationPage, page)
                            && HitHandle(selected, point, scale) is var handle && handle != HandleKind.None)
                        {
                            cursor = CursorFor(handle);
                        }
                        else if (page.Annotations.Any(a => a.HitTest(point, HitTolerance / scale)))
                        {
                            cursor = page.Annotations.Reverse().First(a => a.HitTest(point, HitTolerance / scale)) is LinkAnnotation
                                ? Cursors.Hand
                                : Cursors.SizeAll;
                        }
                        else
                        {
                            var field = doc.Pdf.GetFormFieldTypeAt(page.Index, point);
                            if (field >= 0)
                            {
                                cursor = field == 6 ? Cursors.IBeam : Cursors.Hand;
                            }
                            else if (doc.Pdf.GetLinkAt(page.Index, point) is not null)
                            {
                                cursor = Cursors.Hand;
                            }
                            else if (doc.Pdf.GetTextPage(page.Index).IsOverText(point))
                            {
                                cursor = Cursors.IBeam;
                            }
                        }

                        break;

                    case EditorTool.Hand:
                        cursor = Cursors.Hand;
                        break;

                    case EditorTool.EditText:
                        hoverLine = doc.Pdf.GetTextPage(page.Index).LineAt(point, 2);
                        cursor = hoverLine is not null ? Cursors.IBeam : Cursors.Arrow;
                        break;

                    case EditorTool.TextHighlight:
                    case EditorTool.TextUnderline:
                    case EditorTool.TextStrike:
                    case EditorTool.TextSquiggly:
                        cursor = doc.Pdf.GetTextPage(page.Index).IsOverText(point) ? Cursors.IBeam : Cursors.Arrow;
                        break;

                    case EditorTool.Pen:
                    case EditorTool.Highlighter:
                        cursor = Cursors.Pen;
                        break;

                    default:
                        cursor = Cursors.Cross;
                        break;
                }
            }

            _view.Canvas.Cursor = cursor;

            if (!ReferenceEquals(hoverLine, _hoverLine))
            {
                var previous = _hoverPage;
                _hoverLine = hoverLine;
                _hoverPage = hoverLine is null ? null : hit.Page;
                InvalidateMarks(previous);
                InvalidateMarks(_hoverPage);
            }
        }
        finally
        {
            Monitor.Exit(PdfDoc.Sync);
        }
    }

    private void ClearHover()
    {
        if (_hoverLine is null)
        {
            return;
        }

        var page = _hoverPage;
        _hoverLine = null;
        _hoverPage = null;
        InvalidateMarks(page);
    }

    private void InvalidateMarks(PageViewModel? page)
    {
        if (page is not null)
        {
            _view.Canvas.GetControl(page)?.InvalidateMarks();
        }
    }

    private static Cursor CursorFor(HandleKind handle) => handle switch
    {
        HandleKind.TopLeft or HandleKind.BottomRight => Cursors.SizeNWSE,
        HandleKind.TopRight or HandleKind.BottomLeft => Cursors.SizeNESW,
        HandleKind.Top or HandleKind.Bottom => Cursors.SizeNS,
        HandleKind.Left or HandleKind.Right => Cursors.SizeWE,
        _ => Cursors.Cross
    };

    // =====================================================================
    // Creation d'annotations
    // =====================================================================

    private void FinishInk(DocumentViewModel doc, PageViewModel page, PageControl control)
    {
        var stroke = _stroke;
        if (stroke is null || stroke.Count == 0)
        {
            return;
        }

        var points = stroke.Count > 2 ? InkGeometry.Simplify(stroke, 0.4 / Math.Max(0.1, control.Scale)) : stroke;
        var highlighter = doc.Tool == EditorTool.Highlighter;
        var ink = new InkAnnotation(highlighter);
        DocumentViewModel.ApplyToolStyle(ink, highlighter ? ToolKeys.Highlighter : ToolKeys.Pen);
        ink.AddStroke(points);
        doc.AddAnnotation(page, ink, select: false);
    }

    private void FinishBox(DocumentViewModel doc, PageViewModel page, PageControl control)
    {
        var raw = _previewRect ?? new Rect(_startPage, _startPage);
        var rect = new Rect(raw.TopLeft, raw.BottomRight);
        var clicked = rect.Width < 4 && rect.Height < 4;

        switch (doc.Tool)
        {
            case EditorTool.TextBox:
            {
                var box = new TextBoxAnnotation();
                DocumentViewModel.ApplyToolStyle(box, ToolKeys.Text);
                if (clicked)
                {
                    box.Rect = new Rect(_startPage.X, _startPage.Y - box.FontSize * 0.7, Math.Min(220, page.Width - _startPage.X), box.FontSize * 1.4);
                }
                else
                {
                    box.Rect = new Rect(rect.X, rect.Y, Math.Max(24, rect.Width), Math.Max(box.FontSize * 1.3, rect.Height));
                    box.AutoHeight = rect.Height < box.FontSize * 2.6;
                }

                box.AutoFit();
                page.Annotations.Add(box);
                doc.Select(page, box);
                doc.DiscardPendingEdit();
                _editingIsNew = true;
                doc.Tool = EditorTool.Select;
                BeginInlineEdit(control, page, box, isNew: true);
                break;
            }

            case EditorTool.Rectangle:
            case EditorTool.Ellipse:
            {
                if (clicked)
                {
                    rect = new Rect(_startPage.X - 50, _startPage.Y - 35, 100, 70);
                }

                var shape = new ShapeAnnotation(doc.Tool == EditorTool.Ellipse ? ShapeKind.Ellipse : ShapeKind.Rectangle);
                DocumentViewModel.ApplyToolStyle(shape, ToolKeys.Shape);
                shape.Rect = rect;
                doc.AddAnnotation(page, shape);
                doc.Tool = EditorTool.Select;
                break;
            }

            case EditorTool.Redact when !clicked:
                doc.AddAnnotation(page, new RedactionAnnotation { Rect = rect }, select: false);
                break;

            case EditorTool.Whiteout when !clicked:
            {
                var whiteout = new WhiteoutAnnotation();
                DocumentViewModel.ApplyToolStyle(whiteout, ToolKeys.Whiteout);
                whiteout.Rect = rect;
                doc.AddAnnotation(page, whiteout, select: false);
                break;
            }

            case EditorTool.Link when !clicked:
                CreateLink(doc, page, rect);
                doc.Tool = EditorTool.Select;
                break;
        }
    }

    private void FinishLine(DocumentViewModel doc, PageViewModel page)
    {
        var (start, end) = _previewLine ?? (_startPage, _startPage);
        if ((end - start).Length < 3)
        {
            end = new Point(start.X + 90, start.Y);
        }

        var line = new LineAnnotation { Start = start, End = end, ArrowEnd = doc.Tool == EditorTool.Arrow };
        DocumentViewModel.ApplyToolStyle(line, ToolKeys.Line);
        doc.AddAnnotation(page, line);
        doc.Tool = EditorTool.Select;
    }

    private void PlaceNote(DocumentViewModel doc, PageControl control, PageViewModel page, Point point)
    {
        var note = new NoteAnnotation { Location = new Point(point.X - NoteAnnotation.IconSize / 2, point.Y - NoteAnnotation.IconSize / 2) };
        DocumentViewModel.ApplyToolStyle(note, ToolKeys.Note);
        note.Author = SettingsService.Current.AuthorName;
        page.Annotations.Add(note);
        doc.Select(page, note);
        doc.DiscardPendingEdit();
        doc.Tool = EditorTool.Select;
        OpenNote(control, page, note, isNew: true);
    }

    private void PlaceStamp(DocumentViewModel doc, PageViewModel page, Point point)
    {
        var label = doc.StampLabel;
        var stamp = new StampAnnotation(label);
        var isPreset = StampPresets.All.Any(s => s.Label == label);
        stamp.StrokeColor = isPreset ? StampPresets.ColorFor(label) : ColorUtil.Parse(SettingsService.GetToolStyle(ToolKeys.Stamp).StrokeColor, stamp.StrokeColor);

        var width = Math.Max(84, TextLayout.MeasureWidth(label, TextLayout.StampFont, 20) + 40);
        const double height = 40;
        stamp.Rect = new Rect(point.X - width / 2, point.Y - height / 2, width, height);
        doc.AddAnnotation(page, stamp);
        doc.Tool = EditorTool.Select;
    }

    private void PlaceSignature(DocumentViewModel doc, PageViewModel page, Point point)
    {
        var signature = doc.Signature ?? _view.Main?.Signatures.FirstOrDefault();
        if (signature is null)
        {
            _view.Main?.CreateSignatureCommand.Execute(null);
            return;
        }

        var annotation = doc.CreateSignature(signature, page, point);
        if (annotation is not null)
        {
            doc.AddAnnotation(page, annotation);
        }

        doc.Tool = EditorTool.Select;
    }

    private void EditTextAt(DocumentViewModel doc, PageControl control, PageViewModel page, Point point)
    {
        var text = doc.Pdf.GetTextPage(page.Index);
        var line = text.LineAt(point, 2);
        if (line is null)
        {
            doc.Select(null, null);
            return;
        }

        var existing = page.Annotations.OfType<TextEditAnnotation>().FirstOrDefault(a =>
            a.OriginalRect.IntersectsWith(line.Bounds) && Math.Abs(a.OriginalRect.Y - line.Bounds.Y) < line.Bounds.Height * 0.6);

        if (existing is not null)
        {
            doc.Select(page, existing);
            BeginInlineEdit(control, page, existing);
            return;
        }

        var edit = doc.CreateTextEdit(page.Index, line, control.SampleBackground(line.Bounds));
        doc.AddAnnotation(page, edit);
        BeginInlineEdit(control, page, edit);
    }

    private void CreateLink(DocumentViewModel doc, PageViewModel page, Rect rect)
    {
        var address = _view.Dialogs?.Prompt("Ajouter un lien", "Adresse de la page web à ouvrir :", "https://", "https://exemple.fr", confirm: "Ajouter");
        if (string.IsNullOrWhiteSpace(address) || address.Trim() is "https://" or "http://")
        {
            return;
        }

        doc.AddAnnotation(page, new LinkAnnotation { Rect = rect, Uri = NormalizeUri(address) });
    }

    private void EditLink(DocumentViewModel doc, LinkAnnotation link)
    {
        var address = _view.Dialogs?.Prompt("Modifier le lien", "Adresse de la page web à ouvrir :", link.Uri, confirm: "Modifier");
        if (!string.IsNullOrWhiteSpace(address))
        {
            link.Uri = NormalizeUri(address);
            doc.CommitPendingEdit("Modifier le lien");
        }
    }

    private void EditStamp(DocumentViewModel doc, StampAnnotation stamp)
    {
        var label = _view.Dialogs?.Prompt("Modifier le tampon", "Texte du tampon :", stamp.Label, confirm: "Modifier");
        if (!string.IsNullOrWhiteSpace(label))
        {
            stamp.Label = label.Trim().ToUpperInvariant();
            doc.CommitPendingEdit("Modifier le tampon");
        }
    }

    private static string NormalizeUri(string address)
    {
        var value = address.Trim();
        if (value.Contains('@') && !value.Contains("://") && !value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return "mailto:" + value;
        }

        return value.Contains("://") || value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? value : "https://" + value;
    }

    private void OpenAnnotation(DocumentViewModel doc, PageControl control, PageViewModel page, Annotation annotation)
    {
        switch (annotation)
        {
            case TextBoxAnnotation box:
                BeginInlineEdit(control, page, box);
                break;
            case NoteAnnotation note:
                OpenNote(control, page, note, false);
                break;
            case LinkAnnotation link:
                EditLink(doc, link);
                break;
            case StampAnnotation stamp:
                EditStamp(doc, stamp);
                break;
        }
    }

    /// <summary>Entree ou double-clic : edite l'annotation selectionnee.</summary>
    public void OpenSelected()
    {
        var doc = Document;
        if (doc?.SelectedAnnotation is not { } annotation || doc.SelectedAnnotationPage is not { } page)
        {
            return;
        }

        var control = _view.Canvas.GetControl(page);
        if (control is not null)
        {
            OpenAnnotation(doc, control, page, annotation);
        }
    }

    public void InsertImages(IEnumerable<string> files, Point canvasPoint)
    {
        var doc = Document;
        if (doc is null)
        {
            return;
        }

        var hit = _view.Canvas.HitTest(canvasPoint, nearest: true);
        var page = hit.Page ?? doc.CurrentPage;
        if (page is null)
        {
            return;
        }

        var origin = hit.Page is null ? new Point(page.Width / 2, page.Height / 2) : hit.PagePoint;
        var offset = 0.0;
        foreach (var file in files)
        {
            try
            {
                var image = doc.CreateImage(page, System.IO.File.ReadAllBytes(file), new Point(origin.X + offset, origin.Y + offset));
                if (image is not null)
                {
                    doc.Tool = EditorTool.Select;
                    doc.AddAnnotation(page, image);
                    offset += 18;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Outils] Image ignoree : {ex.Message}");
            }
        }
    }

    // =====================================================================
    // Selection rectangulaire
    // =====================================================================

    private void FinishMarquee(DocumentViewModel doc, PageViewModel page, PageControl control)
    {
        var raw = _previewRect ?? Rect.Empty;
        var rect = Rect.Intersect(new Rect(raw.TopLeft, raw.BottomRight), new Rect(0, 0, page.Width, page.Height));
        if (rect.IsEmpty || rect.Width < 4 || rect.Height < 4)
        {
            ClearMarquee();
            return;
        }

        _marquee = rect;
        _marqueePage = page;
        control.InvalidateOverlay();

        var menu = new ContextMenu();
        menu.Items.Add(Item("Copier en tant qu’image", () =>
        {
            try
            {
                Clipboard.SetImage(doc.RenderArea(page, rect, 220));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Outils] Copie impossible : {ex.Message}");
            }
        }));

        var text = doc.GetTextInArea(page, rect);
        if (text.Length > 0)
        {
            menu.Items.Add(Item("Copier le texte de la zone", () =>
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch
                {
                    // Presse-papiers occupe.
                }
            }));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Recadrer la page sur la sélection", () => doc.CropPages(new[] { page.Index }, rect)));
        menu.Items.Add(Item("Recadrer toutes les pages sur la sélection", () => doc.CropPages(Enumerable.Range(0, doc.PageCount).ToList(), rect)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Ajouter une zone de texte ici", () =>
        {
            var box = new TextBoxAnnotation();
            DocumentViewModel.ApplyToolStyle(box, ToolKeys.Text);
            box.Rect = rect;
            box.AutoHeight = false;
            page.Annotations.Add(box);
            doc.Select(page, box);
            doc.DiscardPendingEdit();
            if (_view.Canvas.GetControl(page) is { } target)
            {
                BeginInlineEdit(target, page, box, isNew: true);
            }
        }));
        menu.Items.Add(Item("Masquer avec le correcteur", () =>
        {
            var whiteout = new WhiteoutAnnotation();
            DocumentViewModel.ApplyToolStyle(whiteout, ToolKeys.Whiteout);
            whiteout.Rect = rect;
            doc.AddAnnotation(page, whiteout);
        }));
        menu.Items.Add(Item("Caviarder la zone", () => doc.AddAnnotation(page, new RedactionAnnotation { Rect = rect })));
        menu.Items.Add(Item("Ajouter un lien…", () => CreateLink(doc, page, rect)));

        menu.Closed += (_, _) => ClearMarquee();
        menu.PlacementTarget = _view.Canvas;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void ClearMarquee()
    {
        if (_marquee is null)
        {
            return;
        }

        var page = _marqueePage;
        _marquee = null;
        _marqueePage = null;
        if (page is not null)
        {
            _view.Canvas.GetControl(page)?.InvalidateOverlay();
        }
    }

    // =====================================================================
    // Edition de texte en place
    // =====================================================================

    public void BeginInlineEdit(PageControl control, PageViewModel page, TextBoxAnnotation annotation, bool isNew = false)
    {
        var doc = Document;
        if (doc is null)
        {
            return;
        }

        if (_editor is not null && !ReferenceEquals(_editing, annotation))
        {
            CommitInlineEdit();
        }

        if (_editor is not null)
        {
            return;
        }

        if (!ReferenceEquals(doc.SelectedAnnotation, annotation))
        {
            doc.Select(page, annotation);
        }

        doc.SuspendEditTracking = true;
        _editing = annotation;
        _editingPage = page;
        _editingIsNew = isNew;
        _editingOriginalText = annotation.Text;

        var box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Text = annotation.Text,
            MinWidth = 4,
            FocusVisualStyle = null
        };

        box.Template = CreateBareTextBoxTemplate();
        SpellCheck.SetIsEnabled(box, false);
        TextOptions.SetTextFormattingMode(box, TextFormattingMode.Ideal);
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentYellow");
        box.SelectionOpacity = 0.45;
        ApplyEditorStyle(box, annotation, control.Scale);

        box.TextChanged += (_, _) =>
        {
            if (ReferenceEquals(_editor, box) && _editing is not null)
            {
                _editing.Text = box.Text;
                ApplyEditorStyle(box, _editing, _view.Canvas.LayoutScale);
            }
        };

        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0))
            {
                e.Handled = true;
                CommitInlineEdit();
                _view.FocusDocument();
            }
        };

        box.LostKeyboardFocus += (_, e) =>
        {
            // La barre d'annotation (police, couleur...) peut prendre le focus sans terminer l'edition.
            if (e.NewFocus is DependencyObject target && IsInsidePopup(target))
            {
                return;
            }

            box.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_editor, box) && !box.IsKeyboardFocusWithin && !IsFocusInPopup())
                {
                    CommitInlineEdit();
                }
            }), DispatcherPriority.Input);
        };

        annotation.Changed += OnEditingAnnotationChanged;
        control.EditorHost.Children.Add(box);
        _editor = box;
        control.InvalidateAnnotations();
        control.InvalidateOverlay();

        box.Dispatcher.BeginInvoke(new Action(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        }), DispatcherPriority.Input);
    }

    private void OnEditingAnnotationChanged(object? sender, EventArgs e)
    {
        if (_editor is not null && _editing is not null)
        {
            ApplyEditorStyle(_editor, _editing, _view.Canvas.LayoutScale);
        }
    }

    private static void ApplyEditorStyle(TextBox box, TextBoxAnnotation annotation, double scale)
    {
        box.FontFamily = FontCatalog.GetWpfFamily(annotation.FontFamily);
        box.FontSize = annotation.FontSize;
        box.FontWeight = annotation.Bold ? FontWeights.Bold : FontWeights.Normal;
        box.FontStyle = annotation.Italic ? FontStyles.Italic : FontStyles.Normal;
        box.TextDecorations = annotation.Underline ? TextDecorations.Underline : null;
        box.TextAlignment = annotation.Alignment;

        var brush = AnnotationRenderer.Brush(AnnotationRenderer.WithOpacity(annotation.TextColor, Math.Max(0.4, annotation.Opacity)));
        box.Foreground = brush;
        box.CaretBrush = AnnotationRenderer.Brush(Color.FromRgb(annotation.TextColor.R, annotation.TextColor.G, annotation.TextColor.B));

        // Le TextBox ajoute 2 px de marge interne de chaque cote : on compense.
        box.Width = Math.Max(8, annotation.Rect.Width - 2 * annotation.Padding + 4);
        box.LayoutTransform = new ScaleTransform(scale, scale);
        Canvas.SetLeft(box, (annotation.Rect.X + annotation.Padding - 2) * scale);
        Canvas.SetTop(box, (annotation.Rect.Y + annotation.Padding) * scale);
    }

    private static ControlTemplate CreateBareTextBoxTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(UIElement.FocusableProperty, false);
        template.VisualTree = host;
        return template;
    }

    public void CommitInlineEdit()
    {
        var box = _editor;
        var annotation = _editing;
        var page = _editingPage;
        if (box is null || annotation is null || page is null)
        {
            return;
        }

        _editor = null;
        _editing = null;
        _editingPage = null;
        annotation.Changed -= OnEditingAnnotationChanged;
        (box.Parent as Panel)?.Children.Remove(box);

        var doc = Document;
        if (doc is null)
        {
            return;
        }

        doc.SuspendEditTracking = false;
        var isNew = _editingIsNew;
        _editingIsNew = false;
        annotation.Text = box.Text;

        if (isNew)
        {
            page.Annotations.Remove(annotation);
            doc.DiscardPendingEdit();
            if (!string.IsNullOrWhiteSpace(annotation.Text))
            {
                doc.AddAnnotation(page, annotation);
            }
            else
            {
                doc.Select(null, null);
            }
        }
        else if (string.IsNullOrWhiteSpace(annotation.Text) && annotation is not TextEditAnnotation)
        {
            annotation.Text = _editingOriginalText;
            doc.DiscardPendingEdit();
            doc.RemoveAnnotation(page, annotation);
        }
        else
        {
            doc.CommitPendingEdit("Modifier le texte");
        }

        var control = _view.Canvas.GetControl(page);
        control?.InvalidateAnnotations();
        control?.InvalidateOverlay();
    }

    private static bool IsInsidePopup(DependencyObject element)
    {
        var node = element;
        while (node is not null)
        {
            if (node is Popup or ContextMenu)
            {
                return true;
            }

            var parent = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            if (parent is null && node is FrameworkElement { Parent: Popup })
            {
                return true;
            }

            node = parent ?? (node as FrameworkElement)?.Parent;
        }

        return false;
    }

    private static bool IsFocusInPopup() =>
        Keyboard.FocusedElement is DependencyObject focused && PresentationSource.FromDependencyObject(focused) is { } source
        && source.RootVisual is FrameworkElement { Parent: Popup };

    // =====================================================================
    // Notes
    // =====================================================================

    private void OpenNote(PageControl control, PageViewModel page, NoteAnnotation note, bool isNew)
    {
        CloseNote();
        var doc = Document;
        if (doc is null)
        {
            return;
        }

        if (!ReferenceEquals(doc.SelectedAnnotation, note))
        {
            doc.Select(page, note);
        }

        doc.SuspendEditTracking = true;

        var card = new Border
        {
            Width = 240,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 9, 12, 10),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xB0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 120, 90, 0)),
            BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(UIElement.EffectProperty, "PopoverShadow");

        var layout = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var close = new Button { Content = "Terminé", Padding = new Thickness(0), Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x00)) };
        close.SetResourceReference(FrameworkElement.StyleProperty, "MacLinkButton");
        close.Click += (_, _) => CloseNote();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = (string.IsNullOrWhiteSpace(note.Author) ? SettingsService.Current.AuthorName : note.Author) + " · " + note.Modified.ToString("d MMM, HH:mm"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x5A, 0x1E)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var text = new TextBox
        {
            Text = note.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 112,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)),
            FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Template = CreateBareTextBoxTemplate()
        };
        text.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentYellow");
        text.TextChanged += (_, _) => note.Text = text.Text;
        text.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseNote();
                _view.FocusDocument();
            }
        };
        layout.Children.Add(text);

        card.Child = layout;
        PositionNoteCard(card, note, control.Scale);
        control.EditorHost.Children.Add(card);

        _noteCard = card;
        _openNote = note;
        _notePage = page;
        _noteIsNew = isNew;

        text.Dispatcher.BeginInvoke(new Action(() =>
        {
            text.Focus();
            Keyboard.Focus(text);
            text.CaretIndex = text.Text.Length;
        }), DispatcherPriority.Input);
    }

    private static void PositionNoteCard(Border card, NoteAnnotation note, double scale)
    {
        Canvas.SetLeft(card, (note.Location.X + NoteAnnotation.IconSize + 6) * scale);
        Canvas.SetTop(card, note.Location.Y * scale);
    }

    private void CloseNote()
    {
        var card = _noteCard;
        var note = _openNote;
        var page = _notePage;
        if (card is null || note is null || page is null)
        {
            return;
        }

        _noteCard = null;
        _openNote = null;
        _notePage = null;
        (card.Parent as Panel)?.Children.Remove(card);

        var doc = Document;
        if (doc is null)
        {
            return;
        }

        doc.SuspendEditTracking = false;
        if (_noteIsNew)
        {
            _noteIsNew = false;
            page.Annotations.Remove(note);
            doc.DiscardPendingEdit();
            doc.AddAnnotation(page, note);
        }
        else
        {
            doc.CommitPendingEdit("Modifier la note");
        }
    }

    // =====================================================================
    // Clavier
    // =====================================================================

    public void OnPreviewKeyDown(KeyEventArgs e)
    {
        var doc = Document;
        if (doc is null)
        {
            return;
        }

        if ((_editor is not null && _editor.IsKeyboardFocusWithin) || (_noteCard is not null && _noteCard.IsKeyboardFocusWithin))
        {
            return;
        }

        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Champ de formulaire actif : les touches lui reviennent.
        if (_formPage is not null && doc.Pdf.FocusedFormPage >= 0)
        {
            if (key == Key.Escape)
            {
                doc.Pdf.FormKillFocus();
                RefreshPage(_formPage);
                _formPage = null;
                e.Handled = true;
                return;
            }

            if (modifiers == ModifierKeys.Control && key == Key.A)
            {
                doc.Pdf.FormSelectAll(_formPage.Index);
                RefreshPage(_formPage);
                e.Handled = true;
                return;
            }

            if ((modifiers & ModifierKeys.Control) != 0)
            {
                return;
            }

            if (key is Key.Back or Key.Delete or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Tab or Key.Enter)
            {
                doc.Pdf.FormKeyDown(_formPage.Index, KeyInterop.VirtualKeyFromKey(key), FormModifiers());
                if (key is Key.Back or Key.Delete or Key.Enter)
                {
                    doc.MarkFormsDirty();
                }

                RefreshPage(_formPage);
                e.Handled = true;
            }

            return;
        }

        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
        {
            return;
        }

        var shift = (modifiers & ModifierKeys.Shift) != 0;
        var selected = doc.SelectedAnnotation;

        switch (key)
        {
            case Key.Delete:
            case Key.Back:
                if (selected is not null)
                {
                    doc.DeleteSelectedAnnotation();
                    e.Handled = true;
                }

                return;

            case Key.Escape:
                if (_marquee is not null)
                {
                    ClearMarquee();
                }
                else if (doc.Tool != EditorTool.Select)
                {
                    doc.Tool = EditorTool.Select;
                }
                else
                {
                    doc.Select(null, null);
                    doc.ClearTextSelection();
                }

                e.Handled = true;
                return;

            case Key.Enter when selected is not null:
                OpenSelected();
                e.Handled = true;
                return;

            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                if (selected is { CanMove: true })
                {
                    var step = shift ? 10 : 1;
                    var delta = key switch
                    {
                        Key.Left => new Vector(-step, 0),
                        Key.Right => new Vector(step, 0),
                        Key.Up => new Vector(0, -step),
                        _ => new Vector(0, step)
                    };
                    selected.Translate(delta);
                    doc.CommitPendingEdit("Déplacer");
                    e.Handled = true;
                }
                else if (doc.ScrollMode == ScrollModeKind.SinglePage && key is Key.Left or Key.Right)
                {
                    doc.GoToPage(doc.CurrentPageIndex + (key == Key.Right ? 1 : -1));
                    e.Handled = true;
                }

                return;

            case Key.PageDown:
            case Key.PageUp:
                doc.GoToPage(doc.CurrentPageIndex + (key == Key.PageDown ? 1 : -1));
                e.Handled = true;
                return;

            case Key.Home:
                doc.GoToPage(0);
                e.Handled = true;
                return;

            case Key.End:
                doc.GoToPage(doc.PageCount - 1);
                e.Handled = true;
                return;
        }

        if (shift)
        {
            return;
        }

        EditorTool? tool = key switch
        {
            Key.V => EditorTool.Select,
            Key.H => EditorTool.Hand,
            Key.M => EditorTool.Marquee,
            Key.T => EditorTool.TextBox,
            Key.E => EditorTool.EditText,
            Key.P => EditorTool.Pen,
            Key.G => EditorTool.Highlighter,
            Key.U => EditorTool.TextHighlight,
            Key.R => EditorTool.Rectangle,
            Key.O => EditorTool.Ellipse,
            Key.L => EditorTool.Line,
            Key.A => EditorTool.Arrow,
            Key.N => EditorTool.Note,
            Key.S => EditorTool.Signature,
            Key.I => EditorTool.Image,
            Key.K => EditorTool.Link,
            Key.X => EditorTool.Redact,
            Key.W => EditorTool.Whiteout,
            _ => null
        };

        if (tool is { } value && _view.Main is { } main)
        {
            if (main.SetToolCommand.CanExecute(value))
            {
                main.SetToolCommand.Execute(value);
            }

            e.Handled = true;
        }
    }

    public void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        var doc = Document;
        if (doc is null || _formPage is null || doc.Pdf.FocusedFormPage < 0 || string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (Keyboard.FocusedElement is TextBoxBase)
        {
            return;
        }

        foreach (var c in e.Text)
        {
            if (c >= ' ')
            {
                doc.Pdf.FormChar(_formPage.Index, c, FormModifiers());
            }
        }

        doc.MarkFormsDirty();
        RefreshPage(_formPage);
        e.Handled = true;
    }

    private static int FormModifiers()
    {
        var modifiers = Keyboard.Modifiers;
        var flags = 0;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            flags |= 1;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            flags |= 2;
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            flags |= 4;
        }

        return flags;
    }

    private void RefreshPage(PageViewModel page) => _view.Canvas.GetControl(page)?.ForceRerender();

    // =====================================================================
    // Liens
    // =====================================================================

    private void FollowLink(DocumentViewModel doc, PdfLinkTarget target)
    {
        if (target.PageIndex >= 0)
        {
            doc.GoToPage(target.PageIndex);
            return;
        }

        if (!string.IsNullOrWhiteSpace(target.Uri))
        {
            OpenUri(target.Uri);
        }
    }

    private void OpenUri(string uri)
    {
        if (!Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "mailto"))
        {
            _view.Dialogs?.Alert("Lien non pris en charge", uri, new[] { "OK" }, icon: AlertIcon.Warning);
            return;
        }

        var choice = _view.Dialogs?.Alert("Ouvrir ce lien ?", parsed.ToString(), new[] { "Ouvrir", "Annuler" }, 0, 1, icon: AlertIcon.Question) ?? 1;
        if (choice != 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(parsed.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Outils] Lien : {ex.Message}");
        }
    }

    // =====================================================================
    // Dessin des calques
    // =====================================================================

    public void DrawMarks(DrawingContext dc, PageControl control)
    {
        var doc = Document;
        var page = control.Page;
        if (doc is null || page is null)
        {
            return;
        }

        var scale = control.Scale;

        if (doc.HasSearchResults)
        {
            var hit = Resource("SearchHitBrush");
            var currentBrush = Resource("SearchCurrentBrush");
            var current = doc.CurrentSearchResult;
            foreach (var result in doc.SearchResults)
            {
                if (result.PageIndex != page.Index)
                {
                    continue;
                }

                var brush = ReferenceEquals(result, current) ? currentBrush : hit;
                foreach (var rect in result.Rects)
                {
                    dc.DrawRoundedRectangle(brush, null, ScaleRect(rect, scale, 1.2), 2, 2);
                }
            }
        }

        if (doc.TextSelection is { } selection && ReferenceEquals(selection.Page, page))
        {
            var brush = Resource("TextSelectionBrush");
            foreach (var rect in selection.Rects)
            {
                dc.DrawRectangle(brush, null, ScaleRect(rect, scale, 0.5));
            }
        }

        if (_hoverLine is { } line && ReferenceEquals(_hoverPage, page))
        {
            var outline = new Pen(Resource("HoverOutlineBrush"), 1.4);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(22, 0xFF, 0xCC, 0x00)), outline, ScaleRect(line.Bounds, scale, 2.5), 3, 3);
        }
    }

    public void DrawOverlay(DrawingContext dc, PageControl control)
    {
        var doc = Document;
        var page = control.Page;
        if (doc is null || page is null)
        {
            return;
        }

        var scale = control.Scale;
        var accent = Resource("SelectionOutlineBrush");

        if (doc.SelectedAnnotation is { } selected && ReferenceEquals(doc.SelectedAnnotationPage, page) && !ReferenceEquals(selected, _openNote))
        {
            var outlinePen = new Pen(accent, 1);
            if (IsInlineEditing(selected))
            {
                outlinePen.DashStyle = new DashStyle(new[] { 3.0, 2.0 }, 0);
            }

            if (selected is not LineAnnotation)
            {
                var bounds = selected.Bounds;
                var extra = selected is InkAnnotation or MarkupAnnotation ? selected.StrokeWidth / 2 * scale + 3 : 2;
                var outline = new Rect(bounds.X * scale, bounds.Y * scale, bounds.Width * scale, bounds.Height * scale);
                outline.Inflate(extra, extra);
                dc.DrawRectangle(null, outlinePen, outline);
            }

            if (!IsInlineEditing(selected))
            {
                var handlePen = new Pen(accent, 1.3);
                var fill = Resource("HandleFillBrush");
                foreach (var (_, position) in GetHandles(selected))
                {
                    dc.DrawEllipse(fill, handlePen, new Point(position.X * scale, position.Y * scale), HandleRadius, HandleRadius);
                }
            }
        }

        if (ReferenceEquals(_dragPage, page))
        {
            if (_drag == DragKind.Ink && _stroke is { Count: > 0 })
            {
                var key = doc.Tool == EditorTool.Highlighter ? ToolKeys.Highlighter : ToolKeys.Pen;
                var style = SettingsService.GetToolStyle(key);
                var color = AnnotationRenderer.WithOpacity(ColorUtil.Parse(style.StrokeColor, Colors.Red), style.Opacity);
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawGeometry(null, AnnotationRenderer.CreatePen(color, style.StrokeWidth), AnnotationRenderer.BuildInkGeometry(new[] { (IReadOnlyList<Point>)_stroke }));
                dc.Pop();
            }

            if (_previewRect is { } preview && _drag is DragKind.Box or DragKind.Marquee)
            {
                DrawBoxPreview(dc, doc.Tool, new Rect(preview.TopLeft, preview.BottomRight), scale);
            }

            if (_previewLine is { } line && _drag == DragKind.Line)
            {
                var style = SettingsService.GetToolStyle(ToolKeys.Line);
                var color = AnnotationRenderer.WithOpacity(ColorUtil.Parse(style.StrokeColor, Colors.Red), style.Opacity);
                var preview2 = new LineAnnotation { Start = line.Start, End = line.End, ArrowEnd = doc.Tool == EditorTool.Arrow, StrokeColor = color, StrokeWidth = style.StrokeWidth, Dashed = style.Dashed };
                dc.PushTransform(new ScaleTransform(scale, scale));
                AnnotationRenderer.Draw(dc, preview2, true);
                dc.Pop();
            }
        }

        if (_marquee is { } marquee && ReferenceEquals(_marqueePage, page) && _drag != DragKind.Marquee)
        {
            DrawBoxPreview(dc, EditorTool.Marquee, marquee, scale);
        }
    }

    private void DrawBoxPreview(DrawingContext dc, EditorTool tool, Rect rect, double scale)
    {
        var scaled = new Rect(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale);
        var dashed = new DashStyle(new[] { 4.0, 3.0 }, 0);

        switch (tool)
        {
            case EditorTool.Rectangle:
            case EditorTool.Ellipse:
            {
                var style = SettingsService.GetToolStyle(ToolKeys.Shape);
                var shape = new ShapeAnnotation(tool == EditorTool.Ellipse ? ShapeKind.Ellipse : ShapeKind.Rectangle)
                {
                    Rect = rect,
                    StrokeColor = ColorUtil.Parse(style.StrokeColor, Colors.Red),
                    FillColor = ColorUtil.Parse(style.FillColor, Colors.Transparent),
                    StrokeWidth = style.StrokeWidth,
                    Opacity = style.Opacity,
                    Dashed = style.Dashed
                };
                dc.PushTransform(new ScaleTransform(scale, scale));
                AnnotationRenderer.Draw(dc, shape, true);
                dc.Pop();
                break;
            }

            case EditorTool.Redact:
                dc.DrawRectangle(Brushes.Black, null, scaled);
                break;

            case EditorTool.Whiteout:
                dc.DrawRectangle(Brushes.White, new Pen(Brushes.Gray, 1) { DashStyle = dashed }, scaled);
                break;

            case EditorTool.Link:
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(28, 0x0A, 0x84, 0xFF)), new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), 1) { DashStyle = dashed }, scaled);
                break;

            case EditorTool.TextBox:
                dc.DrawRectangle(null, new Pen(Resource("TextSecondary"), 1) { DashStyle = dashed }, scaled);
                break;

            default:
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(26, 0xFF, 0xCC, 0x00)), new Pen(Resource("SelectionOutlineBrush"), 1) { DashStyle = dashed }, scaled);
                break;
        }
    }

    // =====================================================================
    // Geometrie
    // =====================================================================

    private static Rect ScaleRect(Rect rect, double scale, double inflate)
    {
        var result = new Rect(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale);
        result.Inflate(inflate, inflate);
        return result;
    }

    private static IEnumerable<(HandleKind Kind, Point Position)> GetHandles(Annotation annotation)
    {
        if (annotation is LineAnnotation line)
        {
            yield return (HandleKind.Start, line.Start);
            yield return (HandleKind.End, line.End);
            yield break;
        }

        if (!annotation.CanResize)
        {
            yield break;
        }

        var b = annotation.Bounds;
        var cx = b.X + b.Width / 2;
        var cy = b.Y + b.Height / 2;
        yield return (HandleKind.TopLeft, b.TopLeft);
        yield return (HandleKind.TopRight, b.TopRight);
        yield return (HandleKind.BottomRight, b.BottomRight);
        yield return (HandleKind.BottomLeft, b.BottomLeft);

        if (!annotation.KeepAspectRatio)
        {
            yield return (HandleKind.Top, new Point(cx, b.Top));
            yield return (HandleKind.Right, new Point(b.Right, cy));
            yield return (HandleKind.Bottom, new Point(cx, b.Bottom));
            yield return (HandleKind.Left, new Point(b.Left, cy));
        }
    }

    private static HandleKind HitHandle(Annotation annotation, Point point, double scale)
    {
        var reach = HandleHitDistance / Math.Max(0.05, scale);
        foreach (var (kind, position) in GetHandles(annotation))
        {
            if ((position - point).Length <= reach)
            {
                return kind;
            }
        }

        return HandleKind.None;
    }

    private static Rect ComputeResize(Rect start, HandleKind handle, Point point, bool keepAspect)
    {
        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;

        switch (handle)
        {
            case HandleKind.TopLeft:
                left = point.X;
                top = point.Y;
                break;
            case HandleKind.Top:
                top = point.Y;
                break;
            case HandleKind.TopRight:
                right = point.X;
                top = point.Y;
                break;
            case HandleKind.Right:
                right = point.X;
                break;
            case HandleKind.BottomRight:
                right = point.X;
                bottom = point.Y;
                break;
            case HandleKind.Bottom:
                bottom = point.Y;
                break;
            case HandleKind.BottomLeft:
                left = point.X;
                bottom = point.Y;
                break;
            case HandleKind.Left:
                left = point.X;
                break;
        }

        var rect = new Rect(new Point(Math.Min(left, right), Math.Min(top, bottom)), new Point(Math.Max(left, right), Math.Max(top, bottom)));

        var corner = handle is HandleKind.TopLeft or HandleKind.TopRight or HandleKind.BottomLeft or HandleKind.BottomRight;
        if (keepAspect && corner && start.Width > 0.01 && start.Height > 0.01)
        {
            var ratio = start.Width / start.Height;
            var width = Math.Max(4, rect.Width);
            var height = Math.Max(4, rect.Height);
            if (width / height > ratio)
            {
                width = height * ratio;
            }
            else
            {
                height = width / ratio;
            }

            rect = handle switch
            {
                HandleKind.TopLeft => new Rect(start.Right - width, start.Bottom - height, width, height),
                HandleKind.TopRight => new Rect(start.Left, start.Bottom - height, width, height),
                HandleKind.BottomLeft => new Rect(start.Right - width, start.Top, width, height),
                _ => new Rect(start.Left, start.Top, width, height)
            };
        }

        if (rect.Width < 4)
        {
            rect.Width = 4;
        }

        if (rect.Height < 4)
        {
            rect.Height = 4;
        }

        return rect;
    }

    private static Point Snap45(Point anchor, Point point)
    {
        var vector = point - anchor;
        var length = vector.Length;
        if (length < 0.001)
        {
            return point;
        }

        var angle = Math.Round(Math.Atan2(vector.Y, vector.X) / (Math.PI / 4)) * (Math.PI / 4);
        return anchor + new Vector(Math.Cos(angle) * length, Math.Sin(angle) * length);
    }

    private static bool IsMarkupTool(EditorTool tool) =>
        tool is EditorTool.TextHighlight or EditorTool.TextUnderline or EditorTool.TextStrike or EditorTool.TextSquiggly;

    private static MarkupKind MarkupFor(EditorTool tool) => tool switch
    {
        EditorTool.TextUnderline => MarkupKind.Underline,
        EditorTool.TextStrike => MarkupKind.StrikeOut,
        EditorTool.TextSquiggly => MarkupKind.Squiggly,
        _ => MarkupKind.Highlight
    };

    private bool IsFromEditor(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is Canvas { Parent: PageControl })
            {
                return true;
            }

            if (ReferenceEquals(node, _view.Canvas))
            {
                return false;
            }

            node = node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    private static MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem CommandItem(string header, ICommand command, string? gesture = null, object? parameter = null) =>
        new() { Header = header, Command = command, CommandParameter = parameter, InputGestureText = gesture ?? "" };
}
