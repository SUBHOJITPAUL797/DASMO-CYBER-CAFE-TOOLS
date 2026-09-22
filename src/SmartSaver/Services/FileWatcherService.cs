using System;
using System.IO;
using System.Collections.Concurrent;
using Serilog;
using SmartSaver.Models;

namespace SmartSaver.Services;

/// <summary>
/// Monitors configured watch folders for new or modified files and dispatches
/// them to <see cref="CompressionEngine"/> for compression. Implements debounce
/// logic (2 s after last write), file-lock checking (5 retries × 1 s), and
/// skips the <c>_Originals</c> backup subfolder.
/// </summary>
public sealed class FileWatcherService : IDisposable
{
    private const int DebounceDelayMs = 2000;
    private const int LockCheckRetries = 5;
    private const int LockCheckIntervalMs = 1000;

    private readonly CompressionEngine _compressionEngine;
    private readonly NotificationService _notificationService;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceTokens = new();
    private readonly ConcurrentDictionary<string, byte> _processing = new();
    private bool _isPaused;
    private bool _disposed;

    /// <summary>Gets or sets whether the watcher is currently paused.</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = !value;
            }
            Log.Information("FileWatcherService {State}", value ? "paused" : "resumed");
        }
    }

    /// <summary>
    /// Initializes a new <see cref="FileWatcherService"/>.
    /// </summary>
    /// <param name="compressionEngine">The engine used to compress detected files.</param>
    /// <param name="notificationService">The notification service for user feedback.</param>
    public FileWatcherService(CompressionEngine compressionEngine, NotificationService notificationService)
    {
        _compressionEngine = compressionEngine ?? throw new ArgumentNullException(nameof(compressionEngine));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
    }

    private static readonly ConcurrentDictionary<string, DateTime> _openedDialogs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTime> _ignoredOutputFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a file path to be ignored by the file watcher (e.g. newly created compressed/resized files).
    /// </summary>
    public static void IgnoreOutputFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            _ignoredOutputFiles[Path.GetFullPath(path)] = DateTime.UtcNow.AddMinutes(5);
        }
        catch { }
    }

    /// <summary>
    /// Starts monitoring standard user folders (Downloads, Desktop, Documents, Pictures) and secondary drives.
    /// </summary>
    public void Start()
    {
        Stop();

        var settings = SettingsManager.Instance.Current;
        if (!settings.AutoCompress.Enabled)
        {
            Log.Information("Auto-compress is disabled. FileWatcherService will not start");
            return;
        }

        var candidateFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Standard user folders where 99.9% of downloads and saved files land
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            string[] stdFolders = [
                Path.Combine(userProfile, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            ];
            foreach (var f in stdFolders)
            {
                if (!string.IsNullOrEmpty(f) && Directory.Exists(f))
                    candidateFolders.Add(f);
            }
        }

        // 2. Secondary fixed and removable drives (D:\, E:\, etc.)
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable))
                {
                    if (!drive.RootDirectory.FullName.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateFolders.Add(drive.RootDirectory.FullName);
                    }
                }
            }
            catch { }
        }

        // 3. User configured watch folders
        foreach (var folder in settings.AutoCompress.WatchFolders)
        {
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                candidateFolders.Add(folder);
        }

        // De-duplicate paths: ensure no overlapping parent/child watchers are registered
        var finalFolders = new List<string>();
        foreach (var folder in candidateFolders
            .Select(f => Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .OrderBy(f => f.Length))
        {
            if (!finalFolders.Any(parent => folder.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                                            folder.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                finalFolders.Add(folder);
            }
        }

        foreach (var folder in finalFolders)
        {
            try
            {
                var watcher = CreateWatcher(folder, settings);
                _watchers.Add(watcher);
                Log.Information("Watching folder/drive: {Folder}", folder);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create watcher for {Folder}", folder);
            }
        }

        Log.Information("FileWatcherService started watching {Count} non-overlapping folder(s)", _watchers.Count);
    }

    /// <summary>
    /// Stops all active file system watchers and cancels pending debounce timers.
    /// </summary>
    public void Stop()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        foreach (var cts in _debounceTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _debounceTokens.Clear();

        Log.Information("FileWatcherService stopped");
    }

    /// <summary>
    /// Restarts watchers with current settings (e.g., after settings change).
    /// </summary>
    public void Restart()
    {
        Log.Information("Restarting FileWatcherService");
        Start();
    }

    private FileSystemWatcher CreateWatcher(string folder, AppSettings settings)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            InternalBufferSize = 64 * 1024, // 64 KB max buffer to prevent buffer overflow
            Filter = "*.*"
        };

        watcher.Created += OnFileEvent;
        watcher.Changed += OnFileEvent;
        watcher.Renamed += OnRenamedFileEvent;
        watcher.Error += OnWatcherError;

        return watcher;
    }

    private void OnRenamedFileEvent(object sender, RenamedEventArgs e)
    {
        if (_isPaused) return;
        OnFileEvent(sender, new FileSystemEventArgs(WatcherChangeTypes.Created, Path.GetDirectoryName(e.FullPath)!, Path.GetFileName(e.FullPath)));
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (_isPaused) return;

        string filePath = e.FullPath;

        // Skip partial browser download files (.crdownload, .part, .tmp, .download, .opdownload)
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || ext is ".crdownload" or ".part" or ".tmp" or ".download" or ".opdownload")
            return;

        // Check if extension is supported in settings
        var settings = SettingsManager.Instance.Current;
        if (!settings.AutoCompress.SupportedExtensions.Any(s => s.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return;

        // Automatically unblock downloaded file so Explorer preview pane works without security warnings
        Helpers.RegistryHelper.UnblockFile(filePath);

        // Skip _Originals backup folder
        if (filePath.Contains(
                Path.DirectorySeparatorChar + settings.AutoCompress.BackupFolderName + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains(
                Path.DirectorySeparatorChar + settings.AutoCompress.BackupFolderName,
                StringComparison.OrdinalIgnoreCase) &&
            filePath.EndsWith(settings.AutoCompress.BackupFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Skip SmartSaver temp files, compressed outputs, and registered output files
        string fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName)) return;

        if (fileName.StartsWith(".smartsaver", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("_compressed", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("_resized", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("_converted", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("_merged", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string fullPath = Path.GetFullPath(filePath);
            if (_ignoredOutputFiles.TryGetValue(fullPath, out var expiry))
            {
                if (DateTime.UtcNow < expiry)
                {
                    Log.Debug("FileWatcherService ignoring registered output file: {Path}", filePath);
                    return;
                }
                _ignoredOutputFiles.TryRemove(fullPath, out _);
            }
        }
        catch { }

        // Skip Windows and Program Files system folders safely
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(winDir) && filePath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase)) return;

        string pfDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pfDir) && filePath.StartsWith(pfDir, StringComparison.OrdinalIgnoreCase)) return;

        string pf86Dir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf86Dir) && filePath.StartsWith(pf86Dir, StringComparison.OrdinalIgnoreCase)) return;

        // Debounce: cancel any existing timer for this file and start a new one
        if (_debounceTokens.TryRemove(filePath, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _debounceTokens[filePath] = cts;

        _ = DebounceAndProcessAsync(filePath, cts.Token);
    }

    private async Task DebounceAndProcessAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceDelayMs, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return; // Debounce reset — a newer event will handle this file
        }
        finally
        {
            _debounceTokens.TryRemove(filePath, out _);
        }

        // Prevent duplicate concurrent processing of the same file
        if (!_processing.TryAdd(filePath, 0))
        {
            Log.Debug("File {Path} is already being processed", filePath);
            return;
        }

        try
        {
            if (!File.Exists(filePath)) return;

            // Check if file is already under target
            var settings = SettingsManager.Instance.Current;
            long targetBytes = settings.AutoCompress.TargetSizeKB * 1024L;
            var fileInfo = new FileInfo(filePath);

            if (fileInfo.Length <= targetBytes && settings.AutoCompress.ActionOnNewFile != "prompt")
            {
                Log.Debug("Skipping {Path} — already under target ({Size} ≤ {Target} bytes)",
                    filePath, fileInfo.Length, targetBytes);
                return;
            }

            // Wait for file lock to be released
            if (!await WaitForFileLockAsync(filePath, cancellationToken))
            {
                Log.Warning("File {Path} remained locked after {Retries} retries. Skipping",
                    filePath, LockCheckRetries);
                _notificationService.NotifySkipped(Path.GetFileName(filePath), "File is locked by another process");
                return;
            }

            Log.Information("Processing file: {Path} ({Size} bytes)", filePath, fileInfo.Length);

            if (settings.AutoCompress.ActionOnNewFile == "prompt")
            {
                // De-duplicate: skip if a prompt was already opened for this file path within the last 10 seconds
                if (_openedDialogs.TryGetValue(filePath, out var lastOpened) && (DateTime.UtcNow - lastOpened).TotalSeconds < 10)
                {
                    Log.Debug("Dialog for {Path} was already prompted recently. Skipping duplicate prompt.", filePath);
                    return;
                }

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // Check if a FileCompressDialog is already open on screen for this file
                        var existingDialog = System.Windows.Application.Current.Windows
                            .OfType<SmartSaver.Views.FileCompressDialog>()
                            .FirstOrDefault(w => w.IsVisible && string.Equals(w.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

                        if (existingDialog != null)
                        {
                            Log.Debug("FileCompressDialog already open for {Path}. Bringing to front.", filePath);
                            existingDialog.Topmost = true;
                            existingDialog.Activate();
                            existingDialog.Focus();
                            return;
                        }

                        // Prune entries older than 5 minutes to prevent memory growth over long sessions
                        var now = DateTime.UtcNow;
                        foreach (var kvp in _openedDialogs)
                        {
                            if ((now - kvp.Value).TotalMinutes > 5)
                            {
                                _openedDialogs.TryRemove(kvp.Key, out _);
                            }
                        }

                        _openedDialogs[filePath] = now;

                        var dialog = new SmartSaver.Views.FileCompressDialog(filePath);
                        dialog.Topmost = true;
                        dialog.Show();
                        dialog.Activate();
                        dialog.Focus();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to show interactive compression dialog for {Path}", filePath);
                    }
                });
                return;
            }

            var result = await _compressionEngine.CompressFileAsync(filePath);

            if (result.Success && result.NewSizeBytes < result.OriginalSizeBytes)
            {
                _notificationService.NotifySuccess(
                    Path.GetFileName(filePath), result.OriginalSizeBytes, result.NewSizeBytes);
            }
            else if (!result.Success)
            {
                _notificationService.NotifyFailure(
                    Path.GetFileName(filePath), result.Message ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error processing file {Path}", filePath);
            _notificationService.NotifyFailure(Path.GetFileName(filePath), ex.Message);
        }
        finally
        {
            _processing.TryRemove(filePath, out _);
        }
    }

    private static async Task<bool> WaitForFileLockAsync(string filePath, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < LockCheckRetries; attempt++)
        {
            try
            {
                using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return true; // File is accessible
            }
            catch (IOException)
            {
                Log.Debug("File {Path} is locked — retry {Attempt}/{Max}",
                    filePath, attempt + 1, LockCheckRetries);
                await Task.Delay(LockCheckIntervalMs, cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                Log.Debug("File {Path} access denied — retry {Attempt}/{Max}",
                    filePath, attempt + 1, LockCheckRetries);
                await Task.Delay(LockCheckIntervalMs, cancellationToken);
            }
        }

        return false;
    }

    private static void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Log.Error(e.GetException(), "FileSystemWatcher error — attempting recovery");
        if (sender is FileSystemWatcher watcher)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
            }
            catch { }
        }
    }

    /// <summary>Releases all watchers and cancels pending operations.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        GC.SuppressFinalize(this);
    }
}
