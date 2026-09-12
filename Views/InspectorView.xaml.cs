using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PDFEditor.Services;
using PDFEditor.ViewModels;

namespace PDFEditor.Views;

/// <summary>Inspecteur : style de la selection ou de l'outil, informations du document.</summary>
public partial class InspectorView : UserControl
{
    private MainViewModel? _main;
    private DocumentViewModel? _document;

    public InspectorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Entree valide les champs numeriques (liaison « perte du focus »).
        AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == Key.Enter && e.OriginalSource is TextBox { AcceptsReturn: false } box)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
        }), true);
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
            _document.SelectionChanged -= Refresh;
            _document.ToolChanged -= Refresh;
        }

        _document = document;
        if (_document is not null)
        {
            _document.PropertyChanged += OnDocumentPropertyChanged;
            _document.SelectionChanged += Refresh;
            _document.ToolChanged += Refresh;
        }

        Refresh();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.HasForms) or nameof(DocumentViewModel.Pdf))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var document = _document;
        FormsText.Text = document is null ? "" : document.HasForms ? "Oui (champs remplissables)" : "Non";

        if (document?.SelectedAnnotation is { } selected && document.SelectedAnnotationPage is { } page)
        {
            SelectionIcon.Data = TryFindResource(new AnnotationListItem(selected, page).IconKey) as Geometry;
        }

        var tool = document?.Tool ?? EditorTool.Select;
        var showTool = document is not null && !document.HasSelection
                       && tool is not (EditorTool.Select or EditorTool.Hand or EditorTool.Marquee or EditorTool.Image or EditorTool.Redact or EditorTool.Link or EditorTool.EditText);
        ToolStylePanel.Visibility = showTool ? Visibility.Visible : Visibility.Collapsed;
        if (!showTool || document is null)
        {
            return;
        }

        var key = document.StyleKey;
        var isText = key == ToolKeys.Text;
        var isMarkup = tool is EditorTool.TextHighlight or EditorTool.TextUnderline or EditorTool.TextStrike or EditorTool.TextSquiggly;

        ToolStyleHeader.Text = tool switch
        {
            EditorTool.Pen => "STYLE DU STYLO",
            EditorTool.Highlighter => "STYLE DU SURLIGNEUR",
            EditorTool.TextBox => "STYLE DU TEXTE",
            EditorTool.Rectangle or EditorTool.Ellipse => "STYLE DES FORMES",
            EditorTool.Line or EditorTool.Arrow => "STYLE DES LIGNES",
            EditorTool.Note => "COULEUR DES NOTES",
            EditorTool.Stamp => "TAMPON",
            EditorTool.Signature => "SIGNATURE",
            EditorTool.Whiteout => "CORRECTEUR",
            _ when isMarkup => "STYLE DU MARQUAGE",
            _ => "STYLE DE L’OUTIL"
        };

        ToolStrokeRow.Visibility = !isText && tool is not (EditorTool.Whiteout or EditorTool.Signature) ? Visibility.Visible : Visibility.Collapsed;
        ToolTextColorRow.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        ToolFillRow.Visibility = tool is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.TextBox or EditorTool.Whiteout ? Visibility.Visible : Visibility.Collapsed;
        ToolWidthRow.Visibility = tool is EditorTool.Pen or EditorTool.Highlighter or EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow or EditorTool.Stamp ? Visibility.Visible : Visibility.Collapsed;
        ToolOpacityRow.Visibility = tool is not (EditorTool.Note or EditorTool.Whiteout or EditorTool.Signature or EditorTool.TextBox) ? Visibility.Visible : Visibility.Collapsed;
        ToolFontRow.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;
        ToolFontSizeRow.Visibility = isText ? Visibility.Visible : Visibility.Collapsed;

        ToolHint.Text = tool switch
        {
            EditorTool.Pen or EditorTool.Highlighter => "Dessinez directement sur la page. Maj contraint les formes.",
            EditorTool.TextBox => "Cliquez pour écrire, ou tracez un cadre pour fixer la largeur.",
            EditorTool.Rectangle or EditorTool.Ellipse => "Tracez la forme ; Maj pour un carré ou un cercle.",
            EditorTool.Line or EditorTool.Arrow => "Tracez la ligne ; Maj pour des angles de 45°.",
            EditorTool.Note => "Cliquez à l’endroit du commentaire.",
            EditorTool.Stamp => $"Cliquez pour apposer « {document.StampLabel} ».",
            EditorTool.Signature => "Cliquez à l’endroit où signer.",
            EditorTool.Whiteout => "Tracez un rectangle pour masquer une zone.",
            _ when isMarkup => "Sélectionnez le texte à marquer.",
            _ => ""
        };
    }
}
