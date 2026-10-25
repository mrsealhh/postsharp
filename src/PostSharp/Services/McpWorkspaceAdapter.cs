using Avalonia.Threading;
using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Services;

public sealed record McpRowInfo(string Key, string Value, bool Enabled);

public sealed record McpRequestInfo(
    Guid Id,
    string Name,
    string Method,
    string Url,
    string BodyMode,
    string Body,
    Guid? CollectionId,
    string CollectionName,
    IReadOnlyList<McpRowInfo> Params,
    IReadOnlyList<McpRowInfo> Headers,
    int Order,
    bool IsFavorite,
    IReadOnlyList<McpRowInfo> FormFields,
    string UploadFilePath,
    string UploadFieldName
);

public sealed record McpCollectionInfo(
    Guid Id,
    string Name,
    Guid? ParentId,
    int Depth,
    int Order,
    bool IsFavorite,
    IReadOnlyList<McpRequestInfo> Requests
);

public sealed record McpEnvironmentInfo(
    Guid Id,
    string Name,
    bool Active,
    IReadOnlyList<McpRowInfo> Variables
);

public sealed record McpHistoryInfo(
    DateTimeOffset Time,
    string Name,
    string Method,
    string Url,
    int StatusCode,
    long DurationMs
);

public sealed record McpAuditSummary(
    Guid Id,
    DateTimeOffset Timestamp,
    string Source,
    string? Client,
    string? Tool,
    string Name,
    string Method,
    string Url,
    int StatusCode,
    string StatusText,
    long ElapsedMs,
    bool IsSuccess,
    bool IsSse,
    int SseEventCount
);

public sealed record McpSendResult(
    Guid RequestId,
    string Name,
    string Method,
    string Url,
    int StatusCode,
    string StatusText,
    string? Error,
    long ElapsedMs,
    long SizeBytes,
    bool IsJson,
    string ContentType,
    string Body,
    IReadOnlyList<McpRowInfo> Headers,
    bool IsSse = false,
    int SseEventCount = 0,
    string SseThinking = "",
    string SseContent = ""
);

public sealed record McpCallContext(string Tool, string? Client);

public sealed record McpSuiteStepInput(
    Guid RequestId,
    int? ExpectedStatus,
    string? BodyContains,
    int? DelayMs,
    IReadOnlyList<McpSuiteMappingInput>? Mappings
);

public sealed record McpSuiteMappingInput(
    Guid? SourceStepId,
    int? SourceStepIndex,
    string SourcePath,
    string TargetKind,
    string TargetPath,
    string Template
);

public sealed record McpSequenceStep(
    Guid RequestId,
    string Name,
    int StatusCode,
    string StatusText,
    string? Error
);

public sealed record McpSequenceResult(
    int Total,
    int Succeeded,
    int Failed,
    bool Stopped,
    IReadOnlyList<McpSequenceStep> Steps
);

public sealed record McpRequestDraft(
    Guid? CollectionId,
    string? Name,
    string? Method,
    string? Url,
    string? BodyMode,
    string? Body,
    IReadOnlyList<McpRowInfo>? Params,
    IReadOnlyList<McpRowInfo>? Headers,
    IReadOnlyList<McpRowInfo>? FormFields,
    string? UploadFilePath,
    string? UploadFieldName,
    bool? IsFavorite,
    int? Position
);

/// <summary>
/// The bridge between MCP tool calls (which run on the local HTTP listener's thread) and the workspace,
/// which belongs to the UI thread. Every read and change is marshalled to the dispatcher; sending uses a
/// detached snapshot so a long request never blocks the window.
/// </summary>
public sealed class McpWorkspaceAdapter
{
    private readonly MainWindowViewModel _viewModel;
    private readonly HttpRequestService _http = new();

    public McpWorkspaceAdapter(MainWindowViewModel viewModel) => _viewModel = viewModel;

