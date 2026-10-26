using System.Text.Json.Serialization;
using PostSharp.Services;

namespace PostSharp.Models;

public static class StorageCategories
{
    public const string Settings = "settings";
    public const string Workspace = "workspace";
    public const string Suites = "suites";

    public static IReadOnlyList<string> All { get; } = [Settings, Workspace, Suites];

    public static string Label(string category) =>
        category switch
        {
            Settings => "系统配置",
            Workspace => "集合与接口记录",
            Suites => "套件自动化",
            _ => category,
        };
}

/// <summary>
/// Local bootstrap for storage profiles and category bindings. It stays small and lives in the project
/// data directory; the actual documents are stored by <see cref="StorageCoordinator" />.
/// </summary>
public sealed class StorageConfig
{
    public const string LocalProfileId = "local";

    public List<StorageProfile> Profiles { get; set; } = new();

    /// <summary>Category name (settings/workspace/suites) to profile id.</summary>
    public Dictionary<string, string> Bindings { get; set; } = new();

    /// <summary>
    /// Categories whose remote profile could not be read. Local cache changes are kept, but automatic remote
    /// writes stay paused until a successful sync/switch so a stale/seed cache cannot overwrite the remote data.
    /// </summary>
    public List<string> BlockedRemoteCategories { get; set; } = new();

    [JsonIgnore]
    public StorageProfile LocalProfile =>
        Profiles.FirstOrDefault(profile => profile.Id == LocalProfileId)
        ?? throw new InvalidOperationException("存储配置缺少本地 SQLite 配置。");

    public static StorageConfig CreateDefault() =>
        new()
        {
            Profiles =
            [
                new StorageProfile
                {
                    Id = LocalProfileId,
                    Name = "本地 SQLite",
                    Provider = StorageProviders.LocalSqlite,
                    ConnectionString = AppPaths.DatabaseFile,
                },
            ],
            Bindings = StorageCategories.All.ToDictionary(
                category => category,
                _ => LocalProfileId,
                StringComparer.OrdinalIgnoreCase
            ),
        };

    public StorageConfig Normalize()
    {
        Profiles ??= new List<StorageProfile>();
        Bindings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        BlockedRemoteCategories ??= new List<string>();

        var local = Profiles.FirstOrDefault(profile => profile.Id == LocalProfileId);
        if (local is null)
        {
            local = new StorageProfile
            {
                Id = LocalProfileId,
                Name = "本地 SQLite",
                Provider = StorageProviders.LocalSqlite,
                ConnectionString = AppPaths.DatabaseFile,
            };
            Profiles.Insert(0, local);
        }
        else
        {
            local.Provider = StorageProviders.LocalSqlite;
            local.ConnectionString = AppPaths.DatabaseFile;
        }

        foreach (var profile in Profiles.Where(profile => profile.IsRemote))
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                profile.Name = profile.ProviderLabel;
            }
        }

        foreach (var category in StorageCategories.All)
        {
            if (
                !Bindings.TryGetValue(category, out var profileId)
                || Profiles.All(profile => profile.Id != profileId)
            )
            {
                Bindings[category] = LocalProfileId;
            }
        }

        BlockedRemoteCategories = BlockedRemoteCategories
            .Where(category =>
                StorageCategories.All.Contains(category, StringComparer.OrdinalIgnoreCase)
                && ProfileFor(category).IsRemote
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return this;
    }

    public StorageProfile ProfileFor(string category) =>
        Profiles.FirstOrDefault(profile => profile.Id == Bindings[category]) ?? LocalProfile;

    public bool IsRemoteBlocked(string category) =>
        BlockedRemoteCategories.Contains(category, StringComparer.OrdinalIgnoreCase);

    public void SetRemoteBlocked(string category, bool blocked)
    {
        BlockedRemoteCategories.RemoveAll(candidate =>
            string.Equals(candidate, category, StringComparison.OrdinalIgnoreCase)
        );
        if (blocked)
        {
            BlockedRemoteCategories.Add(category);
        }
    }

    public void SetBinding(string category, string profileId)
    {
        if (Profiles.All(profile => profile.Id != profileId))
        {
            throw new KeyNotFoundException($"没有找到存储配置 {profileId}。");
        }

        Bindings[category] = profileId;
    }

    public StorageConfig Clone() =>
        new()
        {
            Profiles = Profiles.Select(profile => profile.Clone()).ToList(),
            Bindings = new Dictionary<string, string>(Bindings, StringComparer.OrdinalIgnoreCase),
            BlockedRemoteCategories = new List<string>(BlockedRemoteCategories),
        };
}
