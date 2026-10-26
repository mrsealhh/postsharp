using System.Text.Encodings.Web;
using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Loads and saves the local storage profile bootstrap file.</summary>
public sealed class StorageConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath { get; }

    public StorageConfigStore()
    {
        AppPaths.EnsureRoot();
        FilePath = AppPaths.StorageConfigFile;
    }

    public StorageConfig Load()
    {
        var load = DataFile.Load(
            FilePath,
            text => JsonSerializer.Deserialize<StorageConfig>(text, JsonOptions)
        );
        if (load.IsProblem)
        {
            Log.Warning(
                "存储配置读取异常 {StorageFile} {State} {SetAsideFile}",
                FilePath,
                load.State,
                load.SetAsidePath
            );
        }

        var config = (load.Value ?? StorageConfig.CreateDefault()).Normalize();
        if (load.State == DataFileState.Missing)
        {
            Save(config);
        }

        return config;
    }

    public SaveResult Save(StorageConfig config) =>
        DataFile.Save(FilePath, () => JsonSerializer.Serialize(config.Normalize(), JsonOptions));
}
