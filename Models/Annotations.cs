using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFEditor.Pdf;
using PDFEditor.Rendering;
using PDFEditor.Services;
using PDFEditor.ViewModels;

namespace PDFEditor.Models;

public enum AnnotationKind
{
    Ink, Highlighter, Rectangle, Ellipse, Line, Arrow, TextBox, TextEdit,
    Markup, Note, Image, Stamp, Redaction, Whiteout, Link
}

public enum MarkupKind { Highlight, Underline, StrikeOut, Squiggly }

public enum ShapeKind { Rectangle, Ellipse }

/// <summary>
/// Annotation ajoutee par l'utilisateur. Tant que le document est ouvert, elle
/// reste un objet modifiable ; elle n'est ecrite dans le PDF qu'a l'enregistrement.
/// Toutes les coordonnees sont en points d'affichage de la page.
/// </summary>
public abstract class Annotation : ObservableObject
{
    private Color _strokeColor = Color.FromRgb(0xFF, 0x3B, 0x30);
    private Color _fillColor = Colors.Transparent;
    private double _strokeWidth = 2;
    private double _opacity = 1;
    private bool _dashed;

    public Guid Id { get; private set; } = Guid.NewGuid();

    public string Author { get; set; } = "";

    public DateTime Modified { get; private set; } = DateTime.Now;

    /// <summary>Incremente a chaque modification (caches de geometrie).</summary>
    public int Revision { get; private set; }

    public abstract AnnotationKind Kind { get; }

    /// <summary>Nom du type, affiche dans l'inspecteur et la liste.</summary>
    public abstract string TypeName { get; }

    /// <summary>Resume court (texte de la note, libelle du tampon...).</summary>
    public virtual string Summary => TypeName;

    public abstract Rect Bounds { get; }

    public virtual bool CanMove => true;
    public virtual bool CanResize => true;
    public virtual bool IsLine => false;
    public virtual bool KeepAspectRatio => false;

    // Capacites exposees par l'inspecteur.
    public virtual bool HasStrokeStyle => true;
    public virtual bool HasWidthStyle => true;
    public virtual bool HasFillStyle => false;
    public virtual bool HasTextStyle => false;
    public virtual bool HasDashStyle => false;
    public virtual bool HasOpacityStyle => true;

    public event EventHandler? Changed;

    public Color StrokeColor
    {
        get => _strokeColor;
        set => Update(ref _strokeColor, value);
    }

    public Color FillColor
    {
        get => _fillColor;
        set => Update(ref _fillColor, value);
    }

    public double StrokeWidth
    {
        get => _strokeWidth;
        set => Update(ref _strokeWidth, Math.Clamp(value, 0, 72));
    }

    public double Opacity
    {
        get => _opacity;
        set => Update(ref _opacity, Math.Clamp(value, 0.05, 1));
    }

    public bool Dashed
    {
        get => _dashed;
        set => Update(ref _dashed, value);
    }

    // Position et taille (inspecteur).
    public double X
    {
        get => Bounds.X;
        set
        {
            if (CanMove && !double.IsNaN(value))
            {
                Translate(new Vector(value - Bounds.X, 0));
            }
        }
    }

    public double Y
    {
        get => Bounds.Y;
        set
        {
            if (CanMove && !double.IsNaN(value))
            {
                Translate(new Vector(0, value - Bounds.Y));
            }
        }
    }

    public double Width
    {
        get => Bounds.Width;
        set
        {
            if (!CanResize || double.IsNaN(value))
            {
                return;
            }

            var b = Bounds;
            var width = Math.Max(1, value);
            var height = KeepAspectRatio && b.Width > 0 ? width * b.Height / b.Width : b.Height;
            Resize(new Rect(b.X, b.Y, width, height));
        }
    }

    public double Height
    {
        get => Bounds.Height;
        set
        {
            if (!CanResize || double.IsNaN(value))
            {
                return;
            }

            var b = Bounds;
            var height = Math.Max(1, value);
            var width = KeepAspectRatio && b.Height > 0 ? height * b.Width / b.Height : b.Width;
            Resize(new Rect(b.X, b.Y, width, height));
        }
    }

