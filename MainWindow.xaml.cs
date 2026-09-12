using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PDFEditor.Controls;
using PDFEditor.Services;
using PDFEditor.ViewModels;
using PDFEditor.Views;

namespace PDFEditor;

/// <summary>
/// Fenetre de document : assemble la barre de titre, la barre d'annotation,
/// la barre laterale, la zone du document et l'inspecteur ; gere la geometrie
/// persistante, le repli anime des panneaux et les raccourcis clavier.
/// </summary>
public partial class MainWindow : Window
{
    private const double MinSidebarWidth = 160;
    private const double MaxSidebarWidth = 420;
    private const double MinInspectorWidth = 240;
    private const double MaxInspectorWidth = 440;
    private const double CascadeOffset = 28;

    private static readonly Duration PaneDuration = new(TimeSpan.FromMilliseconds(180));

    private readonly MainViewModel _vm;
    private readonly DialogService _dialogs;
    private bool _sidebarShown;
    private bool _inspectorShown;
    private bool _panesInitialized;
    private int _sidebarVersion;
    private int _inspectorVersion;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        _dialogs = new DialogService(this, FocusSearch);
        _vm = new MainViewModel(_dialogs);
        DataContext = _vm;
        DocumentView.Dialogs = _dialogs;

        RestoreGeometry();
        BuildInputBindings();

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.DocumentChanged += OnDocumentChanged;

        SidebarSplitter.DragCompleted += (_, _) => PersistPaneWidths();
        InspectorSplitter.DragCompleted += (_, _) => PersistPaneWidths();

