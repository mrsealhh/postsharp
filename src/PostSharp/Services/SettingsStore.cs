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

        var load = DataFile.Load(
            FilePath,
            text => JsonSerializer.Deserialize<AppSettings>(text, JsonOptions)
        );
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

    public bool Save(AppSettings settings)
    {
        try
        {
            // False for a file protected after a failed read; the startup notice already says so.
            return DataFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception exception)
        {
            Log.Error(exception, "保存设置文件失败 {SettingsFile}", FilePath);
            return false;
        }
    }
}
