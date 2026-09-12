using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PDFEditor.Models;
using PDFEditor.Services;
using PDFEditor.ViewModels;
using PDFEditor.Views;
using PDFEditor.Views.Dialogs;

namespace PDFEditor;

/// <summary>
/// Point d'entree de l'application.
///
/// Sequence de demarrage :
///   1. filets de securite (exceptions consignees dans error.log) ;
///   2. chargement des reglages et application du theme ;
///   3. une fenetre par fichier passe en argument (ou une fenetre d'accueil).
///
/// Modes outils (sans interface durable) :
///   --make-icon chemin.ico       genere l'icone de l'application ;
///   --snapshot capture.png ...   ouvre une fenetre, la capture en PNG et quitte.
/// </summary>
public partial class App : Application
{
    private const string LogFileName = "error.log";

    private bool _isShowingError;

    /// <summary>Vrai pendant une capture automatique : aucun reglage n'est persiste.</summary>
    public static bool IsSnapshotMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (TryGetOption(args, "--make-icon", out var iconPath))
        {
            Shutdown(MakeIcon(iconPath));
            return;
        }

        var selfTest = TryGetOption(args, "--selftest", out var selfTestReport);
        var snapshot = SnapshotOptions.Parse(args);
        if (snapshot is not null || selfTest)
        {
            IsSnapshotMode = true;
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PDFEDITOR_DATA_DIR")))
            {
                Environment.SetEnvironmentVariable("PDFEDITOR_DATA_DIR", Path.Combine(Path.GetTempPath(), "PDFEditor-snapshot"));
            }
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            LogException(ev.Exception, "Tâche");
            ev.SetObserved();
        };

        SettingsService.Load();
        ThemeManager.Apply(snapshot?.Theme ?? SettingsService.Current.Theme);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        WindowHost.OpenWindow = (path, document) => OpenWindow(path, document);

        if (selfTest)
        {
            _ = RunSelfTestAsync(selfTestReport, TryGetOption(args, "--open", out var source) ? source : "");
            return;
        }

        if (snapshot is not null)
        {
            _ = RunSnapshotAsync(snapshot);
            return;
        }

