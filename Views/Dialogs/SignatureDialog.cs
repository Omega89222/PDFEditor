using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using PDFEditor.Models;
using PDFEditor.Pdf;
using PDFEditor.Services;

namespace PDFEditor.Views.Dialogs;

/// <summary>Creation d'une signature : tracee, tapee ou importee.</summary>
public static class SignatureDialog
{
    private static readonly (Color Color, string Name)[] InkColors =
    {
        (Color.FromRgb(0x1C, 0x1C, 0x1E), "Noir"),
        (Color.FromRgb(0x1C, 0x2A, 0x6B), "Bleu encre"),
        (Color.FromRgb(0x00, 0x5B, 0xD6), "Bleu"),
        (Color.FromRgb(0xB3, 0x26, 0x1E), "Rouge")
    };

    public static SavedSignature? Show(Window? owner)
    {
        var dialog = new MacDialog(owner, 520);
        SavedSignature? result = null;
        var inkColor = InkColors[1].Color;
        byte[]? imageData = null;

        var root = new StackPanel();
        root.Children.Add(MacDialog.Text("Nouvelle signature", "DialogTitle"));
        root.Children.Add(MacDialog.Text("Elle sera enregistrée sur cet ordinateur pour être réutilisée.", "CaptionText", new Thickness(0, 4, 0, 14)));

        // ------------------------------------------------------------ onglets
        var track = new Border { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        track.SetResourceReference(FrameworkElement.StyleProperty, "SegmentTrack");
        var tabs = new UniformGrid { Rows = 1, Columns = 3 };
        track.Child = tabs;

        RadioButton Tab(string label, bool isChecked)
        {
            var tab = new RadioButton { Content = label, GroupName = "SignatureMode", IsChecked = isChecked, MinWidth = 120, Padding = new Thickness(12, 0, 12, 0) };
            tab.SetResourceReference(FrameworkElement.StyleProperty, "SegmentRadio");
            tabs.Children.Add(tab);
            return tab;
        }

        var drawTab = Tab("Dessiner", true);
        var typeTab = Tab("Taper", false);
        var imageTab = Tab("Image", false);
        root.Children.Add(track);

        // ------------------------------------------------------------ dessin
        var canvasHost = new Grid { Height = 200 };
        var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "FieldBg");
        card.SetResourceReference(Border.BorderBrushProperty, "FieldBorderBrush");

        var drawPanel = new Grid();
        var baseline = new Line
        {
            X1 = 28,
            X2 = 470,
            Y1 = 150,
            Y2 = 150,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false
        };
        baseline.SetResourceReference(Shape.StrokeProperty, "SeparatorBrush");
        var cross = new TextBlock { Text = "×", FontSize = 18, Margin = new Thickness(12, 128, 0, 0), IsHitTestVisible = false };
        cross.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
        var hint = new TextBlock
        {
            Text = "Signez ici avec la souris, le pavé tactile ou un stylet",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 30),
            IsHitTestVisible = false
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");

        var ink = new InkCanvas { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Pen };
        ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = inkColor,
            Width = 2.6,
            Height = 2.6,
            FitToCurve = true,
            IgnorePressure = false,
            StylusTip = StylusTip.Ellipse
        };
        ink.StrokeCollected += (_, _) => hint.Visibility = Visibility.Collapsed;

