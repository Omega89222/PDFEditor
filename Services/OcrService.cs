using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PDFEditor.Services;

/// <summary>Mot reconnu, en pixels de l'image analysee.</summary>
public sealed record OcrWord(string Text, System.Windows.Rect PixelRect);

/// <summary>
/// Reconnaissance de texte integree a Windows (Windows.Media.Ocr) :
/// aucune dependance a installer, langues selon les modules linguistiques du poste.
/// </summary>
public static class OcrService
{
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Langues disponibles : (etiquette BCP-47, nom affichable).</summary>
    public static IReadOnlyList<(string Tag, string Name)> Languages
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages
                    .Select(l => (l.LanguageTag, Capitalize(l.NativeName)))
                    .ToList();
            }
            catch
            {
                return Array.Empty<(string, string)>();
            }
        }
    }

    /// <summary>Plus grand cote accepte par le moteur, en pixels.</summary>
    public static int MaxImageDimension
    {
        get
        {
            try
            {
                return (int)OcrEngine.MaxImageDimension;
            }
            catch
            {
                return 4000;
            }
        }
    }

    public static async Task<List<OcrWord>> RecognizeAsync(byte[] bgra, int width, int height, string? languageTag)
    {
        OcrEngine? engine = null;
        if (!string.IsNullOrWhiteSpace(languageTag))
        {
            engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(languageTag));
        }

        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            throw new InvalidOperationException(
                "Aucun moteur de reconnaissance de texte n’est disponible. Ajoutez un module linguistique dans les paramètres de Windows.");
        }

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);

        var result = await engine.RecognizeAsync(bitmap);
        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new OcrWord(word.Text, new System.Windows.Rect(r.X, r.Y, r.Width, r.Height)));
            }
        }

        return words;
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
