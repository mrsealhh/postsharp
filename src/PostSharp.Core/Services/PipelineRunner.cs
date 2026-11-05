using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed record PipelineRunOptions
{
    public string? BaseUrl { get; init; }

    public IReadOnlyDictionary<string, string> Variables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool DryRun { get; init; }

    public Action<string>? Progress { get; init; }
}

/// <summary>
/// 执行流水线：按数据行展开，顺序（或循环）执行步骤，步骤结果按别名登记，
/// 后续步骤可用 <c>${别名.路径}</c> 引用；支持断言、重试和失败即停。
/// </summary>
public sealed class PipelineRunner
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    private readonly IPipelineHttpClient _http;

    public PipelineRunner(IPipelineHttpClient? http = null)
    {
        _http = http ?? new HttpPipelineClient();
    }

    public async Task<PipelineRunResult> RunAsync(
        AutomationPipeline pipeline,
        PipelineRunOptions options,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var notes = new List<string>();
        var baseUrl = (options.BaseUrl ?? pipeline.BaseUrl)?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            notes.Add("未配置 baseUrl，步骤必须使用完整地址。");
        }

        var cases = new List<PipelineCaseResult>();
        var rows =
            pipeline.Data.Count == 0
                ? new List<PipelineDataRow> { new(0, null) }
                : pipeline.Data.ToList();

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roots = BuildRoots(pipeline, options, baseUrl, row);
            var label = LabelFor(row);
            options.Progress?.Invoke($"开始执行 {label}");
            var caseResult = await RunCaseAsync(
                pipeline,
                options,
                roots,
                row,
                label,
                baseUrl,
                notes,
                cancellationToken
            );
            cases.Add(caseResult);
        }

        stopwatch.Stop();
        var passed = cases.All(item => item.Passed);
        return new PipelineRunResult
        {
            Name = pipeline.Name,
            Passed = passed,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Cases = cases,
            Notes = notes,
        };
    }

    private async Task<PipelineCaseResult> RunCaseAsync(
        AutomationPipeline pipeline,
        PipelineRunOptions options,
        Dictionary<string, JsonNode?> roots,
        PipelineDataRow row,
        string label,
        string? baseUrl,
        List<string> notes,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new List<PipelineStepResult>();
        var stopped = false;
        for (var index = 0; index < pipeline.Steps.Count; index++)
        {
            var node = pipeline.Steps[index];
            var (stepResults, failed) = await ExecuteNodeAsync(
                pipeline,
                options,
                node,
                roots,
                baseUrl,
                $"steps[{index}]",
                cancellationToken
            );
            results.AddRange(stepResults);
            if (failed && pipeline.StopOnError && node.OnFailure != "continue")
            {
                stopped = true;
                notes.Add($"{label}：第 {index + 1} 个节点失败后按 stopOnError 停止。");
                break;
            }
        }

        stopwatch.Stop();
        if (stopped && results.Count > 0)
        {
            notes.Add($"{label}：执行了 {results.Count} 个步骤。");
        }

        return new PipelineCaseResult
        {
            Index = row.Index,
            Label = label,
            Passed = results.All(item => item.Passed) && !stopped,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Steps = results,
        };
    }

    private async Task<(IReadOnlyList<PipelineStepResult> Results, bool Failed)> ExecuteNodeAsync(
        AutomationPipeline pipeline,
        PipelineRunOptions options,
        PipelineNode node,
        Dictionary<string, JsonNode?> roots,
        string? baseUrl,
        string location,
        CancellationToken cancellationToken
    )
    {
        if (node.Kind == PipelineNodeKind.Loop)
        {
            return await ExecuteLoopAsync(
                pipeline,
                options,
                node,
                roots,
                baseUrl,
                location,
                cancellationToken
            );
        }

        if (node.Kind == PipelineNodeKind.Parallel)
        {
            return await ExecuteParallelAsync(
                pipeline,
                options,
                node,
                roots,
                baseUrl,
                location,
                cancellationToken
            );
        }

        var result = await ExecuteRequestAsync(
            pipeline,
            options,
            node,
            roots,
            baseUrl,
            location,
            cancellationToken
        );
        return (new[] { result }, !result.Passed);
    }

    private async Task<(
        IReadOnlyList<PipelineStepResult> Results,
        bool Failed
    )> ExecuteParallelAsync(
        AutomationPipeline pipeline,
        PipelineRunOptions options,
        PipelineNode node,
        Dictionary<string, JsonNode?> roots,
        string? baseUrl,
        string location,
        CancellationToken cancellationToken
    )
    {
        var parallelism = Math.Clamp(
            node.Parallelism > 0 ? node.Parallelism : pipeline.MaxParallelism,
            1,
            64
        );
        var count = node.Children.Count;
        var resultsByIndex = new List<PipelineStepResult>[count];
        var failedByIndex = new bool[count];
        var capturesByIndex = new Dictionary<string, JsonNode?>[count];
        var originalKeys = roots.Keys.ToHashSet(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(parallelism, parallelism);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new List<Task>(count);
        for (var index = 0; index < count; index++)
        {
            var childIndex = index;
            var child = node.Children[childIndex];
            tasks.Add(
                Task.Run(
                    async () =>
                    {
                        try
                        {
                            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            resultsByIndex[childIndex] = new List<PipelineStepResult>
                            {
                                FailedStep(child, child.Url ?? string.Empty, 0, "已取消。"),
                            };
                            failedByIndex[childIndex] = true;
                            return;
                        }

                        try
                        {
                            var localRoots = new Dictionary<string, JsonNode?>(
                                roots,
                                StringComparer.Ordinal
                            );
                            var (childResults, childFailed) = await ExecuteNodeAsync(
                                    pipeline,
                                    options,
                                    child,
                                    localRoots,
                                    baseUrl,
                                    $"{location}.parallel.steps[{childIndex}]",
                                    linked.Token
                                )
                                .ConfigureAwait(false);
                            resultsByIndex[childIndex] = childResults.ToList();
                            failedByIndex[childIndex] = childFailed;
                            capturesByIndex[childIndex] = localRoots;
                            if (
                                childFailed
                                && pipeline.StopOnError
                                && child.OnFailure != "continue"
                            )
                            {
                                linked.Cancel();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            resultsByIndex[childIndex] = new List<PipelineStepResult>
                            {
                                FailedStep(child, child.Url ?? string.Empty, 0, "已取消。"),
                            };
                            failedByIndex[childIndex] = true;
                        }
                        catch (Exception exception)
                        {
                            resultsByIndex[childIndex] = new List<PipelineStepResult>
                            {
                                FailedStep(child, child.Url ?? string.Empty, 0, exception.Message),
                            };
                            failedByIndex[childIndex] = true;
                        }
                        finally
                        {
                            gate.Release();
                        }
                    },
                    CancellationToken.None
                )
            );
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var results = new List<PipelineStepResult>();
        for (var index = 0; index < count; index++)
        {
            if (resultsByIndex[index] is { } childResults)
            {
                results.AddRange(childResults);
            }

            if (capturesByIndex[index] is { } localRoots)
            {
                foreach (var entry in localRoots)
                {
                    if (!originalKeys.Contains(entry.Key))
                    {
                        roots[entry.Key] = entry.Value;
                    }
                }
            }
        }

        return (results, failedByIndex.Any(failed => failed));
    }

    private async Task<(IReadOnlyList<PipelineStepResult> Results, bool Failed)> ExecuteLoopAsync(
        AutomationPipeline pipeline,
        PipelineRunOptions options,
        PipelineNode node,
        Dictionary<string, JsonNode?> roots,
        string? baseUrl,
        string location,
        CancellationToken cancellationToken
    )
    {
        var results = new List<PipelineStepResult>();
        var alias = node.LoopAlias ?? "item";
        var overExpression = node.LoopOver ?? string.Empty;
        JsonNode? source;
        try
        {
            source = PipelineTemplate.Evaluate(overExpression, roots, $"{location}.loop.over");
        }
        catch (PipelineRunException exception)
        {
            results.Add(FailedStep(node, overExpression, 0, exception.Message));
            return (results, true);
        }

        if (source is not JsonArray items)
        {
            results.Add(
                FailedStep(
                    node,
                    overExpression,
                    0,
                    $"循环数据源 {overExpression} 不是数组，请检查 ${alias} 的来源。"
                )
            );
            return (results, true);
        }

        if (items.Count > node.LoopMax)
        {
            results.Add(
                FailedStep(
                    node,
                    overExpression,
                    0,
                    $"循环项 {items.Count} 个，超过上限 {node.LoopMax}；如需继续请调大 loop.max。"
                )
            );
            return (results, true);
        }

        var failed = false;
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            roots[alias] = items[index]?.DeepClone();
            if (node.LoopIndexAlias is { Length: > 0 } indexAlias)
            {
                roots[indexAlias] = JsonValue.Create(index);
            }

            for (var child = 0; child < node.Children.Count; child++)
            {
                var childNode = node.Children[child];
                var (childResults, childFailed) = await ExecuteNodeAsync(
                    pipeline,
                    options,
                    childNode,
                    roots,
                    baseUrl,
                    $"{location}.loop.steps[{child}]",
                    cancellationToken
                );
                results.AddRange(childResults);
                if (childFailed)
                {
                    failed = true;
                    if (pipeline.StopOnError && childNode.OnFailure != "continue")
                    {
                        return (results, true);
                    }
                }
            }
        }

        return (results, failed);
    }

    private async Task<PipelineStepResult> ExecuteRequestAsync(
        AutomationPipeline pipeline,
        PipelineRunOptions options,
        PipelineNode node,
        Dictionary<string, JsonNode?> roots,
        string? baseUrl,
        string location,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var url = AppendQuery(
                ResolveUrl(node.Url ?? string.Empty, baseUrl, roots, $"{location}.url"),
                node.Query,
                roots,
                $"{location}.query"
            );
            var headers = ResolveHeaders(pipeline, node, roots, $"{location}.headers");
            headers = ApplyBasicAuth(node, headers, roots, $"{location}.auth");
            var body = BuildBody(node, roots, location, out var contentType);
            if (options.DryRun)
            {
                stopwatch.Stop();
                return new PipelineStepResult
                {
                    Id = node.Id,
                    Name = node.Name ?? node.Method,
                    Method = node.Method,
                    Url = url,
                    Passed = true,
                    Attempts = 0,
                    ElapsedMs = stopwatch.ElapsedMilliseconds,
                    Assertions = new[] { "dryRun：未发送" },
                };
            }

            var retry = node.Retry ?? pipeline.Retry;
            var maxAttempts = 1 + retry.Count;
            var timeoutSeconds =
                node.TimeoutSeconds > 0 ? node.TimeoutSeconds : pipeline.TimeoutSeconds;
            PipelineHttpResponse? response = null;
            var attempts = 0;
            IReadOnlyList<PipelineAssertionOutcome> outcomes =
                Array.Empty<PipelineAssertionOutcome>();
            while (attempts < maxAttempts)
            {
                attempts++;
                response = await _http.SendAsync(
                    new PipelineHttpRequest(
                        node.Method,
                        url,
                        headers,
                        body,
                        contentType,
                        node.FollowRedirects
                    ),
                    timeoutSeconds,
                    cancellationToken
                );
                outcomes = EvaluateAssertions(node, response);
                var accepted = outcomes.All(outcome => outcome.Passed);
                if (accepted || !ShouldRetry(retry, response!) || attempts >= maxAttempts)
                {
                    break;
                }

                if (retry.DelayMs > 0)
                {
                    await Task.Delay(retry.DelayMs, cancellationToken);
                }
            }

            stopwatch.Stop();
            if (response is null)
            {
                return FailedStep(node, url, attempts, "请求没有返回结果。");
            }

            if (node.Id is { Length: > 0 } alias)
            {
                roots[alias] = ParseBody(response);
            }

            var passed = outcomes.All(outcome => outcome.Passed);
            var failures = outcomes
                .Where(outcome => !outcome.Passed)
                .Select(outcome => outcome.Message)
                .ToList();
            return new PipelineStepResult
            {
                Id = node.Id,
                Name = node.Name ?? node.Method,
                Method = node.Method,
                Url = url,
                StatusCode = response.HasResponse ? response.StatusCode : null,
                Passed = passed,
                Attempts = attempts,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                Error = passed ? null : string.Join("；", failures),
                Assertions = outcomes.Select(outcome => outcome.Message).ToList(),
            };
        }
        catch (PipelineRunException exception)
        {
            stopwatch.Stop();
            return FailedStep(node, node.Url ?? string.Empty, 0, exception.Message);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return FailedStep(node, node.Url ?? string.Empty, 0, "已取消。");
        }
        catch (HttpRequestException exception)
        {
            stopwatch.Stop();
            return FailedStep(node, node.Url ?? string.Empty, 0, $"网络错误：{exception.Message}");
        }
    }

    private static Dictionary<string, JsonNode?> BuildRoots(
        AutomationPipeline pipeline,
        PipelineRunOptions options,
        string? baseUrl,
        PipelineDataRow row
    )
    {
        var roots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["baseUrl"] = JsonValue.Create(baseUrl ?? string.Empty),
            ["row"] = row.Value?.DeepClone(),
            ["data"] = row.Value?.DeepClone(),
        };
        foreach (var variable in pipeline.Variables)
        {
            roots[variable.Key] = JsonValue.Create(variable.Value);
        }

        foreach (var variable in options.Variables)
        {
            roots[variable.Key] = JsonValue.Create(variable.Value);
        }

        if (row.Value is JsonObject rowObject)
        {
            foreach (var entry in rowObject)
            {
                roots[entry.Key] = entry.Value?.DeepClone();
            }
        }

        return roots;
    }

    private static string ResolveUrl(
        string template,
        string? baseUrl,
        IReadOnlyDictionary<string, JsonNode?> roots,
        string location
    )
    {
        var resolved = PipelineTemplate.ResolveText(template, roots, location).Trim();
        if (
            Uri.TryCreate(resolved, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
        )
        {
            return resolved;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new PipelineRunException(
                $"{location}：{resolved} 不是完整地址，且流水线没有配置 baseUrl。"
            );
        }

        return $"{baseUrl!.TrimEnd('/')}/{resolved.TrimStart('/')}";
    }

    private static string AppendQuery(
        string url,
        IReadOnlyList<KeyValuePair<string, string>> query,
        IReadOnlyDictionary<string, JsonNode?> roots,
        string location
    )
    {
        if (query.Count == 0)
        {
            return url;
        }

        var parts = query
            .Select(item =>
                $"{Uri.EscapeDataString(PipelineTemplate.ResolveText(item.Key, roots, location))}"
                + $"={Uri.EscapeDataString(PipelineTemplate.ResolveText(item.Value, roots, location))}"
            )
            .ToList();
        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{url}{separator}{string.Join("&", parts)}";
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ApplyBasicAuth(
        PipelineNode node,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        IReadOnlyDictionary<string, JsonNode?> roots,
        string location
    )
    {
        if (node.BasicAuth is not { } auth)
        {
            return headers;
        }

        if (
            headers.Any(header =>
                header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return headers;
        }

        var username = PipelineTemplate.ResolveText(auth.Username, roots, $"{location}.username");
        var password = PipelineTemplate.ResolveText(auth.Password, roots, $"{location}.password");
        var token = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{username}:{password}")
        );
        return headers
            .Concat(new[] { new KeyValuePair<string, string>("Authorization", $"Basic {token}") })
            .ToList();
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ResolveHeaders(
        AutomationPipeline pipeline,
        PipelineNode node,
        IReadOnlyDictionary<string, JsonNode?> roots,
        string location
    )
    {
        var merged = new List<KeyValuePair<string, string>>();
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in pipeline.Headers.Concat(node.Headers))
        {
            if (positions.TryGetValue(header.Key, out var position))
            {
                merged[position] = header;
                continue;
            }

            positions[header.Key] = merged.Count;
            merged.Add(header);
        }

        return merged
            .Select(header => new KeyValuePair<string, string>(
                header.Key,
                PipelineTemplate.ResolveText(header.Value, roots, $"{location}.{header.Key}")
            ))
            .ToList();
    }

    private static string? BuildBody(
        PipelineNode node,
        IReadOnlyDictionary<string, JsonNode?> roots,
        string location,
        out string? contentType
    )
    {
        if (node.JsonBody is not null)
        {
            contentType = node.ContentType ?? "application/json";
            var resolved = PipelineTemplate.ResolveNode(node.JsonBody, roots, $"{location}.json");
            return resolved?.ToJsonString(CompactJson) ?? "null";
        }

        if (node.RawBody is not null)
        {
            contentType = node.ContentType;
            return PipelineTemplate.ResolveText(node.RawBody, roots, $"{location}.body");
        }

        if (node.Form.Count > 0)
        {
            contentType = node.ContentType ?? "application/x-www-form-urlencoded";
            return string.Join(
                "&",
                node.Form.Select(field =>
                    $"{Uri.EscapeDataString(PipelineTemplate.ResolveText(field.Key, roots, $"{location}.form"))}"
                    + $"={Uri.EscapeDataString(PipelineTemplate.ResolveText(field.Value, roots, $"{location}.form"))}"
                )
            );
        }

        contentType = node.ContentType;
        return null;
    }

    private static JsonNode? ParseBody(PipelineHttpResponse response)
    {
        if (!response.HasResponse || string.IsNullOrWhiteSpace(response.Body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(response.Body);
        }
        catch (JsonException)
        {
            return JsonValue.Create(response.Body);
        }
    }

    private static List<PipelineAssertionOutcome> EvaluateAssertions(
        PipelineNode node,
        PipelineHttpResponse response
    )
    {
        var messages = new List<PipelineAssertionOutcome>();
        if (!response.HasResponse)
        {
            messages.Add(
                new PipelineAssertionOutcome($"请求失败：{response.Error ?? "没有响应"}", false)
            );
            return messages;
        }

        var expectation = node.Expectation;
        var statusOk =
            expectation.Statuses.Count == 0
                ? response.StatusCode is >= 200 and <= 299
                : expectation.Statuses.Contains(response.StatusCode);
        messages.Add(
            new PipelineAssertionOutcome(
                statusOk
                    ? $"状态码 {response.StatusCode}"
                    : $"状态码 {response.StatusCode}，期望 {DescribeStatuses(expectation)}",
                statusOk
            )
        );

        foreach (var contains in expectation.BodyContains)
        {
            var found = response.Body.Contains(contains, StringComparison.Ordinal);
            messages.Add(
                new PipelineAssertionOutcome(
                    found ? $"响应包含「{contains}」" : $"响应缺少「{contains}」",
                    found
                )
            );
        }

        if (expectation.JsonPath.Count == 0)
        {
            return messages;
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(response.Body);
        }
        catch (JsonException)
        {
            messages.Add(
                new PipelineAssertionOutcome("响应不是 JSON，无法执行 jsonPath 断言", false)
            );
            return messages;
        }

        foreach (var assertion in expectation.JsonPath)
        {
            var actual = PipelineTemplate.GetByPath(parsed, assertion.Path);
            messages.Add(CheckAssertion(assertion, actual));
        }

        return messages;
    }

    private static PipelineAssertionOutcome CheckAssertion(
        PipelineJsonAssertion assertion,
        JsonNode? actual
    )
    {
        switch (assertion.Kind)
        {
            case "exists":
                var expectedExists =
                    assertion.Expected is JsonValue value && value.TryGetValue<bool>(out var flag)
                        ? flag
                        : true;
                var exists = actual is not null;
                return new PipelineAssertionOutcome(
                    exists == expectedExists
                        ? $"jsonPath {assertion.Path} {(exists ? "存在" : "不存在")}"
                        : $"jsonPath {assertion.Path} {(exists ? "存在" : "不存在")}，期望 {(expectedExists ? "存在" : "不存在")}",
                    exists == expectedExists
                );
            case "contains":
                var expectedText = assertion.Expected?.ToJsonString() ?? string.Empty;
                var actualText = actual?.ToJsonString() ?? string.Empty;
                var contains = actualText.Contains(expectedText, StringComparison.Ordinal);
                return new PipelineAssertionOutcome(
                    contains
                        ? $"jsonPath {assertion.Path} 包含期望值"
                        : $"jsonPath {assertion.Path} = {actualText}，不包含 {expectedText}",
                    contains
                );
            default:
                var equals = assertion.Expected is null
                    ? actual is null
                    : JsonNode.DeepEquals(assertion.Expected, actual);
                return new PipelineAssertionOutcome(
                    equals
                        ? $"jsonPath {assertion.Path} 等于期望值"
                        : $"jsonPath {assertion.Path} = {actual?.ToJsonString() ?? "null"}，期望 {assertion.Expected?.ToJsonString() ?? "null"}",
                    equals
                );
        }
    }

    private static bool ShouldRetry(PipelineRetryPolicy retry, PipelineHttpResponse response)
    {
        if (!response.HasResponse)
        {
            return retry.OnNetworkError;
        }

        return retry.EffectiveStatuses.Contains(response.StatusCode);
    }

    private static string DescribeStatuses(PipelineExpectation expectation) =>
        expectation.Statuses.Count == 0
            ? "2xx"
            : string.Join(
                "/",
                expectation.Statuses.Select(status => status.ToString(CultureInfo.InvariantCulture))
            );

    private static PipelineStepResult FailedStep(
        PipelineNode node,
        string url,
        int attempts,
        string error
    ) =>
        new()
        {
            Id = node.Id,
            Name = node.Name ?? node.Method,
            Method = node.Method,
            Url = url,
            Passed = false,
            Attempts = attempts,
            Error = error,
            Assertions = Array.Empty<string>(),
        };

    private static string LabelFor(PipelineDataRow row)
    {
        if (row.Value is not JsonObject values)
        {
            return "第 1 组";
        }

        var preview = values
            .Where(entry => entry.Value is not null)
            .Take(3)
            .Select(entry => $"{entry.Key}={entry.Value?.ToJsonString()}")
            .ToList();
        return preview.Count == 0
            ? $"第 {row.Index + 1} 组"
            : $"第 {row.Index + 1} 组（{string.Join(", ", preview)}）";
    }

    private readonly record struct PipelineAssertionOutcome(string Message, bool Passed);
}
