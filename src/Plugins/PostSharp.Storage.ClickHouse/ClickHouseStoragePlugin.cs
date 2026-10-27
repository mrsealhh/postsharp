using System.Diagnostics;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using PostSharp.Storage.Abstractions;

namespace PostSharp.Storage.ClickHouse;

public sealed class ClickHouseStoragePlugin : IStorageProviderPlugin
{
    public StorageProviderInfo Info { get; } =
        new(
            "ClickHouse",
            "ClickHouse",
            "ClickHouse OLAP 文档库；使用 ReplacingMergeTree，按 Category 保留最新版本。",
            "Host=127.0.0.1;Port=8123;Database=postsharp;Username=default;Password=***;Protocol=http",
            "1.0.0",
            "PostSharp"
        );

    public IDocumentBackend Create(StorageBackendOptions options) => new Backend(options);

    private sealed class Backend(StorageBackendOptions options) : IDocumentBackend
    {
        private const string TableSql = """
            CREATE TABLE IF NOT EXISTS PostSharpDocuments (
                Category String,
                Payload String,
                Revision UInt64,
                UpdatedAt DateTime64(3)
            )
            ENGINE = ReplacingMergeTree(Revision)
            ORDER BY (Category)
            """;

        public async Task<StorageTestResult> TestAsync(
            CancellationToken cancellationToken = default
        )
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await using var connection = await OpenAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return new StorageTestResult(
                    true,
                    "连接成功（ClickHouse）",
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

        public async Task<StorageDocument?> ReadAsync(
            string category,
            CancellationToken cancellationToken = default
        )
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Payload, Revision, UpdatedAt
                FROM PostSharpDocuments FINAL
                WHERE Category = {category:String}
                ORDER BY Revision DESC
                LIMIT 1
                """;
            AddParameter(command, "category", "String", category);
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
                new DateTimeOffset(reader.GetDateTime(2).ToUniversalTime(), TimeSpan.Zero)
            );
        }

        public async Task<IReadOnlyList<StorageDocument>> ReadAllAsync(
            CancellationToken cancellationToken = default
        )
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    Category,
                    argMax(Payload, Revision) AS Payload,
                    max(Revision) AS Revision,
                    argMax(UpdatedAt, Revision) AS UpdatedAt
                FROM PostSharpDocuments
                GROUP BY Category
                """;
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
                        new DateTimeOffset(reader.GetDateTime(3).ToUniversalTime(), TimeSpan.Zero)
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
            command.CommandText = """
                INSERT INTO PostSharpDocuments (Category, Payload, Revision, UpdatedAt)
                VALUES (
                    {category:String},
                    {payload:String},
                    {revision:UInt64},
                    {updatedAt:DateTime64(3)}
                )
                """;
            AddParameter(command, "category", "String", category);
            AddParameter(command, "payload", "String", payload);
            AddParameter(command, "revision", "UInt64", (ulong)Math.Max(0, revision));
            AddParameter(command, "updatedAt", "DateTime64(3)", DateTime.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<ClickHouseConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = new ClickHouseConnection(options.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        private static async Task EnsureSchemaAsync(
            ClickHouseConnection connection,
            CancellationToken cancellationToken
        )
        {
            await using var command = connection.CreateCommand();
            command.CommandText = TableSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void AddParameter(
            System.Data.Common.DbCommand command,
            string name,
            string type,
            object value
        )
        {
            ((ClickHouseCommand)command).AddParameter(name, type, value);
        }

        private static string Describe(Exception exception)
        {
            var line = exception.Message.Split('\n')[0].Trim();
            return line.Length > 240 ? line[..240] + "…" : line;
        }
    }
}
