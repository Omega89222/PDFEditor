using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PDFEditor.ViewModels;

namespace PDFEditor.Views;

/// <summary>Ecran d'accueil : actions principales, fichiers recents, depot de fichiers.</summary>
public partial class WelcomeView : UserControl
{
    public WelcomeView()
    {
        InitializeComponent();
        IconHost.Content = AppIcon.Create(104);

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => SetDropHighlight(false);
        Drop += OnDrop;
    }

    private static string[] GetPdfFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase)).ToArray()
            : Array.Empty<string>();

    private void SetDropHighlight(bool active)
    {
        if (active)
        {
            DropZone.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentYellow");
            DropText.Text = "Relâchez pour ouvrir";
        }
        else
        {
            DropZone.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "DropZoneBrush");
            DropText.Text = "Déposez un fichier PDF ici pour l’ouvrir";
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var files = GetPdfFiles(e);
        e.Effects = files.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(files.Length > 0);
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        var files = GetPdfFiles(e);
        if (DataContext is not MainViewModel main || files.Length == 0)
        {
            return;
        }

        e.Handled = true;
        if (await main.OpenPathAsync(files[0], inThisWindow: true))
        {
            foreach (var file in files.Skip(1))
            {
                await main.OpenPathAsync(file);
            }
        }
    }
}
