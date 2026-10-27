using System.Data.Common;
using Microsoft.Data.Sqlite;
using PostSharp.Models;

namespace PostSharp.Services;

public static class DocumentBackendFactory
{
    /// <summary>
    /// Local SQLite is always available and is the offline fallback. Remote providers come from storage
    /// plugins installed under the plugin directory; a missing plugin produces a user-facing hint instead of
    /// silently falling back, so a category never writes to an unintended database.
    /// </summary>
    public static IDocumentBackend Create(StorageProfile profile)
    {
        if (profile.IsLocal)
        {
            return new SqliteDocumentBackend(profile);
        }

        var plugin =
            StoragePluginRegistry.Current.Find(profile.Provider)
            ?? throw new StoragePluginMissingException(profile.Provider);
        return plugin.Create(
            new StorageBackendOptions(
                profile.Provider,
                profile.ProviderLabel,
                profile.Name,
                profile.ConnectionString
            )
        );
    }
}

public sealed class SqliteDocumentBackend : SqlDocumentBackend
{
    public SqliteDocumentBackend(StorageProfile profile)
        : base(
            new StorageBackendOptions(
                StorageProviders.LocalSqlite,
                "本地 SQLite",
                profile.Name,
                profile.ConnectionString
            )
        ) { }

    protected override DbConnection CreateConnection()
    {
        var path = string.IsNullOrWhiteSpace(Options.ConnectionString)
            ? AppPaths.DatabaseFile
            : Options.ConnectionString;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString()
        );
    }

    protected override string CreateTableSql =>
        """
            CREATE TABLE IF NOT EXISTS PostSharpDocuments (
                Category TEXT PRIMARY KEY,
                Payload TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;

    protected override string UpsertSql =>
        """
            INSERT INTO PostSharpDocuments (Category, Payload, Revision, UpdatedAt)
            VALUES (@category, @payload, @revision, @updatedAt)
            ON CONFLICT(Category) DO UPDATE SET
                Payload = excluded.Payload,
                Revision = excluded.Revision,
                UpdatedAt = excluded.UpdatedAt;
            """;
}
