using System.Data.Common;
using MySqlConnector;
using PostSharp.Storage.Abstractions;

namespace PostSharp.Storage.MySql;

public sealed class MySqlStoragePlugin : IStorageProviderPlugin
{
    public StorageProviderInfo Info { get; } =
        new(
            "MySql",
            "MySQL",
            "远程 MySQL 文档库，连接串使用 MySqlConnector 格式。",
            "Server=127.0.0.1;Port=3306;Database=postsharp;User ID=postsharp;Password=***",
            "1.0.0",
            "PostSharp"
        );

    public IDocumentBackend Create(StorageBackendOptions options) => new Backend(options);

    private sealed class Backend(StorageBackendOptions options) : SqlDocumentBackend(options)
    {
        protected override DbConnection CreateConnection() =>
            new MySqlConnection(Options.ConnectionString);

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
}
