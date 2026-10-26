using System.Text.Encodings.Web;
using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath { get; }

    public WorkspaceStore()
    {
        AppPaths.EnsureRoot();
        FilePath = AppPaths.WorkspaceFile;
    }

    public DataFileLoad<WorkspaceDto> Load() =>
        DataFile.Load(
            FilePath,
            text => JsonSerializer.Deserialize<WorkspaceDto>(text, JsonOptions)
        );

    public void Save(WorkspaceDto workspace)
    {
        try
        {
            // A file protected after a failed read is skipped; the startup notice already says so.
            DataFile.WriteAllText(FilePath, JsonSerializer.Serialize(workspace, JsonOptions));
        }
        catch (Exception exception)
        {
            // A failed autosave must never break the request the person is sending.
            Log.Error(exception, "保存工作区失败 {WorkspaceFile}", FilePath);
        }
    }

    public static WorkspaceDto Capture(
        IEnumerable<RequestCollection> collections,
        IEnumerable<EnvironmentModel> environments,
        IEnumerable<HistoryEntry> history,
        IEnumerable<HistoryEntry> recycledHistory,
        IEnumerable<TestSuite> suites,
        IEnumerable<TrashedNode> trashedNodes,
        Guid? activeEnvironmentId
    )
    {
        return new WorkspaceDto
        {
            Collections = collections.Select(ToCollectionDto).ToList(),
            Environments = environments
                .Select(environment => new EnvironmentDto
                {
                    Id = environment.Id,
                    Name = environment.Name,
                    Variables = ToRows(environment.Variables),
                })
                .ToList(),
            History = history.Select(ToHistoryDto).ToList(),
            RecycledHistory = recycledHistory.Select(ToHistoryDto).ToList(),
            Suites = suites
                .Select(suite => new SuiteDto
                {
                    Id = suite.Id,
                    Name = suite.Name,
                    StopOnError = suite.StopOnError,
                    LastSummary = suite.LastSummary,
                    Steps = suite
                        .Steps.Select(step => new SuiteStepDto
                        {
                            Id = step.Id,
                            RequestId = step.RequestId,
                            RequestName = step.RequestName,
                            Method = step.Method,
                            Url = step.Url,
                            DelayMs = step.DelayMs,
                            ExpectedStatus = step.ExpectedStatus,
                            BodyContains = step.BodyContains,
                            InputMappings = step
                                .InputMappings.Select(mapping => new SuiteMappingDto
                                {
                                    SourceStepId = mapping.SourceStepId,
                                    SourcePath = mapping.SourcePath,
                                    TargetKind = mapping.TargetKind,
                                    TargetPath = mapping.TargetPath,
                                    Template = mapping.Template,
                                })
                                .ToList(),
                        })
                        .ToList(),
                })
                .ToList(),
            TrashedNodes = trashedNodes.Select(ToTrashedNodeDto).ToList(),
            ActiveEnvironmentId = activeEnvironmentId,
        };
    }

    /// <summary>Builds a collection and its requests; parent links are set by the caller.</summary>
    public static RequestCollection ToCollectionModel(CollectionDto dto)
    {
        var collection = new RequestCollection
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            Name = dto.Name,
            IsFavorite = dto.IsFavorite,
        };

        foreach (var requestDto in dto.Requests)
        {
            var request = ToModel(requestDto);
            request.Owner = collection;
            collection.Requests.Add(request);
        }

        return collection;
    }

    /// <summary>Rebuilds a recycle bin entry; returns null when the stored entry holds nothing to restore.</summary>
    public static TrashedNode? ToTrashedNode(TrashedNodeDto dto)
    {
        RequestCollection? root = null;
        if (dto.Collections.Count > 0)
        {
            var built = new List<RequestCollection>(dto.Collections.Count);
            var byId = new Dictionary<Guid, RequestCollection>();
            foreach (var collectionDto in dto.Collections)
            {
                var collection = ToCollectionModel(collectionDto);

                // Collections are stored root first in tree order, so a parent is always built before its children.
                if (
                    built.Count > 0
                    && collectionDto.ParentId is { } parentId
                    && byId.TryGetValue(parentId, out var parent)
                )
                {
                    collection.Parent = parent;
                    parent.Children.Add(collection);
                }

                byId.TryAdd(collection.Id, collection);
                built.Add(collection);
            }

            root = built[0];
        }

        var request = dto.Request is null ? null : ToModel(dto.Request);
        if (root is null && request is null)
        {
            return null;
        }

        return new TrashedNode
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            DeletedAt = dto.DeletedAt,
            Source = string.IsNullOrWhiteSpace(dto.Source) ? "UI" : dto.Source,
            Collection = root,
            Request = root is null ? request : null,
            OriginalParentId = dto.OriginalParentId,
            OriginalLocation = string.IsNullOrWhiteSpace(dto.OriginalLocation)
                ? "根目录"
                : dto.OriginalLocation,
            OriginalPosition = dto.OriginalPosition,
        };
    }

    private static CollectionDto ToCollectionDto(RequestCollection collection)
    {
        return new CollectionDto
        {
            Id = collection.Id,
            Name = collection.Name,
            IsFavorite = collection.IsFavorite,
            ParentId = collection.Parent?.Id,
            Requests = collection.Requests.Select(ToDto).ToList(),
        };
    }

    private static TrashedNodeDto ToTrashedNodeDto(TrashedNode node)
    {
        var collections = new List<CollectionDto>();
        if (node.Collection is { } root)
        {
            AddSubtree(root, collections);
        }

        return new TrashedNodeDto
        {
            Id = node.Id,
            DeletedAt = node.DeletedAt,
            Source = node.Source,
            OriginalParentId = node.OriginalParentId,
            OriginalLocation = node.OriginalLocation,
            OriginalPosition = node.OriginalPosition,
            Collections = collections,
            Request = node.Request is null ? null : ToDto(node.Request),
        };
    }

    private static void AddSubtree(RequestCollection collection, List<CollectionDto> target)
    {
        target.Add(ToCollectionDto(collection));
        foreach (var child in collection.Children)
        {
            AddSubtree(child, target);
        }
    }

    public static RequestItem ToModel(RequestDto dto)
    {
        var request = new RequestItem
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            Name = dto.Name,
            Method = string.IsNullOrWhiteSpace(dto.Method) ? "GET" : dto.Method,
            Url = dto.Url,
            TimeoutSeconds = dto.TimeoutSeconds,
            BodyMode = dto.BodyMode,
            Body = dto.Body,
            UploadFilePath = dto.UploadFilePath,
            UploadFieldName = string.IsNullOrWhiteSpace(dto.UploadFieldName)
                ? "file"
                : dto.UploadFieldName,
            IsFavorite = dto.IsFavorite,
        };

        FillRows(request.Params, dto.Params);
        FillRows(request.Headers, dto.Headers);
        FillRows(request.FormFields, dto.FormFields);
        return request;
    }

    public static HistoryEntry ToHistory(HistoryDto dto)
    {
        return new HistoryEntry
        {
            Timestamp = dto.Timestamp,
            Name = dto.Name,
            Method = dto.Method,
            Url = dto.Url,
            TimeoutSeconds = dto.TimeoutSeconds,
            BodyMode = dto.BodyMode,
            Body = dto.Body,
            StatusCode = dto.StatusCode,
            DurationMs = dto.DurationMs,
            Params = dto.Params.ToList(),
            Headers = dto.Headers.ToList(),
        };
    }

    private static HistoryDto ToHistoryDto(HistoryEntry entry)
    {
        return new HistoryDto
        {
            Timestamp = entry.Timestamp,
            Name = entry.Name,
            Method = entry.Method,
            Url = entry.Url,
            TimeoutSeconds = entry.TimeoutSeconds,
            BodyMode = entry.BodyMode,
            Body = entry.Body,
            StatusCode = entry.StatusCode,
            DurationMs = entry.DurationMs,
            Params = entry.Params.ToList(),
            Headers = entry.Headers.ToList(),
        };
    }

    private static RequestDto ToDto(RequestItem request)
    {
        return new RequestDto
        {
            Id = request.Id,
            Name = request.Name,
            Method = request.Method,
            Url = request.Url,
            TimeoutSeconds = request.TimeoutSeconds,
            BodyMode = request.BodyMode,
            Body = request.Body,
            UploadFilePath = request.UploadFilePath,
            UploadFieldName = request.UploadFieldName,
            IsFavorite = request.IsFavorite,
            Params = ToRows(request.Params),
            Headers = ToRows(request.Headers),
            FormFields = ToRows(request.FormFields),
        };
    }

    private static List<RowDto> ToRows(EditableRowCollection rows)
    {
        return rows.Where(row =>
                !string.IsNullOrWhiteSpace(row.Key) || !string.IsNullOrWhiteSpace(row.Value)
            )
            .Select(row => new RowDto
            {
                Key = row.Key,
                Value = row.Value,
                IsEnabled = row.IsEnabled,
                IsSecret = row.IsSecret,
            })
            .ToList();
    }

    private static void FillRows(EditableRowCollection target, List<RowDto> rows)
    {
        foreach (var row in rows)
        {
            target.AddRow(row.Key, row.Value, row.IsEnabled, row.IsSecret);
        }

        target.EnsureTrailingRow();
    }
}

