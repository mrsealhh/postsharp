using System.Text.Json.Serialization;
using Avalonia.Threading;
using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Services;

public sealed record McpRowInfo(
    string Key,
    string Value,
    bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Secret = false
);

public sealed record McpAuthInfo(
    string AuthType,
    string Target,
    string Username,
    string ApiKeyName,
    string ApiKeyLocation,
    bool HasToken,
    bool HasPassword,
    bool HasApiKey
);

public sealed record McpAuthInput(
    string? AuthType,
    string? Target,
    string? Token,
    string? Username,
    string? Password,
    string? ApiKeyName,
    string? ApiKeyValue,
    string? ApiKeyLocation
);

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
    string UploadFieldName,
    int TimeoutSeconds,
    McpAuthInfo Auth
);

public sealed record McpCollectionInfo(
    Guid Id,
    string Name,
    Guid? ParentId,
    int Depth,
    int Order,
    bool IsFavorite,
    IReadOnlyList<McpRequestInfo> Requests,
    McpAuthInfo Auth
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
    string SseContent = "",
    bool BodyTruncated = false
)
{
    /// <summary>How much of a response body an AI client gets back.</summary>
    public const int BodyLimit = 30000;
}

public sealed record McpCallContext(string Tool, string? Client);

public sealed record McpSettingsInfo(
    string Theme,
    double CornerScale,
    string UiFont,
    string CodeFont,
    string CodeFontSize,
    int RequestTimeoutSeconds,
    string DefaultBodyMode,
    bool AutoFormatResponse,
    int LargeResponseThresholdKb,
    int AuditLimit,
    string LogDirectory,
    string LogLevel,
    string SseThinkingPath,
    string SseContentPath
);

public sealed record McpSettingsDraft(
    string? Theme,
    double? CornerScale,
    string? UiFont,
    string? CodeFont,
    string? CodeFontSize,
    int? RequestTimeoutSeconds,
    string? DefaultBodyMode,
    bool? AutoFormatResponse,
    int? LargeResponseThresholdKb,
    int? AuditLimit,
    string? LogDirectory,
    string? LogLevel,
    string? SseThinkingPath,
    string? SseContentPath
);

public sealed record McpStorageProfileInfo(
    string Id,
    string Name,
    string Provider,
    bool IsLocal,
    string MaskedConnectionString
);

public sealed record McpStorageConfigInfo(
    string LocalDatabasePath,
    string ConfigFile,
    IReadOnlyList<McpStorageProfileInfo> Profiles,
    IReadOnlyDictionary<string, string> Bindings
);

public sealed record McpStorageSyncResult(
    bool Success,
    string Message,
    int Categories,
    int Documents,
    string? BackupPath
);

public sealed record McpTrashInfo(
    Guid Id,
    string Kind,
    string Name,
    string Source,
    DateTimeOffset DeletedAt,
    string OriginalLocation
);

public sealed record McpSuiteStepInput(
    Guid RequestId,
    int? ExpectedStatus,
    string? BodyContains,
    int? DelayMs,
    IReadOnlyList<McpSuiteMappingInput>? Mappings,
    int? RetryCount = null,
    int? RetryDelayMs = null,
    string? Condition = null,
    int? ParallelGroup = null,
    IReadOnlyList<McpSuiteAssertionInput>? Assertions = null
);

public sealed record McpSuiteMappingInput(
    Guid? SourceStepId,
    int? SourceStepIndex,
    string SourcePath,
    string TargetKind,
    string TargetPath,
    string Template
);

