using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Serilog;
using SmartSaver.Models;

namespace SmartSaver.Services;

public class HistoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public string Operation { get; set; } = "Compressed";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public long OriginalSizeBytes { get; set; }
    public long NewSizeBytes { get; set; }
    public string FormattedSize => CompressionResult.FormatFileSize(NewSizeBytes);
    public string TimeFormatted => Timestamp.ToString("hh:mm tt");
    public string DateFormatted => Timestamp.ToString("dd MMM yyyy");
    public bool FileExists => File.Exists(FilePath);
    public bool IsPdf => !string.IsNullOrEmpty(FilePath) && FilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    public bool HasDraft => PdfDraftService.Instance.HasDraft(FilePath);
    public string ResumeButtonText => HasDraft ? "⚡ Resume Draft" : "✏️ Resume in Editor";
}

public class OutputHistoryService
{
    private static Lazy<OutputHistoryService> _instance = new(() => new OutputHistoryService());
    public static OutputHistoryService Instance => _instance.Value;

    public static string? CustomDataDirectory { get; set; }

    public static void ResetForTesting(string? testDir = null)
    {
        CustomDataDirectory = testDir;
        _instance = new Lazy<OutputHistoryService>(() => new OutputHistoryService(testDir));
    }

    private readonly string _historyFilePath;
    private readonly object _lock = new();
    public ObservableCollection<HistoryRecord> Records { get; } = new();
    public event Action? OnRecordAdded;

    private OutputHistoryService(string? customDir = null)
    {
        string? targetDir = customDir ?? CustomDataDirectory;
        if (targetDir == null)
        {
            try
            {
                var procName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                if (procName.Contains("Test", StringComparison.OrdinalIgnoreCase))
                {
                    targetDir = Path.Combine(Path.GetTempPath(), "DasmoTestSandbox_" + procName);
                }
            }
            catch { }
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string folder = targetDir ?? Path.Combine(appData, "DASMO CYBER CAFE TOOLS");
        Directory.CreateDirectory(folder);
        _historyFilePath = Path.Combine(folder, "history.json");

        // Migration check from older folders (only in production mode)
        if (targetDir == null && !File.Exists(_historyFilePath))
        {
            string oldCompressorPath = Path.Combine(appData, "DASMO CYBER COMPRESSOR", "history.json");
            string legacyPath = Path.Combine(appData, "SmartSaver", "history.json");
            if (File.Exists(oldCompressorPath))
            {
                try { File.Copy(oldCompressorPath, _historyFilePath, overwrite: true); } catch { }
            }
            else if (File.Exists(legacyPath))
            {
                try { File.Copy(legacyPath, _historyFilePath, overwrite: true); } catch { }
            }
        }

        Load();
    }

    public void Record(string filePath, string operation, long origBytes = 0, long newBytes = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            if (newBytes <= 0 && File.Exists(filePath))
            {
                newBytes = new FileInfo(filePath).Length;
            }

            var rec = new HistoryRecord
            {
                FilePath = filePath,
                Operation = operation,
                Timestamp = DateTime.Now,
                OriginalSizeBytes = origBytes > 0 ? origBytes : newBytes,
                NewSizeBytes = newBytes
            };

            lock (_lock)
            {
                Action addAction = () =>
                {
                    // If this file already has a draft or recent entry, replace old one so fresh record is at the top
                    var existing = Records.FirstOrDefault(r => string.Equals(r.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && (existing.Operation.Contains("Draft") || operation.Contains("Draft") || existing.Operation == operation))
                    {
                        Records.Remove(existing);
                    }

                    Records.Insert(0, rec);
                    // Keep at most 200 recent records
                    while (Records.Count > 200) Records.RemoveAt(Records.Count - 1);
                };

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    // Non-blocking: post to the UI thread's queue and return immediately.
                    // Dispatcher.Invoke (blocking) deadlocks when the dispatcher thread is not
                    // pumping messages (e.g. background/STA test threads).
                    dispatcher.BeginInvoke(addAction);
                }
                else
                {
                    addAction();
                }
                SaveInternal();
            }

            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(() => OnRecordAdded?.Invoke());
                }
                else
                {
                    OnRecordAdded?.Invoke();
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to add history record for {Path}", filePath);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => Records.Clear());
            }
            else
            {
                Records.Clear();
            }
            SaveInternal();
        }

        try
        {
            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => OnRecordAdded?.Invoke());
            }
            else
            {
                OnRecordAdded?.Invoke();
            }
        }
        catch { }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_historyFilePath))
            {
                string json = File.ReadAllText(_historyFilePath);
                var list = JsonSerializer.Deserialize<List<HistoryRecord>>(json);
                if (list != null)
                {
                    Records.Clear();
                    foreach (var item in list.OrderByDescending(r => r.Timestamp))
                    {
                        Records.Add(item);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load history from {Path}", _historyFilePath);
        }
    }

    private void SaveInternal()
    {
        try
        {
            var list = Records.ToList();
            string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_historyFilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save history to {Path}", _historyFilePath);
        }
    }
}
