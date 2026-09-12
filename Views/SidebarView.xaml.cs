using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PDFEditor.Models;
using PDFEditor.ViewModels;

namespace PDFEditor.Views;

/// <summary>
/// Barre laterale : vignettes (reorganisables par glisser-deposer), signets,
/// liste des annotations et recherche.
/// </summary>
public partial class SidebarView : UserControl
{
    private const string PagesDataFormat = "PDFEditor.Pages";

    private readonly DispatcherTimer _searchTimer;
    private MainViewModel? _main;
    private DocumentViewModel? _document;
    private bool _syncing;
    private Point _dragStart;
    private bool _dragArmed;

    public SidebarView()
    {
        InitializeComponent();

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _main?.SearchCommand.Execute(SidebarSearchBox.Text);
        };

        SidebarSearchBox.TextChanged += (_, _) =>
        {
            if (SidebarSearchBox.IsKeyboardFocusWithin)
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        };
        SidebarSearchBox.KeyDown += OnSearchKeyDown;
        MatchCaseBox.Click += (_, _) => RerunSearch();
        WholeWordBox.Click += (_, _) => RerunSearch();

        PageList.SelectionChanged += OnPageSelectionChanged;
        PageList.PreviewMouseLeftButtonDown += OnPagePreviewMouseDown;
        PageList.PreviewMouseMove += OnPagePreviewMouseMove;
        PageList.DragOver += OnPageDragOver;
        PageList.DragLeave += (_, _) => DropIndicator.Visibility = Visibility.Collapsed;
        PageList.Drop += OnPageDrop;
        PageList.KeyDown += OnPageListKeyDown;

        OutlineTree.SelectedItemChanged += (_, e) =>
        {
            if (e.NewValue is OutlineItemViewModel item)
            {
                _main?.ShowOutlineItemCommand.Execute(item);
            }
        };

        AnnotationList.SelectionChanged += (_, _) =>
        {
            if (!_syncing && AnnotationList.SelectedItem is AnnotationListItem item)
            {
                _main?.ShowAnnotationCommand.Execute(item);
            }
        };

        SearchList.SelectionChanged += (_, _) =>
        {
            if (!_syncing && SearchList.SelectedItem is SearchResultViewModel result)
            {
                _main?.ShowSearchResultCommand.Execute(result);
            }
        };

