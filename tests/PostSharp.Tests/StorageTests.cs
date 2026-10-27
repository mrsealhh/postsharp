using System.Text.Json;
using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task SqliteDocumentBackendRoundTripsDocuments()
    {
        var database = Path.Combine(
            Path.GetTempPath(),
            $"postsharp-documents-{Guid.NewGuid():N}.db"
        );

        try
        {
            var backend = new SqliteDocumentBackend(
                new StorageProfile
                {
                    Id = "test",
                    Name = "测试 SQLite",
                    Provider = StorageProviders.LocalSqlite,
                    ConnectionString = database,
                }
            );

            var test = await backend.TestAsync();
            Assert.True(test.Success, test.Message);
            await backend.WriteAsync(StorageCategories.Workspace, "{\"value\":1}", 1);
            await backend.WriteAsync(StorageCategories.Suites, "[]", 2);

            var workspace = await backend.ReadAsync(StorageCategories.Workspace);
            Assert.NotNull(workspace);
            Assert.Equal("{\"value\":1}", workspace.Payload);
            Assert.Equal(1, workspace.Revision);

            var all = await backend.ReadAllAsync();
            Assert.Equal(2, all.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public void StorageProfileDoesNotSerializeRemoteSecrets()
    {
        var profile = new StorageProfile
        {
            Id = "remote",
            Name = "远程",
            Provider = StorageProviders.PostgreSql,
            ConnectionString = "Host=db;Username=postsharp;Password=super-secret",
        };

        var json = JsonSerializer.Serialize(profile);

        Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
        var roundTripped = JsonSerializer.Deserialize<StorageProfile>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(profile.ConnectionString, roundTripped.ConnectionString);
    }

    [Fact]
    public async Task ConnectionTestDoesNotCreateTheDocumentTable()
    {
        var database = Path.Combine(Path.GetTempPath(), $"postsharp-probe-{Guid.NewGuid():N}.db");

        try
        {
            var backend = new SqliteDocumentBackend(
                new StorageProfile
                {
                    Id = "probe",
                    Name = "测试 SQLite",
                    Provider = StorageProviders.LocalSqlite,
                    ConnectionString = database,
                }
            );

            var result = await backend.TestAsync();
            Assert.True(result.Success, result.Message);

            await using (
                var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    $"Data Source={database}"
                )
            )
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'PostSharpDocuments';";
                Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }

            await backend.WriteAsync(StorageCategories.Workspace, "{}", 1);
            await using (
                var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                    $"Data Source={database}"
                )
            )
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'PostSharpDocuments';";
                Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [Fact]
    public void MissingStoragePluginIsReportedInsteadOfSilentlyWriting()
    {
        var profile = new StorageProfile
        {
            Id = "missing",
            Name = "未安装",
            Provider = "MissingProvider",
            ConnectionString = "whatever",
        };

        var exception = Assert.Throws<StoragePluginMissingException>(() =>
            DocumentBackendFactory.Create(profile)
        );

        Assert.Contains("MissingProvider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("plugins", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultConfigBindsEveryCategoryToLocalSqlite()
    {
        var config = StorageConfig.CreateDefault().Normalize();

        Assert.Contains(
            config.Profiles,
            profile => profile.Id == StorageConfig.LocalProfileId && profile.IsLocal
        );
        foreach (var category in StorageCategories.All)
        {
            Assert.Equal(StorageConfig.LocalProfileId, config.Bindings[category]);
        }
    }

    [Fact]
    public void BlockedRemoteCategorySurvivesCloneAndLocalBindingClearsIt()
    {
        var config = StorageConfig.CreateDefault();
        var remote = new StorageProfile
        {
            Id = "remote",
            Name = "远程",
            Provider = StorageProviders.PostgreSql,
            ConnectionString = "Host=127.0.0.1;Database=postsharp",
        };
        config.Profiles.Add(remote);
        config.SetBinding(StorageCategories.Workspace, remote.Id);
        config.SetRemoteBlocked(StorageCategories.Workspace, true);

        var clone = config.Clone();
        Assert.True(clone.IsRemoteBlocked(StorageCategories.Workspace));

        clone.SetBinding(StorageCategories.Workspace, StorageConfig.LocalProfileId);
        clone.Normalize();
        Assert.False(clone.IsRemoteBlocked(StorageCategories.Workspace));
    }
}