    protected bool Update<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name))
        {
            return false;
        }

        NotifyChanged();
        return true;
    }

    /// <summary>Signale un changement (geometrie ou style).</summary>
    public void NotifyChanged()
    {
        Revision++;
        Modified = DateTime.Now;
        OnGeometryChanged();
        Raise(nameof(Bounds), nameof(X), nameof(Y), nameof(Width), nameof(Height), nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected virtual void OnGeometryChanged()
    {
    }

    public abstract void Translate(Vector delta);

    public abstract void Resize(Rect bounds);

    /// <summary>
    /// Change de repere (rotation ou recadrage de la page) : chaque point
    /// definissant l'annotation passe par <paramref name="map"/>.
    /// </summary>
    public abstract void TransformFrame(Func<Point, Point> map);

    public virtual bool HitTest(Point point, double tolerance)
    {
        var bounds = Bounds;
        bounds.Inflate(tolerance, tolerance);
        return bounds.Contains(point);
    }

    protected abstract Annotation CreateEmpty();

    /// <summary>Copie de l'etat, meme identifiant (historique).</summary>
    public Annotation Snapshot()
    {
        var copy = CreateEmpty();
        copy.CopyState(this);
        copy.Id = Id;
        return copy;
    }

    /// <summary>Copie independante (dupliquer, copier-coller).</summary>
    public Annotation Duplicate(Vector offset)
    {
        var copy = CreateEmpty();
        copy.CopyState(this);
        copy.Id = Guid.NewGuid();
        if (copy.CanMove)
        {
            copy.Translate(offset);
        }

        return copy;
    }

    /// <summary>Reprend l'etat d'un instantane.</summary>
    public void Restore(Annotation snapshot)
    {
        CopyState(snapshot);
        Raise(string.Empty);
        NotifyChanged();
    }

    protected virtual void CopyState(Annotation source)
    {
        _strokeColor = source._strokeColor;
        _fillColor = source._fillColor;
        _strokeWidth = source._strokeWidth;
        _opacity = source._opacity;
        _dashed = source._dashed;
        Author = source.Author;
    }

    protected static string Preview(string? text, int max = 60)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var flat = string.Join(' ', text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }
}

/// <summary>Annotation definie par un rectangle.</summary>
public abstract class BoxAnnotation : Annotation
{
    private Rect _rect = new(0, 0, 100, 60);

    public Rect Rect
    {
        get => _rect;
        set => Update(ref _rect, Normalize(value));
    }

    public override Rect Bounds => _rect;

    public override void Translate(Vector delta) => Rect = Rect.Offset(_rect, delta);

    public override void Resize(Rect bounds) => Rect = bounds;

    /// <summary>Vrai si la taille est conservee lors d'une rotation (texte, image, tampon).</summary>
    protected virtual bool PreservesSizeOnTransform => false;

    public override void TransformFrame(Func<Point, Point> map)
    {
        if (PreservesSizeOnTransform)
        {
            var center = map(new Point(_rect.X + _rect.Width / 2, _rect.Y + _rect.Height / 2));
            Rect = new Rect(center.X - _rect.Width / 2, center.Y - _rect.Height / 2, _rect.Width, _rect.Height);
        }
        else
        {
            Rect = new Rect(map(_rect.TopLeft), map(_rect.BottomRight));
        }
    }

    protected static Rect Normalize(Rect r) =>
        r.IsEmpty ? new Rect(0, 0, 1, 1) : new Rect(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is BoxAnnotation box)
        {
            _rect = box._rect;
        }
    }
}

// =========================================================================
// Formes
// =========================================================================

public sealed class ShapeAnnotation : BoxAnnotation
{
    private ShapeKind _shape;
    private double _cornerRadius;

    public ShapeAnnotation(ShapeKind shape)
    {
        _shape = shape;
    }

    public ShapeKind Shape
    {
        get => _shape;
        set => Update(ref _shape, value);
    }

