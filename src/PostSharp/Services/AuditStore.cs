using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Audit records live in SQLite so they can grow independently of the small workspace and settings JSON files.
/// Existing audit.json / audit-trash.json files are imported once when the database is first created.
/// </summary>
public sealed class AuditStore
{
    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object InitializationLock = new();
    private static readonly HashSet<string> Initialized = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _importLegacy;

    public string DatabasePath { get; }

    public AuditStore()
        : this(AppPaths.DatabaseFile, importLegacy: true) { }

    public AuditStore(string databasePath, bool importLegacy = false)
    {
        AppPaths.EnsureRoot();
        DatabasePath = databasePath;
        _importLegacy = importLegacy;
    }

    public DataFileLoad<List<AuditEntry>> Load()
    {
        try
        {
            EnsureInitialized();
            using var connection = Open();
            return new DataFileLoad<List<AuditEntry>>(
                ReadEntries(connection, isTrashed: false),
                DataFileState.Loaded,
                null
            );
        }
        catch (Exception exception)
        {
            Log.Error(exception, "读取审计数据库失败 {DatabaseFile}", DatabasePath);
            return new DataFileLoad<List<AuditEntry>>(null, DataFileState.Unreadable, null);
        }
    }

    public DataFileLoad<List<AuditEntry>> LoadTrash()
    {
        try
        {
            EnsureInitialized();
            using var connection = Open();
            return new DataFileLoad<List<AuditEntry>>(
                ReadEntries(connection, isTrashed: true),
                DataFileState.Loaded,
                null
            );
        }
        catch (Exception exception)
        {
            Log.Error(exception, "读取审计回收站失败 {DatabaseFile}", DatabasePath);
            return new DataFileLoad<List<AuditEntry>>(null, DataFileState.Unreadable, null);
        }
    }

    /// <summary>Never throws: auditing must not break a request.</summary>
    public SaveResult Save(IEnumerable<AuditEntry> entries) =>
        SaveEntries(entries, isTrashed: false);

    public SaveResult SaveTrash(IEnumerable<AuditEntry> entries) =>
        SaveEntries(entries, isTrashed: true);

    /// <summary>SQLite transactions are atomic; there are no JSON backups to delete.</summary>
    public IReadOnlyList<string> DeleteBackups() => [];

