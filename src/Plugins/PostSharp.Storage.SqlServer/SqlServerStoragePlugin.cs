using System.Data.Common;
using Microsoft.Data.SqlClient;
using PostSharp.Storage.Abstractions;

namespace PostSharp.Storage.SqlServer;

public sealed class SqlServerStoragePlugin : IStorageProviderPlugin
{
    public StorageProviderInfo Info { get; } =
        new(
            "SqlServer",
            "SQL Server",
            "远程 SQL Server 文档库，连接串使用 Microsoft.Data.SqlClient 格式。",
            "Server=127.0.0.1,1433;Database=postsharp;User ID=postsharp;Password=***;TrustServerCertificate=True",
            "1.0.0",
            "PostSharp"
        );

    public IDocumentBackend Create(StorageBackendOptions options) => new Backend(options);

    private sealed class Backend(StorageBackendOptions options) : SqlDocumentBackend(options)
    {
        protected override DbConnection CreateConnection() =>
            new SqlConnection(Options.ConnectionString);

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
}