public sealed class WorkspaceDto
{
    public List<CollectionDto> Collections { get; set; } = new();

    public List<EnvironmentDto> Environments { get; set; } = new();

    public List<HistoryDto> History { get; set; } = new();

    public List<HistoryDto> RecycledHistory { get; set; } = new();

    public List<SuiteDto> Suites { get; set; } = new();

    public List<TrashedNodeDto> TrashedNodes { get; set; } = new();

    public Guid? ActiveEnvironmentId { get; set; }
}

public sealed class CollectionDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsFavorite { get; set; }

    public Guid? ParentId { get; set; }

    public List<RequestDto> Requests { get; set; } = new();
}

public sealed class TrashedNodeDto
{
    public Guid Id { get; set; }

    public DateTimeOffset DeletedAt { get; set; }

    public string Source { get; set; } = "UI";

    public Guid? OriginalParentId { get; set; }

    public string OriginalLocation { get; set; } = "根目录";

    public int OriginalPosition { get; set; }

    /// <summary>A deleted collection first, then its sub-collections in tree order; empty for a request.</summary>
    public List<CollectionDto> Collections { get; set; } = new();

    public RequestDto? Request { get; set; }
}

public sealed class RequestDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; }

    public string BodyMode { get; set; } = "None";

    public string Body { get; set; } = string.Empty;

    public string UploadFilePath { get; set; } = string.Empty;

    public string UploadFieldName { get; set; } = "file";

    public bool IsFavorite { get; set; }

    public List<RowDto> Params { get; set; } = new();

    public List<RowDto> Headers { get; set; } = new();

    public List<RowDto> FormFields { get; set; } = new();
}

public sealed class EnvironmentDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<RowDto> Variables { get; set; } = new();
}

public sealed class HistoryDto
{
    public DateTimeOffset Timestamp { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; }

    public string BodyMode { get; set; } = "None";

    public string Body { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public long DurationMs { get; set; }

    public List<RowSnapshot> Params { get; set; } = new();

    public List<RowSnapshot> Headers { get; set; } = new();
}

public sealed class RowDto
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public bool IsSecret { get; set; }
}

public sealed class SuiteDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool StopOnError { get; set; } = true;

    public string LastSummary { get; set; } = "尚未运行";

    public List<SuiteStepDto> Steps { get; set; } = new();
}

public sealed class SuiteStepDto
{
    public Guid Id { get; set; }

    public Guid RequestId { get; set; }

    public string RequestName { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public int DelayMs { get; set; } = 200;

    public int? ExpectedStatus { get; set; }

    public string? BodyContains { get; set; }

    public List<SuiteMappingDto> InputMappings { get; set; } = new();
}

public sealed class SuiteMappingDto
{
    public Guid SourceStepId { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string TargetKind { get; set; } = "Body";

    public string TargetPath { get; set; } = string.Empty;

    public string Template { get; set; } = "{value}";
}
