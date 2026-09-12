using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PDFEditorSetup;

internal enum SetupMode
{
    Install,
    Uninstall,
    Broken
}

/// <summary>
/// Point d'entree : Setup.exe (avec archive) installe, Uninstall.exe (sans archive) desinstalle.
/// Codes de sortie en mode silencieux : 0 reussite, 1 erreur, 2 application ouverte, 3 programme incomplet.
/// </summary>
public partial class App : Application
{
    private bool _deleteSelfOnExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ne jamais verrouiller le dossier d'installation par le repertoire courant.
        Environment.CurrentDirectory = Path.GetTempPath();

        var args = new CommandLine(e.Args);
        Log.Write("Démarrage : " + string.Join(" ", e.Args));

        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Write("Erreur inattendue : " + ev.Exception);
            ev.Handled = true;
            if (!args.Quiet)
            {
                MessageBox.Show(ev.Exception.Message, "Installation d’Éditeur PDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        var theme = args.Value("theme");
        Theme.Apply(Resources, theme is null ? Theme.IsSystemDark() : string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase));

        var payload = Payload.Find();
        var existing = InstallerEngine.FindExisting();
        var mode = args.Uninstall || (payload is null && existing is not null)
            ? SetupMode.Uninstall
            : payload is not null ? SetupMode.Install : SetupMode.Broken;

        var uninstallDirectory = args.Value("dir") ?? existing?.Directory ?? Path.GetDirectoryName(Payload.CurrentExecutable)!;

        var snapshot = args.Value("snapshot");
        if (snapshot is not null)
        {
            RunSnapshot(args, snapshot, payload, existing, uninstallDirectory);
            return;
        }

        if (mode == SetupMode.Uninstall && !args.Has("relocated")
            && InstallerEngine.RelaunchFromTemp(uninstallDirectory, args.Quiet, args.Has("remove-data")))
        {
            Shutdown(0);
            return;
        }

        _deleteSelfOnExit = mode == SetupMode.Uninstall && args.Has("relocated");

        if (args.Quiet)
        {
            var code = mode switch
            {
                SetupMode.Install => RunQuietInstall(payload!, existing, args),
                SetupMode.Uninstall => RunQuietUninstall(uninstallDirectory, args),
                _ => 3
            };

            Log.Write($"Fin (code {code})");
            Shutdown(code);
            return;
        }

        var window = new MainWindow(mode, payload, existing, uninstallDirectory);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_deleteSelfOnExit)
        {
            InstallerEngine.ScheduleSelfDelete();
        }

        base.OnExit(e);
    }

    private static int RunQuietInstall(Payload payload, ExistingInstall? existing, CommandLine args)
    {
        var chosen = args.Value("dir");
        var options = new InstallOptions
        {
            Directory = chosen is not null ? Path.GetFullPath(chosen) : existing?.Directory ?? Product.DefaultInstallDirectory,
            DesktopShortcut = args.Has("desktop-shortcut"),
            FileAssociation = !args.Has("no-association")
        };

        var error = InstallerEngine.ValidateDirectory(options.Directory);
        if (error is not null)
        {
            Log.Write("Dossier refusé : " + error);
            return 1;
        }

        if (InstallerEngine.FindRunningInstances(options.Directory).Count > 0)
        {
            Log.Write("Application ouverte : installation annulée");
            return 2;
        }

        try
        {
            InstallerEngine.Install(payload, options, (_, _) => { });
            if (args.Has("launch"))
            {
                InstallerEngine.Launch(options.Directory);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("Échec de l'installation : " + ex);
            return 1;
        }
    }

    private static int RunQuietUninstall(string directory, CommandLine args)
    {
        if (InstallerEngine.FindRunningInstances(directory).Count > 0)
        {
            Log.Write("Application ouverte : désinstallation annulée");
            return 2;
        }

        try
        {
            InstallerEngine.Uninstall(directory, args.Has("remove-data"), (_, _) => { });
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("Échec de la désinstallation : " + ex);
            return 1;
        }
    }

    /// <summary>Capture d'une page de l'assistant (developpement) : --snapshot image.png --page options.</summary>
    private void RunSnapshot(CommandLine args, string output, Payload? payload, ExistingInstall? existing, string uninstallDirectory)
    {
        var page = args.Value("page") ?? "intro";
        var mode = page.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) ? SetupMode.Uninstall : SetupMode.Install;
        var window = new MainWindow(mode, payload, existing, uninstallDirectory, preview: true);
        MainWindow = window;
        window.Show();
        window.ShowPreviewPage(page);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var root = (FrameworkElement)window.Content;
                var dpi = VisualTreeHelper.GetDpi(window);
                var bitmap = new RenderTargetBitmap(
                    (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

                var background = new DrawingVisual();
                using (var dc = background.RenderOpen())
                {
                    dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                }

                bitmap.Render(background);
                bitmap.Render(root);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                using var stream = File.Create(output);
                encoder.Save(stream);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Log.Write("Capture impossible : " + ex);
                Shutdown(1);
            }
        };
        timer.Start();
    }
}
