using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using PDFEditor.ViewModels;

namespace PDFEditor.Views.Dialogs;

/// <summary>Alerte facon macOS : icone, titre en gras, message, boutons.</summary>
public static class AlertDialog
{
    public static int Show(Window? owner, string title, string message, IReadOnlyList<string> buttons,
        int defaultButton = 0, int cancelButton = -1, int destructiveButton = -1, AlertIcon icon = AlertIcon.Info)
    {
        if (buttons.Count == 0)
        {
            buttons = new[] { "OK" };
        }

        var dialog = new MacDialog(owner, 290);
        var result = cancelButton >= 0 ? cancelButton : buttons.Count == 1 ? 0 : buttons.Count - 1;

        var root = new StackPanel();

        var iconHost = new Grid
        {
            Width = 64,
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 12)
        };
        iconHost.Children.Add(AppIcon.Create(64));
        if (icon is AlertIcon.Warning or AlertIcon.Error or AlertIcon.Lock)
        {
            iconHost.Children.Add(CreateBadge(icon));
        }

        root.Children.Add(iconHost);

        var titleBlock = new TextBlock
        {
            Text = title,
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        root.Children.Add(titleBlock);

        if (!string.IsNullOrWhiteSpace(message))
        {
            var messageBlock = new TextBlock
            {
                Text = message,
                TextAlignment = TextAlignment.Center,
                FontSize = 12,
                LineHeight = 17,
                TextWrapping = TextWrapping.Wrap
            };
            messageBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            root.Children.Add(messageBlock);
        }

        var horizontal = buttons.Count <= 2;
        Panel panel = horizontal
            ? new UniformGrid { Rows = 1, Columns = buttons.Count, Margin = new Thickness(-4, 18, -4, 0) }
            : new StackPanel { Margin = new Thickness(0, 16, 0, 0) };

        // A deux boutons, l'action par defaut est a droite ; empiles, elle est en haut.
        IEnumerable<int> order = horizontal
            ? Enumerable.Range(0, buttons.Count).OrderBy(i => i == defaultButton ? 1 : 0)
            : Enumerable.Range(0, buttons.Count).OrderBy(i => i == defaultButton ? 0 : i == cancelButton ? 2 : 1);

        foreach (var index in order)
        {
            var button = new Button
            {
                Content = buttons[index],
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = horizontal ? new Thickness(4, 0, 4, 0) : new Thickness(0, 3, 0, 3),
                IsDefault = index == defaultButton,
                IsCancel = index == cancelButton
            };

            button.SetResourceReference(FrameworkElement.StyleProperty,
                index == defaultButton ? "MacDefaultButton" : index == destructiveButton ? "MacDestructiveButton" : "MacButton");

            var captured = index;
            button.Click += (_, _) =>
            {
                result = captured;
                dialog.Close();
            };

            panel.Children.Add(button);
        }

        root.Children.Add(panel);
        dialog.Body = root;
        dialog.ShowDialog();
        return result;
    }

    private static FrameworkElement CreateBadge(AlertIcon icon)
    {
        var color = icon switch
        {
            AlertIcon.Error => Color.FromRgb(0xFF, 0x3B, 0x30),
            AlertIcon.Lock => Color.FromRgb(0x63, 0x63, 0x66),
            _ => Color.FromRgb(0xFF, 0x95, 0x00)
        };

        var badge = new Grid
        {
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -4, -4)
        };

        badge.Children.Add(new Ellipse { Fill = new SolidColorBrush(color), Stroke = Brushes.White, StrokeThickness = 2 });

        if (icon == AlertIcon.Lock && Application.Current?.TryFindResource("Icon.Lock") is Geometry lockGeometry)
        {
            badge.Children.Add(new Path
            {
                Data = lockGeometry,
                Fill = Brushes.White,
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            badge.Children.Add(new TextBlock
            {
                Text = "!",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0)
            });
        }

        return badge;
    }
}
