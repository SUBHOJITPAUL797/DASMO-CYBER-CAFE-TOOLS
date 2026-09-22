using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using SmartSaver.Models;

namespace SmartSaver.Services;

/// <summary>
/// Serializable DTO representing an in-progress PDF editing session.
/// </summary>
public class PdfDraftSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourcePdfPath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(SourcePdfPath);
    public DateTime LastModified { get; set; } = DateTime.Now;
    public int CurrentPageIndex { get; set; }
    public double ZoomFactor { get; set; } = 1.0;
    public Dictionary<int, List<PdfEditItemDto>> PageEdits { get; set; } = new();

    public int TotalEditCount => PageEdits?.Values.Sum(v => v.Count) ?? 0;
}

/// <summary>
/// Serializable DTO for any <see cref="PdfEditItem"/>.
/// Decouples JSON persistence from WPF types and ensures 100% polymorphic roundtripping.
/// </summary>
public class PdfEditItemDto
{
    public string Id { get; set; } = string.Empty;
    public int PageIndex { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsHitTestVisible { get; set; } = true;
    public string ItemType { get; set; } = string.Empty; // "Whiteout", "Text", "Image", "Ink"

    // Whiteout
    public string? FillColorHex { get; set; }

    // Text
    public string? Text { get; set; }
    public string? FontFamily { get; set; }
    public double FontSizePt { get; set; } = 12.0;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public string? TextColorHex { get; set; }
    public bool HasOpaqueBackground { get; set; }
    public string? BackgroundColorHex { get; set; }
    public string? LinkedWhiteoutId { get; set; }
    public string? OriginalTextToRedact { get; set; }
    public double BaseLineY { get; set; }
    public double OriginalBlockWidth { get; set; }
    public double OriginalBlockHeight { get; set; }

    // Image
    public string? SourceFilePath { get; set; }
    public byte[]? ImageBytes { get; set; }
    public bool MaintainAspectRatio { get; set; } = true;

    // Ink
    public List<double[]> Points { get; set; } = new();
    public string? StrokeColorHex { get; set; }
    public double StrokeThickness { get; set; } = 2.0;
}

/// <summary>
/// Service responsible for persisting and restoring in-progress PDF form drafts.
/// Stores drafts in %APPDATA%\DASMO CYBER CAFE TOOLS\Drafts\
/// </summary>
public class PdfDraftService
{
    private static readonly Lazy<PdfDraftService> _instance = new(() => new PdfDraftService());
    public static PdfDraftService Instance => _instance.Value;

    private string _draftsFolder;
    private readonly object _lock = new();

    /// <summary>
    /// Gets or sets the folder where draft JSON files are stored.
    /// Can be customized during automated tests to keep test runs isolated from user AppData.
    /// </summary>
    public string DraftsFolder
    {
        get
        {
            lock (_lock) return _draftsFolder;
        }
        set
        {
            lock (_lock)
            {
                _draftsFolder = value;
                try
                {
                    if (!Directory.Exists(_draftsFolder))
                        Directory.CreateDirectory(_draftsFolder);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not create drafts directory: {Folder}", _draftsFolder);
                }
            }
        }
    }

    public PdfDraftService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _draftsFolder = Path.Combine(appData, "DASMO CYBER CAFE TOOLS", "Drafts");
        try
        {
            Directory.CreateDirectory(_draftsFolder);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not create drafts directory: {Folder}", _draftsFolder);
        }
    }

