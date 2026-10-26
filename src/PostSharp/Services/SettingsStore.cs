using System.Text.Encodings.Web;
using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static AppSettings? _loaded;

    private readonly StorageCoordinator _storage = StorageCoordinator.Current;

    public string FilePath { get; }

    public SettingsStore()
    {
        AppPaths.EnsureRoot();
        FilePath = AppPaths.SettingsFile;
    }

    /// <summary>
    /// How reading settings went when it was not normal. Program reads settings before the main window exists, so the
    /// window asks here to show the notice.
    /// </summary>
    public static DataFileLoad<AppSettings>? StartupProblem { get; private set; }

    public string StorageDescription => $"设置：{AppPaths.DatabaseFile}（SQLite 文档 settings）";

    /// <summary>
    /// Settings are read once per process and every later call gets the same instance, so a momentary lock during a
    /// second read cannot swap in defaults or block saving.
    /// </summary>
    public AppSettings Load()
    {
        if (_loaded is not null)
        {
            return _loaded;
        }

        var document = _storage.LoadDocument(StorageCategories.Settings);
        DataFileLoad<AppSettings> load;
        if (document is not null)
        {
            try
            {
                var documentSettings = JsonSerializer.Deserialize<AppSettings>(
                    document.Payload,
                    JsonOptions
                );
                load = documentSettings is null
                    ? new DataFileLoad<AppSettings>(null, DataFileState.Unreadable, null)
                    : new DataFileLoad<AppSettings>(documentSettings, DataFileState.Loaded, null);
            }
            catch (Exception exception)
            {
                Log.Error(
                    exception,
                    "读取 SQLite 设置文档失败 {Category}",
                    StorageCategories.Settings
                );
                load = new DataFileLoad<AppSettings>(null, DataFileState.Unreadable, null);
            }
        }
        else
        {
            // One-time migration from the old JSON layout.
            load = DataFile.Load(
                FilePath,
                text => JsonSerializer.Deserialize<AppSettings>(text, JsonOptions)
            );
            if (load.Value is { } migrated)
            {
                _storage.SaveDocument(
                    StorageCategories.Settings,
                    JsonSerializer.Serialize(migrated, JsonOptions)
                );
            }
        }

        if (load.IsProblem)
        {
            StartupProblem = load;
        }

        var settings = load.Value ?? new AppSettings();
        if (string.IsNullOrWhiteSpace(settings.LogDirectory))
        {
            settings.LogDirectory = AppPaths.DefaultLogDirectory;
        }

        _loaded = settings;
        return settings;
    }

    public SaveResult Save(AppSettings settings) =>
        _storage.SaveDocument(
            StorageCategories.Settings,
            JsonSerializer.Serialize(settings, JsonOptions)
        );
}