        ApplyPanes(animate: false);

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => ApplyMaximizedMargin();
        DpiChanged += (_, _) => ApplyMaximizedMargin();
        Closing += OnClosing;
        Closed += OnClosed;
    }

    /// <summary>Le modele de vue de la fenetre.</summary>
    public MainViewModel ViewModel => _vm;

    /// <summary>Rend le clavier a la zone du document (apres une recherche, un dialogue...).</summary>
    public void FocusDocument()
    {
        if (_vm.HasDocument)
        {
            DocumentView.FocusDocument();
        }
    }

    // =====================================================================
    // Cycle de vie
    // =====================================================================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        WindowEffects.EnableRoundedCorners(this);
        WindowEffects.SetDarkTitleBar(this, ThemeManager.IsDark);
        ApplyMaximizedMargin();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        if (!_vm.ConfirmDiscardChanges())
        {
            e.Cancel = true;
            return;
        }

        _closeConfirmed = true;
        if (!App.IsSnapshotMode)
        {
            PersistGeometry();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.DocumentChanged -= OnDocumentChanged;
        DocumentView.Document = null;
        _vm.Dispose();
    }

    private void OnDocumentChanged(DocumentViewModel? previous, DocumentViewModel? document)
    {
        DocumentView.Document = document;

        var hasDocument = document is not null;
        DocumentView.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Visibility = hasDocument ? Visibility.Collapsed : Visibility.Visible;
        ApplyPanes(animate: false);

        if (hasDocument)
        {
            Dispatcher.BeginInvoke(new Action(FocusDocument), DispatcherPriority.Input);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsSidebarVisible):
            case nameof(MainViewModel.IsInspectorVisible):
                ApplyPanes(animate: true);
                break;

            case nameof(MainViewModel.IsMarkupBarVisible):
                ApplyPanes(animate: false);
                break;
        }
    }

    // =====================================================================
    // Geometrie
    // =====================================================================

    private void RestoreGeometry()
    {
        var settings = SettingsService.Current;

        Width = Clamp(settings.WindowWidth, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        Height = Clamp(settings.WindowHeight, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));

        // Nouvelle fenetre : en cascade par rapport a la derniere ouverte.
        var previous = Application.Current?.Windows.OfType<MainWindow>().LastOrDefault(w => !ReferenceEquals(w, this) && w.IsLoaded);
        if (previous is not null)
        {
            var bounds = previous.WindowState == WindowState.Normal
                ? new Rect(previous.Left, previous.Top, previous.ActualWidth, previous.ActualHeight)
                : previous.RestoreBounds;

            if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
            {
                Width = bounds.Width;
                Height = bounds.Height;
            }

            if (IsUsablePosition(bounds.Left + CascadeOffset, bounds.Top + CascadeOffset, Width, Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = bounds.Left + CascadeOffset;
                Top = bounds.Top + CascadeOffset;
                return;
            }

            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        if (IsUsablePosition(settings.WindowLeft, settings.WindowTop, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (settings.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void PersistGeometry()
    {
        var settings = SettingsService.Current;
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (bounds.Width >= MinWidth && bounds.Height >= MinHeight && !double.IsInfinity(bounds.Left))
        {
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
        }

        settings.IsMaximized = WindowState == WindowState.Maximized;
        PersistPaneWidths();
    }

    private void PersistPaneWidths()
    {
        var settings = SettingsService.Current;
        if (_sidebarShown && SidebarColumn.ActualWidth > 1)
        {
            settings.SidebarWidth = Clamp(SidebarColumn.ActualWidth, MinSidebarWidth, MaxSidebarWidth);
        }

        if (_inspectorShown && InspectorColumn.ActualWidth > 1)
        {
            settings.InspectorWidth = Clamp(InspectorColumn.ActualWidth, MinInspectorWidth, MaxInspectorWidth);
        }

        if (!App.IsSnapshotMode)
        {
            SettingsService.SaveSoon();
        }
    }

    private static bool IsUsablePosition(double left, double top, double width, double height)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top))
        {
            return false;
        }

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var candidate = new Rect(left, top, width, height);
        candidate.Intersect(screen);
        return candidate.Width >= 200 && candidate.Height >= 120;
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) || double.IsInfinity(value) ? min : Math.Min(Math.Max(value, min), max);

    // =====================================================================
    // Fenetre agrandie : le cadre de redimensionnement deborde de l'ecran
    // =====================================================================

    private const int SmCxSizeFrame = 32;
    private const int SmCxPaddedBorder = 92;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    private void ApplyMaximizedMargin()
    {
        if (WindowState != WindowState.Maximized)
        {
            RootGrid.Margin = new Thickness(0);
            return;
        }

        var thickness = 8.0;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dpi = handle == IntPtr.Zero ? 0u : GetDpiForWindow(handle);
            if (dpi > 0)
            {
                var pixels = GetSystemMetricsForDpi(SmCxSizeFrame, dpi) + GetSystemMetricsForDpi(SmCxPaddedBorder, dpi);
                thickness = pixels * 96.0 / dpi;
            }
        }
        catch
        {
            // Windows 10 ancien : valeur par defaut.
        }

        RootGrid.Margin = new Thickness(thickness);
    }

    // =====================================================================
    // Panneaux (barre laterale, inspecteur, barre d'annotation)
    // =====================================================================

    private void ApplyPanes(bool animate)
    {
        var hasDocument = _vm.HasDocument;
        var sidebar = hasDocument && _vm.IsSidebarVisible;
        var inspector = hasDocument && _vm.IsInspectorVisible;
        var settings = SettingsService.Current;

        if (!_panesInitialized || sidebar != _sidebarShown)
        {
            _sidebarShown = sidebar;
            TitleBar.IsSidebarShown = sidebar;
            AnimatePane(SidebarColumn, SidebarHost, SidebarSplitter,
                sidebar ? Clamp(settings.SidebarWidth, MinSidebarWidth, MaxSidebarWidth) : 0,
                MinSidebarWidth, sidebar, animate && _panesInitialized, ++_sidebarVersion, () => _sidebarVersion);
        }

        if (!_panesInitialized || inspector != _inspectorShown)
        {
            _inspectorShown = inspector;
            AnimatePane(InspectorColumn, InspectorHost, InspectorSplitter,
                inspector ? Clamp(settings.InspectorWidth, MinInspectorWidth, MaxInspectorWidth) : 0,
                MinInspectorWidth, inspector, animate && _panesInitialized, ++_inspectorVersion, () => _inspectorVersion);
        }

        MarkupBar.Visibility = hasDocument && _vm.IsMarkupBarVisible ? Visibility.Visible : Visibility.Collapsed;
        _panesInitialized = true;
    }

    private void AnimatePane(ColumnDefinition column, FrameworkElement host, GridSplitter splitter,
        double target, double minWidth, bool show, bool animate, int version, Func<int> currentVersion)
    {
        column.BeginAnimation(ColumnDefinition.WidthProperty, null);
        column.MinWidth = 0;

        if (show)
        {
            host.Visibility = Visibility.Visible;
            splitter.Visibility = Visibility.Visible;
        }

        void Finish()
        {
            if (version != currentVersion())
            {
                return;
            }

            column.BeginAnimation(ColumnDefinition.WidthProperty, null);
            column.Width = new GridLength(target);
            column.MinWidth = show ? minWidth : 0;
            if (!show)
            {
                host.Visibility = Visibility.Collapsed;
                splitter.Visibility = Visibility.Collapsed;
            }

            DocumentView.RefreshLayout();
        }

        if (!animate || !IsLoaded)
        {
            Finish();
            return;
        }

        var animation = new GridLengthAnimation
        {
            From = column.ActualWidth,
            To = target,
            Duration = PaneDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) => Finish();
        column.BeginAnimation(ColumnDefinition.WidthProperty, animation);
    }

    // =====================================================================
    // Raccourcis clavier
    // =====================================================================

    private void BuildInputBindings()
    {
        const ModifierKeys ctrl = ModifierKeys.Control;
        const ModifierKeys shift = ModifierKeys.Shift;
        const ModifierKeys alt = ModifierKeys.Alt;

        void Bind(ICommand command, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
            InputBindings.Add(new KeyBinding(command, key, modifiers));

        // Fichier
        Bind(_vm.NewWindowCommand, Key.N, ctrl);
        Bind(_vm.CreateBlankCommand, Key.N, ctrl | shift);
        Bind(_vm.OpenCommand, Key.O, ctrl);
        Bind(_vm.SaveCommand, Key.S, ctrl);
        Bind(_vm.SaveAsCommand, Key.S, ctrl | shift);
        Bind(_vm.PrintCommand, Key.P, ctrl);

        // Edition
        Bind(_vm.UndoCommand, Key.Z, ctrl);
        Bind(_vm.RedoCommand, Key.Y, ctrl);
        Bind(_vm.RedoCommand, Key.Z, ctrl | shift);
        Bind(_vm.CopyCommand, Key.C, ctrl);
        Bind(_vm.CutCommand, Key.X, ctrl);
        Bind(_vm.PasteCommand, Key.V, ctrl);
        Bind(_vm.DuplicateCommand, Key.D, ctrl);
        Bind(_vm.FocusSearchCommand, Key.F, ctrl);
        Bind(_vm.NextResultCommand, Key.F3);
        Bind(_vm.PreviousResultCommand, Key.F3, shift);

        // Affichage
        Bind(_vm.ZoomInCommand, Key.OemPlus, ctrl);
        Bind(_vm.ZoomInCommand, Key.Add, ctrl);
        Bind(_vm.ZoomOutCommand, Key.OemMinus, ctrl);
        Bind(_vm.ZoomOutCommand, Key.Subtract, ctrl);
        Bind(_vm.ActualSizeCommand, Key.D0, ctrl);
        Bind(_vm.ActualSizeCommand, Key.NumPad0, ctrl);
        Bind(_vm.FitWidthCommand, Key.D1, ctrl);
        Bind(_vm.FitPageCommand, Key.D2, ctrl);
        Bind(_vm.ToggleSidebarCommand, Key.S, ctrl | alt);
        Bind(_vm.ToggleInspectorCommand, Key.I, ctrl | alt);
        Bind(_vm.ToggleMarkupBarCommand, Key.A, ctrl | shift);
        Bind(_vm.ToggleThemeCommand, Key.T, ctrl | shift);
        Bind(_vm.GoToPageCommand, Key.G, ctrl);

        // Pages
        Bind(_vm.RotateLeftCommand, Key.L, ctrl);
        Bind(_vm.RotateRightCommand, Key.R, ctrl);

        // Aide et reglages
        Bind(_vm.ShowShortcutsCommand, Key.F1);
        Bind(_vm.PreferencesCommand, Key.OemComma, ctrl);

        // Sans chrome systeme : on retablit Ctrl+W et Alt+F4.
        var close = new RelayCommand(Close);
        Bind(close, Key.W, ctrl);
        Bind(close, Key.F4, alt);
    }

    private void FocusSearch() =>
        Dispatcher.BeginInvoke(new Action(TitleBar.FocusSearch), DispatcherPriority.Input);
}
