using System.Data.Common;
using Npgsql;
using PostSharp.Storage.Abstractions;

namespace PostSharp.Storage.PostgreSql;

public sealed class PostgreSqlStoragePlugin : IStorageProviderPlugin
{
    public StorageProviderInfo Info { get; } =
        new(
            "PostgreSql",
            "PostgreSQL",
            "远程 PostgreSQL 文档库，连接串使用 Npgsql 格式。",
            "Host=127.0.0.1;Port=5432;Database=postsharp;Username=postsharp;Password=***",
            "1.0.0",
            "PostSharp"
        );

    public IDocumentBackend Create(StorageBackendOptions options) => new Backend(options);

    private sealed class Backend(StorageBackendOptions options) : SqlDocumentBackend(options)
    {
        protected override DbConnection CreateConnection() =>
            new NpgsqlConnection(Options.ConnectionString);

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
}