        DataContextChanged += OnDataContextChanged;
    }

    public void FocusSearchBox()
    {
        SidebarSearchBox.Focus();
        Keyboard.Focus(SidebarSearchBox);
        SidebarSearchBox.SelectAll();
    }

    // =====================================================================
    // Liaison
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
            PageList.ContextMenu = BuildPageMenu(_main);
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
            _document.SelectionChanged -= SyncAnnotationSelection;
            _document.SearchResultsChanged -= SyncSearchSelection;
            _document.PagesChanged -= OnPagesChanged;
        }

        _document = document;
        if (_document is not null)
        {
            _document.PropertyChanged += OnDocumentPropertyChanged;
            _document.SelectionChanged += SyncAnnotationSelection;
            _document.SearchResultsChanged += SyncSearchSelection;
            _document.PagesChanged += OnPagesChanged;
        }

        UpdateAnnotationHeader();
        Dispatcher.BeginInvoke(new Action(SyncPageSelection), DispatcherPriority.Background);
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.CurrentPageIndex):
                SyncPageSelection();
                break;
            case nameof(DocumentViewModel.AnnotationCount):
                UpdateAnnotationHeader();
                break;
        }
    }

    private void OnPagesChanged() => Dispatcher.BeginInvoke(new Action(SyncPageSelection), DispatcherPriority.Background);

    private void UpdateAnnotationHeader()
    {
        var count = _document?.AnnotationCount ?? 0;
        AnnotationHeader.Text = count switch
        {
            0 => "",
            1 => "1 ANNOTATION",
            _ => $"{count} ANNOTATIONS"
        };
    }

    // =====================================================================
    // Vignettes
    // =====================================================================

    private void SyncPageSelection()
    {
        if (_document?.CurrentPage is not { } page || PageList.SelectedItems.Count > 1)
        {
            return;
        }

        _syncing = true;
        try
        {
            PageList.SelectedItem = page;
            PageList.ScrollIntoView(page);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnPageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _document is null)
        {
            return;
        }

        if (PageList.SelectedItems.Count == 1 && PageList.SelectedItem is PageViewModel page && page.Index != _document.CurrentPageIndex)
        {
            _document.GoToPage(page.Index);
        }
    }

    private void OnPageListKeyDown(object sender, KeyEventArgs e)
    {
        if (_main is null)
        {
            return;
        }

        if (e.Key is Key.Delete or Key.Back)
        {
            if (_main.DeletePagesCommand.CanExecute(null))
            {
                _main.DeletePagesCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private static ContextMenu BuildPageMenu(MainViewModel main)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Pivoter à gauche", Command = main.RotateLeftCommand, InputGestureText = "Ctrl+L" });
        menu.Items.Add(new MenuItem { Header = "Pivoter à droite", Command = main.RotateRightCommand, InputGestureText = "Ctrl+R" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Dupliquer", Command = main.DuplicatePagesCommand });
        menu.Items.Add(new MenuItem { Header = "Insérer une page vierge…", Command = main.InsertBlankPageCommand });
        menu.Items.Add(new MenuItem { Header = "Insérer des pages d’un PDF…", Command = main.InsertPagesFromFileCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Monter", Command = main.MovePagesUpCommand });
        menu.Items.Add(new MenuItem { Header = "Descendre", Command = main.MovePagesDownCommand });
        menu.Items.Add(new MenuItem { Header = "Extraire dans un nouveau PDF…", Command = main.ExtractPagesCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Supprimer", Command = main.DeletePagesCommand, InputGestureText = "Suppr" });
        return menu;
    }

    private static ListBoxItem? FindContainer(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null and not ListBox)
        {
            if (node is ListBoxItem item)
            {
                return item;
            }

            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private void OnPagePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(PageList);
        _dragArmed = FindContainer(e.OriginalSource) is not null;
    }

    private void OnPagePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || _document is null)
        {
            return;
        }

        if ((e.GetPosition(PageList) - _dragStart).Length < 8)
        {
            return;
        }

        _dragArmed = false;
        var indices = PageList.SelectedItems.OfType<PageViewModel>().Select(p => p.Index).OrderBy(i => i).ToArray();
        if (indices.Length == 0)
        {
            return;
        }

        DragDrop.DoDragDrop(PageList, new DataObject(PagesDataFormat, indices), DragDropEffects.Move);
        DropIndicator.Visibility = Visibility.Collapsed;
    }

    private (int InsertBefore, double Y) ComputeInsertion(Point position)
    {
        var document = _document;
        if (document is null || document.Pages.Count == 0)
        {
            return (0, 0);
        }

        var lastBottom = 0.0;
        var lastIndex = -1;
        for (var i = 0; i < document.Pages.Count; i++)
        {
            if (PageList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item || !item.IsVisible)
            {
                continue;
            }

            var top = item.TranslatePoint(new Point(0, 0), PageList).Y;
            var bottom = top + item.ActualHeight;
            if (position.Y < top + item.ActualHeight / 2)
            {
                return (i, top);
            }

            if (position.Y <= bottom)
            {
                return (i + 1, bottom);
            }

            lastBottom = bottom;
            lastIndex = i;
        }

        return (lastIndex + 1, lastBottom);
    }

    private void OnPageDragOver(object sender, DragEventArgs e)
    {
        var internalDrag = e.Data.GetDataPresent(PagesDataFormat);
        var pdfs = GetPdfFiles(e);
        if (_document is null || (!internalDrag && pdfs.Length == 0))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = internalDrag ? DragDropEffects.Move : DragDropEffects.Copy;
        var (_, y) = ComputeInsertion(e.GetPosition(PageList));
        DropIndicator.Width = Math.Max(20, PageList.ActualWidth - 36);
        Canvas.SetLeft(DropIndicator, 18);
        Canvas.SetTop(DropIndicator, y - 1.5);
        DropIndicator.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void OnPageDrop(object sender, DragEventArgs e)
    {
        DropIndicator.Visibility = Visibility.Collapsed;
        if (_document is null)
        {
            return;
        }

        var (insertBefore, _) = ComputeInsertion(e.GetPosition(PageList));
        if (e.Data.GetData(PagesDataFormat) is int[] indices)
        {
            try
            {
                _document.MovePages(indices, insertBefore);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this)!, ex.Message, "Éditeur PDF");
            }
        }
        else
        {
            var pdfs = GetPdfFiles(e);
            if (pdfs.Length > 0)
            {
                _main?.InsertPdfFiles(pdfs, insertBefore);
            }
        }

        e.Handled = true;
    }

    private static string[] GetPdfFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => string.Equals(System.IO.Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();

    // =====================================================================
    // Annotations et recherche
    // =====================================================================

    private void SyncAnnotationSelection()
    {
        if (_document is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            var selected = _document.SelectedAnnotation;
            AnnotationList.SelectedItem = selected is null
                ? null
                : _document.AnnotationItems.FirstOrDefault(i => ReferenceEquals(i.Annotation, selected));
            if (AnnotationList.SelectedItem is not null)
            {
                AnnotationList.ScrollIntoView(AnnotationList.SelectedItem);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncSearchSelection()
    {
        if (_document is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            SearchList.SelectedItem = _document.CurrentSearchResult;
            if (_document.CurrentSearchResult is not null)
            {
                SearchList.ScrollIntoView(_document.CurrentSearchResult);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RerunSearch()
    {
        if (_document is not null && !string.IsNullOrWhiteSpace(_document.SearchQuery))
        {
            _ = _document.SearchAsync();
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                if (_searchTimer.IsEnabled || !_document.HasSearchResults)
                {
                    _searchTimer.Stop();
                    _ = _document.SearchAsync(SidebarSearchBox.Text);
                }
                else
                {
                    _document.MoveSearch((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                }

                e.Handled = true;
                break;

            case Key.Escape:
                _searchTimer.Stop();
                _document.ClearSearch();
                e.Handled = true;
                break;
        }
    }

    private void OnShowMarkupBarClick(object sender, RoutedEventArgs e)
    {
        if (_main is not null)
        {
            _main.IsMarkupBarVisible = true;
        }
    }
}