    public async Task<IReadOnlyList<McpCollectionInfo>> ListCollectionsAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<McpCollectionInfo>)
                _viewModel.Collections.Select(ToCollectionInfo).ToList()
        );
    }

    public async Task<McpRequestInfo?> GetRequestAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var request = FindRequest(id);
            return request is null ? null : ToInfo(request);
        });
    }

    public async Task<McpCollectionInfo> CreateCollectionAsync(
        string name,
        Guid? parentId,
        bool favorite,
        int? position
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var collection = _viewModel.CreateCollection(name, parentId, favorite, position);
            return ToCollectionInfo(collection);
        });
    }

    public async Task<McpCollectionInfo> UpdateCollectionAsync(
        Guid id,
        string? name,
        Guid? parentId,
        bool changeParent,
        bool? favorite,
        int? position
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var collection = _viewModel.UpdateCollection(
                id,
                name,
                parentId,
                changeParent,
                favorite,
                position
            );
            return ToCollectionInfo(collection);
        });
    }

    public async Task<bool> DeleteCollectionAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.DeleteCollection(id));
    }

    public async Task<McpRequestInfo> MoveRequestAsync(Guid id, Guid collectionId, int? position)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var request = _viewModel.MoveRequest(id, collectionId, position);
            return ToInfo(request);
        });
    }

    public async Task<McpRequestInfo> CreateRequestAsync(McpRequestDraft draft)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RequestCollection? collection = null;
            if (draft.CollectionId is { } collectionId)
            {
                collection =
                    _viewModel.Collections.FirstOrDefault(candidate => candidate.Id == collectionId)
                    ?? throw new KeyNotFoundException($"没有找到 id 为 {collectionId} 的集合。");
            }

            collection ??= _viewModel.Collections.FirstOrDefault();
            if (collection is null)
            {
                collection = new RequestCollection { Name = "MCP 集合" };
                _viewModel.Collections.Add(collection);
            }

            var request = new RequestItem
            {
                Name = string.IsNullOrWhiteSpace(draft.Name) ? "MCP 请求" : draft.Name.Trim(),
                Method = NormalizeMethod(draft.Method),
                Url = draft.Url ?? string.Empty,
                BodyMode = NormalizeBodyMode(draft.BodyMode),
                Body = draft.Body ?? string.Empty,
                UploadFilePath = draft.UploadFilePath ?? string.Empty,
                UploadFieldName = string.IsNullOrWhiteSpace(draft.UploadFieldName)
                    ? "file"
                    : draft.UploadFieldName.Trim(),
                IsFavorite = draft.IsFavorite ?? false,
                Owner = collection,
            };

            ReplaceRows(request.Params, draft.Params);
            ReplaceRows(request.Headers, draft.Headers);
            ReplaceRows(request.FormFields, draft.FormFields);
            var index = draft.Position is { } position
                ? Math.Clamp(position, 0, collection.Requests.Count)
                : collection.Requests.Count;
            collection.Requests.Insert(index, request);
            _viewModel.ToggleCollection(collection, true);
            _viewModel.NotifyWorkspaceChanged();
            return ToInfo(request);
        });
    }

    public async Task<McpRequestInfo?> UpdateRequestAsync(Guid id, McpRequestDraft draft)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var request = FindRequest(id);
            if (request is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(draft.Name))
            {
                request.Name = draft.Name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(draft.Method))
            {
                request.Method = NormalizeMethod(draft.Method);
            }

            if (draft.Url is not null)
            {
                request.Url = draft.Url;
            }

            if (!string.IsNullOrWhiteSpace(draft.BodyMode))
            {
                request.BodyMode = NormalizeBodyMode(draft.BodyMode);
            }

            if (draft.Body is not null)
            {
                request.Body = draft.Body;
            }

            if (draft.Params is not null)
            {
                ReplaceRows(request.Params, draft.Params);
            }

            if (draft.Headers is not null)
            {
                ReplaceRows(request.Headers, draft.Headers);
            }

            if (draft.FormFields is not null)
            {
                ReplaceRows(request.FormFields, draft.FormFields);
            }

            if (draft.UploadFilePath is not null)
            {
                request.UploadFilePath = draft.UploadFilePath;
            }

            if (!string.IsNullOrWhiteSpace(draft.UploadFieldName))
            {
                request.UploadFieldName = draft.UploadFieldName.Trim();
            }

            if (draft.IsFavorite is { } favorite)
            {
                request.IsFavorite = favorite;
            }

            if (draft.CollectionId is not null || draft.Position is not null)
            {
                var targetId =
                    draft.CollectionId
                    ?? request.Owner?.Id
                    ?? throw new InvalidOperationException("请求没有所属集合。");
                _viewModel.MoveRequest(id, targetId, draft.Position);
            }

            _viewModel.NotifyWorkspaceChanged();
            return ToInfo(request);
        });
    }

    public async Task<bool> DeleteRequestAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var request = FindRequest(id);
            if (request is null)
            {
                return false;
            }

            request.Owner?.Requests.Remove(request);
            _viewModel.AfterExternalDelete(request);
            _viewModel.NotifyWorkspaceChanged();
            return true;
        });
    }

    public async Task<McpSendResult> SendAsync(
        Guid id,
        Guid? environmentId,
        McpCallContext context,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var request = FindRequest(id);
            if (request is null)
            {
                return (SendSnapshot?)null;
            }

            var environment = environmentId is { } requested
                ? _viewModel.Environments.FirstOrDefault(candidate => candidate.Id == requested)
                : _viewModel.ActiveEnvironment;

            return new SendSnapshot(request.Clone(), CloneEnvironment(environment));
        });

        if (snapshot is null)
        {
            throw new KeyNotFoundException($"没有找到 id 为 {id} 的请求。");
        }

        snapshot.Request.Name = await Dispatcher.UIThread.InvokeAsync(() =>
            FindRequest(id)?.Name ?? snapshot.Request.Name
        );

        ComposedRequest? composed = null;
        HttpResult result;
        try
        {
            composed = RequestComposer.Compose(snapshot.Request, snapshot.Environment);
            result = await _http.SendAsync(
                composed,
                cancellationToken,
                snapshot.Request.TimeoutSeconds
            );
        }
        catch (Exception exception)
        {
            Log.Error(exception, "MCP 发送请求前组合失败 {RequestId}", id);
            result = HttpRequestService.Failure(exception.Message);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _viewModel.RecordExternalSend(
                snapshot.Request,
                composed,
                result,
                "MCP",
                context.Tool,
                context.Client
            );
            return true;
        });

        var headers = result
            .Headers.Select(header => new McpRowInfo(header.Key, header.Value, true))
            .ToList();

        return new McpSendResult(
            id,
            snapshot.Request.Name,
            composed?.Method ?? snapshot.Request.Method,
            composed?.Url ?? snapshot.Request.Url,
            result.StatusCode,
            string.IsNullOrWhiteSpace(result.ReasonPhrase)
                ? result.StatusCode.ToString()
                : $"{result.StatusCode} {result.ReasonPhrase}",
            result.Error,
            result.ElapsedMs,
            result.SizeBytes,
            result.IsJson,
            result.MediaType,
            result.Body,
            headers,
            result.IsSse,
            result.SseEventCount,
            result.SseThinking,
            result.SseContent
        );
    }

    public async Task<McpSequenceResult> RunSequenceAsync(
        IReadOnlyList<Guid> requestIds,
        bool stopOnError,
        McpCallContext context,
        CancellationToken cancellationToken
    )
    {
        var steps = new List<McpSequenceStep>();
        var stopped = false;

        foreach (var id in requestIds)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                break;
            }

            try
            {
                var result = await SendAsync(id, null, context, cancellationToken);
                steps.Add(
                    new McpSequenceStep(
                        result.RequestId,
                        result.Name,
                        result.StatusCode,
                        result.StatusText,
                        result.Error
                    )
                );

                if (stopOnError && (result.Error is not null || result.StatusCode >= 400))
                {
                    stopped = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "MCP 顺序运行步骤失败 {RequestId}", id);
                steps.Add(new McpSequenceStep(id, string.Empty, 0, "失败", ex.Message));
                if (stopOnError)
                {
                    stopped = true;
                    break;
                }
            }
        }

        return new McpSequenceResult(
            Total: steps.Count,
            Succeeded: steps.Count(step =>
                step.Error is null && step.StatusCode is >= 200 and < 400
            ),
            Failed: steps.Count(step =>
                step.Error is not null || step.StatusCode >= 400 || step.StatusCode == 0
            ),
            Stopped: stopped,
            Steps: steps
        );
    }

    public async Task<string> GetCurlAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var request =
                FindRequest(id) ?? throw new KeyNotFoundException($"没有找到 id 为 {id} 的请求。");
            return RequestComposer.BuildCurl(request, _viewModel.ActiveEnvironment);
        });
    }

    public async Task<IReadOnlyList<McpAuditSummary>> ListAuditAsync(int limit, string? source)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<McpAuditSummary>)
                _viewModel
                    .Audit.Where(entry =>
                        string.IsNullOrWhiteSpace(source)
                        || entry.Source.Equals(source, StringComparison.OrdinalIgnoreCase)
                    )
                    .Take(Math.Clamp(limit, 1, 300))
                    .Select(entry => new McpAuditSummary(
                        entry.Id,
                        entry.Timestamp,
                        entry.Source,
                        entry.Client,
                        entry.Tool,
                        entry.RequestName,
                        entry.Method,
                        entry.Url,
                        entry.StatusCode,
                        entry.StatusText,
                        entry.ElapsedMs,
                        entry.IsSuccess,
                        entry.IsSse,
                        entry.SseEventCount
                    ))
                    .ToList()
        );
    }

    public async Task<AuditEntry?> GetAuditAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            _viewModel.Audit.FirstOrDefault(entry => entry.Id == id)
        );
    }

    public async Task<IReadOnlyList<TestSuite>> ListSuitesAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<TestSuite>)_viewModel.Suites.ToList()
        );
    }

    public async Task<TestSuite?> GetSuiteAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            _viewModel.Suites.FirstOrDefault(suite => suite.Id == id)
        );
    }

    public async Task<TestSuite> CreateSuiteAsync(
        string name,
        IReadOnlyList<McpSuiteStepInput> steps,
        bool stopOnError
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var suite = new TestSuite
            {
                Name = string.IsNullOrWhiteSpace(name) ? "MCP 测试套件" : name.Trim(),
                StopOnError = stopOnError,
            };

            suite.Steps.AddRange(BuildSuiteSteps(steps));
            _viewModel.Suites.Add(suite);
            _viewModel.SelectedSuite = suite;
            _viewModel.NotifyWorkspaceChanged();
            return suite;
        });
    }

    public async Task<TestSuite> CreateSuiteFromCollectionsAsync(
        string name,
        IReadOnlyList<Guid> collectionIds,
        int delayMs,
        bool stopOnError
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var requests = new List<RequestItem>();
            foreach (var collectionId in collectionIds)
            {
                var collection =
                    _viewModel.Collections.FirstOrDefault(candidate => candidate.Id == collectionId)
                    ?? throw new KeyNotFoundException($"没有找到 id 为 {collectionId} 的集合。");
                requests.AddRange(collection.Requests);
            }

            if (requests.Count == 0)
            {
                throw new InvalidOperationException("所选集合中没有可加入套件的请求。");
            }

            var stepInputs = requests
                .Select(request => new McpSuiteStepInput(
                    request.Id,
                    null,
                    null,
                    delayMs,
                    Array.Empty<McpSuiteMappingInput>()
                ))
                .ToList();

            var suite = new TestSuite
            {
                Name = string.IsNullOrWhiteSpace(name) ? "MCP 集合套件" : name.Trim(),
                StopOnError = stopOnError,
            };
            suite.Steps.AddRange(BuildSuiteSteps(stepInputs));

            _viewModel.Suites.Add(suite);
            _viewModel.SelectedSuite = suite;
            _viewModel.NotifyWorkspaceChanged();
            return suite;
        });
    }

    public async Task<TestSuite> UpdateSuiteAsync(
        Guid id,
        string? name,
        bool? stopOnError,
        IReadOnlyList<McpSuiteStepInput>? steps
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var suite =
                _viewModel.Suites.FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new KeyNotFoundException($"没有找到 id 为 {id} 的套件。");

            if (!string.IsNullOrWhiteSpace(name))
            {
                suite.Name = name.Trim();
            }

            if (stopOnError is { } shouldStop)
            {
                suite.StopOnError = shouldStop;
            }

            if (steps is not null)
            {
                var rebuilt = BuildSuiteSteps(steps);
                suite.Steps.Clear();
                suite.Steps.AddRange(rebuilt);
            }

            _viewModel.SelectedSuite = suite;
            _viewModel.NotifyWorkspaceChanged();
            return suite;
        });
    }

    public async Task<bool> DeleteSuiteAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var suite = _viewModel.Suites.FirstOrDefault(candidate => candidate.Id == id);
            if (suite is null)
            {
                return false;
            }

            _viewModel.Suites.Remove(suite);
            if (_viewModel.SelectedSuite?.Id == suite.Id)
            {
                _viewModel.SelectedSuite = _viewModel.Suites.FirstOrDefault();
            }

            _viewModel.NotifyWorkspaceChanged();
            return true;
        });
    }

    public async Task<TestRunResult> RunSuiteAsync(
        Guid id,
        McpCallContext context,
        CancellationToken cancellationToken
    )
    {
        return await _viewModel.RunSuiteForMcpAsync(id, context.Client, cancellationToken);
    }

    public async Task<IReadOnlyList<McpHistoryInfo>> ListHistoryAsync(int limit)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<McpHistoryInfo>)
                _viewModel
                    .History.Take(Math.Clamp(limit, 1, 200))
                    .Select(entry => new McpHistoryInfo(
                        entry.Timestamp,
                        entry.Name,
                        entry.Method,
                        entry.Url,
                        entry.StatusCode,
                        entry.DurationMs
                    ))
                    .ToList()
        );
    }

    public async Task<IReadOnlyList<McpEnvironmentInfo>> ListEnvironmentsAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<McpEnvironmentInfo>)
                _viewModel
                    .Environments.Select(environment => new McpEnvironmentInfo(
                        environment.Id,
                        environment.Name,
                        ReferenceEquals(environment, _viewModel.ActiveEnvironment),
                        environment
                            .Variables.Where(row => !string.IsNullOrWhiteSpace(row.Key))
                            .Select(ToInfo)
                            .ToList()
                    ))
                    .ToList()
        );
    }

    public async Task<McpEnvironmentInfo> SetVariableAsync(
        Guid? environmentId,
        string key,
        string value,
        bool? enabled
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var environment = environmentId is { } requested
                ? _viewModel.Environments.FirstOrDefault(candidate => candidate.Id == requested)
                : _viewModel.ActiveEnvironment;

            if (environment is null)
            {
                throw new KeyNotFoundException("没有找到目标环境。");
            }

            var row = environment.Variables.FirstOrDefault(candidate =>
                candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
            );

            if (row is null)
            {
                environment.Variables.AddRow(key, value, enabled ?? true);
            }
            else
            {
                row.Key = key;
                row.Value = value;
                if (enabled is { } isEnabled)
                {
                    row.IsEnabled = isEnabled;
                }
            }

            environment.Variables.EnsureTrailingRow();
            _viewModel.NotifyWorkspaceChanged();

            return new McpEnvironmentInfo(
                environment.Id,
                environment.Name,
                ReferenceEquals(environment, _viewModel.ActiveEnvironment),
                environment
                    .Variables.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Key))
                    .Select(ToInfo)
                    .ToList()
            );
        });
    }

    private List<TestStep> BuildSuiteSteps(IReadOnlyList<McpSuiteStepInput> steps)
    {
        if (steps.Count == 0)
        {
            throw new InvalidOperationException("steps 不能为空。");
        }

        var built = new List<TestStep>(steps.Count);
        foreach (var stepInput in steps)
        {
            var request =
                FindRequest(stepInput.RequestId)
                ?? throw new KeyNotFoundException($"没有找到 id 为 {stepInput.RequestId} 的请求。");

            built.Add(
                new TestStep
                {
                    RequestId = request.Id,
                    RequestName = request.Name,
                    Method = request.Method,
                    Url = request.Url,
                    DelayMs = Math.Clamp(stepInput.DelayMs ?? 200, 0, 60_000),
                    ExpectedStatus = stepInput.ExpectedStatus,
                    BodyContains = string.IsNullOrWhiteSpace(stepInput.BodyContains)
                        ? null
                        : stepInput.BodyContains,
                }
            );
        }

        for (var stepIndex = 0; stepIndex < steps.Count; stepIndex++)
        {
            var mappings = steps[stepIndex].Mappings;
            if (mappings is null || mappings.Count == 0)
            {
                continue;
            }

            var step = built[stepIndex];
            foreach (var mapping in mappings)
            {
                Guid sourceStepId;
                if (mapping.SourceStepIndex is { } sourceIndex)
                {
                    if (sourceIndex < 0 || sourceIndex >= stepIndex)
                    {
                        throw new InvalidOperationException(
                            $"第 {stepIndex + 1} 步的映射只能引用更早的步骤，sourceStepIndex={sourceIndex} 无效。"
                        );
                    }

                    sourceStepId = built[sourceIndex].Id;
                }
                else if (mapping.SourceStepId is { } requestedSourceId)
                {
                    var resolvedIndex = built.FindIndex(candidate =>
                        candidate.Id == requestedSourceId
                    );
                    if (resolvedIndex < 0 || resolvedIndex >= stepIndex)
                    {
                        throw new InvalidOperationException(
                            $"第 {stepIndex + 1} 步的映射只能引用更早的步骤，sourceStepId={requestedSourceId} 无效。"
                        );
                    }

                    sourceStepId = requestedSourceId;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"第 {stepIndex + 1} 步的映射缺少 sourceStepIndex 或 sourceStepId。"
                    );
                }

                var targetKind = string.IsNullOrWhiteSpace(mapping.TargetKind)
                    ? "Body"
                    : mapping.TargetKind.Trim();
                if (
                    !targetKind.Equals("Body", StringComparison.OrdinalIgnoreCase)
                    && !targetKind.Equals("Header", StringComparison.OrdinalIgnoreCase)
                    && !targetKind.Equals("Query", StringComparison.OrdinalIgnoreCase)
                    && !targetKind.Equals("Path", StringComparison.OrdinalIgnoreCase)
                )
                {
                    throw new InvalidOperationException(
                        $"第 {stepIndex + 1} 步的映射目标类型必须是 Body、Header、Query 或 Path。"
                    );
                }

                if (string.IsNullOrWhiteSpace(mapping.TargetPath))
                {
                    throw new InvalidOperationException(
                        $"第 {stepIndex + 1} 步的映射缺少 targetPath。"
                    );
                }

                step.InputMappings.Add(
                    new InputMapping
                    {
                        SourceStepId = sourceStepId,
                        SourcePath = mapping.SourcePath?.Trim() ?? string.Empty,
                        TargetKind =
                            char.ToUpperInvariant(targetKind[0])
                            + targetKind[1..].ToLowerInvariant(),
                        TargetPath = mapping.TargetPath.Trim(),
                        Template = string.IsNullOrWhiteSpace(mapping.Template)
                            ? "{value}"
                            : mapping.Template,
                    }
                );
            }
        }

        return built;
    }

    private RequestItem? FindRequest(Guid id) =>
        _viewModel
            .Collections.SelectMany(collection => collection.Requests)
            .FirstOrDefault(request => request.Id == id);

    private static McpRequestInfo ToInfo(RequestItem request) =>
        new(
            request.Id,
            request.Name,
            request.Method,
            request.Url,
            request.BodyMode,
            request.Body,
            request.Owner?.Id,
            request.Owner?.Name ?? string.Empty,
            request
                .Params.Where(row => !string.IsNullOrWhiteSpace(row.Key))
                .Select(ToInfo)
                .ToList(),
            request
                .Headers.Where(row => !string.IsNullOrWhiteSpace(row.Key))
                .Select(ToInfo)
                .ToList(),
            request.Owner?.Requests.IndexOf(request) ?? -1,
            request.IsFavorite,
            request
                .FormFields.Where(row => !string.IsNullOrWhiteSpace(row.Key))
                .Select(ToInfo)
                .ToList(),
            request.UploadFilePath,
            request.UploadFieldName
        );

    private McpCollectionInfo ToCollectionInfo(RequestCollection collection) =>
        new(
            collection.Id,
            collection.Name,
            collection.Parent?.Id,
            collection.Depth,
            CollectionOrder(collection),
            collection.IsFavorite,
            collection.Requests.Select(ToInfo).ToList()
        );

    private int CollectionOrder(RequestCollection collection)
    {
        IList<RequestCollection> siblings = collection.Parent is { } parent
            ? parent.Children
            : (IList<RequestCollection>)
                _viewModel.Collections.Where(candidate => candidate.Parent is null).ToList();
        return siblings.IndexOf(collection);
    }

    private static McpRowInfo ToInfo(KeyValueRow row) => new(row.Key, row.Value, row.IsEnabled);

    private static void ReplaceRows(EditableRowCollection target, IReadOnlyList<McpRowInfo>? rows)
    {
        if (rows is null)
        {
            return;
        }

        target.Clear();
        foreach (var row in rows)
        {
            target.AddRow(row.Key, row.Value, row.Enabled);
        }

        target.EnsureTrailingRow();
    }

    private static EnvironmentModel? CloneEnvironment(EnvironmentModel? environment)
    {
        if (environment is null)
        {
            return null;
        }

        var clone = new EnvironmentModel { Name = environment.Name };
        foreach (var row in environment.Variables)
        {
            clone.Variables.AddRow(row.Key, row.Value, row.IsEnabled);
        }

        clone.Variables.EnsureTrailingRow();
        return clone;
    }

    private static string NormalizeMethod(string? method)
    {
        var value = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        return value is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS"
            ? value
            : "GET";
    }

    private static string NormalizeBodyMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "None";
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            "json" => "Json",
            "text" => "Text",
            "multipart" or "form-data" or "formdata" => "Multipart",
            _ => "None",
        };
    }

    private sealed record SendSnapshot(RequestItem Request, EnvironmentModel? Environment);
}
