using System.Windows;
using System.Windows.Media;

namespace PDFEditor.Controls;

/// <summary>
/// Proprietes attachees permettant de poser une icone vectorielle sur n'importe quel
/// controle (bouton de barre d'outils, bouton segmente, element de menu...).
/// Les gabarits (ControlTemplate) de Themes/Controls.xaml lisent ces valeurs via
/// <c>{Binding Path=(ctrl:IconHost.Geometry), RelativeSource={RelativeSource TemplatedParent}}</c>.
/// </summary>
public static class IconHost
{
    /// <summary>Taille par defaut d'une icone, en pixels independants du peripherique.</summary>
    public const double DefaultSize = 17d;

    // ------------------------------------------------------------------
    // Geometry : la silhouette a dessiner (typiquement une cle Icon.Xxx).
    // ------------------------------------------------------------------
    public static readonly DependencyProperty GeometryProperty =
        DependencyProperty.RegisterAttached(
            "Geometry",
            typeof(Geometry),
            typeof(IconHost),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static Geometry? GetGeometry(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (Geometry?)element.GetValue(GeometryProperty);
    }

    public static void SetGeometry(DependencyObject element, Geometry? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(GeometryProperty, value);
    }

    // ------------------------------------------------------------------
    // Size : cote (largeur = hauteur) de la boite de dessin de l'icone.
    // ------------------------------------------------------------------
    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.RegisterAttached(
            "Size",
            typeof(double),
            typeof(IconHost),
            new FrameworkPropertyMetadata(
                DefaultSize,
                FrameworkPropertyMetadataOptions.AffectsMeasure |
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static double GetSize(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(SizeProperty);
    }

    public static void SetSize(DependencyObject element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SizeProperty, value);
    }

    // ------------------------------------------------------------------
    // Label : libelle court affiche a droite de l'icone (boutons « pilule »).
    // ------------------------------------------------------------------
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.RegisterAttached(
            "Label",
            typeof(string),
            typeof(IconHost),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static string? GetLabel(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(LabelProperty);
    }

    public static void SetLabel(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(LabelProperty, value);
    }
}