    public double CornerRadius
    {
        get => _cornerRadius;
        set => Update(ref _cornerRadius, Math.Max(0, value));
    }

    public override AnnotationKind Kind => _shape == ShapeKind.Ellipse ? AnnotationKind.Ellipse : AnnotationKind.Rectangle;

    public override string TypeName => _shape == ShapeKind.Ellipse ? "Ellipse" : "Rectangle";

    public override bool HasFillStyle => true;

    public override bool HasDashStyle => true;

    public override bool HitTest(Point point, double tolerance)
    {
        var band = tolerance + StrokeWidth / 2;
        var outer = Rect;
        outer.Inflate(band, band);
        if (!outer.Contains(point))
        {
            return false;
        }

        if (FillColor.A > 0)
        {
            return true;
        }

        if (_shape == ShapeKind.Rectangle)
        {
            var inner = Rect;
            if (inner.Width <= 2 * band || inner.Height <= 2 * band)
            {
                return true;
            }

            inner.Inflate(-band, -band);
            return !inner.Contains(point);
        }

        var rx = Rect.Width / 2;
        var ry = Rect.Height / 2;
        if (rx < 2 || ry < 2)
        {
            return true;
        }

        var dx = (point.X - (Rect.X + rx)) / rx;
        var dy = (point.Y - (Rect.Y + ry)) / ry;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        return Math.Abs(distance - 1) <= band / Math.Min(rx, ry);
    }

    protected override Annotation CreateEmpty() => new ShapeAnnotation(_shape);

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is ShapeAnnotation shape)
        {
            _shape = shape._shape;
            _cornerRadius = shape._cornerRadius;
        }
    }
}

public sealed class LineAnnotation : Annotation
{
    private Point _start;
    private Point _end;
    private bool _arrowStart;
    private bool _arrowEnd;

    public Point Start
    {
        get => _start;
        set => Update(ref _start, value);
    }

    public Point End
    {
        get => _end;
        set => Update(ref _end, value);
    }

    public bool ArrowStart
    {
        get => _arrowStart;
        set => Update(ref _arrowStart, value);
    }

    public bool ArrowEnd
    {
        get => _arrowEnd;
        set => Update(ref _arrowEnd, value);
    }

    public override AnnotationKind Kind => _arrowStart || _arrowEnd ? AnnotationKind.Arrow : AnnotationKind.Line;

    public override string TypeName => Kind == AnnotationKind.Arrow ? "Flèche" : "Ligne";

    public override Rect Bounds => new(_start, _end);

    public override bool IsLine => true;

    public override bool HasDashStyle => true;

    public override void Translate(Vector delta)
    {
        _start += delta;
        _end += delta;
        NotifyChanged();
    }

    public override void Resize(Rect bounds)
    {
        var old = Bounds;
        _start = MapPoint(_start, old, bounds);
        _end = MapPoint(_end, old, bounds);
        NotifyChanged();
    }

    public override void TransformFrame(Func<Point, Point> map)
    {
        _start = map(_start);
        _end = map(_end);
        NotifyChanged();
    }

    public void SetEndpoint(bool start, Point point)
    {
        if (start)
        {
            _start = point;
        }
        else
        {
            _end = point;
        }

        NotifyChanged();
    }

    public override bool HitTest(Point point, double tolerance) =>
        InkGeometry.DistanceToSegment(point, _start, _end) <= tolerance + StrokeWidth / 2 + 1;

    public static Point MapPoint(Point p, Rect from, Rect to)
    {
        var fx = from.Width < 0.001 ? 0.5 : (p.X - from.X) / from.Width;
        var fy = from.Height < 0.001 ? 0.5 : (p.Y - from.Y) / from.Height;
        return new Point(to.X + fx * to.Width, to.Y + fy * to.Height);
    }

    protected override Annotation CreateEmpty() => new LineAnnotation();

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is LineAnnotation line)
        {
            _start = line._start;
            _end = line._end;
            _arrowStart = line._arrowStart;
            _arrowEnd = line._arrowEnd;
        }
    }
}

