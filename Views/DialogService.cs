using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using PDFEditor.Models;
using PDFEditor.Services;
using PDFEditor.ViewModels;
using PDFEditor.Views.Dialogs;

namespace PDFEditor.Views;

/// <summary>Point d'entree vers l'application pour ouvrir de nouvelles fenetres.</summary>
public static class WindowHost
{
    /// <summary>Ouvre une fenetre (chemin a ouvrir, ou document deja construit, ou rien).</summary>
    public static Action<string?, DocumentViewModel?>? OpenWindow { get; set; }
}

/// <summary>Implementation WPF de <see cref="IDialogService"/> pour une fenetre.</summary>
public sealed class DialogService : IDialogService
{
    private readonly Window _window;
    private readonly Action _focusSearch;

    public DialogService(Window window, Action focusSearch)
    {
        _window = window;
        _focusSearch = focusSearch;
    }

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(_window) == true ? dialog.FileName : null;
    }

    public string[] OpenFiles(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = true };
        return dialog.ShowDialog(_window) == true ? dialog.FileNames : Array.Empty<string>();
    }

    public string? SaveFile(string title, string filter, string suggestedName, string? directory = null)
    {
        var extension = Regex.Match(filter, @"\*(\.[A-Za-z0-9]+)").Groups[1].Value;
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = suggestedName,
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true
        };

        if (!string.IsNullOrWhiteSpace(directory) && System.IO.Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        return dialog.ShowDialog(_window) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(_window) == true ? dialog.FolderName : null;
    }

    public int Alert(string title, string message, IReadOnlyList<string> buttons, int defaultButton = 0, int cancelButton = -1, int destructiveButton = -1, AlertIcon icon = AlertIcon.Info) =>
        AlertDialog.Show(_window, title, message, buttons, defaultButton, cancelButton, destructiveButton, icon);

    public string? Prompt(string title, string message, string value = "", string? placeholder = null, bool password = false, string confirm = "OK")
    {
        var spec = new FormSpec(title)
        {
            Message = message,
            ConfirmLabel = confirm,
            Width = 380,
            Icon = password ? AlertIcon.Lock : AlertIcon.None
        };

        if (password)
        {
            spec.Password("value", "Mot de passe");
        }
        else
        {
            spec.Text("value", "", value, placeholder);
        }

        var result = FormDialog.Show(_window, spec);
        return result?.String("value");
    }

    public FormResult? ShowForm(FormSpec spec) => FormDialog.Show(_window, spec);

    public SavedSignature? CreateSignature() => SignatureDialog.Show(_window);

    public IProgressReporter BeginProgress(string title, string message, bool cancellable) =>
        new ProgressDialog(_window, title, message, cancellable);

    public void OpenInNewWindow(DocumentViewModel document) => WindowHost.OpenWindow?.Invoke(null, document);

    public void OpenPathInNewWindow(string path) => WindowHost.OpenWindow?.Invoke(path, null);

    public void OpenNewWindow() => WindowHost.OpenWindow?.Invoke(null, null);

    public void ShowShortcuts() => InfoDialogs.ShowShortcuts(_window);

    public void ShowAbout() => InfoDialogs.ShowAbout(_window);

    public void FocusSearch() => _focusSearch();

    public void Print(DocumentViewModel document) => PrintService.Print(_window, document, this);
}