        var files = args
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a))
            .Select(Path.GetFullPath)
            .ToList();

        if (files.Count == 0)
        {
            OpenWindow(null, null);
        }
        else
        {
            foreach (var file in files)
            {
                OpenWindow(file, null);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        if (!IsSnapshotMode)
        {
            try
            {
                SettingsService.Flush();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] Sauvegarde des reglages impossible : {ex.Message}");
            }
        }

        base.OnExit(e);
    }

    // =====================================================================
    // Fenetres
    // =====================================================================

    /// <summary>
    /// Ouvre une fenetre de document. Si le fichier est deja ouvert ailleurs,
    /// sa fenetre est simplement ramenee au premier plan.
    /// </summary>
    public static MainWindow OpenWindow(string? path, DocumentViewModel? document)
    {
        if (path is not null)
        {
            var existing = Current.Windows.OfType<MainWindow>().FirstOrDefault(w =>
                string.Equals(w.ViewModel.Document?.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.WindowState == WindowState.Minimized)
                {
                    existing.WindowState = WindowState.Normal;
                }

                existing.Activate();
                return existing;
            }
        }

        var window = new MainWindow();
        window.Show();

        if (document is not null)
        {
            window.ViewModel.AttachDocument(document);
        }
        else if (!string.IsNullOrEmpty(path))
        {
            _ = window.ViewModel.OpenPathAsync(path, inThisWindow: true);
        }

        return window;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
            && SettingsService.Current.Theme == AppTheme.System)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ThemeManager.ResolveIsDark(AppTheme.System) != ThemeManager.IsDark)
                {
                    ThemeManager.Apply(AppTheme.System);
                }
            }));
        }
    }

    // =====================================================================
    // Exceptions
    // =====================================================================

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = LogException(e.Exception, "Interface");
        e.Handled = true;

        if (!IsSnapshotMode)
        {
            ShowDiscreetError(e.Exception, path);
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogException(ex, "Arrière-plan");
        }
    }

    private void ShowDiscreetError(Exception exception, string? logPath)
    {
        if (_isShowingError)
        {
            return;
        }

        _isShowingError = true;
        try
        {
            var message = exception.Message
                + (string.IsNullOrEmpty(logPath) ? "" : $"{Environment.NewLine}{Environment.NewLine}Détails enregistrés dans :{Environment.NewLine}{logPath}");

            var owner = Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Windows.OfType<MainWindow>().FirstOrDefault();
            AlertDialog.Show(owner is { IsLoaded: true } ? owner : null,
                "Une erreur inattendue s’est produite",
                message,
                new[] { "OK" });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Affichage de l'erreur impossible : {ex.Message}");
        }
        finally
        {
            _isShowingError = false;
        }
    }

    /// <summary>Ajoute l'exception au journal ; retourne son chemin (ou null).</summary>
    public static string? LogException(Exception exception, string origin)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataDirectory);
            var path = Path.Combine(SettingsService.DataDirectory, LogFileName);

            var builder = new StringBuilder();
            builder.Append("===== ")
                .Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(" — ")
                .Append(origin)
                .AppendLine(" =====");
            builder.AppendLine(exception.ToString());
            builder.AppendLine();

            File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
            return path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Journalisation impossible : {ex.Message}");
            return null;
        }
    }

    // =====================================================================
    // Outil : generation de l'icone
    // =====================================================================

    private static int MakeIcon(string path)
    {
        try
        {
            var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var images = new List<byte[]>();

            foreach (var size in sizes)
            {
                var element = AppIcon.Create(size);
                element.Measure(new Size(size, size));
                element.Arrange(new Rect(0, 0, size, size));
                element.UpdateLayout();

                var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var memory = new MemoryStream();
                encoder.Save(memory);
                images.Add(memory.ToArray());
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)images.Count);

            var offset = 6 + 16 * images.Count;
            for (var i = 0; i < images.Count; i++)
            {
                var size = sizes[i];
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(images[i].Length);
                writer.Write(offset);
                offset += images[i].Length;
            }

            foreach (var image in images)
            {
                writer.Write(image);
            }

            return 0;
        }
        catch (Exception ex)
        {
            LogException(ex, "Icône");
            return 1;
        }
    }

    // =====================================================================
    // Outil : capture d'une fenetre
    // =====================================================================

    private async Task RunSelfTestAsync(string reportPath, string sourcePath)
    {
        var exitCode = 1;
        try
        {
            exitCode = await Testing.SelfTest.RunAsync(Path.GetFullPath(reportPath), Path.GetFullPath(sourcePath));
        }
        catch (Exception ex)
        {
            LogException(ex, "Auto-test");
        }

        Shutdown(exitCode);
    }

    private sealed class SnapshotOptions
    {
        public string Output { get; private init; } = "";
        public string? Open { get; private init; }
        public AppTheme? Theme { get; private init; }
        public double Width { get; private init; } = 1440;
        public double Height { get; private init; } = 900;
        public string? Tool { get; private init; }
        public SidebarModeKind? Sidebar { get; private init; }
        public string? Search { get; private init; }
        public double? Zoom { get; private init; }
        public int? Page { get; private init; }
        public bool NoInspector { get; private init; }
        public bool Demo { get; private init; }
        public int Delay { get; private init; } = 2500;

        public static SnapshotOptions? Parse(string[] args)
        {
            if (!TryGetOption(args, "--snapshot", out var output))
            {
                return null;
            }

            var size = TryGetOption(args, "--size", out var s) ? s.Split('x', 'X') : Array.Empty<string>();
            return new SnapshotOptions
            {
                Output = Path.GetFullPath(output),
                Open = TryGetOption(args, "--open", out var open) ? open : null,
                Theme = TryGetOption(args, "--theme", out var theme) && Enum.TryParse<AppTheme>(theme, true, out var t) ? t : null,
                Width = size.Length == 2 && double.TryParse(size[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 1440,
                Height = size.Length == 2 && double.TryParse(size[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 900,
                Tool = TryGetOption(args, "--tool", out var tool) ? tool : null,
                Sidebar = TryGetOption(args, "--sidebar", out var sidebar) && Enum.TryParse<SidebarModeKind>(sidebar, true, out var m) ? m : null,
                Search = TryGetOption(args, "--search", out var search) ? search : null,
                Zoom = TryGetOption(args, "--zoom", out var zoom) && double.TryParse(zoom, NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ? z : null,
                Page = TryGetOption(args, "--page", out var page) && int.TryParse(page, out var p) ? p : null,
                NoInspector = args.Contains("--no-inspector"),
                Demo = args.Contains("--demo"),
                Delay = TryGetOption(args, "--delay", out var delay) && int.TryParse(delay, out var d) ? d : 2500
            };
        }
    }

    private static bool TryGetOption(string[] args, string name, out string value)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length)
        {
            value = args[index + 1];
            return true;
        }

        value = "";
        return false;
    }

    private async Task RunSnapshotAsync(SnapshotOptions options)
    {
        var exitCode = 0;
        try
        {
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                WindowState = WindowState.Normal,
                Left = 30,
                Top = 30,
                Width = options.Width,
                Height = options.Height
            };
            window.Show();

            var vm = window.ViewModel;
            vm.IsInspectorVisible = !options.NoInspector;
            vm.IsSidebarVisible = true;
            vm.IsMarkupBarVisible = true;

            if (options.Open is not null)
            {
                await vm.OpenPathAsync(Path.GetFullPath(options.Open), inThisWindow: true);
            }

            if (options.Sidebar is { } mode)
            {
                vm.SidebarMode = mode;
            }

            if (options.Demo)
            {
                AddDemoAnnotations(vm);
            }

            await Task.Delay(400);

            if (vm.Document is { } document)
            {
                if (options.Zoom is { } zoom)
                {
                    vm.SetZoomCommand.Execute(zoom);
                }

                if (options.Page is { } page)
                {
                    document.GoToPage(page - 1);
                }

                if (options.Tool is not null)
                {
                    vm.SetToolCommand.Execute(options.Tool);
                }

                if (options.Search is not null)
                {
                    vm.SidebarMode = SidebarModeKind.Search;
                    await document.SearchAsync(options.Search);
                }
            }

            await Task.Delay(options.Delay);
            SaveSnapshot(window, options.Output);
        }
        catch (Exception ex)
        {
            LogException(ex, "Capture");
            exitCode = 1;
        }

        Shutdown(exitCode);
    }

    /// <summary>Annotations d'exemple sur la premiere page (captures du README).</summary>
    private static void AddDemoAnnotations(MainViewModel vm)
    {
        if (vm.Document is not { Pages.Count: > 0 } document)
        {
            return;
        }

        var page = document.Pages[0];
        var text = document.Pdf.GetTextPage(0);

        Pdf.PdfTextLine? Line(string fragment) =>
            text.Lines.FirstOrDefault(l => l.Text.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        // Modification d'une ligne sur le bandeau jaune : seul le texte change.
        if (Line("Référence 2026") is { } reference)
        {
            var edit = document.CreateTextEdit(0, reference, Colors.White);
            edit.Text = "Pour la librairie Les Pages Vives, Lyon · Référence 2026-042 · version 2";
            document.AddAnnotation(page, edit, select: false);
        }

        if (Line("image plus chaleureuse") is { } highlight)
        {
            document.SetTextSelection(page, text.LineSpanAt(highlight.Start));
            document.CreateMarkupFromSelection(MarkupKind.Highlight);
        }

        if (Line("8 250") is { } total)
        {
            var area = total.Bounds;
            area.Inflate(10, 7);
            document.AddAnnotation(page, new ShapeAnnotation(ShapeKind.Ellipse) { Rect = area, StrokeColor = Color.FromRgb(0xFF, 0x3B, 0x30), StrokeWidth = 2 }, select: false);
        }

        if (Line("Créer un logotype") is { } objective)
        {
            var ink = new InkAnnotation { StrokeColor = Color.FromRgb(0x34, 0xC7, 0x59), StrokeWidth = 2.4 };
            var x = objective.Bounds.Left - 34;
            var y = objective.Bounds.Top + objective.Bounds.Height / 2;
            ink.AddStroke(new[] { new Point(x, y), new Point(x + 5, y + 6), new Point(x + 16, y - 8) });
            document.AddAnnotation(page, ink, select: false);
        }

        document.AddAnnotation(page, new StampAnnotation("APPROUVÉ")
        {
            Rect = new Rect(page.Width - 215, 376, 150, 40),
            StrokeColor = Color.FromRgb(0x1F, 0x9D, 0x55)
        }, select: false);

        document.AddAnnotation(page, new NoteAnnotation
        {
            Location = new Point(page.Width - 52, page.Height * 0.36),
            Text = "Prévoir une rencontre avec l’équipe avant les pistes créatives."
        }, select: false);

        var comment = new TextBoxAnnotation { Rect = new Rect(page.Width - 230, page.Height * 0.74, 180, 40) };
        comment.Text = "Budget validé en réunion";
        comment.FontFamily = "Segoe UI";
        comment.FontSize = 13;
        comment.TextColor = Color.FromRgb(0x0A, 0x60, 0xD6);
        document.AddAnnotation(page, comment, select: false);
    }

    private static void SaveSnapshot(Window window, string path)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        bitmap.Render(background);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
