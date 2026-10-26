using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed record StorageSyncResult(
    bool Success,
    string Message,
    int Categories,
    int Documents,
    string? BackupPath
);

public sealed class StorageCoordinator
{
    private readonly StorageConfigStore _configStore = new();
    private readonly ConcurrentDictionary<string, IDocumentBackend> _backends = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _remoteLocks = new(
        StringComparer.OrdinalIgnoreCase
    );
    private StorageConfig _config;

    public StorageCoordinator()
    {
        _config = _configStore.Load();
    }

    public event EventHandler<string>? StatusChanged;

    public StorageConfig Config => _config;

    public static StorageCoordinator Current { get; } = new();

    public StorageDocument? LoadDocument(string category)
    {
        try
        {
            return LocalBackend.ReadAsync(category).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "读取本地存储文档失败 {Category}", category);
            return null;
        }
    }

    public SaveResult SaveDocument(string category, string payload)
    {
        try
        {
            var current = LocalBackend.ReadAsync(category).GetAwaiter().GetResult();
            LocalBackend
                .WriteAsync(category, payload, (current?.Revision ?? 0) + 1)
                .GetAwaiter()
                .GetResult();
            QueueRemote(category, payload);
            return SaveResult.Saved;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "写入本地存储文档失败 {Category}", category);
            return SaveResult.Failed;
        }
    }

    public async Task<StorageTestResult> TestAsync(
        string profileId,
        CancellationToken cancellationToken = default
    )
    {
        var profile = FindProfile(profileId);
        return await TestAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageTestResult> TestAsync(
        StorageProfile profile,
        CancellationToken cancellationToken = default
    ) =>
        await DocumentBackendFactory
            .Create(profile)
            .TestAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<StorageSyncResult> SyncAsync(
        string sourceProfileId,
        string targetProfileId,
        IReadOnlyList<string> categories,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedCategories = NormalizeCategories(categories);
        var source = FindProfile(sourceProfileId);
        var target = FindProfile(targetProfileId);
        if (source.Id == target.Id)
        {
            return new StorageSyncResult(false, "源库和目标库相同，无需同步。", 0, 0, null);
        }

        string? backup = null;
        if (target.IsLocal)
        {
            backup = BackupLocalDatabase();
        }

        var sourceBackend = Backend(source);
        var targetBackend = Backend(target);
        var copied = 0;
        var nextRevision = 1;
        foreach (var category in normalizedCategories)
        {
            var document = await sourceBackend
                .ReadAsync(category, cancellationToken)
                .ConfigureAwait(false);
            if (document is null)
            {
                continue;
            }

            await targetBackend
                .WriteAsync(category, document.Payload, nextRevision++, cancellationToken)
                .ConfigureAwait(false);
            copied++;
        }

        return new StorageSyncResult(
            true,
            copied == 0
                ? "源库中没有可同步的数据。"
                : $"已从「{source.DisplayName}」同步 {copied} 类数据到「{target.DisplayName}」。",
            normalizedCategories.Count,
            copied,
            backup
        );
    }

    /// <summary>
    /// Copies data from the current binding to the target and then switches every category to that profile.
    /// Local data is backed up first; the caller reloads the window after the switch.
    /// </summary>
    public async Task<StorageSyncResult> SwitchAsync(
        string targetProfileId,
        IReadOnlyList<string> categories,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedCategories = NormalizeCategories(categories);
        var target = FindProfile(targetProfileId);
        var backup = BackupLocalDatabase();
        var copied = 0;
        var revision = 1;

        foreach (var category in normalizedCategories)
        {
            var source = _config.ProfileFor(category);
            var sourceDocument = await Backend(source)
                .ReadAsync(category, cancellationToken)
                .ConfigureAwait(false);
            if (sourceDocument is null)
            {
                continue;
            }

            await Backend(target)
                .WriteAsync(category, sourceDocument.Payload, revision++, cancellationToken)
                .ConfigureAwait(false);
            // Local SQLite is the offline cache even when the selected profile is remote.
            await LocalBackend
                .WriteAsync(category, sourceDocument.Payload, revision++, cancellationToken)
                .ConfigureAwait(false);

            copied++;
        }

        var next = _config.Clone();
        foreach (var category in normalizedCategories)
        {
            next.SetBinding(category, target.Id);
        }

        if (_configStore.Save(next) != SaveResult.Saved)
        {
            return new StorageSyncResult(false, "切换失败：无法保存存储配置。", 0, copied, backup);
        }

        _config = next.Normalize();
        StatusChanged?.Invoke(this, $"已切换到「{target.DisplayName}」");
        return new StorageSyncResult(
            true,
            $"已切换到「{target.DisplayName}」；重启 PostSharp 后使用新存储。",
            normalizedCategories.Count,
            copied,
            backup
        );
    }

    public SaveResult SaveConfig(StorageConfig config)
    {
        var saved = _configStore.Save(config);
        if (saved == SaveResult.Saved)
        {
            _config = config.Normalize();
            _backends.Clear();
        }

        return saved;
    }

    /// <summary>Registers an edited profile without changing any category binding.</summary>
    public SaveResult RegisterProfile(StorageProfile profile)
    {
        var next = _config.Clone();
        var existing = next.Profiles.FirstOrDefault(candidate => candidate.Id == profile.Id);
        if (existing is null)
        {
            next.Profiles.Add(profile.Clone());
        }
        else
        {
            existing.Name = profile.Name;
            existing.Provider = profile.Provider;
            existing.ConnectionString = profile.ConnectionString;
        }

        return SaveConfig(next);
    }

    public StorageProfile FindProfile(string profileId) =>
        _config.Profiles.FirstOrDefault(profile => profile.Id == profileId)
        ?? throw new KeyNotFoundException($"没有找到存储配置 {profileId}。");

    public string? BackupLocalDatabase()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.BackupDirectory);
            using (
                var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = AppPaths.DatabaseFile,
                        Mode = SqliteOpenMode.ReadWriteCreate,
                        Cache = SqliteCacheMode.Shared,
                    }.ToString()
                )
            )
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(FULL);";
                command.ExecuteNonQuery();
            }

            var backup = AppPaths.CreateBackupPath("before-switch");
            File.Copy(AppPaths.DatabaseFile, backup, overwrite: true);
            return backup;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "备份切换前数据库失败");
            return null;
        }
    }

    private IDocumentBackend LocalBackend => Backend(_config.LocalProfile);

    private IDocumentBackend Backend(StorageProfile profile) =>
        _backends.GetOrAdd(profile.Id, _ => DocumentBackendFactory.Create(profile));

    private void QueueRemote(string category, string payload)
    {
        var profile = _config.ProfileFor(category);
        if (profile.IsLocal)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var gate = _remoteLocks.GetOrAdd(category, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var backend = Backend(profile);
                var current = await backend.ReadAsync(category).ConfigureAwait(false);
                await backend
                    .WriteAsync(category, payload, (current?.Revision ?? 0) + 1)
                    .ConfigureAwait(false);
                StatusChanged?.Invoke(
                    this,
                    $"{StorageCategories.Label(category)}已同步到「{profile.DisplayName}」"
                );
            }
            catch (Exception exception)
            {
                Log.Warning(
                    exception,
                    "远程存储同步失败 {Category} {Profile} {Provider}",
                    category,
                    profile.Name,
                    profile.Provider
                );
                StatusChanged?.Invoke(
                    this,
                    $"{StorageCategories.Label(category)}同步失败：{exception.Message}"
                );
            }
            finally
            {
                gate.Release();
            }
        });
    }

    private static IReadOnlyList<string> NormalizeCategories(IReadOnlyList<string> categories)
    {
        var result = categories
            .Where(category =>
                StorageCategories.All.Contains(category, StringComparer.OrdinalIgnoreCase)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return result.Count == 0 ? StorageCategories.All : result;
    }
}
