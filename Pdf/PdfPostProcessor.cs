using System;
using System.IO;
using System.Security.Cryptography;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFEditor.Pdf;

/// <summary>Protection par mot de passe et permissions d'un document.</summary>
public sealed class PdfProtection
{
    /// <summary>Mot de passe demande a l'ouverture (vide = ouverture libre).</summary>
    public string UserPassword { get; set; } = "";

    /// <summary>Mot de passe proprietaire (leve les restrictions).</summary>
    public string OwnerPassword { get; set; } = "";

    public bool AllowPrint { get; set; } = true;
    public bool AllowCopy { get; set; } = true;
    public bool AllowModify { get; set; } = true;
    public bool AllowAnnotations { get; set; } = true;
    public bool AllowForms { get; set; } = true;

    public bool IsRestricted => !AllowPrint || !AllowCopy || !AllowModify || !AllowAnnotations || !AllowForms;

    public bool IsEnabled => !string.IsNullOrEmpty(UserPassword) || !string.IsNullOrEmpty(OwnerPassword) || IsRestricted;

    public PdfProtection Clone() => (PdfProtection)MemberwiseClone();
}

/// <summary>
/// Derniere etape de l'enregistrement : PDFium ne sait ni ecrire les metadonnees
/// ni chiffrer. PDFsharp relit le fichier produit, met a jour /Info et applique
/// un chiffrement AES 256 bits.
/// </summary>
public static class PdfPostProcessor
{
    public static byte[] Apply(byte[] pdf, PdfMetadata? metadata, PdfProtection? protection)
    {
        var protect = protection is { IsEnabled: true };
        if (metadata is null && !protect)
        {
            return pdf;
        }

        using var input = new MemoryStream(pdf, writable: false);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);

        if (metadata is not null)
        {
            var info = document.Info;
            info.Title = metadata.Title ?? "";
            info.Author = metadata.Author ?? "";
            info.Subject = metadata.Subject ?? "";
            info.Keywords = metadata.Keywords ?? "";
            if (!string.IsNullOrWhiteSpace(metadata.Creator))
            {
                info.Creator = metadata.Creator;
            }

            info.ModificationDate = DateTime.Now;
        }

        if (protect)
        {
            var settings = document.SecuritySettings;
            var owner = protection!.OwnerPassword;
            if (string.IsNullOrEmpty(owner))
            {
                // Sans mot de passe proprietaire distinct, les restrictions ne
                // protegeraient rien : on en genere un aleatoire.
                owner = protection.IsRestricted || string.IsNullOrEmpty(protection.UserPassword)
                    ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                    : protection.UserPassword;
            }

            settings.UserPassword = protection.UserPassword ?? "";
            settings.OwnerPassword = owner;
            settings.PermitPrint = protection.AllowPrint;
            settings.PermitFullQualityPrint = protection.AllowPrint;
            settings.PermitExtractContent = protection.AllowCopy;
            settings.PermitModifyDocument = protection.AllowModify;
            settings.PermitAssembleDocument = protection.AllowModify;
            settings.PermitAnnotations = protection.AllowAnnotations;
            settings.PermitFormsFill = protection.AllowForms;
            document.SecurityHandler.SetEncryptionToV5(true);
        }

        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }
}