        var clear = new Button { Content = "Effacer", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 12, 0) };
        clear.SetResourceReference(FrameworkElement.StyleProperty, "MacLinkButton");
        clear.Click += (_, _) =>
        {
            ink.Strokes.Clear();
            hint.Visibility = Visibility.Visible;
        };

        drawPanel.Children.Add(baseline);
        drawPanel.Children.Add(cross);
        drawPanel.Children.Add(hint);
        drawPanel.Children.Add(ink);
        drawPanel.Children.Add(clear);

        // ------------------------------------------------------------ texte
        var typePanel = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(16) };
        typePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        typePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var families = FontCatalog.HandwritingFamilies;
        var typed = new TextBox
        {
            Text = SettingsService.Current.AuthorName,
            FontSize = 38,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = FontCatalog.GetWpfFamily(families[0])
        };
        typed.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        typed.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextPrimary");

        var fontChoice = new ComboBox { ItemsSource = families, SelectedIndex = 0, Width = 220, HorizontalAlignment = HorizontalAlignment.Center };
        fontChoice.SetResourceReference(FrameworkElement.StyleProperty, "MacComboBox");
        fontChoice.SelectionChanged += (_, _) =>
        {
            if (fontChoice.SelectedItem is string family)
            {
                typed.FontFamily = FontCatalog.GetWpfFamily(family);
            }
        };
        Grid.SetRow(fontChoice, 1);
        typePanel.Children.Add(typed);
        typePanel.Children.Add(fontChoice);

        // ------------------------------------------------------------ image
        var imagePanel = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(16) };
        var preview = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 36) };
        var choose = new Button { Content = "Choisir une image…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
        choose.SetResourceReference(FrameworkElement.StyleProperty, "MacButton");
        var imageHint = MacDialog.Text("Une photo ou un scan de votre signature, idéalement sur fond transparent (PNG).", "CaptionText");
        imageHint.HorizontalAlignment = HorizontalAlignment.Center;
        imageHint.VerticalAlignment = VerticalAlignment.Center;
        imageHint.TextAlignment = TextAlignment.Center;
        imageHint.Margin = new Thickness(20, 0, 20, 40);
        choose.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "Image de signature", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
            if (picker.ShowDialog(dialog) != true)
            {
                return;
            }

            try
            {
                imageData = File.ReadAllBytes(picker.FileName);
                preview.Source = ImageTools.Load(imageData, 900);
                imageHint.Visibility = Visibility.Collapsed;
            }
            catch
            {
                imageData = null;
                imageHint.Text = "Cette image n’a pas pu être lue.";
            }
        };
        imagePanel.Children.Add(imageHint);
        imagePanel.Children.Add(preview);
        imagePanel.Children.Add(choose);

        card.Child = new Grid { Children = { drawPanel, typePanel, imagePanel } };
        canvasHost.Children.Add(card);
        root.Children.Add(canvasHost);

        void UpdateMode()
        {
            drawPanel.Visibility = drawTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            typePanel.Visibility = typeTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            imagePanel.Visibility = imageTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        drawTab.Checked += (_, _) => UpdateMode();
        typeTab.Checked += (_, _) => UpdateMode();
        imageTab.Checked += (_, _) => UpdateMode();

        // ------------------------------------------------------------ couleur
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var colorLabel = MacDialog.Text("Couleur de l’encre", "FieldLabel", new Thickness(0, 0, 10, 0));
        colors.Children.Add(colorLabel);
        foreach (var (color, name) in InkColors)
        {
            var swatch = new RadioButton { GroupName = "InkColor", ToolTip = name, IsChecked = color == inkColor, Margin = new Thickness(2, 0, 2, 0), Cursor = System.Windows.Input.Cursors.Arrow };
            swatch.Template = CreateSwatchTemplate();
            swatch.Tag = new SolidColorBrush(color);
            var captured = color;
            swatch.Checked += (_, _) =>
            {
                inkColor = captured;
                ink.DefaultDrawingAttributes.Color = captured;
                foreach (var stroke in ink.Strokes)
                {
                    stroke.DrawingAttributes.Color = captured;
                }

                typed.Foreground = new SolidColorBrush(captured);
            };
            colors.Children.Add(swatch);
        }

        typed.Foreground = new SolidColorBrush(inkColor);
        root.Children.Add(colors);

        var error = new TextBlock { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        root.Children.Add(error);

        // ------------------------------------------------------------ boutons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "MacButton");
        cancel.Click += (_, _) => dialog.Close();

        var save = new Button { Content = "Enregistrer", IsDefault = true };
        save.SetResourceReference(FrameworkElement.StyleProperty, "MacDefaultButton");
        save.Click += (_, _) =>
        {
            void Fail(string message)
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
            }

            if (drawTab.IsChecked == true)
            {
                if (ink.Strokes.Count == 0)
                {
                    Fail("Tracez votre signature avant de l’enregistrer.");
                    return;
                }

                var bounds = ink.Strokes.GetBounds();
                var signature = new SavedSignature
                {
                    Kind = SignatureKind.Ink,
                    Width = Math.Max(1, bounds.Width),
                    Height = Math.Max(1, bounds.Height),
                    StrokeWidth = 2.6,
                    Color = ColorUtil.ToHex(inkColor)
                };

                foreach (var stroke in ink.Strokes)
                {
                    var points = stroke.StylusPoints.Select(p => new Point(p.X - bounds.X, p.Y - bounds.Y)).ToList();
                    var simplified = InkGeometry.Simplify(points, 0.35);
                    signature.Strokes.Add(simplified.Select(p => new[] { Math.Round(p.X, 2), Math.Round(p.Y, 2) }).ToList());
                }

                result = signature;
            }
            else if (typeTab.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(typed.Text))
                {
                    Fail("Saisissez le texte de la signature.");
                    return;
                }

                result = new SavedSignature
                {
                    Kind = SignatureKind.Text,
                    Text = typed.Text.Trim(),
                    FontFamily = fontChoice.SelectedItem as string ?? families[0],
                    Color = ColorUtil.ToHex(inkColor)
                };
            }
            else
            {
                if (imageData is null)
                {
                    Fail("Choisissez une image.");
                    return;
                }

                result = new SavedSignature
                {
                    Kind = SignatureKind.Image,
                    ImageBase64 = Convert.ToBase64String(imageData),
                    Color = ColorUtil.ToHex(inkColor)
                };
            }

            dialog.Close();
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        dialog.Body = root;
        dialog.ShowDialog();
        return result;
    }

    private static ControlTemplate CreateSwatchTemplate()
    {
        var template = new ControlTemplate(typeof(RadioButton));

        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetValue(FrameworkElement.WidthProperty, 24.0);
        grid.SetValue(FrameworkElement.HeightProperty, 24.0);
        grid.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

        var halo = new FrameworkElementFactory(typeof(Ellipse), "Halo");
        halo.SetValue(Shape.StrokeThicknessProperty, 2.0);
        halo.SetResourceReference(Shape.StrokeProperty, "AccentYellow");
        halo.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);

        var disc = new FrameworkElementFactory(typeof(Ellipse));
        disc.SetValue(FrameworkElement.MarginProperty, new Thickness(4));
        disc.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Tag") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        grid.AppendChild(halo);
        grid.AppendChild(disc);
        template.VisualTree = grid;

        var trigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        trigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Halo"));
        template.Triggers.Add(trigger);
        return template;
    }
}
