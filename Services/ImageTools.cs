using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PDFEditor.Services;

/// <summary>Image decodee en BGRA 32 bits, prete a etre inseree dans un PDF.</summary>
public sealed class DecodedImage
{
    public required byte[] Bgra { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool HasAlpha { get; init; }

    /// <summary>Donnees JPEG d'origine (reutilisees telles quelles pour rester compact).</summary>
    public byte[]? JpegData { get; init; }
}

/// <summary>Decodage, encodage et conversion d'images (WIC via WPF).</summary>
public static class ImageTools
{
    public static DecodedImage Decode(byte[] data, int maxDimension = 3200)
    {
        using var stream = new MemoryStream(data, writable: false);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        BitmapSource source = frame;
        var scaled = false;
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > maxDimension)
        {
            var factor = (double)maxDimension / longest;
            source = new TransformedBitmap(source, new ScaleTransform(factor, factor));
            scaled = true;
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 250)
            {
                hasAlpha = true;
                break;
            }
        }

        var isJpeg = data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

        return new DecodedImage
        {
            Bgra = pixels,
            Width = width,
            Height = height,
            HasAlpha = hasAlpha,
            JpegData = isJpeg && !scaled ? data : null
        };
    }

    /// <summary>Image affichable (figee) a partir de donnees encodees.</summary>
    public static BitmapSource Load(byte[] data, int decodePixelWidth = 0)
    {
        var image = new BitmapImage();
        using (var stream = new MemoryStream(data, writable: false))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth > 0)
            {
                image.DecodePixelWidth = decodePixelWidth;
            }

            image.StreamSource = stream;
            image.EndInit();
        }

        image.Freeze();
        return image;
    }

    /// <summary>Taille en pixels d'une image encodee, sans la decoder entierement.</summary>
    public static Size GetPixelSize(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return new Size(frame.PixelWidth, frame.PixelHeight);
    }

    public static BitmapSource FromBgra(byte[] bgra, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static byte[] EncodeJpeg(BitmapSource source, int quality = 90)
    {
        // JPEG ne gere pas la transparence : on compose sur du blanc.
        var flattened = FlattenOnWhite(source);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 10, 100) };
        encoder.Frames.Add(BitmapFrame.Create(flattened));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Compose l'image sur un fond blanc, pixel par pixel : aucun objet visuel
    /// n'est cree, la methode fonctionne donc depuis n'importe quel fil.
    /// </summary>
    public static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255)
            {
                continue;
            }

            var inverse = 255 - alpha;
            pixels[i] = (byte)((pixels[i] * alpha + 255 * inverse) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 255 * inverse) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 255 * inverse) / 255);
            pixels[i + 3] = 255;
        }

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Inversion douce des couleurs (mode lecture nuit), sur place.</summary>
    public static void InvertForNightMode(byte[] bgra)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            // Inversion de la luminance en gardant une teinte proche, fond gris tres sombre.
            var b = bgra[i];
            var g = bgra[i + 1];
            var r = bgra[i + 2];
            bgra[i] = (byte)(28 + (255 - b) * 212 / 255);
            bgra[i + 1] = (byte)(28 + (255 - g) * 212 / 255);
            bgra[i + 2] = (byte)(30 + (255 - r) * 210 / 255);
        }
    }
}
