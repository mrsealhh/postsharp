using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Loads and saves the local storage profile bootstrap file.</summary>
public sealed class StorageConfigStore
{
    public string FilePath { get; }

    public StorageConfigStore()
    {
        AppPaths.EnsureRoot();
        FilePath = AppPaths.StorageConfigFile;
    }

    public StorageConfig Load()
    {
        var load = DataFile.Load(FilePath, text => DataJson.Deserialize<StorageConfig>(text));
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
        DataFile.Save(FilePath, () => DataJson.Serialize(config.Normalize()));
}
