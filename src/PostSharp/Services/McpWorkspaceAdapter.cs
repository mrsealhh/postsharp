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
    IReadOnlyList<McpRowInfo> Headers);

public sealed record McpCollectionInfo(Guid Id, string Name, IReadOnlyList<McpRequestInfo> Requests);

public sealed record McpEnvironmentInfo(
    Guid Id,
    string Name,
    bool Active,
    IReadOnlyList<McpRowInfo> Variables);

public sealed record McpHistoryInfo(
    DateTimeOffset Time,
    string Name,
    string Method,
    string Url,
    int StatusCode,
    long DurationMs);

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
    int SseEventCount);

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
    string SseContent = "");

public sealed record McpCallContext(string Tool, string? Client);

public sealed record McpSuiteStepInput(Guid RequestId, int? ExpectedStatus, string? BodyContains);

public sealed record McpSequenceStep(Guid RequestId, string Name, int StatusCode, string StatusText, string? Error);

public sealed record McpSequenceResult(
    int Total,
    int Succeeded,
    int Failed,
    bool Stopped,
    IReadOnlyList<McpSequenceStep> Steps);

public sealed record McpRequestDraft(
    Guid? CollectionId,
    string? Name,
    string? Method,
    string? Url,
    string? BodyMode,
    string? Body,
    IReadOnlyList<McpRowInfo>? Params,
    IReadOnlyList<McpRowInfo>? Headers);

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
            (IReadOnlyList<McpCollectionInfo>)_viewModel.Collections
                .Select(collection => new McpCollectionInfo(
                    collection.Id,
                    collection.Name,
                    collection.Requests.Select(ToInfo).ToList()))
                .ToList());
    }

    public async Task<McpRequestInfo?> GetRequestAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var request = FindRequest(id);
            return request is null ? null : ToInfo(request);
        });
    }

    public async Task<McpCollectionInfo> CreateCollectionAsync(string name)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var collection = new RequestCollection
            {
                Name = string.IsNullOrWhiteSpace(name) ? "MCP 集合" : name.Trim()
            };

            _viewModel.Collections.Add(collection);
            _viewModel.ToggleCollection(collection, true);
            _viewModel.NotifyWorkspaceChanged();
            return new McpCollectionInfo(collection.Id, collection.Name, Array.Empty<McpRequestInfo>());
        });
    }

    public async Task<McpRequestInfo> CreateRequestAsync(McpRequestDraft draft)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var collection = draft.CollectionId is { } collectionId
                ? _viewModel.Collections.FirstOrDefault(candidate => candidate.Id == collectionId)
                : null;

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
                Owner = collection
            };

            ReplaceRows(request.Params, draft.Params);
            ReplaceRows(request.Headers, draft.Headers);
            collection.Requests.Add(request);
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

            if (draft.CollectionId is { } targetCollectionId)
            {
                var target = _viewModel.Collections.FirstOrDefault(
                    candidate => candidate.Id == targetCollectionId);

                if (target is not null && !ReferenceEquals(request.Owner, target))
                {
                    request.Owner?.Requests.Remove(request);
                    request.Owner = target;
                    target.Requests.Add(request);
                    _viewModel.ToggleCollection(target, true);
                }
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
        CancellationToken cancellationToken)
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

        snapshot.Request.Name = await Dispatcher.UIThread.InvokeAsync(() => FindRequest(id)?.Name ?? snapshot.Request.Name);

        ComposedRequest? composed = null;
        HttpResult result;
        try
        {
            composed = RequestComposer.Compose(snapshot.Request, snapshot.Environment);
            result = await _http.SendAsync(composed, cancellationToken);
        }
        catch (Exception exception)
        {
            result = HttpRequestService.Failure(exception.Message);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _viewModel.RecordExternalSend(snapshot.Request, composed, result, "MCP", context.Tool, context.Client);
            return true;
        });

        var headers = result.Headers
            .Select(header => new McpRowInfo(header.Key, header.Value, true))
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
            result.SseContent);
    }

    public async Task<McpSequenceResult> RunSequenceAsync(
        IReadOnlyList<Guid> requestIds,
        bool stopOnError,
        McpCallContext context,
        CancellationToken cancellationToken)
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
                steps.Add(new McpSequenceStep(
                    result.RequestId,
                    result.Name,
                    result.StatusCode,
                    result.StatusText,
                    result.Error));

                if (stopOnError && (result.Error is not null || result.StatusCode >= 400))
                {
                    stopped = true;
                    break;
                }
            }
            catch (Exception ex)
            {
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
            Succeeded: steps.Count(step => step.Error is null && step.StatusCode is >= 200 and < 400),
            Failed: steps.Count(step => step.Error is not null || step.StatusCode >= 400 || step.StatusCode == 0),
            Stopped: stopped,
            Steps: steps);
    }

    public async Task<string> GetCurlAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var request = FindRequest(id)
                          ?? throw new KeyNotFoundException($"没有找到 id 为 {id} 的请求。");
            return RequestComposer.BuildCurl(request, _viewModel.ActiveEnvironment);
        });
    }

    public async Task<IReadOnlyList<McpAuditSummary>> ListAuditAsync(int limit, string? source)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<McpAuditSummary>)_viewModel.Audit
                .Where(entry => string.IsNullOrWhiteSpace(source)
                                || entry.Source.Equals(source, StringComparison.OrdinalIgnoreCase))
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
                    entry.SseEventCount))
                .ToList());
    }

    public async Task<AuditEntry?> GetAuditAsync(Guid id)
    {
        return await Dispatcher.UIThread.InvokeAsync(
            () => _viewModel.Audit.FirstOrDefault(entry => entry.Id == id));
    }

    public async Task<IReadOnlyList<TestSuite>> ListSuitesAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(
            () => (IReadOnlyList<TestSuite>)_viewModel.Suites.ToList());
    }

    public async Task<TestSuite> CreateSuiteAsync(
        string name,
        IReadOnlyList<McpSuiteStepInput> steps,
        bool stopOnError)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (steps.Count == 0)
            {
                throw new InvalidOperationException("steps 不能为空。");
            }

            var suite = new TestSuite
            {
                Name = string.IsNullOrWhiteSpace(name) ? "MCP 测试套件" : name.Trim(),
                StopOnError = stopOnError
            };

            foreach (var stepInput in steps)
            {
                var request = FindRequest(stepInput.RequestId)
                              ?? throw new KeyNotFoundException(
                                  $"没有找到 id 为 {stepInput.RequestId} 的请求。");

                suite.Steps.Add(new TestStep
                {
                    RequestId = request.Id,
                    RequestName = request.Name,
                    Method = request.Method,
                    Url = request.Url,
                    ExpectedStatus = stepInput.ExpectedStatus,
                    BodyContains = stepInput.BodyContains
                });
            }

            _viewModel.Suites.Add(suite);
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
        CancellationToken cancellationToken)
    {
        return await _viewModel.RunSuiteForMcpAsync(id, context.Client, cancellationToken);
    }

    public async Task<IReadOnlyList<McpHistoryInfo>> ListHistoryAsync(int limit)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<McpHistoryInfo>)_viewModel.History
                .Take(Math.Clamp(limit, 1, 200))
                .Select(entry => new McpHistoryInfo(
                    entry.Timestamp,
                    entry.Name,
                    entry.Method,
                    entry.Url,
                    entry.StatusCode,
                    entry.DurationMs))
                .ToList());
    }

    public async Task<IReadOnlyList<McpEnvironmentInfo>> ListEnvironmentsAsync()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
            (IReadOnlyList<McpEnvironmentInfo>)_viewModel.Environments
                .Select(environment => new McpEnvironmentInfo(
                    environment.Id,
                    environment.Name,
                    ReferenceEquals(environment, _viewModel.ActiveEnvironment),
                    environment.Variables
                        .Where(row => !string.IsNullOrWhiteSpace(row.Key))
                        .Select(ToInfo)
                        .ToList()))
                .ToList());
    }

    public async Task<McpEnvironmentInfo> SetVariableAsync(
        Guid? environmentId,
        string key,
        string value,
        bool? enabled)
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

            var row = environment.Variables.FirstOrDefault(
                candidate => candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

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
                environment.Variables
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Key))
                    .Select(ToInfo)
                    .ToList());
        });
    }

    private RequestItem? FindRequest(Guid id) =>
        _viewModel.Collections
            .SelectMany(collection => collection.Requests)
            .FirstOrDefault(request => request.Id == id);

    private static McpRequestInfo ToInfo(RequestItem request) => new(
        request.Id,
        request.Name,
        request.Method,
        request.Url,
        request.BodyMode,
        request.Body,
        request.Owner?.Id,
        request.Owner?.Name ?? string.Empty,
        request.Params
            .Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .Select(ToInfo)
            .ToList(),
        request.Headers
            .Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .Select(ToInfo)
            .ToList());

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
            _ => "None"
        };
    }

    private sealed record SendSnapshot(RequestItem Request, EnvironmentModel? Environment);
}
