using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PDFEditor.Views;

/// <summary>
/// Icone de l'application, dessinee en vectoriel (tuile jaune facon Notes,
/// feuille blanche, pastille « PDF » et crayon). Sert aux alertes, a l'ecran
/// d'accueil et a la generation du fichier .ico.
/// </summary>
public static class AppIcon
{
    public static FrameworkElement Create(double size)
    {
        var canvas = new Canvas { Width = 64, Height = 64, SnapsToDevicePixels = true };

        var tile = new Rectangle
        {
            Width = 60,
            Height = 60,
            RadiusX = 14,
            RadiusY = 14,
            Fill = new LinearGradientBrush(Color.FromRgb(0xFF, 0xDD, 0x55), Color.FromRgb(0xFF, 0xB4, 0x00), 90)
        };
        Canvas.SetLeft(tile, 2);
        Canvas.SetTop(tile, 2);
        canvas.Children.Add(tile);

        var sheetShadow = new Path
        {
            Data = Geometry.Parse("M18,13.5 H38 L48,23.5 V53.5 A2,2 0 0 1 46,55.5 H18 A2,2 0 0 1 16,53.5 V15.5 A2,2 0 0 1 18,13.5 Z"),
            Fill = new SolidColorBrush(Color.FromArgb(40, 120, 70, 0))
        };
        canvas.Children.Add(sheetShadow);

        var sheet = new Path
        {
            Data = Geometry.Parse("M18,12 H38 L48,22 V52 A2,2 0 0 1 46,54 H18 A2,2 0 0 1 16,52 V14 A2,2 0 0 1 18,12 Z"),
            Fill = Brushes.White
        };
        canvas.Children.Add(sheet);

        var fold = new Path
        {
            Data = Geometry.Parse("M38,12 V20 A2,2 0 0 0 40,22 H48 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE7))
        };
        canvas.Children.Add(fold);

        foreach (var (top, width) in new[] { (26.0, 20.0), (31.0, 24.0), (36.0, 16.0) })
        {
            var line = new Rectangle
            {
                Width = width,
                Height = 2.2,
                RadiusX = 1.1,
                RadiusY = 1.1,
                Fill = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xCC))
            };
            Canvas.SetLeft(line, 21);
            Canvas.SetTop(line, top);
            canvas.Children.Add(line);
        }

        var badge = new Border
        {
            Width = 25,
            Height = 11,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            Child = new TextBlock
            {
                Text = "PDF",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 7.6,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(badge, 20);
        Canvas.SetTop(badge, 42);
        canvas.Children.Add(badge);

        var pencil = new Path
        {
            Data = Geometry.Parse("M50.2,29.4 C51.0,28.6 52.3,28.6 53.1,29.4 L55.6,31.9 C56.4,32.7 56.4,34.0 55.6,34.8 L42.4,48.0 L36.6,50.0 L38.6,44.2 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C))
        };
        canvas.Children.Add(pencil);

        var tip = new Path
        {
            Data = Geometry.Parse("M38.6,44.2 L42.4,48.0 L36.6,50.0 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0xC4, 0x8A))
        };
        canvas.Children.Add(tip);

        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
    }
}
