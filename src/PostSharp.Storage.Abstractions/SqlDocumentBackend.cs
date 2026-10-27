using System.Data.Common;
using System.Diagnostics;

namespace PostSharp.Storage.Abstractions;

/// <summary>
/// ADO.NET document backend shared by the local SQLite fallback and the SQL plugins. The category column
/// is the primary key; every write replaces the whole JSON payload and bumps the revision.
/// </summary>
public abstract class SqlDocumentBackend : IDocumentBackend
{
    protected SqlDocumentBackend(StorageBackendOptions options) => Options = options;

    public StorageBackendOptions Options { get; }

    protected abstract DbConnection CreateConnection();

    protected abstract string CreateTableSql { get; }

    protected abstract string UpsertSql { get; }

    public async Task<StorageTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return new StorageTestResult(
                true,
                $"连接成功（{Options.ProviderDisplayName}）",
                watch.ElapsedMilliseconds
            );
        }
        catch (Exception exception)
        {
            return new StorageTestResult(
                false,
                $"连接失败：{Describe(exception)}",
                watch.ElapsedMilliseconds
            );
        }
    }

    private static string Describe(Exception exception)
    {
        var line = exception.Message.Split('\n')[0].Trim();
        return line.Length > 240 ? line[..240] + "…" : line;
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
