using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PDFEditor.Views.Dialogs;

/// <summary>
/// Fenetre de dialogue facon macOS : feuille arrondie, ombre portee, sans
/// barre de titre systeme. Deplacable en la saisissant n'importe ou ;
/// Echap la ferme.
/// </summary>
public class MacDialog : Window
{
    private readonly Border _chrome;

    public MacDialog(Window? owner, double contentWidth)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontSize = 13;
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(ForegroundProperty, "TextPrimary");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        if (owner is { IsLoaded: true, IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _chrome = new Border
        {
            Width = contentWidth,
            Margin = new Thickness(26, 18, 26, 34),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 18, 20, 18),
            SnapsToDevicePixels = true
        };

        _chrome.SetResourceReference(Border.BackgroundProperty, "PopoverBg");
        _chrome.SetResourceReference(Border.BorderBrushProperty, "PopoverBorderBrush");
        _chrome.SetResourceReference(EffectProperty, "PopoverShadow");
        _chrome.MouseLeftButtonDown += OnChromeMouseLeftButtonDown;

        Content = _chrome;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Echap ferme la fenetre (desactive pour la progression).</summary>
    public bool CloseOnEscape { get; set; } = true;

    public event Action? EscapePressed;

    public UIElement? Body
    {
        get => _chrome.Child;
        set => _chrome.Child = value;
    }

    private void OnChromeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Bouton relache avant le deplacement.
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        EscapePressed?.Invoke();
        if (CloseOnEscape)
        {
            e.Handled = true;
            Close();
        }
    }

    // --------------------------------------------------------------- utilitaires

    public static TextBlock Text(string text, string styleKey, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, Margin = margin ?? new Thickness(0) };
        block.SetResourceReference(StyleProperty, styleKey);
        return block;
    }

    public static Button Button(string label, string styleKey, Action onClick)
    {
        var button = new Button { Content = label };
        button.SetResourceReference(StyleProperty, styleKey);
        button.Click += (_, _) => onClick();
        return button;
    }
}
