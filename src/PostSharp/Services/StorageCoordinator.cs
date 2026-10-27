using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly ConcurrentDictionary<string, Channel<string>> _remoteChannels = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<string, bool> _remoteReady = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<string, bool> _remoteBlocked = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly SemaphoreSlim _validationGate = new(1, 1);
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
            var local = LocalBackend.ReadAsync(category).GetAwaiter().GetResult();
            if (local is not null)
            {
                var localProfile = _config.ProfileFor(category);
                if (localProfile.IsLocal)
                {
                    _remoteReady[category] = true;
                    _remoteBlocked.TryRemove(category, out _);
                    return local;
                }

                // A local cache never proves that the remote database is reachable. Writes stay local until a
                // real connection test succeeds, so a down/unreachable database can never receive changes.
                _remoteReady.TryRemove(category, out _);
                if (_config.IsRemoteBlocked(category))
                {
                    _remoteBlocked[category] = true;
                }
                else
                {
                    _ = ValidateRemoteCategoryAsync(category, localProfile);
                }

                return local;
            }

            var profile = _config.ProfileFor(category);
            if (profile.IsLocal)
            {
                _remoteReady[category] = true;
                return null;
            }

            // A remote binding with no local cache must be read before the app saves seed/default data. Otherwise
            // a fresh checkout with an existing storage.json could overwrite the remote database with defaults.
            try
            {
                var remoteTask = Task.Run(() => Backend(profile).ReadAsync(category));
                if (!remoteTask.Wait(TimeSpan.FromSeconds(3)))
                {
                    _ = remoteTask.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
                    SetRemoteBlocked(category, true);
                    _remoteReady.TryRemove(category, out _);
                    Log.Warning("远程存储读取超时，已暂停自动回写 {Category}", category);
                    return null;
                }

                var remote = remoteTask.GetAwaiter().GetResult();
                if (remote is not null)
                {
                    LocalBackend
                        .WriteAsync(category, remote.Payload, remote.Revision + 1)
                        .GetAwaiter()
                        .GetResult();
                }

                _remoteReady[category] = true;
                _remoteBlocked.TryRemove(category, out _);
                SetRemoteBlocked(category, false);
                return remote;
            }
            catch (StoragePluginMissingException exception)
            {
                SetRemoteBlocked(category, true);
                _remoteReady.TryRemove(category, out _);
                Log.Warning(
                    exception,
                    "远程存储插件缺失，已暂停自动回写 {Category} {Provider}",
                    category,
                    profile.Provider
                );
                StatusChanged?.Invoke(
                    this,
                    $"{StorageCategories.Label(category)}远程存储插件未安装，已暂停自动回写"
                );
                return null;
            }
            catch (Exception exception)
            {
                SetRemoteBlocked(category, true);
                _remoteReady.TryRemove(category, out _);
                Log.Warning(
                    exception,
                    "远程存储不可用，已暂停自动回写 {Category} {Profile}",
                    category,
                    profile.Name
                );
                StatusChanged?.Invoke(
                    this,
                    $"{StorageCategories.Label(category)}远程存储不可用，已暂停自动回写"
                );
                return null;
            }
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
            if (_remoteReady.ContainsKey(category) && !_remoteBlocked.ContainsKey(category))
            {
                QueueRemote(category, payload);
            }

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
    )
    {
        StorageTestResult result;
        try
        {
            result = await DocumentBackendFactory
                .Create(profile)
                .TestAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoragePluginMissingException exception)
        {
            result = new StorageTestResult(false, exception.Message, 0);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "创建存储后端失败 {Profile}", profile.Name);
            result = new StorageTestResult(false, $"连接失败：{exception.Message}", 0);
        }

        ApplyProfileAvailability(profile, result.Success, result.Message);
        return result;
    }

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
            backup = await Task.Run(BackupLocalDatabase, cancellationToken).ConfigureAwait(false);
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
            if (target.IsRemote)
            {
                _remoteReady[category] = true;
                _remoteBlocked.TryRemove(category, out _);
                SetRemoteBlocked(category, false);
            }

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
        var backup = await Task.Run(BackupLocalDatabase, cancellationToken).ConfigureAwait(false);
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
            if (target.IsRemote)
            {
                _remoteReady[category] = true;
                _remoteBlocked.TryRemove(category, out _);
                SetRemoteBlocked(category, false);
            }

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
            next.SetRemoteBlocked(category, false);
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

    private async Task ValidateRemoteCategoryAsync(string category, StorageProfile profile)
    {
        try
        {
            await _validationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_remoteReady.ContainsKey(category))
                {
                    return;
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await DocumentBackendFactory
                    .Create(profile)
                    .TestAsync(timeout.Token)
                    .ConfigureAwait(false);
                ApplyProfileAvailability(profile, result.Success, result.Message);
            }
            finally
            {
                _validationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            ApplyProfileAvailability(profile, false, "连接超时，已暂停远程回写。");
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "远程存储连接校验失败 {Category} {Profile}",
                category,
                profile.Name
            );
            ApplyProfileAvailability(profile, false, exception.Message);
        }
    }

    private void ApplyProfileAvailability(StorageProfile profile, bool available, string message)
    {
        if (!profile.IsRemote)
        {
            return;
        }

        var matched = 0;
        foreach (var category in StorageCategories.All)
        {
            var bound = _config.ProfileFor(category);
            if (
                !string.Equals(bound.Id, profile.Id, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(bound.Provider, profile.Provider, StringComparison.Ordinal)
                || !string.Equals(
                    bound.ConnectionString,
                    profile.ConnectionString,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            matched++;
            if (available)
            {
                _remoteReady[category] = true;
                _remoteBlocked.TryRemove(category, out _);
                SetRemoteBlocked(category, false);
            }
            else
            {
                _remoteReady.TryRemove(category, out _);
                _remoteBlocked[category] = true;
                SetRemoteBlocked(category, true);
            }
        }

        StatusChanged?.Invoke(
            this,
            matched == 0
                ? available
                    ? $"「{profile.DisplayName}」连接成功。"
                    : $"「{profile.DisplayName}」连接失败：{message}"
                : available
                    ? $"「{profile.DisplayName}」连接成功，远程回写已启用。"
                    : $"「{profile.DisplayName}」不可用，已暂停远程回写：{message}"
        );
    }

    private void SetRemoteBlocked(string category, bool blocked)
    {
        if (_config.IsRemoteBlocked(category) == blocked)
        {
            return;
        }

        _config.SetRemoteBlocked(category, blocked);
        _configStore.Save(_config);
    }

    public SaveResult SaveConfig(StorageConfig config)
    {
        var previous = _config;
        var next = config.Clone().Normalize();
        foreach (var category in StorageCategories.All)
        {
            var previousId = previous.Bindings.TryGetValue(category, out var oldId)
                ? oldId
                : StorageConfig.LocalProfileId;
            var nextProfile = next.ProfileFor(category);
            if (
                previous.IsRemoteBlocked(category)
                && string.Equals(previousId, nextProfile.Id, StringComparison.OrdinalIgnoreCase)
                && nextProfile.IsRemote
            )
            {
                next.SetRemoteBlocked(category, true);
            }
            else if (!string.Equals(previousId, nextProfile.Id, StringComparison.OrdinalIgnoreCase))
            {
                next.SetRemoteBlocked(category, false);
            }
        }

        var saved = _configStore.Save(next);
        if (saved == SaveResult.Saved)
        {
            _config = next.Normalize();
            _backends.Clear();
            foreach (var category in StorageCategories.All)
            {
                var profile = _config.ProfileFor(category);
                if (profile.IsRemote && !_config.IsRemoteBlocked(category))
                {
                    // The new binding is not trusted until the user tests it or a switch/sync succeeds.
                    _remoteReady.TryRemove(category, out _);
                    _remoteBlocked.TryRemove(category, out _);
                }
                else if (!profile.IsRemote)
                {
                    _remoteReady.TryRemove(category, out _);
                    _remoteBlocked.TryRemove(category, out _);
                }
                else
                {
                    _remoteReady.TryRemove(category, out _);
                    _remoteBlocked[category] = true;
                }
            }

            foreach (var category in StorageCategories.All)
            {
                var profile = _config.ProfileFor(category);
                if (profile.IsRemote && !_config.IsRemoteBlocked(category))
                {
                    _ = ValidateRemoteCategoryAsync(category, profile);
                }
            }
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

    /// <summary>Drops cached backends so a freshly rescanned/replaced plugin is used on the next operation.</summary>
    public void InvalidateBackends() => _backends.Clear();

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
        if (
            profile.IsLocal
            || !_remoteReady.ContainsKey(category)
            || _remoteBlocked.ContainsKey(category)
        )
        {
            return;
        }

        var channel = _remoteChannels.GetOrAdd(category, CreateRemoteChannel);
        channel.Writer.TryWrite(payload);
    }

    private Channel<string> CreateRemoteChannel(string category)
    {
        var channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }
        );
        _ = ProcessRemoteChannelAsync(category, channel.Reader);
        return channel;
    }

    private async Task ProcessRemoteChannelAsync(string category, ChannelReader<string> reader)
    {
        await foreach (var payload in reader.ReadAllAsync().ConfigureAwait(false))
        {
            var profile = _config.ProfileFor(category);
            if (
                profile.IsLocal
                || !_remoteReady.ContainsKey(category)
                || _remoteBlocked.ContainsKey(category)
            )
            {
                continue;
            }

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
                ApplyProfileAvailability(
                    profile,
                    false,
                    "写入失败，已暂停远程回写；请检查连接后在设置中测试连接。"
                );
            }
        }
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
