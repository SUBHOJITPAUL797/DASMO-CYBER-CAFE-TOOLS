using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using SmartSaver.Models;

namespace SmartSaver.Services;

/// <summary>
/// Thread-safe singleton that manages application settings persistence.
/// Settings are stored as JSON in <c>%APPDATA%\SmartSaver\settings.json</c>.
/// </summary>
public sealed class SettingsManager
{
    private static readonly Lazy<SettingsManager> _instance = new(() => new SettingsManager());

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsDirectory;
    private readonly string _settingsFilePath;
    private readonly object _lock = new();

    private AppSettings _current;

    /// <summary>Gets the singleton instance of <see cref="SettingsManager"/>.</summary>
    public static SettingsManager Instance => _instance.Value;

    /// <summary>Gets the current application settings snapshot.</summary>
    public AppSettings Current
    {
        get { lock (_lock) { return _current; } }
    }

    private SettingsManager()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _settingsDirectory = Path.Combine(appData, "DASMO CYBER CAFE TOOLS");
        Directory.CreateDirectory(_settingsDirectory);

        _settingsFilePath = Path.Combine(_settingsDirectory, "settings.json");

        // Migration check from older folders
        if (!File.Exists(_settingsFilePath))
        {
            string oldCompressorPath = Path.Combine(appData, "DASMO CYBER COMPRESSOR", "settings.json");
            string oldSmartSaverPath = Path.Combine(appData, "SmartSaver", "settings.json");
            if (File.Exists(oldCompressorPath))
            {
                try { File.Copy(oldCompressorPath, _settingsFilePath, overwrite: true); } catch { }
            }
            else if (File.Exists(oldSmartSaverPath))
            {
                try { File.Copy(oldSmartSaverPath, _settingsFilePath, overwrite: true); } catch { }
            }
        }

        _current = Load();
    }

    /// <summary>
    /// Loads settings from disk. If the file does not exist or is corrupt,
    /// creates a new default <see cref="AppSettings"/> and persists it.
    /// </summary>
    /// <returns>The loaded or newly created <see cref="AppSettings"/>.</returns>
    public AppSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    Log.Information("Settings file not found at {Path}. Creating defaults", _settingsFilePath);
                    _current = new AppSettings();
                    SaveInternal();
                    return _current;
                }

                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);

                if (settings is null)
                {
                    Log.Warning("Settings file deserialized to null. Resetting to defaults");
                    _current = new AppSettings();
                    SaveInternal();
                    return _current;
                }

                settings.AutoCompress ??= new AutoCompressSettings();
                if (string.IsNullOrEmpty(settings.AutoCompress.ActionOnNewFile))
                {
                    settings.AutoCompress.ActionOnNewFile = "prompt";
                }

                _current = settings;
                Log.Debug("Settings loaded successfully from {Path}", _settingsFilePath);
                return _current;
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "Corrupt settings file at {Path}. Resetting to defaults", _settingsFilePath);
                _current = new AppSettings();
                SaveInternal();
                return _current;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error loading settings from {Path}", _settingsFilePath);
                _current = new AppSettings();
                return _current;
            }
        }
    }

    /// <summary>
    /// Persists the current settings to disk.
    /// </summary>
    public void Save()
    {
        lock (_lock)
        {
            SaveInternal();
        }
    }

    /// <summary>
    /// Applies a mutation to the current settings and saves them to disk.
    /// </summary>
    /// <param name="mutator">An action that modifies the <see cref="AppSettings"/>.</param>
    public void Update(Action<AppSettings> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);

        lock (_lock)
        {
            mutator(_current);
            SaveInternal();
        }
    }

    /// <summary>Gets the full path to the settings file.</summary>
    public string SettingsFilePath => _settingsFilePath;

    /// <summary>
    /// Resets all settings to their defaults and persists the change.
    /// </summary>
    public void ResetToDefaults()
    {
        lock (_lock)
        {
            Log.Information("Resetting settings to defaults");
            _current = new AppSettings();
            SaveInternal();
        }
    }

    private void SaveInternal()
    {
        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var json = JsonSerializer.Serialize(_current, _jsonOptions);
            string tempPath = _settingsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsFilePath, overwrite: true);
            Log.Debug("Settings saved to {Path}", _settingsFilePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save settings to {Path}", _settingsFilePath);
        }
    }
}
