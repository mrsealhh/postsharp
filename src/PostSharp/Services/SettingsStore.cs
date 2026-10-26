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

    private static bool _saveBlocked;

    public string FilePath { get; }

    public SettingsStore()
    {
        AppPaths.EnsureRoot();
        FilePath = AppPaths.SettingsFile;
    }

    /// <summary>
    /// The first unusual outcome of reading settings in this process. Program reads settings before the main
    /// window exists, and that first read is the one that moves an unreadable file aside, so the window asks here.
    /// </summary>
    public static DataFileLoad<AppSettings>? StartupProblem { get; private set; }

    public AppSettings Load()
    {
        var load = DataFile.Load(
            FilePath,
            text => JsonSerializer.Deserialize<AppSettings>(text, JsonOptions)
        );
        if (load.IsProblem)
        {
            StartupProblem ??= load;
        }

        if (load.State == DataFileState.Inaccessible)
        {
            _saveBlocked = true;
        }

        var settings = load.Value ?? new AppSettings();
        if (string.IsNullOrWhiteSpace(settings.LogDirectory))
        {
            settings.LogDirectory = AppPaths.DefaultLogDirectory;
        }

        return settings;
    }

    public bool Save(AppSettings settings)
    {
        if (_saveBlocked)
        {
            Log.Warning("设置文件启动时无法读取，为保护原文件不写入 {SettingsFile}", FilePath);
            return false;
        }

        try
        {
            DataFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
            return true;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "保存设置文件失败 {SettingsFile}", FilePath);
            return false;
        }
    }
}
