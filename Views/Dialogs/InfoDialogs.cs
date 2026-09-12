using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PDFEditor.Services;

namespace PDFEditor.Views.Dialogs;

/// <summary>Liste des raccourcis clavier et fenetre « A propos ».</summary>
public static class InfoDialogs
{
    private static readonly (string Section, (string Keys, string Action)[] Items)[] Shortcuts =
    {
        ("Fichier", new[]
        {
            ("Ctrl O", "Ouvrir un document"),
            ("Ctrl S", "Enregistrer"),
            ("Ctrl Maj S", "Enregistrer sous"),
            ("Ctrl P", "Imprimer"),
            ("Ctrl N", "Nouvelle fenêtre"),
            ("Ctrl W", "Fermer la fenêtre")
        }),
        ("Édition", new[]
        {
            ("Ctrl Z", "Annuler"),
            ("Ctrl Y", "Rétablir"),
            ("Ctrl C / X / V", "Copier, couper, coller"),
            ("Ctrl D", "Dupliquer l’annotation"),
            ("Suppr", "Supprimer l’annotation"),
            ("Flèches", "Déplacer l’annotation (Maj : ×10)"),
            ("Ctrl A", "Sélectionner le texte de la page"),
            ("Échap", "Désélectionner, revenir à l’outil Sélection")
        }),
        ("Affichage", new[]
        {
            ("Ctrl + / Ctrl −", "Zoom avant / arrière"),
            ("Ctrl molette", "Zoom sous le pointeur"),
            ("Ctrl 0", "Taille réelle"),
            ("Ctrl 1", "Ajuster à la largeur"),
            ("Ctrl 2", "Page entière"),
            ("Ctrl F", "Rechercher"),
            ("F3 / Maj F3", "Résultat suivant / précédent"),
            ("Ctrl G", "Aller à la page"),
            ("Pg préc. / Pg suiv.", "Page précédente / suivante"),
            ("Ctrl Alt S", "Barre latérale"),
            ("Ctrl Alt I", "Inspecteur"),
            ("Ctrl Maj A", "Barre d’annotation")
        }),
        ("Pages", new[]
        {
            ("Ctrl L", "Pivoter à gauche"),
            ("Ctrl R", "Pivoter à droite")
        }),
        ("Outils", new[]
        {
            ("V", "Sélection"),
            ("H", "Main"),
            ("M", "Sélection rectangulaire"),
            ("T", "Zone de texte"),
            ("E", "Modifier le texte existant"),
            ("P", "Stylo"),
            ("G", "Surligneur"),
            ("U", "Surligner le texte"),
            ("R / O / L / A", "Rectangle, ellipse, ligne, flèche"),
            ("N", "Note"),
            ("S", "Signature"),
            ("I", "Image"),
            ("K", "Lien"),
            ("W", "Correcteur"),
            ("X", "Caviarder")
        })
    };

    public static void ShowShortcuts(Window? owner)
    {
        var dialog = new MacDialog(owner, 520);
        var root = new StackPanel();
        root.Children.Add(MacDialog.Text("Raccourcis clavier", "DialogTitle", new Thickness(0, 0, 0, 12)));

        var list = new StackPanel();
        foreach (var (section, items) in Shortcuts)
        {
            list.Children.Add(MacDialog.Text(section.ToUpperInvariant(), "SectionHeader", new Thickness(0, list.Children.Count == 0 ? 0 : 14, 0, 6)));
            foreach (var (keys, action) in items)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var caps = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var part in keys.Split(' '))
                {
                    var cap = new Border
                    {
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(6, 1, 6, 2),
                        Margin = new Thickness(0, 0, 4, 0),
                        MinWidth = 22,
                        Child = new TextBlock { Text = part, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center }
                    };
                    cap.SetResourceReference(Border.BackgroundProperty, "SegmentTrackBg");
                    caps.Children.Add(cap);
                }

                DockPanel.SetDock(caps, Dock.Right);
                row.Children.Add(caps);
                row.Children.Add(new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 });
                list.Children.Add(row);
            }
        }

        var scroller = new ScrollViewer { Content = list, MaxHeight = 520, Padding = new Thickness(0, 0, 10, 0) };
        scroller.SetResourceReference(FrameworkElement.StyleProperty, "MacScrollViewer");
        root.Children.Add(scroller);

        var close = new Button { Content = "Fermer", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        close.SetResourceReference(FrameworkElement.StyleProperty, "MacDefaultButton");
        close.Click += (_, _) => dialog.Close();
        root.Children.Add(close);

        dialog.Body = root;
        dialog.ShowDialog();
    }

    public static void ShowAbout(Window? owner)
    {
        var dialog = new MacDialog(owner, 320);
        var root = new StackPanel();

        var icon = AppIcon.Create(84);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 4, 0, 12);
        root.Children.Add(icon);

        var name = new TextBlock { Text = "Éditeur PDF", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "UiFontDisplay");
        root.Children.Add(name);

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = MacDialog.Text($"Version {version?.Major}.{version?.Minor}.{version?.Build}", "CaptionText", new Thickness(0, 2, 0, 14));
        versionText.HorizontalAlignment = HorizontalAlignment.Center;
        root.Children.Add(versionText);

        var credits = MacDialog.Text(
            "Rendu, texte et formulaires : PDFium\nMétadonnées et chiffrement : PDFsharp\nReconnaissance de texte : Windows OCR\n\nRéglages : " + SettingsService.DataDirectory,
            "CaptionText");
        credits.TextAlignment = TextAlignment.Center;
        credits.HorizontalAlignment = HorizontalAlignment.Center;
        root.Children.Add(credits);

        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "MacDefaultButton");
        ok.Click += (_, _) => dialog.Close();
        root.Children.Add(ok);

        dialog.Body = root;
        dialog.ShowDialog();
    }
}
