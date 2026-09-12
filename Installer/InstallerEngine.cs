using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace PDFEditorSetup;

internal sealed class InstallOptions
{
    public string Directory { get; set; } = Product.DefaultInstallDirectory;

    public bool DesktopShortcut { get; set; }

    public bool FileAssociation { get; set; } = true;
}

internal sealed class ExistingInstall
{
    public ExistingInstall(string directory, string version)
    {
        Directory = directory;
        Version = version;
    }

    public string Directory { get; }

    public string Version { get; }
}

/// <summary>Erreur destinee a l'utilisateur (message deja redige).</summary>
internal sealed class SetupException : Exception
{
    public SetupException(string message) : base(message)
    {
    }
}

/// <summary>Installation et desinstallation : fichiers, raccourcis, registre (HKCU uniquement).</summary>
internal static class InstallerEngine
{
    // =====================================================================
    // Etat
    // =====================================================================

    public static ExistingInstall? FindExisting()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Product.UninstallKey);
            var directory = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrEmpty(directory) || !File.Exists(Path.Combine(directory, Product.ExecutableName)))
            {
                return null;
            }

            return new ExistingInstall(directory!, key!.GetValue("DisplayVersion") as string ?? "");
        }
        catch
        {
            return null;
        }
    }

    public static string WithSeparator(string directory) =>
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>Instances de l'application lancees depuis ce dossier.</summary>
    public static List<Process> FindRunningInstances(string directory)
    {
        var prefix = WithSeparator(directory);
        var result = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Product.ProcessName))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(process);
                    continue;
                }
            }
            catch
            {
                // Processus d'un autre utilisateur : ignore.
            }

            process.Dispose();
        }

        return result;
    }

    public static long GetInstalledSize(Payload payload)
    {
        using var archive = new ZipArchive(payload.OpenArchive(), ZipArchiveMode.Read);
        return archive.Entries.Sum(e => e.Length) + payload.Offset;
    }

    /// <summary>Espace libre sur le disque du dossier, ou -1 si inconnu.</summary>
    public static long GetFreeSpace(string directory)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Dossier choisi par l'utilisateur : un sous-dossier « PDFEditor » est ajoute,
    /// sauf s'il s'agit deja d'une installation, pour ne jamais melanger nos fichiers aux siens.
    /// </summary>
    public static string ResolveChosenDirectory(string chosen)
    {
        var full = Path.GetFullPath(chosen);
        if (File.Exists(Path.Combine(full, Product.ExecutableName))
            || string.Equals(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)), "PDFEditor", StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        return Path.Combine(full, "PDFEditor");
    }

    /// <summary>Message d'erreur si le dossier ne convient pas, sinon null.</summary>
    public static string? ValidateDirectory(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            if (full.Length <= 3)
            {
                return "Choisissez un dossier plutôt que la racine d’un disque.";
            }

            if (System.IO.Directory.Exists(full) && System.IO.Directory.EnumerateFileSystemEntries(full).Any()
                && !File.Exists(Path.Combine(full, Product.ExecutableName)))
            {
                return "Ce dossier contient déjà d’autres fichiers. Choisissez un dossier vide.";
            }

            // Droit d'ecriture : essai dans le dossier ou dans son plus proche parent existant.
            var probeDirectory = full;
            while (!System.IO.Directory.Exists(probeDirectory))
            {
                probeDirectory = Path.GetDirectoryName(probeDirectory);
                if (probeDirectory is null)
                {
                    return "Ce disque n’est pas disponible.";
                }
            }

            var probe = Path.Combine(probeDirectory, ".pdfeditor-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Vous n’avez pas le droit d’écrire dans ce dossier. Choisissez un dossier de votre profil utilisateur.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // =====================================================================
    // Installation
    // =====================================================================

    public static void Install(Payload payload, InstallOptions options, Action<double, string> report)
    {
        var directory = Path.GetFullPath(options.Directory);
        Log.Write($"Installation de la version {Product.Version} dans {directory}");
        report(0, "Préparation…");

        if (System.IO.Directory.Exists(directory) && System.IO.Directory.EnumerateFileSystemEntries(directory).Any())
        {
            if (!File.Exists(Path.Combine(directory, Product.ExecutableName)))
            {
                throw new SetupException("Le dossier d’installation contient d’autres fichiers.");
            }

            report(0.01, "Remplacement de la version précédente…");
            DeleteContents(directory);
        }

        System.IO.Directory.CreateDirectory(directory);
        ExtractArchive(payload, directory, report);

        var executable = Path.Combine(directory, Product.ExecutableName);
        if (!File.Exists(executable))
        {
            throw new SetupException("L’archive d’installation ne contient pas l’application.");
        }

        report(0.92, "Création du programme de désinstallation…");
        payload.WriteProgramOnly(Path.Combine(directory, Product.UninstallerName));

        report(0.95, "Création des raccourcis…");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Product.StartMenuShortcut)!);
        NativeMethods.CreateShortcut(Product.StartMenuShortcut, executable, directory, Product.Description);
        if (options.DesktopShortcut)
        {
            NativeMethods.CreateShortcut(Product.DesktopShortcut, executable, directory, Product.Description);
        }
        else
        {
            TryDeleteFile(Product.DesktopShortcut);
        }

        report(0.98, "Enregistrement dans Windows…");
        var sizeKb = (GetDirectorySize(directory) + 1023) / 1024;
        RegisterUninstall(directory, executable, sizeKb);
        RegisterAppPath(directory, executable);
        if (options.FileAssociation)
        {
            RegisterFileAssociation(executable);
        }
        else
        {
            UnregisterFileAssociation();
        }

        NativeMethods.NotifyAssociationsChanged();
        report(1, "Installation terminée.");
        Log.Write("Installation terminée");
    }

    private static void ExtractArchive(Payload payload, string directory, Action<double, string> report)
    {
        var root = WithSeparator(directory);
        using var archive = new ZipArchive(payload.OpenArchive(), ZipArchiveMode.Read);
        var total = Math.Max(1L, archive.Entries.Sum(e => e.Length));
        var done = 0L;
        var lastReported = 0.0;
        var buffer = new byte[81920];

        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(directory, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException("L’archive d’installation est invalide.");
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            {
                System.IO.Directory.CreateDirectory(destination);
                continue;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                done += read;
                var fraction = 0.02 + 0.88 * done / total;
                if (fraction - lastReported >= 0.004)
                {
                    lastReported = fraction;
                    report(fraction, "Copie des fichiers…");
                }
            }
        }
    }

    // =====================================================================
    // Desinstallation
    // =====================================================================

    public static void Uninstall(string directory, bool removeUserData, Action<double, string> report)
    {
        Log.Write($"Désinstallation de {directory} (réglages supprimés : {removeUserData})");

        report(0.05, "Suppression des raccourcis…");
        TryDeleteFile(Product.StartMenuShortcut);
        TryDeleteFile(Product.DesktopShortcut);

        report(0.15, "Nettoyage du registre…");
        UnregisterFileAssociation();
        UnregisterAppPath();
        NativeMethods.NotifyAssociationsChanged();

        report(0.3, "Suppression des fichiers…");
        var ours = File.Exists(Path.Combine(directory, Product.ExecutableName))
                   || File.Exists(Path.Combine(directory, Product.UninstallerName));
        if (System.IO.Directory.Exists(directory) && ours)
        {
            Retry(() => System.IO.Directory.Delete(directory, true));
        }

        report(0.85, "Retrait de la liste des applications…");
        Registry.CurrentUser.DeleteSubKeyTree(Product.UninstallKey, false);

        if (removeUserData && System.IO.Directory.Exists(Product.DataDirectory))
        {
            report(0.92, "Suppression des réglages…");
            Retry(() => System.IO.Directory.Delete(Product.DataDirectory, true));
        }

        report(1, "Désinstallation terminée.");
        Log.Write("Désinstallation terminée");
    }

    /// <summary>
    /// Un programme ne peut pas supprimer son propre dossier : le desinstalleur se copie
    /// dans %TEMP% et s'y relance. Retourne vrai si la copie a pris le relais.
    /// </summary>
    public static bool RelaunchFromTemp(string installDirectory, bool quiet, bool removeUserData)
    {
        var current = Payload.CurrentExecutable;
        if (!current.StartsWith(WithSeparator(installDirectory), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var temp = Path.Combine(Path.GetTempPath(), "PDFEditor-Desinstallation-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
        File.Copy(current, temp, true);

        var arguments = $"--uninstall --relocated --dir \"{installDirectory.TrimEnd(Path.DirectorySeparatorChar)}\"";
        if (quiet)
        {
            arguments += " --quiet";
        }

        if (removeUserData)
        {
            arguments += " --remove-data";
        }

        Log.Write($"Relance depuis {temp}");
        Process.Start(new ProcessStartInfo(temp, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        return true;
    }

    /// <summary>Copie temporaire du desinstalleur : effacee quelques secondes apres sa fermeture.</summary>
    public static void ScheduleSelfDelete()
    {
        var current = Payload.CurrentExecutable;
        if (!Path.GetFileName(current).StartsWith("PDFEditor-Desinstallation-", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c ping 127.0.0.1 -n 4 > nul & del /f /q \"{current}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            });
        }
        catch (Exception ex)
        {
            Log.Write("Auto-suppression impossible : " + ex.Message);
        }
    }

    // =====================================================================
    // Registre
    // =====================================================================

    private static void RegisterUninstall(string directory, string executable, long sizeKb)
    {
        var uninstaller = Path.Combine(directory, Product.UninstallerName);
        using var key = Registry.CurrentUser.CreateSubKey(Product.UninstallKey);
        key.SetValue("DisplayName", Product.Name);
        key.SetValue("DisplayVersion", Product.Version);
        key.SetValue("DisplayIcon", executable + ",0");
        key.SetValue("Comments", Product.Description);
        key.SetValue("InstallLocation", directory);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --quiet");
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void RegisterAppPath(string directory, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Product.AppPathKey);
        key.SetValue("", executable);
        key.SetValue("Path", directory);
    }

    private static void UnregisterAppPath() => Registry.CurrentUser.DeleteSubKeyTree(Product.AppPathKey, false);

    /// <summary>
    /// Propose l'application pour les PDF (« Ouvrir avec », Applications par defaut),
    /// sans jamais s'imposer : Windows laisse le choix final a l'utilisateur.
    /// </summary>
    private static void RegisterFileAssociation(string executable)
    {
        var command = $"\"{executable}\" \"%1\"";
        var icon = executable + ",0";

        using (var progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Product.ProgId))
        {
            progId.SetValue("", "Document PDF");
            progId.SetValue("FriendlyTypeName", "Document PDF");
            using (var defaultIcon = progId.CreateSubKey("DefaultIcon"))
            {
                defaultIcon.SetValue("", icon);
            }

            using (var open = progId.CreateSubKey(@"shell\open"))
            {
                open.SetValue("FriendlyAppName", Product.Name);
                using var openCommand = open.CreateSubKey("command");
                openCommand.SetValue("", command);
            }
        }

        using (var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
        {
            openWith.SetValue(Product.ProgId, "");
        }

        using (var application = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\" + Product.ExecutableName))
        {
            application.SetValue("FriendlyAppName", Product.Name);
            using (var types = application.CreateSubKey("SupportedTypes"))
            {
                types.SetValue(".pdf", "");
            }

            using (var defaultIcon = application.CreateSubKey("DefaultIcon"))
            {
                defaultIcon.SetValue("", icon);
            }

            using var openCommand = application.CreateSubKey(@"shell\open\command");
            openCommand.SetValue("", command);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(Product.CapabilitiesKey))
        {
            capabilities.SetValue("ApplicationName", Product.Name);
            capabilities.SetValue("ApplicationDescription", Product.Description);
            capabilities.SetValue("ApplicationIcon", icon);
            using var associations = capabilities.CreateSubKey("FileAssociations");
            associations.SetValue(".pdf", Product.ProgId);
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            registered.SetValue(Product.Name, Product.CapabilitiesKey);
        }
    }

    private static void UnregisterFileAssociation()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Product.ProgId, false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\" + Product.ExecutableName, false);
        Registry.CurrentUser.DeleteSubKeyTree(Product.VendorKey, false);

        using (var openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", true))
        {
            openWith?.DeleteValue(Product.ProgId, false);
        }

        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
        {
            registered?.DeleteValue(Product.Name, false);
        }
    }

    // =====================================================================
    // Divers
    // =====================================================================

    public static void Launch(string directory)
    {
        var executable = Path.Combine(directory, Product.ExecutableName);
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = directory });
    }

    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(Product.Name)) { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
    }

    private static long GetDirectorySize(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    private static void DeleteContents(string directory)
    {
        foreach (var file in System.IO.Directory.GetFiles(directory))
        {
            Retry(() =>
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            });
        }

        foreach (var subdirectory in System.IO.Directory.GetDirectories(directory))
        {
            Retry(() => System.IO.Directory.Delete(subdirectory, true));
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Suppression impossible de {path} : {ex.Message}");
        }
    }

    /// <summary>Fichiers brievement verrouilles (antivirus, indexation) : quelques nouvelles tentatives.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < 24 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SetupException("Certains fichiers sont utilisés par un autre programme. Fermez Éditeur PDF puis réessayez.\n\n" + ex.Message);
            }
        }
    }
}
