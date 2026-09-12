using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PDFEditor.Converters;

/// <summary>Vrai si la valeur (enum ou texte) est egale au parametre ; en retour, fixe la valeur.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
            return type.IsEnum ? Enum.Parse(type, parameter.ToString()!) : parameter;
        }

        return Binding.DoNothing;
    }
}

/// <summary>Visible si la valeur est egale au parametre (Invert pour l'inverse).</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var equal = false;
        if (value is not null && parameter is not null)
        {
            foreach (var option in parameter.ToString()!.Split('|'))
            {
                if (string.Equals(value.ToString(), option.Trim(), StringComparison.Ordinal))
                {
                    equal = true;
                    break;
                }
            }
        }

        return equal ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public bool Hide { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true ^ Invert;
        return visible ? Visibility.Visible : Hide ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) ^ Invert;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = (value is not null && value is not string { Length: 0 }) ^ Invert;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        return (count > 0) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ColorToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color)
        {
            return Brushes.Transparent;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SolidColorBrush brush ? brush.Color : Binding.DoNothing;
}

/// <summary>Couleur opaque (pour afficher une pastille meme si la couleur est transparente).</summary>
public sealed class OpaqueColorBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color)
        {
            return Brushes.Transparent;
        }

        var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible si la couleur est transparente (motif « aucune couleur »).</summary>
public sealed class TransparentColorToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Color { A: 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Cle de ressource (« Icon.Xxx ») -> geometrie.</summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? Application.Current?.TryFindResource(key) as Geometry : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>0..1 -> « 45 % ».</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? $"{Math.Round(d * 100)} %" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Nombre -> texte arrondi (parametre = nombre de decimales) et retour.</summary>
public sealed class NumberConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var decimals = parameter is string s && int.TryParse(s, out var d) ? d : 1;
        return value is double number ? Math.Round(number, decimals).ToString(CultureInfo.CurrentCulture) : "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string ?? "").Trim().Replace(" ", "");
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var result)
            || double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
        {
            return result;
        }

        return Binding.DoNothing;
    }
}

/// <summary>Largeur disponible -> hauteur d'une vignette selon le ratio de la page.</summary>
public sealed class AspectHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = values.Length > 0 && values[0] is double w ? w : 120;
        var ratio = values.Length > 1 && values[1] is double r && r > 0 ? r : 1.294;
        return Math.Max(20, width * ratio);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
