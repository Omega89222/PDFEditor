using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PDFEditor.Services;

/// <summary>
/// Petits effets de fenetre propres a Windows 11 (coins arrondis DWM,
/// bordure sombre). Tout est encapsule dans des <c>try/catch</c> :
/// sur Windows 10, les attributs sont simplement ignores.
/// </summary>
public static class WindowEffects
{
    // Attributs DWM (dwmapi.h)
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;   // Windows 10 1809 -> 1903
    private const int DwmwaWindowCornerPreference = 33;

    // DWM_WINDOW_CORNER_PREFERENCE
    private const int DwmwcpDefault = 0;
    private const int DwmwcpRound = 2;
    private const int DwmwcpRoundSmall = 3;

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size);

    /// <summary>
    /// Demande a DWM d'arrondir les coins de la fenetre (Windows 11).
    /// A appeler une fois le handle disponible (SourceInitialized ou Loaded).
    /// </summary>
    public static void EnableRoundedCorners(Window w) => SetCornerPreference(w, DwmwcpRound);

    /// <summary>Petits coins arrondis (menus, fenetres secondaires).</summary>
    public static void EnableSmallRoundedCorners(Window w) => SetCornerPreference(w, DwmwcpRoundSmall);

    /// <summary>Retablit la forme de coins par defaut du systeme.</summary>
    public static void ResetCorners(Window w) => SetCornerPreference(w, DwmwcpDefault);

    /// <summary>
    /// Bascule la decoration systeme de la fenetre en mode sombre ou clair
    /// (bordure et menu systeme ; la barre de titre est dessinee par l'app).
    /// </summary>
    public static void SetDarkTitleBar(Window w, bool dark)
    {
        var handle = TryGetHandle(w);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var value = dark ? 1 : 0;

        try
        {
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                // Anciennes versions de Windows 10 : l'attribut portait le numero 19.
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowEffects] Bordure sombre indisponible : {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ interne

    private static void SetCornerPreference(Window w, int preference)
    {
        var handle = TryGetHandle(w);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var value = preference;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref value, sizeof(int));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowEffects] Coins arrondis indisponibles : {ex.Message}");
        }
    }

    private static IntPtr TryGetHandle(Window? w)
    {
        if (w is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            return new WindowInteropHelper(w).Handle;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowEffects] Handle de fenetre indisponible : {ex.Message}");
            return IntPtr.Zero;
        }
    }
}