public sealed record McpSuiteAssertionInput(string Kind, string Path, string Expected);

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
    int? Position,
    int? TimeoutSeconds,
    McpAuthInput? Auth
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
        int? position,
        McpAuthInput? auth = null
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var collection = _viewModel.CreateCollection(name, parentId, favorite, position);
            if (auth is not null)
            {
                ApplyAuth(collection.Auth, auth);
                _viewModel.NotifyWorkspaceChanged();
            }

            return ToCollectionInfo(collection);
        });
    }

    public async Task<McpCollectionInfo> UpdateCollectionAsync(
        Guid id,
        string? name,
        Guid? parentId,
        bool changeParent,
        bool? favorite,
        int? position,
        McpAuthInput? auth = null
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
            if (auth is not null)
            {
                ApplyAuth(collection.Auth, auth);
                _viewModel.NotifyWorkspaceChanged();
            }

            return ToCollectionInfo(collection);
        });
    }

    public async Task<bool> DeleteCollectionAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.DeleteCollection(id, "MCP"));
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
                TimeoutSeconds = Math.Clamp(draft.TimeoutSeconds ?? 0, 0, 3600),
                Owner = collection,
            };

            if (draft.Auth is { } auth)
            {
                ApplyAuth(request.Auth, auth);
            }

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

            if (draft.TimeoutSeconds is { } timeoutSeconds)
            {
                request.TimeoutSeconds = Math.Clamp(timeoutSeconds, 0, 3600);
            }

            if (draft.Auth is { } auth)
            {
                ApplyAuth(request.Auth, auth);
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
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.DeleteRequest(id, "MCP"));
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

            return new SendSnapshot(
                request.Clone(copyAuthorization: false),
                CloneEnvironment(environment),
                SecretMasker.CollectSecrets(_viewModel.Environments)
            );
        });

        if (snapshot is null)
        {
            throw new KeyNotFoundException($"没有找到 id 为 {id} 的请求。");
        }

        snapshot.Request.Name = await Dispatcher.UIThread.InvokeAsync(() =>
            FindRequest(id)?.Name ?? snapshot.Request.Name
        );

        // The snapshot cannot change, so the masked copy for the audit and the AI is built once, off the UI thread.
        var secrets = snapshot.Secrets;
        ComposedRequest? composed = null;
        ComposedRequest? stored = null;
        HttpResult result;
        try
        {
            var auth = await _viewModel.ResolveAuthAsync(snapshot.Request, cancellationToken);
            composed = RequestComposer.Compose(snapshot.Request, snapshot.Environment, auth);
            stored = SecretMasker.MaskComposed(
                snapshot.Request,
                snapshot.Environment,
                composed,
                secrets
            );
            result = await _http.SendAsync(
                composed,
                stored.Url,
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
                stored,
                result,
                "MCP",
                context.Tool,
                context.Client,
                secrets
            );
            return true;
        });

        // Secret values never go back to the AI client, even when a server echoes them.
        // Credential headers such as Set-Cookie are masked by name, the same as in the audit.
        var headers = result
            .Headers.Select(header => new McpRowInfo(
                header.Key,
                SecretMasker.MaskHeaderValue(header.Key, header.Value, secrets),
                true
            ))
            .ToList();
        var body = SecretMasker.MaskHead(result.Body, McpSendResult.BodyLimit, secrets);

        return new McpSendResult(
            id,
            snapshot.Request.Name,
            composed?.Method ?? snapshot.Request.Method,
            stored?.Url ?? SecretMasker.MaskSecrets(snapshot.Request.Url, secrets),
            result.StatusCode,
            string.IsNullOrWhiteSpace(result.ReasonPhrase)
                ? result.StatusCode.ToString()
                : $"{result.StatusCode} {result.ReasonPhrase}",
            result.Error is null ? null : SecretMasker.MaskSecrets(result.Error, secrets),
            result.ElapsedMs,
            result.SizeBytes,
            result.IsJson,
            result.MediaType,
            body,
            headers,
            result.IsSse,
            result.SseEventCount,
            SecretMasker.MaskSecrets(result.SseThinking, secrets),
            SecretMasker.MaskSecrets(result.SseContent, secrets),
            // MaskHead returns more than the limit exactly when it had to cut.
            body.Length > McpSendResult.BodyLimit
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
            var environment = _viewModel.ActiveEnvironment;
            var secrets = SecretMasker.CollectSecrets(_viewModel.Environments);
            var auth = _viewModel.ResolveAuth(request);

            ComposedRequest composed;
            try
            {
                composed = RequestComposer.Compose(request, environment, auth);
            }
            catch (InvalidOperationException exception)
            {
                // BuildCurl turns the same problem into a "# 无法生成 cURL" hint for the client.
                Log.Debug(exception, "MCP 获取 cURL 时请求无法生成 {RequestId}", id);
                return SecretMasker.MaskSecrets(
                    RequestComposer.BuildCurl(request, environment),
                    secrets
                );
            }

            // Mask environment secrets and injected credentials before quoting.
            var masked = SecretMasker.MaskRequest(
                composed,
                SecretMasker.CollectAllSecrets(_viewModel.Environments)
            );
            return RequestComposer.BuildCurl(RequestComposer.MaskAuthorization(masked));
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
        bool? runInParallel,
        int? scheduleMinutes,
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

            if (runInParallel is { } parallel)
            {
                suite.RunInParallel = parallel;
            }

            if (scheduleMinutes is { } schedule)
            {
                suite.ScheduleMinutes = Math.Clamp(schedule, 0, 1440);
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
        {
            // Older history entries were stored before suite mappings were masked; mask again at the MCP boundary.
            var secrets = SecretMasker.CollectAllSecrets(_viewModel.Environments);
            return (IReadOnlyList<McpHistoryInfo>)
                _viewModel
                    .History.Take(Math.Clamp(limit, 1, 200))
                    .Select(entry => new McpHistoryInfo(
                        entry.Timestamp,
                        entry.Name,
                        entry.Method,
                        SecretMasker.MaskSecrets(entry.Url, secrets),
                        entry.StatusCode,
                        entry.DurationMs
                    ))
                    .ToList();
        });
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
                            .Select(ToVariableInfo)
                            .ToList()
                    ))
                    .ToList()
        );
    }

    public async Task<McpEnvironmentInfo> SetVariableAsync(
        Guid? environmentId,
        string key,
        string? value,
        bool? enabled,
        bool? secret
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
                environment.Variables.AddRow(
                    key,
                    value ?? string.Empty,
                    enabled ?? true,
                    secret ?? false
                );
            }
            else
            {
                // Otherwise an AI client could clear the flag and then read the value in clear text.
                if (row.IsSecret && secret == false)
                {
                    throw new InvalidOperationException(
                        $"变量 {row.Key} 是机密变量，只能在 PostSharp 的环境变量对话框中取消机密。"
                    );
                }

                row.Key = key;

                // list_environments shows a secret as the mask; writing the mask back keeps the real value.
                if (value is not null && !(row.IsSecret && value == SecretMasker.Mask))
                {
                    row.Value = value;
                }

                if (enabled is { } isEnabled)
                {
                    row.IsEnabled = isEnabled;
                }

                if (secret == true)
                {
                    row.IsSecret = true;
                }
            }

            environment.Variables.EnsureTrailingRow();
            _viewModel.NotifyWorkspaceChanged();
            if (secret == true)
            {
                _viewModel.ScrubAuditSecrets();
            }

            return new McpEnvironmentInfo(
                environment.Id,
                environment.Name,
                ReferenceEquals(environment, _viewModel.ActiveEnvironment),
                environment
                    .Variables.Where(candidate => !string.IsNullOrWhiteSpace(candidate.Key))
                    .Select(ToVariableInfo)
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

            var step = new TestStep
            {
                RequestId = request.Id,
                RequestName = request.Name,
                Method = request.Method,
                Url = request.Url,
                DelayMs = Math.Clamp(stepInput.DelayMs ?? 200, 0, 60_000),
                RetryCount = Math.Clamp(stepInput.RetryCount ?? 0, 0, 10),
                RetryDelayMs = Math.Clamp(stepInput.RetryDelayMs ?? 500, 0, 60_000),
                Condition = NormalizeCondition(stepInput.Condition),
                ParallelGroup = Math.Clamp(stepInput.ParallelGroup ?? 0, 0, 1000),
                ExpectedStatus = stepInput.ExpectedStatus,
                BodyContains = string.IsNullOrWhiteSpace(stepInput.BodyContains)
                    ? null
                    : stepInput.BodyContains,
            };

            foreach (var assertion in stepInput.Assertions ?? [])
            {
                step.Assertions.Add(
                    new TestAssertion
                    {
                        Kind = assertion.Kind,
                        Path = assertion.Path,
                        Expected = assertion.Expected,
                    }
                );
            }

            built.Add(step);
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

    public async Task<McpSettingsInfo> GetSettingsAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var settings = _viewModel.Settings;
            return new McpSettingsInfo(
                settings.Theme,
                settings.CornerScale,
                settings.UiFont,
                settings.CodeFont,
                settings.CodeFontSize,
                settings.RequestTimeoutSeconds,
                settings.DefaultBodyMode,
                settings.AutoFormatResponse,
                settings.LargeResponseThresholdKb,
                settings.AuditLimit,
                settings.LogDirectory,
                settings.LogLevel,
                settings.SseThinkingPath,
                settings.SseContentPath
            );
        });
    }

    public async Task<McpSettingsInfo> UpdateSettingsAsync(McpSettingsDraft draft)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var settings = _viewModel.Settings;
            if (!string.IsNullOrWhiteSpace(draft.Theme))
            {
                settings.Theme = draft.Theme;
            }

            if (draft.CornerScale is { } cornerScale)
            {
                settings.CornerScale = Math.Clamp(cornerScale, 0, 2);
            }

            if (!string.IsNullOrWhiteSpace(draft.UiFont))
            {
                settings.UiFont = draft.UiFont;
            }

            if (!string.IsNullOrWhiteSpace(draft.CodeFont))
            {
                settings.CodeFont = draft.CodeFont;
            }

            if (!string.IsNullOrWhiteSpace(draft.CodeFontSize))
            {
                settings.CodeFontSize = draft.CodeFontSize;
            }

            if (draft.RequestTimeoutSeconds is { } timeout)
            {
                settings.RequestTimeoutSeconds = Math.Clamp(timeout, 1, 3600);
            }

            if (!string.IsNullOrWhiteSpace(draft.DefaultBodyMode))
            {
                settings.DefaultBodyMode = NormalizeBodyMode(draft.DefaultBodyMode);
            }

            if (draft.AutoFormatResponse is { } autoFormat)
            {
                settings.AutoFormatResponse = autoFormat;
            }

            if (draft.LargeResponseThresholdKb is { } threshold)
            {
                settings.LargeResponseThresholdKb = Math.Clamp(threshold, 8, 4096);
            }

            if (draft.AuditLimit is { } auditLimit)
            {
                settings.AuditLimit = Math.Clamp(auditLimit, 20, 5000);
            }

            if (!string.IsNullOrWhiteSpace(draft.LogDirectory))
            {
                settings.LogDirectory = Path.GetFullPath(draft.LogDirectory);
            }

            if (!string.IsNullOrWhiteSpace(draft.LogLevel))
            {
                settings.LogLevel = draft.LogLevel;
            }

            settings.SseThinkingPath = draft.SseThinkingPath ?? settings.SseThinkingPath;
            settings.SseContentPath = draft.SseContentPath ?? settings.SseContentPath;

            if (_viewModel.SaveSettingsForMcp() != SaveResult.Saved)
            {
                throw new InvalidOperationException("设置保存失败，请检查项目数据目录是否可写。");
            }

            return GetSettingsCore(settings);
        });
    }

    public async Task<McpStorageConfigInfo> GetStorageConfigAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            ToStorageInfo(StorageCoordinator.Current.Config)
        );
    }

    public async Task<McpStorageProfileInfo> UpsertStorageProfileAsync(
        string? id,
        string name,
        string provider,
        string? connectionString
    )
    {
        if (!StorageProviders.All.Contains(provider, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"不支持的存储类型：{provider}");
        }

        if (provider == StorageProviders.LocalSqlite)
        {
            throw new InvalidOperationException(
                "本地 SQLite 配置固定为项目数据目录，不能通过 MCP 修改。"
            );
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("存储配置名称不能为空。");
        }

        var coordinator = StorageCoordinator.Current;
        var existing = string.IsNullOrWhiteSpace(id)
            ? null
            : coordinator.Config.Profiles.FirstOrDefault(profile => profile.Id == id);
        var profile = existing?.Clone() ?? new StorageProfile();
        profile.Name = name.Trim();
        profile.Provider = provider;
        if (connectionString is not null)
        {
            profile.ConnectionString = connectionString;
        }

        if (string.IsNullOrWhiteSpace(profile.ConnectionString))
        {
            throw new InvalidOperationException("远程存储连接串不能为空。");
        }

        if (coordinator.RegisterProfile(profile) != SaveResult.Saved)
        {
            throw new InvalidOperationException("保存存储配置失败，请检查项目数据目录是否可写。");
        }

        return ToStorageProfileInfo(
            coordinator.Config.Profiles.First(candidate => candidate.Id == profile.Id)
        );
    }

    public async Task<bool> DeleteStorageProfileAsync(string id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (id == StorageConfig.LocalProfileId)
            {
                throw new InvalidOperationException("不能删除本地 SQLite 存储。");
            }

            var coordinator = StorageCoordinator.Current;
            var next = coordinator.Config.Clone();
            var removed = next.Profiles.RemoveAll(profile => profile.Id == id);
            if (removed == 0)
            {
                return false;
            }

            foreach (var category in StorageCategories.All)
            {
                if (next.Bindings.TryGetValue(category, out var bound) && bound == id)
                {
                    next.Bindings[category] = StorageConfig.LocalProfileId;
                }
            }

            if (coordinator.SaveConfig(next) != SaveResult.Saved)
            {
                throw new InvalidOperationException("保存存储配置失败。");
            }

            return true;
        });
    }

    public async Task<McpStorageConfigInfo> SetStorageBindingAsync(
        string category,
        string profileId
    )
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!StorageCategories.All.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"未知存储分类：{category}");
            }

            var coordinator = StorageCoordinator.Current;
            var profile =
                coordinator.Config.Profiles.FirstOrDefault(candidate => candidate.Id == profileId)
                ?? throw new KeyNotFoundException($"没有找到存储配置 {profileId}。");
            if (profile.IsRemote && string.IsNullOrWhiteSpace(profile.ConnectionString))
            {
                throw new InvalidOperationException("远程存储配置缺少连接串，不能绑定。");
            }

            var next = coordinator.Config.Clone();
            next.SetBinding(
                StorageCategories.All.First(candidate =>
                    string.Equals(candidate, category, StringComparison.OrdinalIgnoreCase)
                ),
                profileId
            );
            if (coordinator.SaveConfig(next) != SaveResult.Saved)
            {
                throw new InvalidOperationException("保存存储配置失败。");
            }

            return ToStorageInfo(coordinator.Config);
        });
    }

    public async Task<StorageTestResult> TestStorageAsync(string profileId)
    {
        return await StorageCoordinator.Current.TestAsync(profileId);
    }

    public async Task<McpStorageSyncResult> SyncStorageAsync(
        string sourceProfileId,
        string targetProfileId,
        IReadOnlyList<string> categories,
        CancellationToken cancellationToken
    )
    {
        var result = await StorageCoordinator.Current.SyncAsync(
            sourceProfileId,
            targetProfileId,
            categories,
            cancellationToken
        );
        return new McpStorageSyncResult(
            result.Success,
            result.Message,
            result.Categories,
            result.Documents,
            result.BackupPath
        );
    }

    public async Task<McpStorageSyncResult> SwitchStorageAsync(
        string targetProfileId,
        IReadOnlyList<string> categories,
        CancellationToken cancellationToken
    )
    {
        var result = await StorageCoordinator.Current.SwitchAsync(
            targetProfileId,
            categories,
            cancellationToken
        );
        return new McpStorageSyncResult(
            result.Success,
            result.Message,
            result.Categories,
            result.Documents,
            result.BackupPath
        );
    }

    public async Task<McpEnvironmentInfo> CreateEnvironmentAsync(string name)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var environment = _viewModel.CreateEnvironmentForMcp(name);
            return ToEnvironmentInfo(environment);
        });
    }

    public async Task<McpEnvironmentInfo> RenameEnvironmentAsync(Guid id, string name)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            ToEnvironmentInfo(_viewModel.RenameEnvironmentForMcp(id, name))
        );
    }

    public async Task<bool> DeleteEnvironmentAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.DeleteEnvironmentForMcp(id));
    }

    public async Task<McpEnvironmentInfo> ActivateEnvironmentAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_viewModel.ActivateEnvironmentForMcp(id))
            {
                throw new KeyNotFoundException($"没有找到 id 为 {id} 的环境。");
            }

            return ToEnvironmentInfo(
                _viewModel.Environments.First(environment => environment.Id == id)
            );
        });
    }

    public async Task<bool> DeleteVariableAsync(Guid? environmentId, string key)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            _viewModel.DeleteVariableForMcp(environmentId, key)
        );
    }

    public async Task<int> ClearHistoryAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.MoveAllHistoryToTrash());
    }

    public async Task<bool> RestoreHistoryAsync(DateTimeOffset timestamp)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            _viewModel.RestoreHistoryForMcp(timestamp)
        );
    }

    public async Task<bool> PurgeHistoryAsync(DateTimeOffset timestamp)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            _viewModel.PurgeHistoryForMcp(timestamp)
        );
    }

    public async Task<int> EmptyHistoryTrashAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.EmptyHistoryTrashForMcp());
    }

    public async Task<IReadOnlyList<McpHistoryInfo>> ListHistoryTrashAsync(int limit)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var secrets = SecretMasker.CollectAllSecrets(_viewModel.Environments);
            return (IReadOnlyList<McpHistoryInfo>)
                _viewModel
                    .RecycledHistory.Take(Math.Clamp(limit, 1, 200))
                    .Select(entry => new McpHistoryInfo(
                        entry.Timestamp,
                        entry.Name,
                        entry.Method,
                        SecretMasker.MaskSecrets(entry.Url, secrets),
                        entry.StatusCode,
                        entry.DurationMs
                    ))
                    .ToList();
        });
    }

    public async Task<int> ClearAuditAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.MoveAllAuditToTrash());
    }

    public async Task<IReadOnlyList<McpAuditSummary>> ListAuditTrashAsync(int limit)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<McpAuditSummary>)
                _viewModel
                    .AuditTrash.Take(Math.Clamp(limit, 1, 500))
                    .Select(ToAuditSummary)
                    .ToList()
        );
    }

    public async Task<bool> RestoreAuditAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.RestoreAuditForMcp(id));
    }

    public async Task<bool> PurgeAuditAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.PurgeAuditForMcp(id));
    }

    public async Task<int> EmptyAuditTrashAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.EmptyAuditTrashForMcp());
    }

    public async Task<IReadOnlyList<McpTrashInfo>> ListCollectionTrashAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<McpTrashInfo>)
                _viewModel
                    .TreeTrash.Items.Select(node => new McpTrashInfo(
                        node.Id,
                        node.KindLabel,
                        node.Name,
                        node.Source,
                        node.DeletedAt,
                        node.OriginalLocation
                    ))
                    .ToList()
        );
    }

    public async Task<bool> RestoreCollectionTrashAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.TreeTrash.RestoreById(id));
    }

    public async Task<bool> PurgeCollectionTrashAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.TreeTrash.PurgeById(id));
    }

    public async Task<int> EmptyCollectionTrashAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() => _viewModel.TreeTrash.Empty());
    }

    public async Task<string> ExportWorkspaceAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            PostmanExportService.ExportWorkspace(_viewModel.Collections.ToList())
        );
    }

    public async Task<ImportSummary> ImportContentAsync(
        string? source,
        string content,
        string? fileName,
        bool includeStaticResources,
        CancellationToken cancellationToken
    )
    {
        var summary = await Dispatcher.UIThread.InvokeAsync(() =>
            _viewModel.Import.ImportContentAsync(
                source,
                content,
                fileName,
                includeStaticResources,
                cancellationToken
            )
        );
        return summary;
    }

    public async Task<McpRequestInfo> DuplicateRequestAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var request =
                FindRequest(id) ?? throw new KeyNotFoundException($"没有找到 id 为 {id} 的请求。");
            var owner = request.Owner ?? throw new InvalidOperationException("请求没有所属集合。");
            var copy = request.Clone(copyAuthorization: true);
            copy.Name = $"{request.Name}（副本）";
            copy.Owner = owner;
            owner.Requests.Insert(owner.Requests.IndexOf(request) + 1, copy);
            _viewModel.NotifyWorkspaceChanged();
            return ToInfo(copy);
        });
    }

    private static McpSettingsInfo GetSettingsCore(AppSettings settings) =>
        new(
            settings.Theme,
            settings.CornerScale,
            settings.UiFont,
            settings.CodeFont,
            settings.CodeFontSize,
            settings.RequestTimeoutSeconds,
            settings.DefaultBodyMode,
            settings.AutoFormatResponse,
            settings.LargeResponseThresholdKb,
            settings.AuditLimit,
            settings.LogDirectory,
            settings.LogLevel,
            settings.SseThinkingPath,
            settings.SseContentPath
        );

    private static McpStorageConfigInfo ToStorageInfo(StorageConfig config) =>
        new(
            AppPaths.DatabaseFile,
            AppPaths.StorageConfigFile,
            config.Profiles.Select(ToStorageProfileInfo).ToList(),
            config.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value)
        );

    private static McpStorageProfileInfo ToStorageProfileInfo(StorageProfile profile) =>
        new(
            profile.Id,
            profile.DisplayName,
            profile.Provider,
            profile.IsLocal,
            profile.MaskedConnectionString
        );

    private McpEnvironmentInfo ToEnvironmentInfo(EnvironmentModel environment) =>
        new(
            environment.Id,
            environment.Name,
            ReferenceEquals(environment, _viewModel.ActiveEnvironment),
            environment
                .Variables.Where(row => !string.IsNullOrWhiteSpace(row.Key))
                .Select(ToVariableInfo)
                .ToList()
        );

    private static McpAuditSummary ToAuditSummary(AuditEntry entry) =>
        new(
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
        );

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
            request.UploadFieldName,
            request.TimeoutSeconds,
            ToAuthInfo(request.Auth)
        );

    private static void ApplyAuth(RequestAuth auth, McpAuthInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.AuthType))
        {
            if (!AuthKinds.IsKnownType(input.AuthType))
            {
                throw new InvalidOperationException($"未知授权类型：{input.AuthType}");
            }

            auth.AuthType = input.AuthType;
        }

        if (!string.IsNullOrWhiteSpace(input.Target))
        {
            if (!AuthKinds.IsKnownTarget(input.Target))
            {
                throw new InvalidOperationException($"未知授权位置：{input.Target}");
            }

            auth.Target = input.Target;
        }

        if (input.Username is not null)
        {
            auth.Username = input.Username;
        }

        if (input.Token is not null && input.Token != SecretMasker.Mask)
        {
            auth.Token = input.Token;
        }

        if (input.Password is not null && input.Password != SecretMasker.Mask)
        {
            auth.Password = input.Password;
        }

        if (!string.IsNullOrWhiteSpace(input.ApiKeyName))
        {
            auth.ApiKeyName = input.ApiKeyName;
        }

        if (input.ApiKeyValue is not null && input.ApiKeyValue != SecretMasker.Mask)
        {
            auth.ApiKeyValue = input.ApiKeyValue;
        }

        if (!string.IsNullOrWhiteSpace(input.ApiKeyLocation))
        {
            if (!AuthKinds.IsKnownTarget(input.ApiKeyLocation))
            {
                throw new InvalidOperationException($"未知 API Key 位置：{input.ApiKeyLocation}");
            }

            auth.ApiKeyLocation = input.ApiKeyLocation;
        }
    }

    private McpCollectionInfo ToCollectionInfo(RequestCollection collection) =>
        new(
            collection.Id,
            collection.Name,
            collection.Parent?.Id,
            collection.Depth,
            CollectionOrder(collection),
            collection.IsFavorite,
            collection.Requests.Select(ToInfo).ToList(),
            ToAuthInfo(collection.Auth)
        );

    private static McpAuthInfo ToAuthInfo(RequestAuth auth) =>
        new(
            auth.AuthType,
            auth.Target,
            auth.Username,
            auth.ApiKeyName,
            auth.ApiKeyLocation,
            !string.IsNullOrEmpty(auth.Token),
            !string.IsNullOrEmpty(auth.Password),
            !string.IsNullOrEmpty(auth.ApiKeyValue)
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

    private static McpRowInfo ToVariableInfo(KeyValueRow row) =>
        new(row.Key, row.IsSecret ? SecretMasker.Mask : row.Value, row.IsEnabled, row.IsSecret);

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
            clone.Variables.AddRow(row.Key, row.Value, row.IsEnabled, row.IsSecret);
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

    private static string NormalizeCondition(string? condition)
    {
        var value = string.IsNullOrWhiteSpace(condition) ? "Always" : condition.Trim();
        return value is "Always" or "OnPreviousSuccess" or "OnPreviousFailure" ? value : "Always";
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

    private sealed record SendSnapshot(
        RequestItem Request,
        EnvironmentModel? Environment,
        IReadOnlyList<string> Secrets
    );
}
