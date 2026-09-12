using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PDFEditor.Rendering;
using PDFEditor.Services;

namespace PDFEditor.Controls;

/// <summary>Palette de couleurs facon macOS (pastilles systeme, recentes, saisie hexadecimale).</summary>
public sealed class ColorPalette : Border
{
    public ColorPalette(bool allowTransparent, Color current)
    {
        Width = 244;
        Margin = new Thickness(12, 4, 12, 16);
        Padding = new Thickness(10);
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "PopoverBg");
        SetResourceReference(BorderBrushProperty, "PopoverBorderBrush");
        SetResourceReference(EffectProperty, "PopoverShadow");

        var root = new StackPanel();

        var grid = new UniformGrid { Columns = 8 };
        foreach (var (color, name) in ColorUtil.Palette)
        {
            grid.Children.Add(CreateSwatch(color, name, current.A > 0 && ColorUtil.SameRgb(color, current)));
        }

        root.Children.Add(grid);

        var recent = SettingsService.Current.RecentColors
            .Select(hex => ColorUtil.Parse(hex, Colors.Transparent))
            .Where(c => c.A > 0 && !ColorUtil.Palette.Any(p => ColorUtil.SameRgb(p.Color, c)))
            .Take(8)
            .ToList();

        if (recent.Count > 0)
        {
            var caption = new TextBlock { Text = "Récentes", Margin = new Thickness(3, 8, 0, 3), FontSize = 11 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            root.Children.Add(caption);

            var recentGrid = new UniformGrid { Columns = 8, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var color in recent)
            {
                recentGrid.Children.Add(CreateSwatch(color, ColorUtil.ToRgbHex(color), ColorUtil.SameRgb(color, current)));
            }

            root.Children.Add(recentGrid);
        }

        var bottom = new DockPanel { Margin = new Thickness(2, 10, 2, 0), LastChildFill = true };

        var apply = new Button { Content = "OK", Margin = new Thickness(6, 0, 0, 0) };
        apply.SetResourceReference(StyleProperty, "MacSmallButton");
        DockPanel.SetDock(apply, Dock.Right);

        var hex = new TextBox
        {
            Text = current.A > 0 ? ColorUtil.ToRgbHex(current) : "#",
            Height = 24,
            MinHeight = 24,
            FontSize = 12,
            Padding = new Thickness(6, 1, 6, 1)
        };
        hex.SetResourceReference(StyleProperty, "MacTextBox");

        void ApplyHex()
        {
            if (ColorUtil.TryParse(hex.Text, out var parsed))
            {
                ColorPicked?.Invoke(Color.FromRgb(parsed.R, parsed.G, parsed.B));
            }
        }

        apply.Click += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ApplyHex();
                e.Handled = true;
            }
        };

        if (allowTransparent)
        {
            var none = new Button { Content = "Aucune", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            none.SetResourceReference(StyleProperty, "MacLinkButton");
            none.Click += (_, _) => ColorPicked?.Invoke(Colors.Transparent);
            DockPanel.SetDock(none, Dock.Left);
            bottom.Children.Add(none);
        }

        bottom.Children.Add(apply);
        bottom.Children.Add(hex);
        root.Children.Add(bottom);

        Child = root;
    }

    public event Action<Color>? ColorPicked;

    private Button CreateSwatch(Color color, string name, bool selected)
    {
        var button = new Button
        {
            Tag = AnnotationRenderer.Brush(color),
            ToolTip = name,
            Width = 26,
            Height = 26,
            Margin = new Thickness(0)
        };

        button.SetResourceReference(StyleProperty, "ColorSwatchButton");
        if (selected)
        {
            button.Loaded += (_, _) =>
            {
                if (button.Template?.FindName("Halo", button) is Ellipse halo)
                {
                    halo.Visibility = Visibility.Visible;
                }
            };
        }

        button.Click += (_, _) => ColorPicked?.Invoke(color);
        return button;
    }
}

/// <summary>Bouton affichant une pastille de couleur et ouvrant la palette.</summary>
public sealed class ColorPickerButton : Button
{
    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(
            nameof(SelectedColor),
            typeof(Color),
            typeof(ColorPickerButton),
            new FrameworkPropertyMetadata(Colors.Black, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    public static readonly DependencyProperty AllowTransparentProperty =
        DependencyProperty.Register(nameof(AllowTransparent), typeof(bool), typeof(ColorPickerButton), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowNameProperty =
        DependencyProperty.Register(nameof(ShowName), typeof(bool), typeof(ColorPickerButton), new PropertyMetadata(true, OnSelectedColorChanged));

    private readonly Border _swatch;
    private readonly Line _slash;
    private readonly TextBlock _name;
    private Popup? _popup;

    public ColorPickerButton()
    {
        SetResourceReference(StyleProperty, "MacSmallButton");
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Padding = new Thickness(6, 0, 10, 0);

        var swatchHost = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        _swatch = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
            Background = Brushes.White
        };
        _slash = new Line
        {
            X1 = 2,
            Y1 = 14,
            X2 = 14,
            Y2 = 2,
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            StrokeThickness = 1.6,
            Visibility = Visibility.Collapsed
        };
        swatchHost.Children.Add(_swatch);
        swatchHost.Children.Add(_slash);

        _name = new TextBlock { Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(swatchHost);
        content.Children.Add(_name);
        Content = content;

        Click += (_, _) => OpenPalette();
        Refresh();
    }

    public event EventHandler<Color>? ColorChanged;

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    public bool AllowTransparent
    {
        get => (bool)GetValue(AllowTransparentProperty);
        set => SetValue(AllowTransparentProperty, value);
    }

    public bool ShowName
    {
        get => (bool)GetValue(ShowNameProperty);
        set => SetValue(ShowNameProperty, value);
    }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ColorPickerButton)d).Refresh();

    private void Refresh()
    {
        var color = SelectedColor;
        _swatch.Background = color.A == 0 ? Brushes.White : AnnotationRenderer.Brush(Color.FromRgb(color.R, color.G, color.B));
        _slash.Visibility = color.A == 0 ? Visibility.Visible : Visibility.Collapsed;
        _name.Text = ColorUtil.NameOf(color);
        _name.Visibility = ShowName ? Visibility.Visible : Visibility.Collapsed;
        if (!ShowName)
        {
            Padding = new Thickness(5, 0, 5, 0);
        }
    }

    private void OpenPalette()
    {
        var palette = new ColorPalette(AllowTransparent, SelectedColor);
        _popup = new Popup
        {
            Child = palette,
            PlacementTarget = this,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -12,
            VerticalOffset = -2,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };

        palette.ColorPicked += color =>
        {
            SelectedColor = color;
            if (_popup is not null)
            {
                _popup.IsOpen = false;
            }

            ColorChanged?.Invoke(this, color);
        };

        _popup.IsOpen = true;
    }
}
