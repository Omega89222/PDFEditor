using System;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using PDFEditor.Models;

namespace PDFEditor.Services;

/// <summary>
/// Bascule clair / sombre de l'application.
///
/// Le dictionnaire de palette occupe TOUJOURS l'index 0 de
/// <c>Application.Current.Resources.MergedDictionaries</c> (voir App.xaml) :
/// changer de theme revient donc a remplacer cette seule entree, les autres
/// dictionnaires (Icons, Controls) n'utilisant que des <c>DynamicResource</c>.
/// </summary>
public static class ThemeManager
{
    private const string LightSource = "pack://application:,,,/Themes/Palette.xaml";
    private const string DarkSource = "pack://application:,,,/Themes/Palette.Dark.xaml";

    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Theme demande par l'utilisateur (Clair, Sombre ou Systeme).</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>Vrai si la palette actuellement chargee est la palette sombre.</summary>
    public static bool IsDark { get; private set; }

    /// <summary>Declenche apres chaque changement effectif de palette.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Applique la palette correspondant au theme demande et met a jour la
    /// bordure systeme de toutes les fenetres ouvertes.
    /// </summary>
    public static void Apply(AppTheme theme)
    {
        Current = theme;

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var dark = ResolveIsDark(theme);

        try
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri(dark ? DarkSource : LightSource, UriKind.Absolute)
            };

            var merged = app.Resources.MergedDictionaries;
            if (merged.Count == 0)
            {
                merged.Add(dictionary);
            }
            else
            {
                merged[0] = dictionary;
            }

            IsDark = dark;

            foreach (Window window in app.Windows)
            {
                WindowEffects.SetDarkTitleBar(window, dark);
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeManager] Palette « {(dark ? "sombre" : "claire")} » introuvable : {ex.Message}");
        }
    }

    /// <summary>
    /// Traduit un <see cref="AppTheme"/> en « faut-il la palette sombre ? ».
    /// <see cref="AppTheme.System"/> interroge la cle de registre de Windows
    /// (<c>AppsUseLightTheme</c>) ; toute erreur retombe sur le theme clair.
    /// </summary>
    public static bool ResolveIsDark(AppTheme theme)
    {
        return theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => IsSystemDark()
        };
    }

    /// <summary>Lit la preference d'apparence de Windows (applications).</summary>
    public static bool IsSystemDark()
    {
        try
        {
            // 1 = applications claires, 0 = applications sombres.
            var value = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1);
            return value is int light && light == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeManager] Lecture du registre impossible : {ex.Message}");
            return false;
        }
    }
}
