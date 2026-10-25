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

    public string FilePath { get; }

    public SettingsStore()
    {
        AppPaths.EnsureRoot();
        FilePath = AppPaths.SettingsFile;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var settings =
                JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions)
                ?? new AppSettings();

            if (string.IsNullOrWhiteSpace(settings.LogDirectory))
            {
                settings.LogDirectory = AppPaths.DefaultLogDirectory;
            }

            return settings;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "读取设置文件失败，使用默认设置 {SettingsFile}", FilePath);
            return new AppSettings();
        }
    }

    public bool Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(FilePath, json);
            return true;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "保存设置文件失败 {SettingsFile}", FilePath);
            return false;
        }
    }
}
