using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed record StorageDocument(
    string Category,
    string Payload,
    long Revision,
    DateTimeOffset UpdatedAt
);

public sealed record StorageTestResult(bool Success, string Message, long ElapsedMs);

/// <summary>A document table shared by local SQLite and all supported remote SQL databases.</summary>
public interface IDocumentBackend
{
    StorageProfile Profile { get; }

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

public static class DocumentBackendFactory
{
    public static IDocumentBackend Create(StorageProfile profile) =>
        profile.Provider switch
        {
            StorageProviders.LocalSqlite => new SqliteDocumentBackend(profile),
            StorageProviders.PostgreSql => new PostgreSqlDocumentBackend(profile),
            StorageProviders.MySql => new MySqlDocumentBackend(profile),
            StorageProviders.SqlServer => new SqlServerDocumentBackend(profile),
            _ => throw new InvalidOperationException($"不支持的存储类型：{profile.Provider}"),
        };
}

public abstract class SqlDocumentBackend : IDocumentBackend
{
    protected SqlDocumentBackend(StorageProfile profile) => Profile = profile;

    public StorageProfile Profile { get; }

    protected abstract DbConnection CreateConnection();

    protected abstract string CreateTableSql { get; }

    protected abstract string UpsertSql { get; }

    public async Task<StorageTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            return new StorageTestResult(
                true,
                $"连接成功（{Profile.ProviderLabel}）",
                watch.ElapsedMilliseconds
            );
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "存储连接测试失败 {Profile} {Provider}",
                Profile.Name,
                Profile.Provider
            );
            return new StorageTestResult(
                false,
                $"连接失败：{exception.Message}",
                watch.ElapsedMilliseconds
            );
        }
    }

    public async Task<StorageDocument?> ReadAsync(
        string category,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Payload, Revision, UpdatedAt FROM PostSharpDocuments WHERE Category = @category;";
        AddParameter(command, "@category", category);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new StorageDocument(
            category,
            reader.GetString(0),
            Convert.ToInt64(reader.GetValue(1)),
            DateTimeOffset.Parse(
                reader.GetString(2),
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
    }

    public async Task<IReadOnlyList<StorageDocument>> ReadAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Category, Payload, Revision, UpdatedAt FROM PostSharpDocuments;";
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var documents = new List<StorageDocument>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(
                new StorageDocument(
                    reader.GetString(0),
                    reader.GetString(1),
                    Convert.ToInt64(reader.GetValue(2)),
                    DateTimeOffset.Parse(
                        reader.GetString(3),
                        System.Globalization.CultureInfo.InvariantCulture
                    )
                )
            );
        }

        return documents;
    }

    public async Task WriteAsync(
        string category,
        string payload,
        long revision,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = UpsertSql;
        AddParameter(command, "@category", category);
        AddParameter(command, "@payload", payload);
        AddParameter(command, "@revision", revision);
        AddParameter(
            command,
            "@updatedAt",
            DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        );
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    protected async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    protected async Task EnsureSchemaAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = CreateTableSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    protected static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

public sealed class SqliteDocumentBackend : SqlDocumentBackend
{
    public SqliteDocumentBackend(StorageProfile profile)
        : base(profile) { }

    protected override DbConnection CreateConnection()
    {
        var path = string.IsNullOrWhiteSpace(Profile.ConnectionString)
            ? AppPaths.DatabaseFile
            : Profile.ConnectionString;
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

public sealed class PostgreSqlDocumentBackend : SqlDocumentBackend
{
    public PostgreSqlDocumentBackend(StorageProfile profile)
        : base(profile) { }

    protected override DbConnection CreateConnection() =>
        new NpgsqlConnection(Profile.ConnectionString);

    protected override string CreateTableSql =>
        """
            CREATE TABLE IF NOT EXISTS PostSharpDocuments (
                Category TEXT PRIMARY KEY,
                Payload TEXT NOT NULL,
                Revision BIGINT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;

    protected override string UpsertSql =>
        """
            INSERT INTO PostSharpDocuments (Category, Payload, Revision, UpdatedAt)
            VALUES (@category, @payload, @revision, @updatedAt)
            ON CONFLICT (Category) DO UPDATE SET
                Payload = EXCLUDED.Payload,
                Revision = EXCLUDED.Revision,
                UpdatedAt = EXCLUDED.UpdatedAt;
            """;
}

public sealed class MySqlDocumentBackend : SqlDocumentBackend
{
    public MySqlDocumentBackend(StorageProfile profile)
        : base(profile) { }

    protected override DbConnection CreateConnection() =>
        new MySqlConnection(Profile.ConnectionString);

    protected override string CreateTableSql =>
        """
            CREATE TABLE IF NOT EXISTS PostSharpDocuments (
                Category VARCHAR(64) PRIMARY KEY,
                Payload LONGTEXT NOT NULL,
                Revision BIGINT NOT NULL,
                UpdatedAt VARCHAR(40) NOT NULL
            );
            """;

    protected override string UpsertSql =>
        """
            INSERT INTO PostSharpDocuments (Category, Payload, Revision, UpdatedAt)
            VALUES (@category, @payload, @revision, @updatedAt)
            ON DUPLICATE KEY UPDATE
                Payload = VALUES(Payload),
                Revision = VALUES(Revision),
                UpdatedAt = VALUES(UpdatedAt);
            """;
}

public sealed class SqlServerDocumentBackend : SqlDocumentBackend
{
    public SqlServerDocumentBackend(StorageProfile profile)
        : base(profile) { }

    protected override DbConnection CreateConnection() =>
        new SqlConnection(Profile.ConnectionString);

    protected override string CreateTableSql =>
        """
            IF OBJECT_ID(N'PostSharpDocuments', N'U') IS NULL
            BEGIN
                CREATE TABLE PostSharpDocuments (
                    Category NVARCHAR(64) NOT NULL PRIMARY KEY,
                    Payload NVARCHAR(MAX) NOT NULL,
                    Revision BIGINT NOT NULL,
                    UpdatedAt NVARCHAR(40) NOT NULL
                );
            END;
            """;

    protected override string UpsertSql =>
        """
            MERGE PostSharpDocuments AS target
            USING (SELECT @category AS Category) AS source
            ON target.Category = source.Category
            WHEN MATCHED THEN
                UPDATE SET Payload = @payload, Revision = @revision, UpdatedAt = @updatedAt
            WHEN NOT MATCHED THEN
                INSERT (Category, Payload, Revision, UpdatedAt)
                VALUES (@category, @payload, @revision, @updatedAt);
            """;
}
