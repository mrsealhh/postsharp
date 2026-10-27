namespace PostSharp.Storage.Abstractions;

/// <summary>Configuration handed to a storage plugin. The connection string is already decrypted.</summary>
public sealed record StorageBackendOptions(
    string ProviderId,
    string ProviderDisplayName,
    string Name,
    string ConnectionString
);

public sealed record StorageDocument(
    string Category,
    string Payload,
    long Revision,
    DateTimeOffset UpdatedAt
);

public sealed record StorageTestResult(bool Success, string Message, long ElapsedMs);

/// <summary>A document table shared by the local SQLite fallback and every installed storage plugin.</summary>
public interface IDocumentBackend
{
    Task<StorageTestResult> TestAsync(CancellationToken cancellationToken = default);

    Task<StorageDocument?> ReadAsync(
        string category,
        CancellationToken cancellationToken = default
    );

    Task<IReadOnlyList<StorageDocument>> ReadAllAsync(
        CancellationToken cancellationToken = default
    );

    Task WriteAsync(
        string category,
        string payload,
        long revision,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Metadata shown in the settings window before a plugin is selected.</summary>
public sealed record StorageProviderInfo(
    string Id,
    string DisplayName,
    string Description,
    string ConnectionStringPlaceholder,
    string Version,
    string Author
);

/// <summary>
/// Entry point exported by every remote storage plugin. Implementations need a public parameterless
/// constructor and must live in an assembly listed by the plugin's plugin.json.
/// </summary>
public interface IStorageProviderPlugin
{
    StorageProviderInfo Info { get; }

    IDocumentBackend Create(StorageBackendOptions options);
}
