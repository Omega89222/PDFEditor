using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace PDFEditorSetup;

/// <summary>Identite de l'application installee.</summary>
internal static class Product
{
    public const string Name = "Éditeur PDF";
    public const string ExecutableName = "PDFEditor.exe";
    public const string ProcessName = "PDFEditor";
    public const string UninstallerName = "Uninstall.exe";
    public const string ProgId = "PDFEditor.Document";
    public const string Description = "Lire, annoter, remplir, signer et réorganiser des documents PDF.";

    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PDFEditor";
    public const string AppPathKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\PDFEditor.exe";
    public const string VendorKey = @"Software\PDFEditor";
    public const string CapabilitiesKey = @"Software\PDFEditor\Capabilities";

    public static string Version
    {
        get
        {
            var v = Assembly.GetEntryAssembly()!.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    /// <summary>Installation par utilisateur, comme la plupart des applications grand public.</summary>
    public static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PDFEditor");

    /// <summary>Reglages, signatures et fichiers recents de l'application.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDFEditor");

    public static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");

    public static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");
}

/// <summary>Journal de diagnostic dans %TEMP%\PDFEditor-Setup.log.</summary>
internal static class Log
{
    public static string FilePath => Path.Combine(Path.GetTempPath(), "PDFEditor-Setup.log");

    public static void Write(string message)
    {
        try
        {
            var processId = System.Diagnostics.Process.GetCurrentProcess().Id;
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{processId}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Journal facultatif.
        }
    }
}

/// <summary>
/// Options de la ligne de commande, sous les formes --nom, -nom ou /nom :
///   --quiet (/S, /silent)  installation ou desinstallation sans fenetre
///   --uninstall            desinstaller
///   --dir chemin           dossier d'installation
///   --desktop-shortcut     creer un raccourci sur le Bureau
///   --no-association       ne pas proposer l'application pour les fichiers PDF
///   --launch               lancer l'application a la fin
///   --remove-data          supprimer aussi les reglages (desinstallation)
/// </summary>
internal sealed class CommandLine
{
    private readonly List<string> _args;

    public CommandLine(IEnumerable<string> args)
    {
        _args = args.ToList();
    }

    public IReadOnlyList<string> Raw => _args;

    public bool Quiet => Has("quiet", "q", "s", "silent", "verysilent");

    public bool Uninstall => Has("uninstall");

    public bool Has(params string[] names) =>
        _args.Any(a => names.Any(n => string.Equals(Normalize(a), n, StringComparison.OrdinalIgnoreCase)));

    public string? Value(string name)
    {
        for (var i = 0; i < _args.Count - 1; i++)
        {
            if (string.Equals(Normalize(_args[i]), name, StringComparison.OrdinalIgnoreCase))
            {
                return _args[i + 1];
            }
        }

        return null;
    }

    private static string Normalize(string arg)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            return arg.Substring(2);
        }

        return arg.StartsWith("/", StringComparison.Ordinal) || arg.StartsWith("-", StringComparison.Ordinal) ? arg.Substring(1) : arg;
    }
}