/// <summary>Dessin a main levee (stylo, surligneur ou signature manuscrite).</summary>
public sealed class InkAnnotation : Annotation
{
    private List<List<Point>> _strokes = new();
    private bool _isHighlighter;
    private Rect? _bounds;

    public InkAnnotation(bool highlighter = false)
    {
        _isHighlighter = highlighter;
    }

    public IReadOnlyList<List<Point>> Strokes => _strokes;

    public bool IsHighlighter => _isHighlighter;

    /// <summary>Vrai pour une signature placee depuis la bibliotheque.</summary>
    public bool IsSignature { get; set; }

    public override AnnotationKind Kind => _isHighlighter ? AnnotationKind.Highlighter : AnnotationKind.Ink;

    public override string TypeName => IsSignature ? "Signature" : _isHighlighter ? "Surligneur" : "Dessin";

    public override bool KeepAspectRatio => IsSignature;

    public void AddStroke(IEnumerable<Point> points)
    {
        var list = points.ToList();
        if (list.Count == 0)
        {
            return;
        }

        _strokes.Add(list);
        NotifyChanged();
    }

    public override Rect Bounds
    {
        get
        {
            if (_bounds is null)
            {
                var r = Rect.Empty;
                foreach (var stroke in _strokes)
                {
                    foreach (var p in stroke)
                    {
                        r.Union(p);
                    }
                }

                _bounds = r.IsEmpty ? new Rect(0, 0, 0, 0) : r;
            }

            return _bounds.Value;
        }
    }

    protected override void OnGeometryChanged() => _bounds = null;

    public override void TransformFrame(Func<Point, Point> map)
    {
        foreach (var stroke in _strokes)
        {
            for (var i = 0; i < stroke.Count; i++)
            {
                stroke[i] = map(stroke[i]);
            }
        }

        NotifyChanged();
    }

    public override void Translate(Vector delta)
    {
        foreach (var stroke in _strokes)
        {
            for (var i = 0; i < stroke.Count; i++)
            {
                stroke[i] += delta;
            }
        }

        NotifyChanged();
    }

    public override void Resize(Rect bounds)
    {
        var old = Bounds;
        foreach (var stroke in _strokes)
        {
            for (var i = 0; i < stroke.Count; i++)
            {
                stroke[i] = LineAnnotation.MapPoint(stroke[i], old, bounds);
            }
        }

        NotifyChanged();
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var bounds = Bounds;
        var reach = tolerance + StrokeWidth / 2 + 1;
        bounds.Inflate(reach, reach);
        if (!bounds.Contains(point))
        {
            return false;
        }

        foreach (var stroke in _strokes)
        {
            if (stroke.Count == 1 && (stroke[0] - point).Length <= reach)
            {
                return true;
            }

            for (var i = 1; i < stroke.Count; i++)
            {
                if (InkGeometry.DistanceToSegment(point, stroke[i - 1], stroke[i]) <= reach)
                {
                    return true;
                }
            }
        }

        return false;
    }

    protected override Annotation CreateEmpty() => new InkAnnotation(_isHighlighter);

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is InkAnnotation ink)
        {
            _isHighlighter = ink._isHighlighter;
            IsSignature = ink.IsSignature;
            _strokes = ink._strokes.Select(s => new List<Point>(s)).ToList();
            _bounds = null;
        }
    }
}

// =========================================================================
// Texte
// =========================================================================

public class TextBoxAnnotation : BoxAnnotation
{
    private string _text = "";
    private string _fontFamily = "Arial";
    private double _fontSize = 14;
    private bool _bold;
    private bool _italic;
    private bool _underline;
    private TextAlignment _alignment = TextAlignment.Left;
    private Color _textColor = Color.FromRgb(0x1C, 0x1C, 0x1E);
    private double _padding = 3;
    private bool _autoHeight = true;
    private TextLayoutResult? _layout;
    private int _layoutRevision = -1;

    public TextBoxAnnotation()
    {
        StrokeWidth = 0;
        StrokeColor = Color.FromRgb(0x1C, 0x1C, 0x1E);
    }

