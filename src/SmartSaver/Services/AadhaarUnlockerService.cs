using System;
using System.IO;
using PdfSharpCore.Pdf.IO;
using Serilog;

namespace SmartSaver.Services;

public static class AadhaarUnlockerService
{
    /// <summary>
    /// Removes password protection from an e-Aadhaar or encrypted PDF and saves an unlocked copy.
    /// </summary>
    public static bool UnlockPdf(string inputPath, string outputPath, string password)
    {
        try
        {
            if (!File.Exists(inputPath)) return false;

            byte[] pdfBytes = File.ReadAllBytes(inputPath);
            using var inputStream = new MemoryStream(pdfBytes);

            // Open with password
            using var doc = PdfReader.Open(inputStream, password, PdfDocumentOpenMode.Modify);
            
            // Remove security / encryption
            doc.SecuritySettings.OwnerPassword = string.Empty;
            doc.SecuritySettings.UserPassword = string.Empty;

            doc.Save(outputPath);
            Log.Information("Successfully unlocked PDF: {Src} -> {Dst}", inputPath, outputPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to unlock PDF with password '{Pw}': {Path}", password, inputPath);
            return false;
        }
    }
}
