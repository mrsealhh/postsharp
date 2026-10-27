using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using PostSharp.Storage.Abstractions;

namespace PostSharp.Storage.MongoDb;

public sealed class MongoStoragePlugin : IStorageProviderPlugin
{
    public StorageProviderInfo Info { get; } =
        new(
            "MongoDb",
            "MongoDB",
            "远程 MongoDB 文档库；连接串可带数据库名，未填写时使用 postsharp。",
            "mongodb://user:password@host:27017/postsharp?authSource=admin",
            "1.0.0",
            "PostSharp"
        );

    public IDocumentBackend Create(StorageBackendOptions options) => new Backend(options);

    private sealed class Backend : IDocumentBackend
    {
        private const string DefaultDatabase = "postsharp";
        private const string CollectionName = "PostSharpDocuments";
        private readonly Lazy<IMongoCollection<BsonDocument>> _collection;
        private readonly Lazy<IMongoClient> _client;
        private readonly StorageBackendOptions _options;

        public Backend(StorageBackendOptions options)
        {
            _options = options;
            _client = new Lazy<IMongoClient>(CreateClient);
            _collection = new Lazy<IMongoCollection<BsonDocument>>(() =>
                _client
                    .Value.GetDatabase(DatabaseName())
                    .GetCollection<BsonDocument>(CollectionName)
            );
        }

        public async Task<StorageTestResult> TestAsync(
            CancellationToken cancellationToken = default
        )
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await _client
                    .Value.GetDatabase(DatabaseName())
                    .RunCommandAsync<BsonDocument>(
                        new BsonDocument("ping", 1),
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);
                return new StorageTestResult(
                    true,
                    $"连接成功（MongoDB，数据库 {DatabaseName()}）",
                    watch.ElapsedMilliseconds
                );
            }
            catch (Exception exception)
            {
                var message = exception is MongoException or TimeoutException
                    ? "无法连接 MongoDB，请检查连接串、网络和账号权限。"
                    : $"连接失败：{Describe(exception)}";
                return new StorageTestResult(false, message, watch.ElapsedMilliseconds);
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
            var document = await _collection
                .Value.Find(Builders<BsonDocument>.Filter.Eq("_id", category))
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return document is null ? null : ToStorageDocument(document, category);
        }

        public async Task<IReadOnlyList<StorageDocument>> ReadAllAsync(
            CancellationToken cancellationToken = default
        )
        {
            var documents = await _collection
                .Value.Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return documents
                .Select(document =>
                    ToStorageDocument(document, document.GetValue("_id", string.Empty).AsString)
                )
                .ToList();
        }

        public async Task WriteAsync(
            string category,
            string payload,
            long revision,
            CancellationToken cancellationToken = default
        )
        {
            var document = new BsonDocument
            {
                { "_id", category },
                { "Payload", payload },
                { "Revision", revision },
                { "UpdatedAt", DateTime.UtcNow },
            };
            await _collection
                .Value.ReplaceOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", category),
                    document,
                    new ReplaceOptions { IsUpsert = true },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        private static StorageDocument ToStorageDocument(BsonDocument document, string category)
        {
            var payload = document.GetValue("Payload", string.Empty).AsString;
            var revision = document.GetValue("Revision", 0).ToInt64();
            var updatedAt = document.GetValue("UpdatedAt", DateTime.UtcNow);
            var timestamp =
                updatedAt.IsBsonDateTime
                    ? new DateTimeOffset(updatedAt.ToUniversalTime(), TimeSpan.Zero)
                : DateTimeOffset.TryParse(updatedAt.AsString, out var parsed) ? parsed
                : DateTimeOffset.UtcNow;
            return new StorageDocument(category, payload, revision, timestamp);
        }

        private IMongoClient CreateClient()
        {
            var url = MongoUrl.Create(_options.ConnectionString);
            var settings = MongoClientSettings.FromUrl(url);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(8);
            settings.ConnectTimeout = TimeSpan.FromSeconds(8);
            return new MongoClient(settings);
        }

        private string DatabaseName()
        {
            var url = MongoUrl.Create(_options.ConnectionString);
            return string.IsNullOrWhiteSpace(url.DatabaseName) ? DefaultDatabase : url.DatabaseName;
        }
    }
}