    public string Text
    {
        get => _text;
        set
        {
            if (Update(ref _text, value ?? ""))
            {
                AutoFit();
            }
        }
    }

    public string FontFamily
    {
        get => _fontFamily;
        set
        {
            if (Update(ref _fontFamily, string.IsNullOrWhiteSpace(value) ? "Arial" : value))
            {
                AutoFit();
            }
        }
    }

    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Update(ref _fontSize, Math.Clamp(value, 2, 400)))
            {
                AutoFit();
            }
        }
    }

    public bool Bold
    {
        get => _bold;
        set
        {
            if (Update(ref _bold, value))
            {
                AutoFit();
            }
        }
    }

    public bool Italic
    {
        get => _italic;
        set
        {
            if (Update(ref _italic, value))
            {
                AutoFit();
            }
        }
    }

    public bool Underline
    {
        get => _underline;
        set => Update(ref _underline, value);
    }

    public TextAlignment Alignment
    {
        get => _alignment;
        set => Update(ref _alignment, value);
    }

    public Color TextColor
    {
        get => _textColor;
        set => Update(ref _textColor, value);
    }

    public double Padding
    {
        get => _padding;
        set
        {
            if (Update(ref _padding, Math.Clamp(value, 0, 40)))
            {
                AutoFit();
            }
        }
    }

    /// <summary>La hauteur suit le texte (desactive si l'utilisateur la fixe).</summary>
    public bool AutoHeight
    {
        get => _autoHeight;
        set
        {
            if (Update(ref _autoHeight, value))
            {
                AutoFit();
            }
        }
    }

    public PdfFont Font => new(_fontFamily, _bold, _italic);

    public override AnnotationKind Kind => AnnotationKind.TextBox;

    public override string TypeName => "Zone de texte";

    public override string Summary => string.IsNullOrWhiteSpace(_text) ? TypeName : Preview(_text);

    public override bool HasFillStyle => true;

    public override bool HasTextStyle => true;

    public override bool HasDashStyle => true;

    protected override bool PreservesSizeOnTransform => true;

    public TextLayoutResult GetLayout()
    {
        if (_layout is null || _layoutRevision != Revision)
        {
            _layout = TextLayout.Layout(_text, Font, _fontSize, Math.Max(1, Rect.Width - 2 * _padding), _alignment);
            _layoutRevision = Revision;
        }

        return _layout;
    }

    public override void Resize(Rect bounds)
    {
        base.Resize(bounds);
        AutoFit();
    }

    /// <summary>Ajuste la hauteur au contenu.</summary>
    public virtual void AutoFit()
    {
        if (!_autoHeight)
        {
            return;
        }

        var layout = TextLayout.Layout(_text, Font, _fontSize, Math.Max(1, Rect.Width - 2 * _padding), _alignment);
        var height = Math.Max(layout.LineHeight, layout.ContentHeight) + 2 * _padding;
        if (Math.Abs(height - Rect.Height) > 0.05)
        {
            Rect = new Rect(Rect.X, Rect.Y, Rect.Width, height);
        }
    }

    /// <summary>Largeur ajustee au texte (creation d'une zone par simple clic).</summary>
    public void FitWidth(double maxWidth)
    {
        var layout = TextLayout.Layout(_text.Length == 0 ? "Texte" : _text, Font, _fontSize, double.PositiveInfinity, _alignment);
        var width = Math.Min(maxWidth, Math.Max(40, layout.MaxLineWidth + 2 * _padding + 4));
        Rect = new Rect(Rect.X, Rect.Y, width, Rect.Height);
        AutoFit();
    }

    protected override Annotation CreateEmpty() => new TextBoxAnnotation();

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is TextBoxAnnotation text)
        {
            _text = text._text;
            _fontFamily = text._fontFamily;
            _fontSize = text._fontSize;
            _bold = text._bold;
            _italic = text._italic;
            _underline = text._underline;
            _alignment = text._alignment;
            _textColor = text._textColor;
            _padding = text._padding;
            _autoHeight = text._autoHeight;
            _layout = null;
        }
    }
}

