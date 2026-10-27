using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using PostSharp.Services;

namespace PostSharp.Models;

public static class StorageProviders
{
    public const string LocalSqlite = "LocalSqlite";
    public const string PostgreSql = "PostgreSql";
    public const string MySql = "MySql";
    public const string SqlServer = "SqlServer";
    public const string MongoDb = "MongoDb";

    /// <summary>Local SQLite plus every provider exposed by an installed storage plugin.</summary>
    public static IReadOnlyList<string> All =>
        [LocalSqlite, .. StoragePluginRegistry.Current.ProviderIds];

    /// <summary>Providers that require a downloaded plugin (everything except the local fallback).</summary>
    public static IReadOnlyList<string> Remote => StoragePluginRegistry.Current.ProviderIds;

    public static string Label(string provider) =>
        provider switch
        {
            LocalSqlite => "本地 SQLite",
            _ => SafePluginLabel(provider),
        };

    private static string SafePluginLabel(string provider)
    {
        try
        {
            var info = StoragePluginRegistry.Current.Find(provider)?.Info;
            if (!string.IsNullOrWhiteSpace(info?.DisplayName))
            {
                return info.DisplayName;
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "读取存储插件名称失败 {Provider}", provider);
        }

        return provider switch
        {
            PostgreSql => "PostgreSQL",
            MySql => "MySQL",
            SqlServer => "SQL Server",
            MongoDb => "MongoDB",
            _ => provider,
        };
    }
}

public sealed partial class StorageProfile : ObservableObject
{
    private string _connectionString = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = "远程数据库";

    [ObservableProperty]
    private string _provider = StorageProviders.LocalSqlite;

    [JsonIgnore]
    public string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (SetProperty(ref _connectionString, value))
            {
                ConnectionStringCipher = StorageSecretProtector.Protect(value);
            }
        }
    }

    /// <summary>DPAPI-protected connection string as stored on disk.</summary>
    public string ConnectionStringCipher
    {
        get => _connectionStringCipher;
        set
        {
            _connectionStringCipher = value;
            _connectionString = StorageSecretProtector.Unprotect(value);
        }
    }

    private string _connectionStringCipher = string.Empty;

    [JsonIgnore]
    public bool IsLocal =>
        string.Equals(Provider, StorageProviders.LocalSqlite, StringComparison.Ordinal);

    [JsonIgnore]
    public bool IsRemote => !IsLocal;

    [JsonIgnore]
    public string ProviderLabel => StorageProviders.Label(Provider);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ProviderLabel : Name;

    [JsonIgnore]
    public string MaskedConnectionString =>
        IsLocal ? ConnectionString : "连接串已隐藏（可在设置中查看或替换）";

    public StorageProfile Clone() =>
        new()
        {
            Id = Id,
            Name = Name,
            Provider = Provider,
            ConnectionString = ConnectionString,
        };

    public override string ToString() => DisplayName;
}
