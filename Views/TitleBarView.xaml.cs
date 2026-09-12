using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PDFEditor.Models;
using PDFEditor.ViewModels;

namespace PDFEditor.Views;

/// <summary>
/// Barre de titre et d'outils : feux tricolores, titre du document, zoom,
/// rotation, annotation, menus (exporter, plus), recherche, inspecteur.
/// </summary>
public partial class TitleBarView : UserControl
{
    public static readonly DependencyProperty SidebarWidthProperty =
        DependencyProperty.Register(nameof(SidebarWidth), typeof(double), typeof(TitleBarView),
            new FrameworkPropertyMetadata(212d, (d, _) => ((TitleBarView)d).ApplyMetrics()));

    public static readonly DependencyProperty IsSidebarShownProperty =
        DependencyProperty.Register(nameof(IsSidebarShown), typeof(bool), typeof(TitleBarView),
            new FrameworkPropertyMetadata(true, (d, _) => ((TitleBarView)d).ApplyMetrics()));

    private readonly DispatcherTimer _searchTimer;
    private MainViewModel? _main;
    private DocumentViewModel? _document;

    public TitleBarView()
    {
        InitializeComponent();

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RunSearch();
        };

        SearchBox.TextChanged += (_, _) =>
        {
            if (SearchBox.IsKeyboardFocusWithin)
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        };
        SearchBox.KeyDown += OnSearchKeyDown;