/// <summary>
/// Remplacement d'un texte existant du PDF : la zone d'origine est masquee
/// (ou le texte reellement retire a l'enregistrement) et le nouveau texte dessine.
/// </summary>
public sealed class TextEditAnnotation : TextBoxAnnotation
{
    private Rect _originalRect;
    private Color _coverColor = Colors.White;
    private bool _useCover;

    public Rect OriginalRect
    {
        get => _originalRect;
        set => Update(ref _originalRect, value);
    }

    public Color CoverColor
    {
        get => _coverColor;
        set => Update(ref _coverColor, value);
    }

    /// <summary>
    /// Vrai seulement quand le texte d'origine n'est pas un vrai texte PDF (page numerisee) :
    /// il est alors masque par un aplat. Sinon le texte d'origine est retire et le fond reste intact.
    /// </summary>
    public bool UseCover
    {
        get => _useCover;
        set => Update(ref _useCover, value);
    }

    public string OriginalText { get; set; } = "";

    public override AnnotationKind Kind => AnnotationKind.TextEdit;

    public override string TypeName => "Texte modifié";

    /// <summary>
    /// Une ligne modifiee s'elargit avec son texte, comme dans un traitement de texte,
    /// au lieu de passer a la ligne ; elle ne devient jamais plus etroite que la ligne d'origine.
    /// </summary>
    public override void AutoFit()
    {
        if (Text.Length > 0 && _originalRect.Width > 0)
        {
            var layout = TextLayout.Layout(Text, Font, FontSize, double.PositiveInfinity, Alignment);
            var width = Math.Min(4000, Math.Max(_originalRect.Width, layout.MaxLineWidth + 2 * Padding + FontSize * 0.5));
            if (Math.Abs(width - Rect.Width) > 0.05)
            {
                Rect = new Rect(Rect.X, Rect.Y, width, Rect.Height);
            }
        }

        base.AutoFit();
    }

    public override void TransformFrame(Func<Point, Point> map)
    {
        _originalRect = new Rect(map(_originalRect.TopLeft), map(_originalRect.BottomRight));
        base.TransformFrame(map);
    }

    protected override Annotation CreateEmpty() => new TextEditAnnotation();

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is TextEditAnnotation edit)
        {
            _originalRect = edit._originalRect;
            _coverColor = edit._coverColor;
            _useCover = edit._useCover;
            OriginalText = edit.OriginalText;
        }
    }
}

/// <summary>Surlignage, soulignement ou texte barre applique au texte du PDF.</summary>
public sealed class MarkupAnnotation : Annotation
{
    private MarkupKind _markup;
    private List<Rect> _rects;
    private Rect? _bounds;

    public MarkupAnnotation(MarkupKind markup, IEnumerable<Rect> rects, string text)
    {
        _markup = markup;
        _rects = rects.ToList();
        SelectedText = text ?? "";
    }

    public MarkupKind Markup
    {
        get => _markup;
        set => Update(ref _markup, value);
    }

    public IReadOnlyList<Rect> Rects => _rects;

    public string SelectedText { get; private set; }

    public override AnnotationKind Kind => AnnotationKind.Markup;

    public override string TypeName => _markup switch
    {
        MarkupKind.Highlight => "Surlignage",
        MarkupKind.Underline => "Soulignement",
        MarkupKind.StrikeOut => "Texte barré",
        _ => "Soulignement ondulé"
    };

    public override string Summary => string.IsNullOrWhiteSpace(SelectedText) ? TypeName : Preview(SelectedText);

    public override bool CanMove => false;

    public override bool CanResize => false;

    public override bool HasWidthStyle => false;

    public override Rect Bounds
    {
        get
        {
            if (_bounds is null)
            {
                var r = Rect.Empty;
                foreach (var rect in _rects)
                {
                    r.Union(rect);
                }

                _bounds = r.IsEmpty ? new Rect(0, 0, 0, 0) : r;
            }

            return _bounds.Value;
        }
    }

