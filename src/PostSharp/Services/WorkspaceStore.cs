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

    private readonly StorageCoordinator _storage = StorageCoordinator.Current;

    public string FilePath { get; }

    public WorkspaceStore()
    {
        AppPaths.EnsureRoot();
        FilePath = AppPaths.WorkspaceFile;
    }

    public DataFileLoad<WorkspaceDto> Load()
    {
        var document = _storage.LoadDocument(StorageCategories.Workspace);
        if (document is not null)
        {
            try
            {
                var workspace = JsonSerializer.Deserialize<WorkspaceDto>(
                    document.Payload,
                    JsonOptions
                );
                return workspace is null
                    ? new DataFileLoad<WorkspaceDto>(null, DataFileState.Unreadable, null)
                    : new DataFileLoad<WorkspaceDto>(workspace, DataFileState.Loaded, null);
            }
            catch (Exception exception)
            {
                Log.Error(exception, "读取 SQLite 工作区文档失败");
                return new DataFileLoad<WorkspaceDto>(null, DataFileState.Unreadable, null);
            }
        }

        // One-time migration from the old JSON layout.
        var load = DataFile.Load(
            FilePath,
            text => JsonSerializer.Deserialize<WorkspaceDto>(text, JsonOptions)
        );
        if (load.Value is { } migrated)
        {
            _storage.SaveDocument(
                StorageCategories.Workspace,
                JsonSerializer.Serialize(migrated, JsonOptions)
            );
        }

        return load;
    }

    /// <summary>Never throws: a failed autosave must not break the request being sent.</summary>
    public SaveResult Save(WorkspaceDto workspace) =>
        _storage.SaveDocument(
            StorageCategories.Workspace,
            JsonSerializer.Serialize(workspace, JsonOptions)
        );

    public static WorkspaceDto Capture(
        IEnumerable<RequestCollection> collections,
        IEnumerable<EnvironmentModel> environments,
        IEnumerable<HistoryEntry> history,
        IEnumerable<HistoryEntry> recycledHistory,
        IEnumerable<TestSuite> suites,
        IEnumerable<TrashedNode> trashedNodes,
        Guid? activeEnvironmentId,
        bool includeSuites = true
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
            Suites = includeSuites ? CaptureSuites(suites) : new List<SuiteDto>(),
            TrashedNodes = trashedNodes.Select(ToTrashedNodeDto).ToList(),
            ActiveEnvironmentId = activeEnvironmentId,
        };
    }

    public static List<SuiteDto> CaptureSuites(IEnumerable<TestSuite> suites) =>
        suites.Select(ToSuiteDto).ToList();

    public static TestSuite ToSuiteModel(SuiteDto suiteDto)
    {
        var suite = new TestSuite
        {
            Id = suiteDto.Id == Guid.Empty ? Guid.NewGuid() : suiteDto.Id,
            Name = suiteDto.Name,
            StopOnError = suiteDto.StopOnError,
            RunInParallel = suiteDto.RunInParallel,
            ScheduleMinutes = suiteDto.ScheduleMinutes,
            LastSummary = string.IsNullOrWhiteSpace(suiteDto.LastSummary)
                ? "尚未运行"
                : suiteDto.LastSummary,
        };

        foreach (var stepDto in suiteDto.Steps)
        {
            var step = new TestStep
            {
                Id = stepDto.Id == Guid.Empty ? Guid.NewGuid() : stepDto.Id,
                RequestId = stepDto.RequestId,
                RequestName = stepDto.RequestName,
                Method = stepDto.Method,
                Url = stepDto.Url,
                DelayMs = stepDto.DelayMs,
                RetryCount = stepDto.RetryCount,
                RetryDelayMs = stepDto.RetryDelayMs,
                Condition = string.IsNullOrWhiteSpace(stepDto.Condition)
                    ? "Always"
                    : stepDto.Condition,
                ParallelGroup = stepDto.ParallelGroup,
                ExpectedStatus = stepDto.ExpectedStatus,
                BodyContains = stepDto.BodyContains,
            };

            foreach (var mappingDto in stepDto.InputMappings)
            {
                step.InputMappings.Add(
                    new InputMapping
                    {
                        SourceStepId = mappingDto.SourceStepId,
                        SourcePath = mappingDto.SourcePath,
                        TargetKind = mappingDto.TargetKind,
                        TargetPath = mappingDto.TargetPath,
                        Template = mappingDto.Template,
                    }
                );
            }

            foreach (var assertionDto in stepDto.Assertions)
            {
                step.Assertions.Add(
                    new TestAssertion
                    {
                        Kind = assertionDto.Kind,
                        Path = assertionDto.Path,
                        Expected = assertionDto.Expected,
                    }
                );
            }

            suite.Steps.Add(step);
        }

        if (suiteDto.Workflow is { } workflow)
        {
            suite.Workflow = new SuiteWorkflow();
            foreach (var node in workflow.Nodes)
            {
                suite.Workflow.Nodes.Add(
                    new SuiteWorkflowNode
                    {
                        Id = node.Id,
                        Kind = node.Kind,
                        CollectionId = node.CollectionId,
                        RequestId = node.RequestId,
                        X = node.X,
                        Y = node.Y,
                    }
                );
            }

            foreach (var edge in workflow.Edges)
            {
                suite.Workflow.Edges.Add(
                    new SuiteWorkflowEdge { SourceId = edge.SourceId, TargetId = edge.TargetId }
                );
            }
        }

        return suite;
    }

    private static SuiteDto ToSuiteDto(TestSuite suite) =>
        new()
        {
            Id = suite.Id,
            Name = suite.Name,
            StopOnError = suite.StopOnError,
            RunInParallel = suite.RunInParallel,
            ScheduleMinutes = suite.ScheduleMinutes,
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
                    RetryCount = step.RetryCount,
                    RetryDelayMs = step.RetryDelayMs,
                    Condition = step.Condition,
                    ParallelGroup = step.ParallelGroup,
                    ExpectedStatus = step.ExpectedStatus,
                    BodyContains = step.BodyContains,
                    Assertions = step
                        .Assertions.Select(assertion => new SuiteAssertionDto
                        {
                            Kind = assertion.Kind,
                            Path = assertion.Path,
                            Expected = assertion.Expected,
                        })
                        .ToList(),
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
            Workflow = suite.Workflow is { } workflow
                ? new SuiteWorkflowDto
                {
                    Nodes = workflow
                        .Nodes.Select(node => new SuiteWorkflowNodeDto
                        {
                            Id = node.Id,
                            Kind = node.Kind,
                            CollectionId = node.CollectionId,
                            RequestId = node.RequestId,
                            X = node.X,
                            Y = node.Y,
                        })
                        .ToList(),
                    Edges = workflow
                        .Edges.Select(edge => new SuiteWorkflowEdgeDto
                        {
                            SourceId = edge.SourceId,
                            TargetId = edge.TargetId,
                        })
                        .ToList(),
                }
                : null,
        };

    /// <summary>Builds a collection and its requests; parent links are set by the caller.</summary>
    public static RequestCollection ToCollectionModel(CollectionDto dto)
    {
        var collection = new RequestCollection
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            Name = dto.Name,
            IsFavorite = dto.IsFavorite,
            OpenApiSource = dto.OpenApiSource,
        };
        collection.Auth.CopyFrom(ToAuth(dto.Auth));

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
            OpenApiSource = collection.OpenApiSource,
            Requests = collection.Requests.Select(ToDto).ToList(),
            Auth = ToAuthDto(collection.Auth),
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
            UploadFilePath = dto.UploadFiles.Count == 0 ? dto.UploadFilePath : string.Empty,
            UploadFieldName = string.IsNullOrWhiteSpace(dto.UploadFieldName)
                ? "file"
                : dto.UploadFieldName,
            IsFavorite = dto.IsFavorite,
            Auth = ToAuth(dto.Auth),
            OpenApiKey = dto.OpenApiKey,
            OpenApiDoc = dto.OpenApiDoc,
        };

        foreach (var file in dto.UploadFiles)
        {
            request.UploadFiles.Add(
                new UploadFileItem
                {
                    FilePath = file.FilePath,
                    FieldName = string.IsNullOrWhiteSpace(file.FieldName) ? "file" : file.FieldName,
                    SizeBytes = file.SizeBytes,
                }
            );
        }

        FillRows(request.Params, dto.Params);
        FillRows(request.Headers, dto.Headers);
        FillRows(request.FormFields, dto.FormFields);
        return request;
    }

    /// <summary>把落盘的授权配置还原成可用的对象；没有配置时得到默认的「继承」。</summary>
    public static RequestAuth ToAuth(RequestAuthDto? dto)
    {
        var auth = new RequestAuth();
        if (dto is null)
        {
            return auth;
        }

        auth.AuthType = AuthKinds.IsKnownType(dto.AuthType) ? dto.AuthType : AuthKinds.Inherit;
        auth.Target = AuthKinds.IsKnownTarget(dto.Target) ? dto.Target : AuthKinds.InHeader;
        auth.Username = dto.Username;
        auth.ApiKeyName = string.IsNullOrWhiteSpace(dto.ApiKeyName)
            ? RequestAuth.DefaultApiKeyName
            : dto.ApiKeyName;
        auth.ApiKeyLocation = AuthKinds.IsKnownTarget(dto.ApiKeyLocation)
            ? dto.ApiKeyLocation
            : AuthKinds.InHeader;
        auth.TokenCipher = dto.TokenCipher;
        auth.PasswordCipher = dto.PasswordCipher;
        auth.ApiKeyValueCipher = dto.ApiKeyValueCipher;
        if (dto.OAuth2 is not null)
        {
            auth.OAuth2 = dto.OAuth2;
        }

        return auth;
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
            Body = HistoryEntry.LimitBody(dto.Body),
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
            Body = HistoryEntry.LimitBody(entry.Body),
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
            UploadFiles = request
                .UploadFiles.Select(file => new UploadFileDto
                {
                    FilePath = file.FilePath,
                    FieldName = file.FieldName,
                    SizeBytes = file.SizeBytes,
                })
                .ToList(),
            IsFavorite = request.IsFavorite,
            Params = ToRows(request.Params),
            Headers = ToRows(request.Headers),
            FormFields = ToRows(request.FormFields),
            Auth = ToAuthDto(request.Auth),
            OpenApiKey = request.OpenApiKey,
            OpenApiDoc = request.OpenApiDoc,
        };
    }

    private static RequestAuthDto? ToAuthDto(RequestAuth auth)
    {
        if (auth.IsDefault)
        {
            return null;
        }

        return new RequestAuthDto
        {
            AuthType = auth.AuthType,
            Target = auth.Target,
            TokenCipher = auth.TokenCipher,
            Username = auth.Username,
            PasswordCipher = auth.PasswordCipher,
            ApiKeyName = auth.ApiKeyName,
            ApiKeyValueCipher = auth.ApiKeyValueCipher,
            ApiKeyLocation = auth.ApiKeyLocation,
            OAuth2 = auth.OAuth2,
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

    public OpenApiSource? OpenApiSource { get; set; }

    public List<RequestDto> Requests { get; set; } = new();

    public RequestAuthDto? Auth { get; set; }
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

    public List<UploadFileDto> UploadFiles { get; set; } = new();

    public bool IsFavorite { get; set; }

    public List<RowDto> Params { get; set; } = new();

    public List<RowDto> Headers { get; set; } = new();

    public List<RowDto> FormFields { get; set; } = new();

    public RequestAuthDto? Auth { get; set; }

    public string OpenApiKey { get; set; } = string.Empty;

    public OpenApiOperationDoc? OpenApiDoc { get; set; }
}

/// <summary>一处授权配置的落盘形态；令牌、密码、API Key 值只保存 DPAPI 密文。</summary>
public sealed class RequestAuthDto
{
    public string AuthType { get; set; } = AuthKinds.Inherit;

    public string Target { get; set; } = AuthKinds.InHeader;

    public string TokenCipher { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string PasswordCipher { get; set; } = string.Empty;

    public string ApiKeyName { get; set; } = RequestAuth.DefaultApiKeyName;

    public string ApiKeyValueCipher { get; set; } = string.Empty;

    public string ApiKeyLocation { get; set; } = AuthKinds.InHeader;

    public AuthorizationProfile? OAuth2 { get; set; }
}

public sealed class UploadFileDto
{
    public string FilePath { get; set; } = string.Empty;

    public string FieldName { get; set; } = "file";

    public long SizeBytes { get; set; }
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

    public bool RunInParallel { get; set; }

    public int ScheduleMinutes { get; set; }

    public string LastSummary { get; set; } = "尚未运行";

    public List<SuiteStepDto> Steps { get; set; } = new();

    public SuiteWorkflowDto? Workflow { get; set; }
}

public sealed class SuiteWorkflowDto
{
    public List<SuiteWorkflowNodeDto> Nodes { get; set; } = new();

    public List<SuiteWorkflowEdgeDto> Edges { get; set; } = new();
}

public sealed class SuiteWorkflowNodeDto
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = "request";

    public Guid CollectionId { get; set; }

    public Guid RequestId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }
}

public sealed class SuiteWorkflowEdgeDto
{
    public string SourceId { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;
}

public sealed class SuiteStepDto
{
    public Guid Id { get; set; }

    public Guid RequestId { get; set; }

    public string RequestName { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public int DelayMs { get; set; } = 200;

    public int RetryCount { get; set; }

    public int RetryDelayMs { get; set; } = 500;

    public string Condition { get; set; } = "Always";

    public int ParallelGroup { get; set; }

    public int? ExpectedStatus { get; set; }

    public string? BodyContains { get; set; }

    public List<SuiteAssertionDto> Assertions { get; set; } = new();

    public List<SuiteMappingDto> InputMappings { get; set; } = new();
}

public sealed class SuiteAssertionDto
{
    public string Kind { get; set; } = "JSONPath";

    public string Path { get; set; } = string.Empty;

    public string Expected { get; set; } = string.Empty;
}

public sealed class SuiteMappingDto
{
    public Guid SourceStepId { get; set; }

    public string SourcePath { get; set; } = string.Empty;

    public string TargetKind { get; set; } = "Body";

    public string TargetPath { get; set; } = string.Empty;

    public string Template { get; set; } = "{value}";
}
