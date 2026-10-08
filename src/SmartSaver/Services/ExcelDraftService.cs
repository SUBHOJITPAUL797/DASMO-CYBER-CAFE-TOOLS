using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace SmartSaver.Services;

/// <summary>
/// Serializable DTO representing an in-progress Excel-to-A4 printing and formatting session.
/// Preserves filters, ranges, villages, column visibility, orientations, and page breaks.
/// </summary>
public class ExcelDraftSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public DateTime LastModified { get; set; } = DateTime.Now;

    public string SelectedSheetName { get; set; } = string.Empty;
    public string RangeMode { get; set; } = "Entire";
    public int StartRow { get; set; } = 1;
    public int EndRow { get; set; } = 100;
    public string Orientation { get; set; } = "Auto"; // "Auto", "Landscape", "Portrait"

    public string MarginPreset { get; set; } = "Narrow";
    public double MarginLeftMm { get; set; } = 5.0;
    public double MarginRightMm { get; set; } = 5.0;
    public double MarginTopMm { get; set; } = 5.0;
    public double MarginBottomMm { get; set; } = 5.0;

    public bool RepeatHeaderOnEveryPage { get; set; } = true;
    public string FontSizePreset { get; set; } = "Standard";
    public bool AddSerialNo { get; set; } = true;
    public bool ZebraStriping { get; set; } = true;
    public bool DrawGridlines { get; set; } = true;
    public string DocumentTitle { get; set; } = string.Empty;
    public string ShopFooter { get; set; } = "DASMO CYBER CAFE TOOLS";
    public string CellAlignment { get; set; } = "Left";

    public int SelectedGroupByColumnIndex { get; set; } = -1;
    public string SelectedGroupByColumnName { get; set; } = string.Empty;
    public string SelectedGroupValue { get; set; } = "All (Print All Records / Values)";
    public bool SortByGroupColumn { get; set; } = true;
    public bool PageBreakOnGroupChange { get; set; } = false;
    public bool RestartSerialNoPerGroup { get; set; } = false;
    public int MaxRowsPerPage { get; set; } = 0;

    public List<int> SelectedColumnIndices { get; set; } = new();
    public int TotalPages { get; set; } = 0;
    public int RowsPrinted { get; set; } = 0;

    public string SummaryText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(SelectedSheetName)) parts.Add($"Sheet: {SelectedSheetName}");
            if (RangeMode == "Custom") parts.Add($"Rows {StartRow}–{EndRow}");
            if (SelectedGroupByColumnIndex >= 0 && !string.IsNullOrEmpty(SelectedGroupValue) && !SelectedGroupValue.StartsWith("All", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"{SelectedGroupByColumnName}: {SelectedGroupValue}");
            }
            if (Orientation != "Auto") parts.Add(Orientation);
            if (TotalPages > 0) parts.Add($"{TotalPages} Pages");
            return parts.Count > 0 ? string.Join(" • ", parts) : "Custom Layout";
        }
    }
}

/// <summary>
/// Service responsible for persisting and restoring in-progress Excel to PDF printing sessions.
/// Stores draft configurations in %APPDATA%\DASMO CYBER CAFE TOOLS\ExcelDrafts\
/// </summary>
public class ExcelDraftService
{
    private static readonly Lazy<ExcelDraftService> _instance = new(() => new ExcelDraftService());
    public static ExcelDraftService Instance => _instance.Value;

    private string _draftsFolder;
    private readonly object _lock = new();

    public static string? CustomDataDirectory { get; set; }

    public static void ResetForTesting(string? testDir = null)
    {
        CustomDataDirectory = testDir;
        if (testDir != null)
        {
            Instance.DraftsFolder = Path.Combine(testDir, "ExcelDrafts");
        }
    }

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
                    Log.Warning(ex, "Could not create Excel drafts directory: {Folder}", _draftsFolder);
                }
            }
        }
    }

    public ExcelDraftService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _draftsFolder = CustomDataDirectory != null
            ? Path.Combine(CustomDataDirectory, "ExcelDrafts")
            : Path.Combine(appData, "DASMO CYBER CAFE TOOLS", "ExcelDrafts");

        try
        {
            Directory.CreateDirectory(_draftsFolder);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not create Excel drafts directory: {Folder}", _draftsFolder);
        }
    }

    private string GetDraftFilePath(string excelPath)
    {
        if (string.IsNullOrWhiteSpace(excelPath)) return string.Empty;

        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(excelPath.Trim().ToLowerInvariant()));
        string hashHex = BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
        string safeName = Path.GetFileNameWithoutExtension(excelPath);
        foreach (char c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');

        return Path.Combine(_draftsFolder, $"{safeName}_{hashHex}.json");
    }

    /// <summary>
    /// Checks whether an in-progress draft exists for the given Excel file path.
    /// </summary>
    public bool HasDraft(string excelPath)
    {
        if (string.IsNullOrWhiteSpace(excelPath)) return false;
        string path = GetDraftFilePath(excelPath);
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }

    /// <summary>
    /// Saves or updates an in-progress draft session.
    /// </summary>
    public bool SaveDraft(ExcelDraftSession session)
    {
        if (session == null || string.IsNullOrWhiteSpace(session.FilePath)) return false;

        string draftPath = GetDraftFilePath(session.FilePath);
        if (string.IsNullOrEmpty(draftPath)) return false;

        try
        {
            lock (_lock)
            {
                session.LastModified = DateTime.Now;
                string json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(draftPath, json);
                Log.Debug("Saved Excel draft session for {Path} to {DraftPath}", session.FilePath, draftPath);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save Excel draft for {Path}", session.FilePath);
            return false;
        }
    }

    /// <summary>
    /// Loads a saved draft session for the given Excel file path.
    /// </summary>
    public ExcelDraftSession? LoadDraft(string excelPath)
    {
        if (!HasDraft(excelPath)) return null;

        string draftPath = GetDraftFilePath(excelPath);
        try
        {
            lock (_lock)
            {
                string json = File.ReadAllText(draftPath);
                var session = JsonSerializer.Deserialize<ExcelDraftSession>(json);
                return session;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read Excel draft from {DraftPath}", draftPath);
            return null;
        }
    }

    /// <summary>
    /// Deletes a saved draft session.
    /// </summary>
    public bool DeleteDraft(string excelPath)
    {
        if (string.IsNullOrWhiteSpace(excelPath)) return false;
        string draftPath = GetDraftFilePath(excelPath);
        try
        {
            lock (_lock)
            {
                if (File.Exists(draftPath))
                {
                    File.Delete(draftPath);
                    Log.Debug("Deleted Excel draft session for {Path}", excelPath);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete Excel draft for {Path}", excelPath);
        }
        return false;
    }

    /// <summary>
    /// Returns the most recently modified draft session where the source file still exists.
    /// </summary>
    public ExcelDraftSession? GetMostRecentDraft()
    {
        try
        {
            lock (_lock)
            {
                if (!Directory.Exists(_draftsFolder)) return null;

                var files = Directory.GetFiles(_draftsFolder, "*.json");
                foreach (var file in files.OrderByDescending(f => File.GetLastWriteTime(f)))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var session = JsonSerializer.Deserialize<ExcelDraftSession>(json);
                        if (session != null && !string.IsNullOrWhiteSpace(session.FilePath) && File.Exists(session.FilePath))
                        {
                            return session;
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to find most recent Excel draft");
        }
        return null;
    }
}