    protected override void OnGeometryChanged() => _bounds = null;

    public override void Translate(Vector delta)
    {
    }

    public override void Resize(Rect bounds)
    {
    }

    public override void TransformFrame(Func<Point, Point> map)
    {
        _rects = _rects.Select(r => new Rect(map(r.TopLeft), map(r.BottomRight))).ToList();
        NotifyChanged();
    }

    public override bool HitTest(Point point, double tolerance)
    {
        foreach (var rect in _rects)
        {
            var r = rect;
            r.Inflate(tolerance, tolerance);
            if (r.Contains(point))
            {
                return true;
            }
        }

        return false;
    }

    protected override Annotation CreateEmpty() => new MarkupAnnotation(_markup, _rects, SelectedText);

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is MarkupAnnotation markup)
        {
            _markup = markup._markup;
            _rects = new List<Rect>(markup._rects);
            SelectedText = markup.SelectedText;
            _bounds = null;
        }
    }
}

/// <summary>Note (commentaire) : devient une vraie annotation PDF a l'enregistrement.</summary>
public sealed class NoteAnnotation : Annotation
{
    public const double IconSize = 22;

    private Point _location;
    private string _text = "";

    public NoteAnnotation()
    {
        StrokeColor = Color.FromRgb(0xFF, 0xCC, 0x00);
    }

    public Point Location
    {
        get => _location;
        set => Update(ref _location, value);
    }

    public string Text
    {
        get => _text;
        set => Update(ref _text, value ?? "");
    }

    public override AnnotationKind Kind => AnnotationKind.Note;

    public override string TypeName => "Note";

    public override string Summary => string.IsNullOrWhiteSpace(_text) ? "Note vide" : Preview(_text);

    public override Rect Bounds => new(_location, new Size(IconSize, IconSize));

    public override bool CanResize => false;

    public override bool HasWidthStyle => false;

    public override bool HasOpacityStyle => false;

    public override void Translate(Vector delta)
    {
        _location += delta;
        NotifyChanged();
    }

    public override void TransformFrame(Func<Point, Point> map)
    {
        var center = map(new Point(_location.X + IconSize / 2, _location.Y + IconSize / 2));
        _location = new Point(center.X - IconSize / 2, center.Y - IconSize / 2);
        NotifyChanged();
    }

    public override void Resize(Rect bounds)
    {
    }

    protected override Annotation CreateEmpty() => new NoteAnnotation();

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is NoteAnnotation note)
        {
            _location = note._location;
            _text = note._text;
        }
    }
}

public sealed class ImageAnnotation : BoxAnnotation
{
    private byte[] _data;
    private BitmapSource? _bitmap;
    private bool _loadFailed;

    public ImageAnnotation(byte[] data)
    {
        _data = data ?? Array.Empty<byte>();
    }

    public byte[] Data => _data;

    public BitmapSource? Bitmap
    {
        get
        {
            if (_bitmap is null && !_loadFailed && _data.Length > 0)
            {
                try
                {
                    _bitmap = ImageTools.Load(_data, 2000);
                }
                catch
                {
                    _loadFailed = true;
                }
            }

            return _bitmap;
        }
    }

    public override AnnotationKind Kind => AnnotationKind.Image;

    public override string TypeName => "Image";

    protected override bool PreservesSizeOnTransform => true;

    public override bool KeepAspectRatio => true;

    public override bool HasStrokeStyle => false;

    public override bool HasWidthStyle => false;

    protected override Annotation CreateEmpty() => new ImageAnnotation(_data);

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is ImageAnnotation image)
        {
            _data = image._data;
            _bitmap = image._bitmap;
        }
    }
}

public sealed class StampAnnotation : BoxAnnotation
{
    private string _label;

    public StampAnnotation(string label)
    {
        _label = label;
        StrokeColor = Color.FromRgb(0xD6, 0x3A, 0x2F);
        StrokeWidth = 2.5;
    }

    public string Label
    {
        get => _label;
        set => Update(ref _label, string.IsNullOrWhiteSpace(value) ? "TAMPON" : value);
    }

