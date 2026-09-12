using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PDFEditor.Controls;
using PDFEditor.Models;
using PDFEditor.Pdf;
using PDFEditor.Rendering;
using PDFEditor.Services;
using PDFEditor.ViewModels;

namespace PDFEditor.Views;

/// <summary>
/// Barre d'annotation (facon « Annoter » d'Apercu) : outils, formes, tampons,
/// signatures, couleurs, epaisseur et police.
/// </summary>
public partial class MarkupBarView : UserControl
{
    private readonly Dictionary<EditorTool, ToggleButton> _toolButtons;
    private MainViewModel? _main;
    private DocumentViewModel? _document;
    private EditorTool _shapeTool = EditorTool.Rectangle;
    private EditorTool _markupTool = EditorTool.TextHighlight;

    public MarkupBarView()
    {
        InitializeComponent();

        _toolButtons = new Dictionary<EditorTool, ToggleButton>
        {
            [EditorTool.Select] = SelectTool,
            [EditorTool.Hand] = HandTool,
            [EditorTool.Marquee] = MarqueeTool,
            [EditorTool.EditText] = EditTextTool,
            [EditorTool.TextBox] = TextBoxTool,
            [EditorTool.Pen] = PenTool,
            [EditorTool.Highlighter] = HighlighterTool,
            [EditorTool.Note] = NoteTool,
            [EditorTool.Stamp] = StampTool,
            [EditorTool.Signature] = SignatureTool,
            [EditorTool.Link] = LinkTool,
            [EditorTool.Whiteout] = WhiteoutTool,
            [EditorTool.Redact] = RedactTool
        };

        BuildWidthPresets();
        DataContextChanged += OnDataContextChanged;

        FontSizeBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FontSizeBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
        };
    }

    // =====================================================================
    // Liaison au document
    // =====================================================================

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

        Attach(_main?.Document);
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Document))
        {
            Attach(_main?.Document);
        }
    }

    private void Attach(DocumentViewModel? document)
    {
        if (_document is not null)
        {
            _document.PropertyChanged -= OnDocumentPropertyChanged;
            _document.ToolChanged -= Refresh;
            _document.SelectionChanged -= Refresh;
        }

        _document = document;
        if (_document is not null)
        {
            _document.PropertyChanged += OnDocumentPropertyChanged;
            _document.ToolChanged += Refresh;
            _document.SelectionChanged += Refresh;
        }

        Refresh();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && (name.StartsWith("Style", StringComparison.Ordinal) || name == nameof(DocumentViewModel.Tool)))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var tool = _document?.Tool ?? EditorTool.Select;

        foreach (var (key, button) in _toolButtons)
        {
            button.IsChecked = key == tool;
        }

        if (tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow)
        {
            _shapeTool = tool;
        }

        if (tool is EditorTool.TextHighlight or EditorTool.TextUnderline or EditorTool.TextStrike or EditorTool.TextSquiggly)
        {
            _markupTool = tool;
        }

        ShapeTool.IsChecked = tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow;
        ShapeTool.Tag = _shapeTool.ToString();
        IconHost.SetGeometry(ShapeTool, Icon(_shapeTool switch
        {
            EditorTool.Ellipse => "Icon.Ellipse",
            EditorTool.Line => "Icon.Line",
            EditorTool.Arrow => "Icon.Arrow",
            _ => "Icon.Rectangle"
        }));
        ShapeTool.ToolTip = _shapeTool switch
        {
            EditorTool.Ellipse => "Ellipse (O)",
            EditorTool.Line => "Ligne (L)",
            EditorTool.Arrow => "Flèche (A)",
            _ => "Rectangle (R)"
        };

        TextMarkupTool.IsChecked = tool is EditorTool.TextHighlight or EditorTool.TextUnderline or EditorTool.TextStrike or EditorTool.TextSquiggly;
        TextMarkupTool.Tag = _markupTool.ToString();
        IconHost.SetGeometry(TextMarkupTool, Icon(_markupTool switch
        {
            EditorTool.TextUnderline => "Icon.Underline",
            EditorTool.TextStrike => "Icon.Strikethrough",
            _ => "Icon.TextHighlight"
        }));
        TextMarkupTool.ToolTip = _markupTool switch
        {
            EditorTool.TextUnderline => "Souligner le texte",
            EditorTool.TextStrike => "Barrer le texte",
            EditorTool.TextSquiggly => "Souligner (ondulé)",
            _ => "Surligner le texte (U)"
        };

        UpdateSwatches();
    }

    private Geometry? Icon(string key) => TryFindResource(key) as Geometry;

    private void UpdateSwatches()
    {
        var document = _document;
        if (document is null)
        {
            return;
        }

        var textStyle = document.StyleKey == ToolKeys.Text;
        var stroke = textStyle ? document.StyleTextColor : document.StyleStrokeColor;
        StrokeSwatch.Fill = AnnotationRenderer.Brush(Color.FromRgb(stroke.R, stroke.G, stroke.B));
        StrokeColorButton.ToolTip = textStyle ? "Couleur du texte" : "Couleur du trait";

        var fill = document.StyleFillColor;
        FillSwatch.Background = fill.A == 0 ? Brushes.White : AnnotationRenderer.Brush(Color.FromRgb(fill.R, fill.G, fill.B));
        FillSlash.Visibility = fill.A == 0 ? Visibility.Visible : Visibility.Collapsed;

        FillColorButton.IsEnabled = document.SelectedAnnotation?.HasFillStyle
                                    ?? document.Tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.TextBox or EditorTool.Whiteout;
        FontButton.IsEnabled = document.SelectedAnnotation?.HasTextStyle ?? document.Tool is EditorTool.TextBox or EditorTool.EditText or EditorTool.Select;
    }

    // =====================================================================
    // Outils
    // =====================================================================

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (_main is not null && sender is FrameworkElement { Tag: string tool })
        {
            _main.SetToolCommand.Execute(tool);
        }

        Refresh();
    }

    private void OpenMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -24;
        menu.VerticalOffset = -2;
        menu.IsOpen = true;
    }

    private MenuItem ToolItem(string header, EditorTool tool, bool isChecked)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => _main?.SetToolCommand.Execute(tool);
        return item;
    }

    private void OnTextMarkupMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(ToolItem("Surligner", EditorTool.TextHighlight, _markupTool == EditorTool.TextHighlight));
        menu.Items.Add(ToolItem("Souligner", EditorTool.TextUnderline, _markupTool == EditorTool.TextUnderline));
        menu.Items.Add(ToolItem("Barrer", EditorTool.TextStrike, _markupTool == EditorTool.TextStrike));
        menu.Items.Add(ToolItem("Souligner (ondulé)", EditorTool.TextSquiggly, _markupTool == EditorTool.TextSquiggly));
        OpenMenu(menu, TextMarkupTool);
    }

    private void OnShapeMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(ToolItem("Rectangle", EditorTool.Rectangle, _shapeTool == EditorTool.Rectangle));
        menu.Items.Add(ToolItem("Ellipse", EditorTool.Ellipse, _shapeTool == EditorTool.Ellipse));
        menu.Items.Add(ToolItem("Ligne", EditorTool.Line, _shapeTool == EditorTool.Line));
        menu.Items.Add(ToolItem("Flèche", EditorTool.Arrow, _shapeTool == EditorTool.Arrow));
        OpenMenu(menu, ShapeTool);
    }

    private void OnStampMenuClick(object sender, RoutedEventArgs e)
    {
        if (_main is null)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var stamp in _main.StampChoices)
        {
            menu.Items.Add(StampItem(stamp.Label, stamp.Color));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(StampItem($"REÇU LE {DateTime.Now:dd/MM/yyyy}", Color.FromRgb(0x1F, 0x5F, 0xC9)));
        menu.Items.Add(StampItem($"VALIDÉ LE {DateTime.Now:dd/MM/yyyy}", Color.FromRgb(0x2E, 0x9E, 0x4F)));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Tampon personnalisé…", Command = _main.CustomStampCommand });
        OpenMenu(menu, StampTool);
    }

    private MenuItem StampItem(string label, Color color)
    {
        var brush = AnnotationRenderer.Brush(color);
        var header = new Border
        {
            BorderBrush = brush,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 6, 1),
            Child = new TextBlock { Text = label, Foreground = brush, FontWeight = FontWeights.Bold, FontSize = 11 }
        };

        var item = new MenuItem { Header = header, Height = 28 };
        item.Click += (_, _) => _main?.SelectStampCommand.Execute(label);
        return item;
    }

    private void OnSignatureMenuClick(object sender, RoutedEventArgs e)
    {
        if (_main is null)
        {
            return;
        }

        var menu = new ContextMenu();
        foreach (var signature in _main.Signatures)
        {
            var item = new MenuItem { Header = CreateSignaturePreview(signature), Height = 42 };
            var captured = signature;
            item.Click += (_, _) => _main.SelectSignatureCommand.Execute(captured);
            menu.Items.Add(item);
        }

        if (_main.Signatures.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = "Créer une signature…", Command = _main.CreateSignatureCommand });

        if (_main.Signatures.Count > 0)
        {
            var remove = new MenuItem { Header = "Supprimer une signature" };
            foreach (var signature in _main.Signatures)
            {
                var item = new MenuItem { Header = CreateSignaturePreview(signature), Height = 42 };
                var captured = signature;
                item.Click += (_, _) => _main.DeleteSignatureCommand.Execute(captured);
                remove.Items.Add(item);
            }

            menu.Items.Add(remove);
        }

        OpenMenu(menu, SignatureTool);
    }

    public static FrameworkElement CreateSignaturePreview(SavedSignature signature)
    {
        var color = AnnotationRenderer.Brush(ColorUtil.Parse(signature.Color, Color.FromRgb(0x1C, 0x2A, 0x6B)));

        try
        {
            switch (signature.Kind)
            {
                case SignatureKind.Ink when signature.Strokes.Count > 0:
                {
                    var strokes = signature.Strokes
                        .Select(s => (IReadOnlyList<Point>)s.Where(p => p.Length >= 2).Select(p => new Point(p[0], p[1])).ToList())
                        .ToList();
                    var path = new Path
                    {
                        Data = AnnotationRenderer.BuildInkGeometry(strokes),
                        Stroke = color,
                        StrokeThickness = Math.Max(1.5, signature.StrokeWidth),
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    return new Viewbox { Width = 120, Height = 32, Stretch = Stretch.Uniform, Child = path };
                }

                case SignatureKind.Text:
                    return new TextBlock
                    {
                        Text = signature.Text,
                        FontFamily = FontCatalog.GetWpfFamily(signature.FontFamily ?? "Segoe Script"),
                        FontSize = 18,
                        Foreground = color,
                        MaxWidth = 180,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };

                case SignatureKind.Image when !string.IsNullOrEmpty(signature.ImageBase64):
                    return new Image
                    {
                        Source = ImageTools.Load(Convert.FromBase64String(signature.ImageBase64!), 240),
                        Height = 32,
                        MaxWidth = 140,
                        Stretch = Stretch.Uniform
                    };
            }
        }
        catch
        {
            // Signature illisible : libelle generique.
        }

        return new TextBlock { Text = "Signature" };
    }

    // =====================================================================
    // Couleurs, epaisseur, police
    // =====================================================================

    private void OnStrokeColorClick(object sender, RoutedEventArgs e)
    {
        var document = _document;
        var main = _main;
        if (document is null || main is null)
        {
            return;
        }

        var textStyle = document.StyleKey == ToolKeys.Text;
        OpenPalette(StrokeColorButton, textStyle ? document.StyleTextColor : document.StyleStrokeColor, false, color =>
        {
            if (textStyle)
            {
                main.TextColorCommand.Execute(color);
            }
            else
            {
                main.StrokeColorCommand.Execute(color);
            }
        });
    }

    private void OnFillColorClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || _main is null)
        {
            return;
        }

        var main = _main;
        OpenPalette(FillColorButton, _document.StyleFillColor, true, color => main.FillColorCommand.Execute(color));
    }

    private static void OpenPalette(UIElement target, Color current, bool allowTransparent, Action<Color> apply)
    {
        var palette = new ColorPalette(allowTransparent, current);
        var popup = new Popup
        {
            Child = palette,
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -110,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };

        palette.ColorPicked += color =>
        {
            apply(color);
            popup.IsOpen = false;
        };

        popup.IsOpen = true;
    }

    private void BuildWidthPresets()
    {
        foreach (var width in new[] { 1.0, 2.0, 3.0, 5.0, 8.0, 12.0 })
        {
            var line = new Rectangle
            {
                Width = 22,
                Height = Math.Min(10, Math.Max(1, width * 0.8)),
                RadiusX = Math.Min(5, width * 0.4),
                RadiusY = Math.Min(5, width * 0.4)
            };
            line.SetResourceReference(Shape.FillProperty, "IconActiveBrush");

            var button = new Button { Content = line, Width = 34, Height = 28, ToolTip = $"{width} pt" };
            button.SetResourceReference(StyleProperty, "ToolbarIconButton");
            var captured = width;
            button.Click += (_, _) =>
            {
                if (_document is not null)
                {
                    _document.StyleStrokeWidth = captured;
                }
            };

            WidthPresets.Children.Add(button);
        }
    }

    private void OnWidthClick(object sender, RoutedEventArgs e) => WidthPopup.IsOpen = true;

    private void OnFontClick(object sender, RoutedEventArgs e) => FontPopup.IsOpen = true;

    private void OnFontSmaller(object sender, RoutedEventArgs e) => StepFontSize(-1);

    private void OnFontLarger(object sender, RoutedEventArgs e) => StepFontSize(1);

    private void StepFontSize(int direction)
    {
        if (_document is null || _main is null)
        {
            return;
        }

        var sizes = _main.FontSizes;
        var current = _document.StyleFontSize;
        var next = direction > 0
            ? sizes.FirstOrDefault(s => s > current + 0.01, current + 4)
            : sizes.LastOrDefault(s => s < current - 0.01, Math.Max(4, current - 1));
        _document.StyleFontSize = next;
    }
}
