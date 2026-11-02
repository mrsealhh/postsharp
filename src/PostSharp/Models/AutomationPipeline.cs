using System.Text.Json.Nodes;

namespace PostSharp.Models;

/// <summary>
/// 一条 YAML/JSON 流水线：可带数据行（CSV/JSON 矩阵），按步骤顺序执行，步骤可用
/// <c>${步骤别名.字段}</c> 引用上一步响应。
/// </summary>
public sealed class AutomationPipeline
{
    public string Name { get; init; } = "未命名流水线";

    /// <summary>相对路径的接口地址会拼在这个前缀后面；同时以 <c>${baseUrl}</c> 暴露给模板。</summary>
    public string? BaseUrl { get; init; }

    public int TimeoutSeconds { get; init; } = 60;

    public bool StopOnError { get; init; } = true;

    public PipelineRetryPolicy Retry { get; init; } = new();

    public IReadOnlyDictionary<string, string> Variables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<PipelineNode> Steps { get; init; } = Array.Empty<PipelineNode>();

    /// <summary>数据驱动行；为空表示整条流水线只跑一次。</summary>
    public IReadOnlyList<PipelineDataRow> Data { get; init; } = Array.Empty<PipelineDataRow>();

    public string? SourcePath { get; init; }

    public string? SourceDirectory { get; init; }
}

public enum PipelineNodeKind
{
    Request,
    Loop,
}

/// <summary>流水线节点：一次 HTTP 请求，或一段对数组逐项执行的循环。</summary>
public sealed class PipelineNode
{
    public PipelineNodeKind Kind { get; init; } = PipelineNodeKind.Request;

    /// <summary>步骤别名；执行成功后整段响应 JSON 以它注册，供后续 <c>${别名.路径}</c> 使用。</summary>
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string Method { get; init; } = "GET";

    public string? Url { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();

    /// <summary>结构化请求体；与 <see cref="RawBody"/> 二选一。</summary>
    public JsonNode? JsonBody { get; init; }

    public string? RawBody { get; init; }

    public string? ContentType { get; init; }

    public PipelineExpectation Expectation { get; init; } = new();

    public PipelineRetryPolicy? Retry { get; init; }

    /// <summary>循环数据源表达式，例如 <c>${list.items}</c>，解析结果必须是数组。</summary>
    public string? LoopOver { get; init; }

    /// <summary>循环项在模板中的名字，例如 <c>item</c> 可用 <c>${item.id}</c>。</summary>
    public string? LoopAlias { get; init; }

    /// <summary>循环下标的模板名，例如 <c>i</c> 可用 <c>${i}</c>。</summary>
    public string? LoopIndexAlias { get; init; }

    public int LoopMax { get; init; } = 1000;

    public IReadOnlyList<PipelineNode> Children { get; init; } = Array.Empty<PipelineNode>();

    /// <summary>失败后的动作：<c>stop</c>（默认跟随流水线）或 <c>continue</c>。</summary>
    public string? OnFailure { get; init; }
}

public sealed class PipelineExpectation
{
    /// <summary>可接受的状态码；为空时接受任意 2xx。</summary>
    public IReadOnlyList<int> Statuses { get; init; } = Array.Empty<int>();

    public IReadOnlyList<string> BodyContains { get; init; } = Array.Empty<string>();

    public IReadOnlyList<PipelineJsonAssertion> JsonPath { get; init; } =
        Array.Empty<PipelineJsonAssertion>();
}

public sealed class PipelineJsonAssertion
{
    public string Path { get; init; } = string.Empty;

    /// <summary>equals / exists / contains。</summary>
    public string Kind { get; init; } = "equals";

    public JsonNode? Expected { get; init; }
}

public sealed class PipelineRetryPolicy
{
    public const int MaxCount = 10;

    /// <summary>额外重试次数；0 表示不重试。</summary>
    public int Count { get; init; }

    public int DelayMs { get; init; } = 500;

    /// <summary>触发重试的状态码；为空时使用 408/429/500/502/503/504。</summary>
    public IReadOnlyList<int> Statuses { get; init; } = Array.Empty<int>();

    public bool OnNetworkError { get; init; } = true;

    public IReadOnlyList<int> EffectiveStatuses =>
        Statuses.Count > 0 ? Statuses : new[] { 408, 429, 500, 502, 503, 504 };
}

public sealed record PipelineDataRow(int Index, JsonNode? Value);

public sealed class PipelineRunResult
{
    public string Name { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public long ElapsedMs { get; init; }

    public IReadOnlyList<PipelineCaseResult> Cases { get; init; } =
        Array.Empty<PipelineCaseResult>();

    public string? ReportFileName { get; init; }

    public string? ReportPath { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class PipelineCaseResult
{
    public int Index { get; init; }

    public string Label { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public long ElapsedMs { get; init; }

    public IReadOnlyList<PipelineStepResult> Steps { get; init; } =
        Array.Empty<PipelineStepResult>();
}

public sealed class PipelineStepResult
{
    public string? Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public int? StatusCode { get; init; }

    public bool Passed { get; init; }

    public int Attempts { get; init; }

    public long ElapsedMs { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<string> Assertions { get; init; } = Array.Empty<string>();
}

public sealed record PipelineIssue(string Path, string Message, bool IsError);

public sealed record McpPipelineValidation(
    string Name,
    bool Valid,
    int StepCount,
    int CaseCount,
    IReadOnlyList<PipelineIssue> Issues
);
