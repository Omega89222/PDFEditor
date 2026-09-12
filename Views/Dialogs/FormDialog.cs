using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PDFEditor.Controls;
using PDFEditor.ViewModels;

namespace PDFEditor.Views.Dialogs;

/// <summary>
/// Construit un dialogue de saisie a partir d'un <see cref="FormSpec"/> :
/// libelles alignes a droite, champs a gauche, comme les feuilles de macOS.
/// </summary>
public static class FormDialog
{
    public static FormResult? Show(Window? owner, FormSpec spec)
    {
        var dialog = new MacDialog(owner, spec.Width);
        var getters = new Dictionary<string, Func<object?>>();
        UIElement? firstFocus = null;

        var root = new StackPanel();

        // ---------------------------------------------------------------- titre
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16), LastChildFill = true };
        if (spec.Icon != AlertIcon.None)
        {
            var icon = AppIcon.Create(42);
            icon.Margin = new Thickness(0, 0, 14, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(icon, Dock.Left);
            header.Children.Add(icon);
        }

        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(MacDialog.Text(spec.Title, "DialogTitle"));
        if (!string.IsNullOrWhiteSpace(spec.Message))
        {
            titles.Children.Add(MacDialog.Text(spec.Message!, "CaptionText", new Thickness(0, 4, 0, 0)));
        }

        header.Children.Add(titles);
        root.Children.Add(header);

        // ---------------------------------------------------------------- champs
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var row = 0;
        foreach (var field in spec.Fields)
        {
            FrameworkElement? control = null;
            var spanning = false;
            var labelTop = false;

            switch (field.Kind)
            {
                case FormFieldKind.Text:
                case FormFieldKind.MultilineText:
                {
                    var box = new TextBox
                    {
                        Text = field.Value as string ?? "",
                        Tag = field.Placeholder
                    };
                    box.SetResourceReference(FrameworkElement.StyleProperty, "MacTextBox");

                    if (field.Kind == FormFieldKind.MultilineText)
                    {
                        box.AcceptsReturn = true;
                        box.TextWrapping = TextWrapping.Wrap;
                        box.Height = 86;
                        box.VerticalContentAlignment = VerticalAlignment.Top;
                        labelTop = true;
                    }

                    getters[field.Key] = () => box.Text;
                    firstFocus ??= box;
                    control = box;
                    break;
                }

                case FormFieldKind.Password:
                {
                    var box = new PasswordBox();
                    box.SetResourceReference(FrameworkElement.StyleProperty, "MacPasswordBox");
                    getters[field.Key] = () => box.Password;
                    firstFocus ??= box;

                    if (!string.IsNullOrWhiteSpace(field.Placeholder))
                    {
                        var stack = new StackPanel();
                        stack.Children.Add(box);
                        stack.Children.Add(MacDialog.Text(field.Placeholder!, "CaptionText", new Thickness(2, 3, 0, 0)));
                        control = stack;
                        labelTop = true;
                    }
                    else
                    {
                        control = box;
                    }

                    break;
                }

                case FormFieldKind.Number:
                    control = CreateNumber(field, getters, ref firstFocus);
                    break;

                case FormFieldKind.Choice:
                {
                    var combo = new ComboBox
                    {
                        ItemsSource = field.Options,
                        SelectedIndex = field.Value is int index ? index : 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    combo.SetResourceReference(FrameworkElement.StyleProperty, "MacComboBox");
                    getters[field.Key] = () => combo.SelectedIndex;
                    control = combo;
                    break;
                }

                case FormFieldKind.Check:
                {
                    var check = new CheckBox { Content = field.Label, IsChecked = field.Value is true };
                    check.SetResourceReference(FrameworkElement.StyleProperty, "MacCheckBox");
                    getters[field.Key] = () => check.IsChecked == true;
                    control = check;
                    spanning = true;
                    break;
                }

                case FormFieldKind.Switch:
                {
                    var toggle = new ToggleButton { Content = field.Label, IsChecked = field.Value is true };
                    toggle.SetResourceReference(FrameworkElement.StyleProperty, "MacSwitch");
                    getters[field.Key] = () => toggle.IsChecked == true;
                    control = toggle;
                    spanning = true;
                    break;
                }

                case FormFieldKind.Slider:
                {
                    var slider = new Slider
                    {
                        Minimum = field.Minimum,
                        Maximum = field.Maximum,
                        Value = field.Value is double d ? d : field.Minimum,
                        SmallChange = 1,
                        LargeChange = Math.Max(1, (field.Maximum - field.Minimum) / 10)
                    };
                    slider.SetResourceReference(FrameworkElement.StyleProperty, "MacSlider");

                    var value = new TextBlock { Width = 54, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                    value.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                    void UpdateLabel() => value.Text = Math.Round(slider.Value, field.Decimals).ToString(CultureInfo.CurrentCulture) + (field.Suffix is null ? "" : " " + field.Suffix);
                    slider.ValueChanged += (_, _) => UpdateLabel();
                    UpdateLabel();

                    var dock = new DockPanel();
                    DockPanel.SetDock(value, Dock.Right);
                    dock.Children.Add(value);
                    dock.Children.Add(slider);

                    getters[field.Key] = () => Math.Round(slider.Value, field.Decimals);
                    control = dock;
                    break;
                }

                case FormFieldKind.Color:
                {
                    var picker = new ColorPickerButton
                    {
                        SelectedColor = field.Value is Color c ? c : Colors.Black,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    getters[field.Key] = () => picker.SelectedColor;
                    control = picker;
                    break;
                }

                case FormFieldKind.Label:
                    control = MacDialog.Text(field.Label, "CaptionText");
                    spanning = true;
                    break;

                case FormFieldKind.Separator:
                {
                    var separator = new Border { Margin = new Thickness(0, 4, 0, 4) };
                    separator.SetResourceReference(FrameworkElement.StyleProperty, "MacSeparator");
                    control = separator;
                    spanning = true;
                    break;
                }
            }

            if (control is null)
            {
                continue;
            }

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var top = row == 0 ? 0 : 10;

            if (!spanning)
            {
                var label = new TextBlock
                {
                    Text = field.Label,
                    TextAlignment = TextAlignment.Right,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = labelTop ? VerticalAlignment.Top : VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 180,
                    Margin = new Thickness(0, top + (labelTop ? 5 : 0), 12, 0)
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                label.FontSize = 12.5;
                Grid.SetRow(label, row);
                grid.Children.Add(label);
            }

            var host = new ContentControl
            {
                Content = control,
                Focusable = false,
                IsTabStop = false,
                Margin = new Thickness(0, top, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetRow(host, row);
            Grid.SetColumn(host, spanning ? 0 : 1);
            Grid.SetColumnSpan(host, spanning ? 2 : 1);
            grid.Children.Add(host);
            row++;

            if (!string.IsNullOrWhiteSpace(field.Help))
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var help = MacDialog.Text(field.Help!, "CaptionText", new Thickness(spanning ? 24 : 2, 3, 0, 0));
                Grid.SetRow(help, row);
                Grid.SetColumn(help, spanning ? 0 : 1);
                Grid.SetColumnSpan(help, spanning ? 2 : 1);
                grid.Children.Add(help);
                row++;
            }
        }

        root.Children.Add(grid);

        var error = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        root.Children.Add(error);

        // ---------------------------------------------------------------- boutons
        FormResult? result = null;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };

        var cancel = new Button { Content = spec.CancelLabel, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "MacButton");
        cancel.Click += (_, _) => dialog.Close();

        var confirm = new Button { Content = spec.ConfirmLabel, IsDefault = true };
        confirm.SetResourceReference(FrameworkElement.StyleProperty, "MacDefaultButton");
        confirm.Click += (_, _) =>
        {
            var candidate = new FormResult();
            foreach (var (key, getter) in getters)
            {
                candidate.Values[key] = getter();
            }

            var message = spec.Validate?.Invoke(candidate);
            if (message is not null)
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
                return;
            }

            result = candidate;
            dialog.Close();
        };

        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        root.Children.Add(buttons);

        dialog.Body = root;
        dialog.Loaded += (_, _) =>
        {
            if (firstFocus is Control focus)
            {
                focus.Focus();
                Keyboard.Focus(focus);
                if (focus is TextBox text)
                {
                    text.SelectAll();
                }
            }
        };

        dialog.ShowDialog();
        return result;
    }

    private static FrameworkElement CreateNumber(FormField field, Dictionary<string, Func<object?>> getters, ref UIElement? firstFocus)
    {
        var initial = field.Value is double d ? d : field.Minimum;

        string Format(double value) => Math.Round(value, field.Decimals).ToString(CultureInfo.CurrentCulture);

        var box = new TextBox
        {
            Text = Format(initial),
            Width = 96,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        box.SetResourceReference(FrameworkElement.StyleProperty, "MacTextBox");

        double Read()
        {
            var text = box.Text.Trim().Replace(" ", "").Replace(' ', ' ');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
                && !double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                value = initial;
            }

            return Math.Clamp(Math.Round(value, field.Decimals), field.Minimum, field.Maximum);
        }

        void Step(int direction)
        {
            box.Text = Format(Math.Clamp(Read() + direction * field.Step, field.Minimum, field.Maximum));
            box.CaretIndex = box.Text.Length;
        }

        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Up)
            {
                Step(1);
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                Step(-1);
                e.Handled = true;
            }
        };

        box.MouseWheel += (_, e) =>
        {
            if (box.IsKeyboardFocusWithin)
            {
                Step(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
            }
        };

        box.LostKeyboardFocus += (_, _) => box.Text = Format(Read());

        getters[field.Key] = () => Read();
        firstFocus ??= box;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(box);
        if (!string.IsNullOrWhiteSpace(field.Suffix))
        {
            var suffix = new TextBlock { Text = field.Suffix, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            suffix.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(suffix);
        }

        var range = new TextBlock
        {
            Text = $"({Format(field.Minimum)} à {Format(field.Maximum)})",
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11
        };
        range.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
        panel.Children.Add(range);
        return panel;
    }
}