    private SaveResult SaveEntries(IEnumerable<AuditEntry> entries, bool isTrashed)
    {
        try
        {
            EnsureInitialized();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM AuditEntries WHERE IsTrashed = $trashed;";
                delete.Parameters.AddWithValue("$trashed", isTrashed ? 1 : 0);
                delete.ExecuteNonQuery();
            }

            foreach (var entry in entries)
            {
                Insert(connection, transaction, entry, isTrashed);
            }

            transaction.Commit();
            return SaveResult.Saved;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "写入审计数据库失败 {DatabaseFile}", DatabasePath);
            return SaveResult.Failed;
        }
    }

    private void EnsureInitialized()
    {
        var key = Path.GetFullPath(DatabasePath);
        lock (InitializationLock)
        {
            if (Initialized.Contains(key))
            {
                return;
            }

            using var connection = Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS AuditEntries (
                        Id TEXT PRIMARY KEY,
                        Timestamp TEXT NOT NULL,
                        Source TEXT NOT NULL,
                        Tool TEXT,
                        Client TEXT,
                        RequestId TEXT,
                        RequestName TEXT NOT NULL,
                        Method TEXT NOT NULL,
                        Url TEXT NOT NULL,
                        Environment TEXT,
                        StatusCode INTEGER NOT NULL,
                        StatusText TEXT NOT NULL,
                        ElapsedMs INTEGER NOT NULL,
                        SizeBytes INTEGER NOT NULL,
                        IsSuccess INTEGER NOT NULL,
                        Error TEXT,
                        Curl TEXT,
                        RequestHeaders TEXT NOT NULL,
                        RequestBodyPreview TEXT NOT NULL,
                        ResponseHeaders TEXT NOT NULL,
                        ResponseBodyPreview TEXT NOT NULL,
                        ResponseContentType TEXT NOT NULL,
                        IsSse INTEGER NOT NULL,
                        SseEventCount INTEGER NOT NULL,
                        AiThinking TEXT NOT NULL,
                        AiContent TEXT NOT NULL,
                        IsTrashed INTEGER NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS IX_AuditEntries_Trash_Timestamp
                        ON AuditEntries (IsTrashed, Timestamp DESC);
                    CREATE TABLE IF NOT EXISTS Meta (
                        Key TEXT PRIMARY KEY,
                        Value TEXT NOT NULL
                    );
                    """;
                command.ExecuteNonQuery();
            }

            if (_importLegacy)
            {
                MigrateLegacyJson(connection);
            }

            Initialized.Add(key);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString()
        );
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText =
            "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void MigrateLegacyJson(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT Value FROM Meta WHERE Key = 'legacy-audit-imported';";
            if (check.ExecuteScalar() is not null)
            {
                return;
            }
        }

        var audit = ReadLegacy(AppPaths.AuditFile);
        var trash = ReadLegacy(AppPaths.AuditTrashFile);
        if (audit.Count > 0 || trash.Count > 0)
        {
            using var transaction = connection.BeginTransaction();
            foreach (var entry in audit)
            {
                Insert(connection, transaction, entry, isTrashed: false);
            }

            foreach (var entry in trash)
            {
                Insert(connection, transaction, entry, isTrashed: true);
            }

            transaction.Commit();
            Log.Information(
                "已将旧审计 JSON 导入 SQLite {AuditCount} {TrashCount}",
                audit.Count,
                trash.Count
            );
        }

        using var mark = connection.CreateCommand();
        mark.CommandText =
            "INSERT INTO Meta (Key, Value) VALUES ('legacy-audit-imported', '1') "
            + "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";
        mark.ExecuteNonQuery();
    }

    private static List<AuditEntry> ReadLegacy(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<AuditEntry>>(
                    File.ReadAllText(path),
                    LegacyJsonOptions
                ) ?? [];
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "导入旧审计 JSON 失败 {AuditFile}", path);
            return [];
        }
    }

    private static List<AuditEntry> ReadEntries(SqliteConnection connection, bool isTrashed)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT * FROM AuditEntries WHERE IsTrashed = $trashed ORDER BY Timestamp DESC;";
        command.Parameters.AddWithValue("$trashed", isTrashed ? 1 : 0);
        using var reader = command.ExecuteReader();
        var entries = new List<AuditEntry>();
        while (reader.Read())
        {
            entries.Add(ReadEntry(reader));
        }

        return entries;
    }

    private static AuditEntry ReadEntry(SqliteDataReader reader)
    {
        var timestamp = DateTimeOffset.Parse(
            reader.GetString(reader.GetOrdinal("Timestamp")),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        );
        var requestId =
            reader.IsDBNull(reader.GetOrdinal("RequestId")) ? (Guid?)null
            : Guid.TryParse(reader.GetString(reader.GetOrdinal("RequestId")), out var id) ? id
            : null;

        return new AuditEntry
        {
            Id = Guid.TryParse(reader.GetString(reader.GetOrdinal("Id")), out var entryId)
                ? entryId
                : Guid.NewGuid(),
            Timestamp = timestamp,
            Source = reader.GetString(reader.GetOrdinal("Source")),
            Tool = GetNullableString(reader, "Tool"),
            Client = GetNullableString(reader, "Client"),
            RequestId = requestId,
            RequestName = reader.GetString(reader.GetOrdinal("RequestName")),
            Method = reader.GetString(reader.GetOrdinal("Method")),
            Url = reader.GetString(reader.GetOrdinal("Url")),
            Environment = GetNullableString(reader, "Environment"),
            StatusCode = reader.GetInt32(reader.GetOrdinal("StatusCode")),
            StatusText = reader.GetString(reader.GetOrdinal("StatusText")),
            ElapsedMs = reader.GetInt64(reader.GetOrdinal("ElapsedMs")),
            SizeBytes = reader.GetInt64(reader.GetOrdinal("SizeBytes")),
            IsSuccess = reader.GetInt32(reader.GetOrdinal("IsSuccess")) != 0,
            Error = GetNullableString(reader, "Error"),
            Curl = AuditEntry.LimitCurl(GetNullableString(reader, "Curl")),
            RequestHeaders = reader.GetString(reader.GetOrdinal("RequestHeaders")),
            RequestBodyPreview = reader.GetString(reader.GetOrdinal("RequestBodyPreview")),
            ResponseHeaders = reader.GetString(reader.GetOrdinal("ResponseHeaders")),
            ResponseBodyPreview = reader.GetString(reader.GetOrdinal("ResponseBodyPreview")),
            ResponseContentType = reader.GetString(reader.GetOrdinal("ResponseContentType")),
            IsSse = reader.GetInt32(reader.GetOrdinal("IsSse")) != 0,
            SseEventCount = reader.GetInt32(reader.GetOrdinal("SseEventCount")),
            AiThinking = AuditEntry.LimitAiText(reader.GetString(reader.GetOrdinal("AiThinking"))),
            AiContent = AuditEntry.LimitAiText(reader.GetString(reader.GetOrdinal("AiContent"))),
        };
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static void Insert(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuditEntry entry,
        bool isTrashed
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AuditEntries (
                Id, Timestamp, Source, Tool, Client, RequestId, RequestName, Method, Url,
                Environment, StatusCode, StatusText, ElapsedMs, SizeBytes, IsSuccess, Error,
                Curl, RequestHeaders, RequestBodyPreview, ResponseHeaders, ResponseBodyPreview,
                ResponseContentType, IsSse, SseEventCount, AiThinking, AiContent, IsTrashed
            ) VALUES (
                $id, $timestamp, $source, $tool, $client, $requestId, $requestName, $method, $url,
                $environment, $statusCode, $statusText, $elapsedMs, $sizeBytes, $isSuccess, $error,
                $curl, $requestHeaders, $requestBodyPreview, $responseHeaders, $responseBodyPreview,
                $responseContentType, $isSse, $sseEventCount, $aiThinking, $aiContent, $isTrashed
            )
            ON CONFLICT(Id) DO UPDATE SET
                Timestamp = excluded.Timestamp,
                Source = excluded.Source,
                Tool = excluded.Tool,
                Client = excluded.Client,
                RequestId = excluded.RequestId,
                RequestName = excluded.RequestName,
                Method = excluded.Method,
                Url = excluded.Url,
                Environment = excluded.Environment,
                StatusCode = excluded.StatusCode,
                StatusText = excluded.StatusText,
                ElapsedMs = excluded.ElapsedMs,
                SizeBytes = excluded.SizeBytes,
                IsSuccess = excluded.IsSuccess,
                Error = excluded.Error,
                Curl = excluded.Curl,
                RequestHeaders = excluded.RequestHeaders,
                RequestBodyPreview = excluded.RequestBodyPreview,
                ResponseHeaders = excluded.ResponseHeaders,
                ResponseBodyPreview = excluded.ResponseBodyPreview,
                ResponseContentType = excluded.ResponseContentType,
                IsSse = excluded.IsSse,
                SseEventCount = excluded.SseEventCount,
                AiThinking = excluded.AiThinking,
                AiContent = excluded.AiContent,
                IsTrashed = excluded.IsTrashed;
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue(
            "$timestamp",
            entry.Timestamp.ToString("O", CultureInfo.InvariantCulture)
        );
        command.Parameters.AddWithValue("$source", entry.Source);
        command.Parameters.AddWithValue("$tool", (object?)entry.Tool ?? DBNull.Value);
        command.Parameters.AddWithValue("$client", (object?)entry.Client ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$requestId",
            entry.RequestId is { } requestId ? requestId.ToString("D") : DBNull.Value
        );
        command.Parameters.AddWithValue("$requestName", entry.RequestName);
        command.Parameters.AddWithValue("$method", entry.Method);
        command.Parameters.AddWithValue("$url", entry.Url);
        command.Parameters.AddWithValue("$environment", (object?)entry.Environment ?? DBNull.Value);
        command.Parameters.AddWithValue("$statusCode", entry.StatusCode);
        command.Parameters.AddWithValue("$statusText", entry.StatusText);
        command.Parameters.AddWithValue("$elapsedMs", entry.ElapsedMs);
        command.Parameters.AddWithValue("$sizeBytes", entry.SizeBytes);
        command.Parameters.AddWithValue("$isSuccess", entry.IsSuccess ? 1 : 0);
        command.Parameters.AddWithValue("$error", (object?)entry.Error ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$curl",
            (object?)AuditEntry.LimitCurl(entry.Curl) ?? DBNull.Value
        );
        command.Parameters.AddWithValue("$requestHeaders", entry.RequestHeaders);
        command.Parameters.AddWithValue("$requestBodyPreview", entry.RequestBodyPreview);
        command.Parameters.AddWithValue("$responseHeaders", entry.ResponseHeaders);
        command.Parameters.AddWithValue("$responseBodyPreview", entry.ResponseBodyPreview);
        command.Parameters.AddWithValue("$responseContentType", entry.ResponseContentType);
        command.Parameters.AddWithValue("$isSse", entry.IsSse ? 1 : 0);
        command.Parameters.AddWithValue("$sseEventCount", entry.SseEventCount);
        command.Parameters.AddWithValue("$aiThinking", AuditEntry.LimitAiText(entry.AiThinking));
        command.Parameters.AddWithValue("$aiContent", AuditEntry.LimitAiText(entry.AiContent));
        command.Parameters.AddWithValue("$isTrashed", isTrashed ? 1 : 0);
        command.ExecuteNonQuery();
    }
}