    private string GetDraftFilePath(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) return string.Empty;

        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(pdfPath.Trim().ToLowerInvariant()));
        string hashHex = BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
        string safeName = Path.GetFileNameWithoutExtension(pdfPath);
        foreach (char c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');

        return Path.Combine(_draftsFolder, $"{safeName}_{hashHex}.json");
    }

    /// <summary>
    /// Checks whether an in-progress draft exists for the given PDF path.
    /// </summary>
    public bool HasDraft(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) return false;
        string path = GetDraftFilePath(pdfPath);
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }

    /// <summary>
    /// Saves or updates an in-progress draft session using pre-built DTOs.
    /// Safe to execute on background threads as it does not reference any WPF UI objects.
    /// </summary>
    public bool SaveDraftFromDtos(string pdfPath, Dictionary<int, List<PdfEditItemDto>> pageEdits, int currentPageIndex = 0, double zoomFactor = 1.0)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) return false;

        string draftPath = GetDraftFilePath(pdfPath);
        if (string.IsNullOrEmpty(draftPath)) return false;

        int editCount = pageEdits?.Values.Sum(v => v?.Count ?? 0) ?? 0;
        if (editCount == 0)
        {
            DeleteDraft(pdfPath);
            return true;
        }

        try
        {
            lock (_lock)
            {
                var session = new PdfDraftSession
                {
                    SourcePdfPath = pdfPath,
                    LastModified = DateTime.Now,
                    CurrentPageIndex = currentPageIndex,
                    ZoomFactor = zoomFactor,
                    PageEdits = pageEdits ?? new Dictionary<int, List<PdfEditItemDto>>()
                };

                string json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(draftPath, json);
                Log.Debug("Saved PDF draft session for {Path} with {Count} edits to {DraftPath}", pdfPath, editCount, draftPath);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save draft for {Path}", pdfPath);
            return false;
        }
    }

    /// <summary>
    /// Saves or updates an in-progress draft session for a PDF document.
    /// </summary>
    public bool SaveDraft(string pdfPath, IReadOnlyDictionary<int, List<PdfEditItem>> pageEdits, int currentPageIndex = 0, double zoomFactor = 1.0)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) return false;

        var dtos = new Dictionary<int, List<PdfEditItemDto>>();
        if (pageEdits != null)
        {
            foreach (var kvp in pageEdits)
            {
                if (kvp.Value == null || kvp.Value.Count == 0) continue;
                dtos[kvp.Key] = kvp.Value.Select(ToDto).ToList();
            }
        }

        return SaveDraftFromDtos(pdfPath, dtos, currentPageIndex, zoomFactor);
    }

    /// <summary>
    /// Loads a saved draft session and converts items back into active <see cref="PdfEditItem"/> objects.
    /// </summary>
    public (Dictionary<int, List<PdfEditItem>> Edits, int PageIndex, double ZoomFactor, DateTime LastModified)? LoadDraft(string pdfPath)
    {
        if (!HasDraft(pdfPath)) return null;

        string draftPath = GetDraftFilePath(pdfPath);
        try
        {
            lock (_lock)
            {
                string json = File.ReadAllText(draftPath);
                var session = JsonSerializer.Deserialize<PdfDraftSession>(json);
                if (session == null) return null;

                var result = new Dictionary<int, List<PdfEditItem>>();
                if (session.PageEdits != null)
                {
                    foreach (var kvp in session.PageEdits)
                    {
                        var items = kvp.Value.Select(FromDto).Where(i => i != null).Select(i => i!).ToList();
                        if (items.Count > 0)
                        {
                            result[kvp.Key] = items;
                        }
                    }
                }

                return (result, session.CurrentPageIndex, session.ZoomFactor, session.LastModified);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read draft from {DraftPath}", draftPath);
            return null;
        }
    }

    /// <summary>
    /// Deletes the draft session file when the document is saved or discarded.
    /// </summary>
    public bool DeleteDraft(string pdfPath)
    {
        if (string.IsNullOrWhiteSpace(pdfPath)) return false;
        string draftPath = GetDraftFilePath(pdfPath);
        if (string.IsNullOrEmpty(draftPath) || !File.Exists(draftPath)) return false;

        try
        {
            lock (_lock)
            {
                File.Delete(draftPath);
                Log.Debug("Deleted draft for {Path}", pdfPath);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not delete draft {DraftPath}", draftPath);
            return false;
        }
    }

    /// <summary>
    /// Converts a <see cref="PdfEditItem"/> into its serializable DTO representation.
    /// </summary>
    public static PdfEditItemDto ToDto(PdfEditItem item)
    {
        var dto = new PdfEditItemDto
        {
            Id = item.Id,
            PageIndex = item.PageIndex,
            X = item.X,
            Y = item.Y,
            Width = item.Width,
            Height = item.Height,
            IsHitTestVisible = item.IsHitTestVisible,
            ItemType = item.ItemType.ToString()
        };

        switch (item)
        {
            case PdfWhiteoutItem w:
                dto.FillColorHex = w.FillColorHex;
                break;

            case PdfTextItem t:
                dto.Text = t.Text;
                dto.FontFamily = t.FontFamily;
                dto.FontSizePt = t.FontSizePt;
                dto.IsBold = t.IsBold;
                dto.IsItalic = t.IsItalic;
                dto.TextColorHex = t.TextColorHex;
                dto.HasOpaqueBackground = t.HasOpaqueBackground;
                dto.BackgroundColorHex = t.BackgroundColorHex;
                dto.LinkedWhiteoutId = t.LinkedWhiteoutId;
                dto.OriginalTextToRedact = t.OriginalTextToRedact;
                dto.BaseLineY = t.BaseLineY;
                dto.OriginalBlockWidth = t.OriginalBlockWidth;
                dto.OriginalBlockHeight = t.OriginalBlockHeight;
                break;

            case PdfImageItem img:
                dto.SourceFilePath = img.SourceFilePath;
                dto.ImageBytes = img.ImageBytes;
                dto.MaintainAspectRatio = img.MaintainAspectRatio;
                break;

            case PdfInkItem ink:
                dto.StrokeColorHex = ink.StrokeColorHex;
                dto.StrokeThickness = ink.StrokeThickness;
                dto.Points = ink.Points?.Select(p => new double[] { p.X, p.Y }).ToList() ?? new List<double[]>();
                break;
        }

        return dto;
    }

    /// <summary>
    /// Reconstitutes a runtime <see cref="PdfEditItem"/> from its DTO representation.
    /// </summary>
    public static PdfEditItem? FromDto(PdfEditItemDto dto)
    {
        if (dto == null) return null;

        switch (dto.ItemType)
        {
            case "Whiteout":
                return new PdfWhiteoutItem
                {
                    Id = dto.Id,
                    PageIndex = dto.PageIndex,
                    X = dto.X,
                    Y = dto.Y,
                    Width = dto.Width,
                    Height = dto.Height,
                    IsHitTestVisible = dto.IsHitTestVisible,
                    FillColorHex = dto.FillColorHex ?? "#FFFFFF"
                };

            case "Text":
                return new PdfTextItem
                {
                    Id = dto.Id,
                    PageIndex = dto.PageIndex,
                    X = dto.X,
                    Y = dto.Y,
                    Width = dto.Width,
                    Height = dto.Height,
                    IsHitTestVisible = dto.IsHitTestVisible,
                    FontFamily = dto.FontFamily ?? "Arial",
                    FontSizePt = dto.FontSizePt > 0 ? dto.FontSizePt : 12.0,
                    IsBold = dto.IsBold,
                    IsItalic = dto.IsItalic,
                    TextColorHex = dto.TextColorHex ?? "#000000",
                    HasOpaqueBackground = dto.HasOpaqueBackground,
                    BackgroundColorHex = dto.BackgroundColorHex ?? "#FFFFFF",
                    LinkedWhiteoutId = dto.LinkedWhiteoutId,
                    OriginalTextToRedact = dto.OriginalTextToRedact,
                    OriginalBlockWidth = dto.OriginalBlockWidth,
                    OriginalBlockHeight = dto.OriginalBlockHeight,
                    BaseLineY = dto.BaseLineY,
                    Text = dto.Text ?? string.Empty
                };

            case "Image":
                return new PdfImageItem
                {
                    Id = dto.Id,
                    PageIndex = dto.PageIndex,
                    X = dto.X,
                    Y = dto.Y,
                    Width = dto.Width,
                    Height = dto.Height,
                    IsHitTestVisible = dto.IsHitTestVisible,
                    SourceFilePath = dto.SourceFilePath,
                    ImageBytes = dto.ImageBytes,
                    MaintainAspectRatio = dto.MaintainAspectRatio
                };

            case "Ink":
                var ink = new PdfInkItem
                {
                    Id = dto.Id,
                    PageIndex = dto.PageIndex,
                    X = dto.X,
                    Y = dto.Y,
                    Width = dto.Width,
                    Height = dto.Height,
                    IsHitTestVisible = dto.IsHitTestVisible,
                    StrokeColorHex = dto.StrokeColorHex ?? "#000000",
                    StrokeThickness = dto.StrokeThickness > 0 ? dto.StrokeThickness : 2.0
                };
                if (dto.Points != null)
                {
                    foreach (var p in dto.Points)
                    {
                        if (p.Length >= 2) ink.Points.Add((p[0], p[1]));
                    }
                }
                return ink;

            default:
                return null;
        }
    }
}
