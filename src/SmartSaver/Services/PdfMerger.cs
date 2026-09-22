using System.IO;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Merges multiple PDF files into a single output PDF.
/// Uses PdfSharpCore to import all pages from each source PDF in order.
/// </summary>
public sealed class PdfMerger
{
    /// <summary>
    /// Merges the given list of PDF file paths into a single output PDF at <paramref name="outputPath"/>.
    /// Returns true on success. Files are merged in the order provided.
    /// </summary>
    public bool MergePdfs(IReadOnlyList<string> sourcePaths, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (sourcePaths.Count == 0)
        {
            Log.Warning("PdfMerger: No source files provided.");
            return false;
        }

        var missing = sourcePaths.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            Log.Warning("PdfMerger: Missing source files: {Files}", string.Join(", ", missing));
            return false;
        }

        try
        {
            using var outDoc = new PdfDocument();
            outDoc.Info.Creator = "DASMO CYBER CAFE — SmartSaver";

            int totalPagesMerged = 0;

            foreach (var sourcePath in sourcePaths)
            {
                try
                {
                    using var srcDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                    for (int i = 0; i < srcDoc.PageCount; i++)
                    {
                        outDoc.AddPage(srcDoc.Pages[i]);
                        totalPagesMerged++;
                    }
                    Log.Debug("Merged {Pages} pages from {File}", srcDoc.PageCount, Path.GetFileName(sourcePath));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to read PDF for merge: {Path}", sourcePath);
                    return false;
                }
            }

            if (totalPagesMerged == 0)
            {
                Log.Warning("PdfMerger: No pages were merged — all source PDFs may be empty.");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            outDoc.Save(outputPath);

            long outSize = new FileInfo(outputPath).Length;
            Log.Information("PdfMerger: Merged {Count} PDFs ({Pages} pages) → {Output} ({Size} bytes)",
                sourcePaths.Count, totalPagesMerged, outputPath, outSize);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PdfMerger: Failed to merge PDFs into {Output}", outputPath);
            if (File.Exists(outputPath)) try { File.Delete(outputPath); } catch { }
            return false;
        }
    }
}
