using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PDFEditorSetup;

/// <summary>Assistant facon « Programme d'installation » de macOS : etapes a gauche, contenu a droite.</summary>
public partial class MainWindow : Window
{
    private enum WizardPage
    {
        Intro,
        Options,
        Progress,
        UninstallConfirm,
        Result
    }

    private readonly SetupMode _mode;
    private readonly Payload? _payload;
    private readonly ExistingInstall? _existing;
    private readonly string _uninstallDirectory;
    private readonly bool _preview;
    private readonly InstallOptions _options = new();
    private WizardPage _page;
    private bool _busy;
    private bool _succeeded;
    private long _requiredBytes = -1;

    internal MainWindow(SetupMode mode, Payload? payload, ExistingInstall? existing, string uninstallDirectory, bool preview = false)
    {
        InitializeComponent();

        _mode = mode;
        _payload = payload;
        _existing = existing;
        _uninstallDirectory = uninstallDirectory;
        _preview = preview;

        if (existing is not null)
        {
            _options.Directory = existing.Directory;
        }

        Title = mode == SetupMode.Uninstall ? "Désinstallation d’Éditeur PDF" : "Installation d’Éditeur PDF";
        VersionText.Text = "Version " + Product.Version;
        BuildBullets();

        SourceInitialized += (_, _) => NativeMethods.ApplyWindowEffects(this, IsDarkPalette);
        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy)
            {
                Close();
            }
        };

        switch (mode)
        {
            case SetupMode.Install:
                ShowPage(WizardPage.Intro);
                if (!preview && payload is not null)
                {
                    ComputeRequiredSpace(payload);
                }

                break;

            case SetupMode.Uninstall:
                ShowPage(WizardPage.UninstallConfirm);
                break;

            default:
                ShowResult(false, "Programme d’installation incomplet",
                    "Ce fichier ne contient pas l’application. Utilisez le fichier « EditeurPDF-Setup » produit par build-installer.ps1.");
                break;
        }
    }

    private bool IsDarkPalette => TryFindResource("WindowBg") is SolidColorBrush brush && brush.Color.R < 128;

    // =====================================================================
    // Pages
    // =====================================================================

    private string[] Steps => _mode == SetupMode.Uninstall
        ? new[] { "Confirmation", "Désinstallation", "Résumé" }
        : new[] { "Introduction", "Options", "Installation", "Résumé" };

    private int StepIndex(WizardPage page) => _mode == SetupMode.Uninstall
        ? page switch { WizardPage.UninstallConfirm => 0, WizardPage.Progress => 1, _ => 2 }
        : page switch { WizardPage.Intro => 0, WizardPage.Options => 1, WizardPage.Progress => 2, _ => 3 };

    private void ShowPage(WizardPage page)
    {
        _page = page;
        IntroPage.Visibility = page == WizardPage.Intro ? Visibility.Visible : Visibility.Collapsed;
        OptionsPage.Visibility = page == WizardPage.Options ? Visibility.Visible : Visibility.Collapsed;
        ProgressPage.Visibility = page == WizardPage.Progress ? Visibility.Visible : Visibility.Collapsed;
        UninstallPage.Visibility = page == WizardPage.UninstallConfirm ? Visibility.Visible : Visibility.Collapsed;
        ResultPage.Visibility = page == WizardPage.Result ? Visibility.Visible : Visibility.Collapsed;
        RenderSteps(StepIndex(page));

        BackButton.Visibility = Visibility.Visible;
        BackButton.Content = "Revenir";
        NextButton.IsEnabled = true;
        NextButton.Style = (Style)FindResource("MacDefaultButton");
        CloseButton.IsEnabled = true;

        switch (page)
        {
            case WizardPage.Intro:
                PageTitle.Text = "Bienvenue dans l’installation d’Éditeur PDF";
                IntroLead.Text = "Ce programme va installer Éditeur PDF sur cet ordinateur.";
                if (_existing is not null)
                {
                    UpgradeNotice.Visibility = Visibility.Visible;
                    UpgradeNotice.Text = _existing.Version == Product.Version
                        ? $"Éditeur PDF {_existing.Version} est déjà installé : il sera réinstallé. Vos réglages sont conservés."
                        : $"La version {_existing.Version} installée sera remplacée par la version {Product.Version}. Vos réglages sont conservés.";
                }

                BackButton.Visibility = Visibility.Hidden;
                NextButton.Content = "Continuer";
                break;

            case WizardPage.Options:
                PageTitle.Text = "Options d’installation";
                UpdateFolder();
                NextButton.Content = "Installer";
                break;

            case WizardPage.Progress:
                PageTitle.Text = _mode == SetupMode.Uninstall ? "Désinstallation d’Éditeur PDF" : "Installation d’Éditeur PDF";
                BackButton.Visibility = Visibility.Hidden;
                NextButton.IsEnabled = false;
                CloseButton.IsEnabled = false;
                InstallProgress.Value = 0;
                ProgressText.Text = "Préparation…";
                ProgressDetail.Text = "";
                break;

            case WizardPage.UninstallConfirm:
                PageTitle.Text = "Désinstaller Éditeur PDF ?";
                var version = _existing?.Version;
                UninstallText.Text = string.IsNullOrEmpty(version)
                    ? $"Éditeur PDF va être supprimé de cet ordinateur ({_uninstallDirectory})."
                    : $"Éditeur PDF {version} va être supprimé de cet ordinateur ({_uninstallDirectory}).";
                BackButton.Content = "Annuler";
                NextButton.Content = "Désinstaller";
                NextButton.Style = (Style)FindResource("MacDestructiveButton");
                break;
        }
    }

    private void ShowResult(bool success, string title, string text)
    {
        ShowPage(WizardPage.Result);
        var install = _mode != SetupMode.Uninstall;

        PageTitle.Text = success
            ? install ? "L’installation a réussi" : "La désinstallation est terminée"
            : install ? "L’installation a échoué" : "La désinstallation a échoué";

        ResultCircle.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, success ? "SuccessBrush" : "DangerBrush");
        ResultGlyph.Data = Geometry.Parse(success ? "M18,31 L26,39 L43,21" : "M22,22 L38,38 M38,22 L22,38");
        ResultTitle.Text = title;
        ResultText.Text = text;

        LaunchCheck.Visibility = success && install ? Visibility.Visible : Visibility.Collapsed;
        DefaultAppsLink.Visibility = success && install && _options.FileAssociation ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = Visibility.Hidden;
        NextButton.Content = "Fermer";
    }

    private void RenderSteps(int current)
    {
        StepList.Children.Clear();
        var steps = Steps;
        for (var i = 0; i < steps.Length; i++)
        {
            var active = i == current;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 11, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            if (active)
            {
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentYellow");
            }
            else if (i < current)
            {
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextTertiary");
            }
            else
            {
                dot.StrokeThickness = 1.2;
                dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextTertiary");
            }

            var label = new TextBlock
            {
                Text = steps[i],
                FontSize = 13,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, active ? "TextPrimary" : "TextSecondary");

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 13) };
            row.Children.Add(dot);
            row.Children.Add(label);
            StepList.Children.Add(row);
        }
    }

    private void BuildBullets()
    {
        var lines = new[]
        {
            "Aucun droit d’administrateur n’est nécessaire.",
            "Tout est inclus : rien d’autre à télécharger ni à installer.",
            "Désinstallation depuis Paramètres › Applications, comme toute application."
        };

        foreach (var line in lines)
        {
            var tick = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M1,5 L4.5,8.5 L11,1.5"),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 13,
                Height = 11,
                Margin = new Thickness(0, 4, 10, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            tick.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentDeep");

            var label = new TextBlock { Text = line };
            label.SetResourceReference(StyleProperty, "BodyText");

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(tick, Dock.Left);
            row.Children.Add(tick);
            row.Children.Add(label);
            IntroBullets.Children.Add(row);
        }
    }

    /// <summary>Apercu d'une page sans rien installer (captures de developpement).</summary>
    internal void ShowPreviewPage(string page)
    {
        switch (page.ToLowerInvariant())
        {
            case "options":
                _requiredBytes = 214L * 1024 * 1024;
                ShowPage(WizardPage.Options);
                break;

            case "progress":
                ShowPage(WizardPage.Progress);
                InstallProgress.Value = 0.62;
                ProgressText.Text = "Copie des fichiers…";
                ProgressDetail.Text = "62 %";
                break;

            case "done":
                ShowResult(true, "Éditeur PDF est installé.", "Vous le trouverez dans le menu Démarrer.");
                break;

            case "error":
                ShowResult(false, "L’installation n’a pas pu se terminer.", "Certains fichiers sont utilisés par un autre programme. Fermez Éditeur PDF puis réessayez.");
                break;

            case "uninstall":
                ShowPage(WizardPage.UninstallConfirm);
                break;

            case "uninstalled":
                ShowResult(true, "Éditeur PDF a été supprimé.", "Vos réglages sont conservés : vous les retrouverez si vous réinstallez l’application.");
                break;

            default:
                ShowPage(WizardPage.Intro);
                break;
        }
    }

    // =====================================================================
    // Options
    // =====================================================================

    private void ComputeRequiredSpace(Payload payload)
    {
        Task.Run(() => InstallerEngine.GetInstalledSize(payload)).ContinueWith(task =>
        {
            _requiredBytes = task.Status == TaskStatus.RanToCompletion ? task.Result : 0;
            if (_page == WizardPage.Options)
            {
                UpdateSpace();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdateFolder()
    {
        FolderText.Text = _options.Directory;
        FolderText.ToolTip = _options.Directory;
        ShowOptionsError(InstallerEngine.ValidateDirectory(_options.Directory));
        UpdateSpace();
    }

    private void UpdateSpace()
    {
        var free = InstallerEngine.GetFreeSpace(_options.Directory);
        SpaceText.Text = _requiredBytes < 0
            ? "Calcul de l’espace nécessaire…"
            : "Espace nécessaire : " + FormatSize(_requiredBytes) + (free >= 0 ? " · Disponible : " + FormatSize(free) : "");
    }

    private void ShowOptionsError(string? message)
    {
        FolderError.Text = message ?? "";
        FolderError.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30
            ? (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.CurrentCulture) + " Go"
            : Math.Max(1, bytes / (1L << 20)).ToString(CultureInfo.CurrentCulture) + " Mo";

    private void OnChangeFolderClick(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(_options.Directory);
        var chosen = NativeMethods.PickFolder(this, "Choisir l’emplacement d’Éditeur PDF", parent);
        if (chosen is null)
        {
            return;
        }

        _options.Directory = InstallerEngine.ResolveChosenDirectory(chosen);
        UpdateFolder();
    }

    // =====================================================================
    // Actions
    // =====================================================================

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_preview)
        {
            return;
        }

        switch (_page)
        {
            case WizardPage.Intro:
                ShowPage(WizardPage.Options);
                break;

            case WizardPage.Options:
                await RunInstallAsync();
                break;

            case WizardPage.UninstallConfirm:
                await RunUninstallAsync();
                break;

            case WizardPage.Result:
                if (_succeeded && _mode == SetupMode.Install && LaunchCheck.IsChecked == true)
                {
                    try
                    {
                        InstallerEngine.Launch(_options.Directory);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("Lancement impossible : " + ex.Message);
                    }
                }

                Close();
                break;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_page == WizardPage.Options)
        {
            ShowPage(WizardPage.Intro);
        }
        else if (_page == WizardPage.UninstallConfirm)
        {
            Close();
        }
    }

    private async Task RunInstallAsync()
    {
        var error = InstallerEngine.ValidateDirectory(_options.Directory);
        if (error is not null)
        {
            ShowOptionsError(error);
            return;
        }

        if (InstallerEngine.FindRunningInstances(_options.Directory).Count > 0)
        {
            ShowOptionsError("Éditeur PDF est ouvert. Fermez-le, puis cliquez à nouveau sur Installer.");
            return;
        }

        _options.DesktopShortcut = DesktopCheck.IsChecked == true;
        _options.FileAssociation = AssociationCheck.IsChecked == true;

        var payload = _payload!;
        var options = _options;
        ShowPage(WizardPage.Progress);
        _busy = true;

        IProgress<(double Fraction, string Message)> reporter = new Progress<(double Fraction, string Message)>(p =>
        {
            InstallProgress.Value = p.Fraction;
            ProgressText.Text = p.Message;
            ProgressDetail.Text = Math.Round(p.Fraction * 100) + " %";
        });

        try
        {
            await Task.Run(() => InstallerEngine.Install(payload, options, (fraction, message) => reporter.Report((fraction, message))));
            _succeeded = true;
            ShowResult(true, "Éditeur PDF est installé.",
                options.DesktopShortcut ? "Vous le trouverez dans le menu Démarrer et sur le Bureau." : "Vous le trouverez dans le menu Démarrer.");
        }
        catch (Exception ex)
        {
            Log.Write("Échec de l'installation : " + ex);
            ShowResult(false, "L’installation n’a pas pu se terminer.",
                ex is SetupException ? ex.Message : ex.Message + "\n\nDétails : " + Log.FilePath);
        }
        finally
        {
            _busy = false;
            CloseButton.IsEnabled = true;
        }
    }

    private async Task RunUninstallAsync()
    {
        if (InstallerEngine.FindRunningInstances(_uninstallDirectory).Count > 0)
        {
            UninstallError.Text = "Éditeur PDF est ouvert. Fermez-le, puis cliquez à nouveau sur Désinstaller.";
            UninstallError.Visibility = Visibility.Visible;
            return;
        }

        var removeData = RemoveDataCheck.IsChecked == true;
        var directory = _uninstallDirectory;
        ShowPage(WizardPage.Progress);
        _busy = true;

        IProgress<(double Fraction, string Message)> reporter = new Progress<(double Fraction, string Message)>(p =>
        {
            InstallProgress.Value = p.Fraction;
            ProgressText.Text = p.Message;
            ProgressDetail.Text = Math.Round(p.Fraction * 100) + " %";
        });

        try
        {
            await Task.Run(() => InstallerEngine.Uninstall(directory, removeData, (fraction, message) => reporter.Report((fraction, message))));
            _succeeded = true;
            ShowResult(true, "Éditeur PDF a été supprimé.",
                removeData ? "Vos réglages ont également été supprimés." : "Vos réglages sont conservés : vous les retrouverez si vous réinstallez l’application.");
        }
        catch (Exception ex)
        {
            Log.Write("Échec de la désinstallation : " + ex);
            ShowResult(false, "La désinstallation n’a pas pu se terminer.",
                ex is SetupException ? ex.Message : ex.Message + "\n\nDétails : " + Log.FilePath);
        }
        finally
        {
            _busy = false;
            CloseButton.IsEnabled = true;
        }
    }

    // =====================================================================
    // Fenetre
    // =====================================================================

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnDefaultAppsClick(object sender, RoutedEventArgs e) => InstallerEngine.OpenDefaultAppsSettings();

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Bouton relache avant le deplacement.
        }
    }
}