        DataContextChanged += OnDataContextChanged;
        ApplyMetrics();
    }

    public double SidebarWidth
    {
        get => (double)GetValue(SidebarWidthProperty);
        set => SetValue(SidebarWidthProperty, value);
    }

    public bool IsSidebarShown
    {
        get => (bool)GetValue(IsSidebarShownProperty);
        set => SetValue(IsSidebarShownProperty, value);
    }

    private void ApplyMetrics()
    {
        var shown = IsSidebarShown;
        SidebarColumn.Width = new GridLength(shown ? Math.Max(0, SidebarWidth) : 0);
        SidebarPane.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        CollapsedSidebarTools.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        ShowSidebarButton.Visibility = _main?.HasDocument == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================
    // Titre
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

        AttachDocument(_main?.Document);
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Document) or nameof(MainViewModel.HasDocument))
        {
            AttachDocument(_main?.Document);
            ApplyMetrics();
        }
    }

    private void AttachDocument(DocumentViewModel? document)
    {
        if (_document is not null)
        {
            _document.PropertyChanged -= OnDocumentPropertyChanged;
        }

        _document = document;
        if (_document is not null)
        {
            _document.PropertyChanged += OnDocumentPropertyChanged;
        }

        UpdateTitle();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.DisplayName) or nameof(DocumentViewModel.PageStatus)
            or nameof(DocumentViewModel.IsModified) or nameof(DocumentViewModel.IsProtected))
        {
            UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        var document = _document;
        if (document is null)
        {
            TitleText.Text = "Éditeur PDF";
            SubtitleText.Text = "Aucun document ouvert";
            return;
        }

        TitleText.Text = document.DisplayName;
        var subtitle = document.PageStatus;
        if (document.IsModified)
        {
            subtitle += " · Modifié";
        }

        if (document.IsProtected)
        {
            subtitle += " · Protégé";
        }

        SubtitleText.Text = subtitle;
    }

    // =====================================================================
    // Recherche
    // =====================================================================

    public void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }

    private void RunSearch()
    {
        if (_main is null || _main.Document is null)
        {
            return;
        }

        var text = SearchBox.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            _main.SidebarMode = SidebarModeKind.Search;
            _main.IsSidebarVisible = true;
        }

        _main.SearchCommand.Execute(text);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (_main?.Document is not { } document)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                if (_searchTimer.IsEnabled || !document.HasSearchResults)
                {
                    _searchTimer.Stop();
                    RunSearch();
                }
                else
                {
                    document.MoveSearch((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                }

                e.Handled = true;
                break;

            case Key.Escape:
                _searchTimer.Stop();
                document.ClearSearch();
                (Window.GetWindow(this) as MainWindow)?.FocusDocument();
                e.Handled = true;
                break;
        }
    }

    // =====================================================================
    // Deplacement de la fenetre
    // =====================================================================

    private void OnBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        try
        {
            window.DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // Bouton relache avant le deplacement.
        }
    }

    // =====================================================================
    // Menus
    // =====================================================================

    private static MenuItem Command(string header, ICommand command, string? gesture = null, object? parameter = null, bool? isChecked = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Command = command,
            CommandParameter = parameter,
            InputGestureText = gesture ?? ""
        };

        if (isChecked is { } value)
        {
            item.IsCheckable = true;
            item.IsChecked = value;
        }

        return item;
    }

    private static MenuItem Submenu(string header, params object[] items)
    {
        var menu = new MenuItem { Header = header };
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }

        return menu;
    }

    private static void Open(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -8;
        menu.VerticalOffset = -2;
        menu.IsOpen = true;
    }

    private void OnZoomClick(object sender, RoutedEventArgs e)
    {
        if (_main?.Document is not { } document)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Command("Ajuster à la largeur", _main.FitWidthCommand, "Ctrl+1", isChecked: document.ZoomMode == ZoomModeKind.FitWidth));
        menu.Items.Add(Command("Page entière", _main.FitPageCommand, "Ctrl+2", isChecked: document.ZoomMode == ZoomModeKind.FitPage));
        menu.Items.Add(Command("Taille réelle", _main.ActualSizeCommand, "Ctrl+0"));
        menu.Items.Add(new Separator());

        foreach (var zoom in new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0 })
        {
            var current = document.ZoomMode == ZoomModeKind.Custom && Math.Abs(document.Zoom - zoom) < 0.005;
            menu.Items.Add(Command($"{zoom * 100:0} %", _main.SetZoomCommand, parameter: zoom, isChecked: current));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Command("Défilement continu", _main.SetScrollModeCommand, parameter: ScrollModeKind.Continuous, isChecked: document.ScrollMode == ScrollModeKind.Continuous));
        menu.Items.Add(Command("Page unique", _main.SetScrollModeCommand, parameter: ScrollModeKind.SinglePage, isChecked: document.ScrollMode == ScrollModeKind.SinglePage));
        menu.Items.Add(Command("Deux pages", _main.SetScrollModeCommand, parameter: ScrollModeKind.TwoPages, isChecked: document.ScrollMode == ScrollModeKind.TwoPages));
        menu.Items.Add(new Separator());
        menu.Items.Add(Command("Mode lecture nuit", _main.ToggleNightModeCommand, isChecked: _main.NightMode));
        Open(menu, ZoomButton);
    }

    private void OnInsertPageClick(object sender, RoutedEventArgs e)
    {
        if (_main is null)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Command("Page vierge…", _main.InsertBlankPageCommand));
        menu.Items.Add(Command("Pages d’un autre PDF…", _main.InsertPagesFromFileCommand));
        menu.Items.Add(Command("Images comme pages…", _main.InsertImagePagesCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Command("Dupliquer la page", _main.DuplicatePagesCommand));
        Open(menu, InsertPageButton);
    }

    private void OnShareClick(object sender, RoutedEventArgs e)
    {
        if (_main is null)
        {
            return;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Command("Enregistrer", _main.SaveCommand, "Ctrl+S"));
        menu.Items.Add(Command("Enregistrer une copie sous…", _main.SaveAsCommand, "Ctrl+Maj+S"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Command("Imprimer…", _main.PrintCommand, "Ctrl+P"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Command("Exporter en images…", _main.ExportImagesCommand));
        menu.Items.Add(Command("Exporter le texte…", _main.ExportTextCommand));
        menu.Items.Add(Command("Extraire des pages…", _main.ExtractPagesCommand));
        menu.Items.Add(Command("Diviser le document…", _main.SplitCommand));
        menu.Items.Add(Command("Pages par feuille…", _main.NUpCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Command("Afficher dans l’Explorateur", _main.RevealInExplorerCommand));
        menu.Items.Add(Command("Copier le chemin d’accès", _main.CopyPathCommand));
        Open(menu, ShareButton);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var main = _main;
        if (main is null)
        {
            return;
        }

        var document = main.Document;
        var menu = new ContextMenu();

        menu.Items.Add(Command("Nouvelle fenêtre", main.NewWindowCommand, "Ctrl+N"));
        menu.Items.Add(Command("Ouvrir…", main.OpenCommand, "Ctrl+O"));

        var recent = new MenuItem { Header = "Ouvrir un fichier récent", IsEnabled = main.HasRecentFiles };
        foreach (var file in main.RecentFiles.Take(10))
        {
            recent.Items.Add(Command(file.Name, main.OpenRecentCommand, parameter: file.Path));
        }

        if (main.HasRecentFiles)
        {
            recent.Items.Add(new Separator());
            recent.Items.Add(Command("Effacer la liste", main.ClearRecentCommand));
        }

        menu.Items.Add(recent);
        menu.Items.Add(Command("Nouveau document vierge…", main.CreateBlankCommand));
        menu.Items.Add(Command("Créer un PDF à partir d’images…", main.CreateFromImagesCommand));
        menu.Items.Add(Command("Fusionner des PDF…", main.MergeCommand));

        if (document is not null)
        {
            menu.Items.Add(new Separator());

            menu.Items.Add(Submenu("Pages",
                Command("Insérer une page vierge…", main.InsertBlankPageCommand),
                Command("Insérer des pages d’un PDF…", main.InsertPagesFromFileCommand),
                Command("Insérer des images comme pages…", main.InsertImagePagesCommand),
                new Separator(),
                Command("Dupliquer", main.DuplicatePagesCommand),
                Command("Supprimer", main.DeletePagesCommand),
                Command("Monter", main.MovePagesUpCommand),
                Command("Descendre", main.MovePagesDownCommand),
                new Separator(),
                Command("Pivoter à gauche", main.RotateLeftCommand, "Ctrl+L"),
                Command("Pivoter à droite", main.RotateRightCommand, "Ctrl+R"),
                Command("Pivoter tout le document à droite", main.RotateAllCommand, parameter: 1),
                Command("Pivoter tout le document à gauche", main.RotateAllCommand, parameter: -1),
                new Separator(),
                Command("Recadrer les marges…", main.CropPagesCommand),
                Command("Aller à la page…", main.GoToPageCommand, "Ctrl+G")));

            menu.Items.Add(Submenu("Document",
                Command("Propriétés du document", main.PropertiesCommand),
                Command("Protéger par mot de passe…", main.ProtectCommand),
                Command("Retirer la protection", main.RemoveProtectionCommand),
                new Separator(),
                Command("Ajouter un filigrane…", main.WatermarkCommand),
                Command("Numéroter les pages…", main.HeaderFooterCommand),
                Command("Reconnaissance de texte (OCR)…", main.OcrCommand),
                new Separator(),
                Command("Intégrer les annotations au document…", main.FlattenAnnotationsCommand)));

            menu.Items.Add(Submenu("Affichage",
                Command("Barre latérale", main.ToggleSidebarCommand, "Ctrl+Alt+S", isChecked: main.IsSidebarVisible),
                Command("Inspecteur", main.ToggleInspectorCommand, "Ctrl+Alt+I", isChecked: main.IsInspectorVisible),
                Command("Barre d’annotation", main.ToggleMarkupBarCommand, "Ctrl+Maj+A", isChecked: main.IsMarkupBarVisible),
                new Separator(),
                Command("Défilement continu", main.SetScrollModeCommand, parameter: ScrollModeKind.Continuous, isChecked: document.ScrollMode == ScrollModeKind.Continuous),
                Command("Page unique", main.SetScrollModeCommand, parameter: ScrollModeKind.SinglePage, isChecked: document.ScrollMode == ScrollModeKind.SinglePage),
                Command("Deux pages", main.SetScrollModeCommand, parameter: ScrollModeKind.TwoPages, isChecked: document.ScrollMode == ScrollModeKind.TwoPages),
                new Separator(),
                Command("Mode lecture nuit", main.ToggleNightModeCommand, isChecked: main.NightMode),
                Command("Surligner les champs de formulaire", main.ToggleFormHighlightCommand, isChecked: main.HighlightFormFields)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Submenu("Apparence",
            Command("Claire", main.SetThemeCommand, parameter: AppTheme.Light, isChecked: main.Theme == AppTheme.Light),
            Command("Sombre", main.SetThemeCommand, parameter: AppTheme.Dark, isChecked: main.Theme == AppTheme.Dark),
            Command("Selon Windows", main.SetThemeCommand, parameter: AppTheme.System, isChecked: main.Theme == AppTheme.System)));
        menu.Items.Add(Command("Réglages…", main.PreferencesCommand));
        menu.Items.Add(Command("Raccourcis clavier", main.ShowShortcutsCommand, "F1"));
        menu.Items.Add(Command("À propos d’Éditeur PDF", main.ShowAboutCommand));

        if (document is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Command("Fermer le document", main.CloseDocumentCommand));
        }

        Open(menu, MoreButton);
    }
}
