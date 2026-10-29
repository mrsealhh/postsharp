using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class AuditStoreTests
{
    [Fact]
    public void PersistsAuditAndTrashSeparately()
    {
        var database = Path.Combine(Path.GetTempPath(), $"postsharp-audit-{Guid.NewGuid():N}.db");

        try
        {
            var store = new AuditStore(database);
            var audit = new AuditEntry
            {
                RequestName = "登录",
                Method = "POST",
                Url = "https://example.com/login",
                StatusCode = 200,
                StatusText = "OK",
                ResponseBodyPreview = "ok",
                Source = "MCP",
                Permission = "ReadWrite",
            };
            var trash = new AuditEntry
            {
                RequestName = "旧请求",
                Method = "GET",
                Url = "https://example.com/old",
                StatusCode = 404,
                StatusText = "Not Found",
            };

            Assert.Equal(SaveResult.Saved, store.Save([audit]));
            Assert.Equal(SaveResult.Saved, store.SaveTrash([trash]));

            var loaded = store.Load();
            var loadedTrash = store.LoadTrash();
            Assert.Equal(DataFileState.Loaded, loaded.State);
            Assert.Equal(DataFileState.Loaded, loadedTrash.State);
            Assert.Single(loaded.Value!);
            Assert.Single(loadedTrash.Value!);
            Assert.Equal("登录", loaded.Value![0].RequestName);
            Assert.Equal("ReadWrite", loaded.Value[0].Permission);
            Assert.Equal("旧请求", loadedTrash.Value![0].RequestName);
            Assert.NotEqual(loaded.Value[0].Id, loadedTrash.Value[0].Id);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }
}