    public override AnnotationKind Kind => AnnotationKind.Stamp;

    public override string TypeName => "Tampon";

    protected override bool PreservesSizeOnTransform => true;

    public override string Summary => _label;

    protected override Annotation CreateEmpty() => new StampAnnotation(_label);

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is StampAnnotation stamp)
        {
            _label = stamp._label;
        }
    }
}

/// <summary>Zone a caviarder : le contenu sous-jacent est definitivement detruit a l'enregistrement.</summary>
public sealed class RedactionAnnotation : BoxAnnotation
{
    public RedactionAnnotation()
    {
        FillColor = Colors.Black;
        StrokeWidth = 0;
    }

    public override AnnotationKind Kind => AnnotationKind.Redaction;

    public override string TypeName => "Caviardage";

    public override bool HasStrokeStyle => false;

    public override bool HasWidthStyle => false;

    public override bool HasOpacityStyle => false;

    protected override Annotation CreateEmpty() => new RedactionAnnotation();
}

/// <summary>Rectangle opaque (correcteur blanc) pour masquer une zone.</summary>
public sealed class WhiteoutAnnotation : BoxAnnotation
{
    public WhiteoutAnnotation()
    {
        FillColor = Colors.White;
        StrokeWidth = 0;
    }

    public override AnnotationKind Kind => AnnotationKind.Whiteout;

    public override string TypeName => "Correcteur";

    public override bool HasStrokeStyle => false;

    public override bool HasWidthStyle => false;

    public override bool HasFillStyle => true;

    public override bool HasOpacityStyle => false;

    protected override Annotation CreateEmpty() => new WhiteoutAnnotation();
}

/// <summary>Zone cliquable vers une adresse web.</summary>
public sealed class LinkAnnotation : BoxAnnotation
{
    private string _uri = "https://";

    public string Uri
    {
        get => _uri;
        set => Update(ref _uri, value ?? "");
    }

    public override AnnotationKind Kind => AnnotationKind.Link;

    public override string TypeName => "Lien";

    public override string Summary => _uri;

    public override bool HasStrokeStyle => false;

    public override bool HasWidthStyle => false;

    public override bool HasOpacityStyle => false;

    protected override Annotation CreateEmpty() => new LinkAnnotation();

    protected override void CopyState(Annotation source)
    {
        base.CopyState(source);
        if (source is LinkAnnotation link)
        {
            _uri = link._uri;
        }
    }
}

/// <summary>Tampons proposes par defaut.</summary>
public static class StampPresets
{
    public static readonly (string Label, Color Color)[] All =
    {
        ("APPROUVÉ", Color.FromRgb(0x2E, 0x9E, 0x4F)),
        ("PAYÉ", Color.FromRgb(0x2E, 0x9E, 0x4F)),
        ("REÇU", Color.FromRgb(0x2E, 0x9E, 0x4F)),
        ("VU", Color.FromRgb(0x2E, 0x9E, 0x4F)),
        ("FINAL", Color.FromRgb(0x2E, 0x9E, 0x4F)),
        ("REJETÉ", Color.FromRgb(0xD6, 0x3A, 0x2F)),
        ("CONFIDENTIEL", Color.FromRgb(0xD6, 0x3A, 0x2F)),
        ("URGENT", Color.FromRgb(0xD6, 0x3A, 0x2F)),
        ("NON CONFORME", Color.FromRgb(0xD6, 0x3A, 0x2F)),
        ("BROUILLON", Color.FromRgb(0x1F, 0x5F, 0xC9)),
        ("COPIE", Color.FromRgb(0x1F, 0x5F, 0xC9)),
        ("À SIGNER", Color.FromRgb(0x1F, 0x5F, 0xC9))
    };

    public static Color ColorFor(string label)
    {
        foreach (var (name, color) in All)
        {
            if (string.Equals(name, label, StringComparison.OrdinalIgnoreCase))
            {
                return color;
            }
        }

        return Color.FromRgb(0x1F, 0x5F, 0xC9);
    }
}
