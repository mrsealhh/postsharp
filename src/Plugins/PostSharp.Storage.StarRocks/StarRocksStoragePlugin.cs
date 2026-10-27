using System.Data.Common;
using MySqlConnector;
using PostSharp.Storage.Abstractions;

namespace PostSharp.Storage.StarRocks;

public sealed class StarRocksStoragePlugin : IStorageProviderPlugin
{
    public StorageProviderInfo Info { get; } =
        new(
            "StarRocks",
            "StarRocks",
            "StarRocks OLAP 文档库；使用 MySQL 协议与 Unique Key 表，按 Category 覆盖最新版本。",
            "Server=127.0.0.1;Port=9030;Database=postsharp;User ID=root;Password=***",
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
                    Category VARCHAR(64) NOT NULL,
                    Payload STRING NOT NULL,
                    Revision BIGINT NOT NULL,
                    UpdatedAt VARCHAR(40) NOT NULL
                )
                ENGINE = OLAP
                UNIQUE KEY(Category)
                DISTRIBUTED BY HASH(Category) BUCKETS 1
                PROPERTIES ("replication_num" = "1")
                """;

        protected override string UpsertSql =>
            """
                INSERT INTO PostSharpDocuments (Category, Payload, Revision, UpdatedAt)
                VALUES (@category, @payload, @revision, @updatedAt)
                """;
    }
}
