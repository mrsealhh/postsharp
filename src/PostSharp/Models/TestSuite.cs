using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace PostSharp.Models;

/// <summary>A saved automation suite: an ordered list of requests with optional assertions.</summary>
public partial class TestSuite : ObservableObject
{
    public const int RunHistoryLimit = 20;

    public Guid Id { get; init; } = Guid.NewGuid();

    [ObservableProperty]
    private string _name = "新建套件";

    [ObservableProperty]
    private string _lastSummary = "尚未运行";

    [ObservableProperty]
    private bool _runInParallel;

    [ObservableProperty]
    private int _scheduleMinutes;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSelected;

    /// <summary>Maximum steps running at the same time in workflow (DAG) mode; 0 means no extra limit.</summary>
    public int MaxParallelism { get; set; } = 4;

    public bool StopOnError { get; set; } = true;

    public List<TestStep> Steps { get; } = new();

    /// <summary>Most recent runs, newest first. Persisted with the suite for quick comparison.</summary>
    [JsonIgnore]
    public ObservableCollection<TestRunResult> Runs { get; } = new();

    /// <summary>Optional visual graph used to author the suite.</summary>
    public SuiteWorkflow? Workflow { get; set; }

    public override string ToString() => Name;
}

public sealed class TestStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RequestId { get; set; }

    public string RequestName { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    /// <summary>Delay before the following step, in milliseconds.</summary>
    public int DelayMs { get; set; } = 200;

    public int RetryCount { get; set; }

    public int RetryDelayMs { get; set; } = 500;

    /// <summary>Always, OnPreviousSuccess, OnPreviousFailure or Expression.</summary>
    public string Condition { get; set; } = "Always";

    /// <summary>Details used when <see cref="Condition"/> is Expression.</summary>
    public SuiteCondition ConditionRules { get; } = new();

    /// <summary>
    /// Workflow mode: All waits for every incoming branch, Any starts once one branch is taken.
    /// </summary>
    public string JoinMode { get; set; } = "All";

    /// <summary>Steps with the same non-zero group may run concurrently.</summary>
    public int ParallelGroup { get; set; }

    public List<InputMapping> InputMappings { get; } = new();

    public List<TestAssertion> Assertions { get; } = new();

    public int? ExpectedStatus { get; set; }

    public string? BodyContains { get; set; }

    public BadgeTone MethodTone => MethodTones.Of(Method);

    public string ExpectationText =>
        ExpectedStatus is null
        && string.IsNullOrWhiteSpace(BodyContains)
        && Assertions.Count == 0
        && RetryCount == 0
        && Condition == "Always"
        && ConditionRules.IsEmpty
        && ParallelGroup == 0
            ? "仅检查网络成功"
            : string.Join(
                " · ",
                new[]
                {
                    ExpectedStatus is null ? null : $"状态 {ExpectedStatus}",
                    string.IsNullOrWhiteSpace(BodyContains) ? null : $"包含 {BodyContains}",
                    Assertions.Count == 0 ? null : $"{Assertions.Count} 条断言",
                    RetryCount == 0 ? null : $"重试 {RetryCount} 次",
                    Condition == "Always" ? null : $"条件 {Condition}",
                    ConditionRules.IsEmpty ? null : ConditionRules.Summary,
                    ParallelGroup == 0 ? null : $"并行组 {ParallelGroup}",
                }.Where(value => value is not null)
            );
}

/// <summary>A JSONPath or response-header assertion on a suite step.</summary>
public sealed class TestAssertion
{
    public string Kind { get; set; } = "JSONPath";

    public string Path { get; set; } = string.Empty;

    public string Expected { get; set; } = string.Empty;

    public string Label => $"{(Kind == "Header" ? "响应头" : "JSONPath")} {Path} = {Expected}";
}

/// <summary>A custom mapping from a previous step's response into this request's input.</summary>
public sealed class InputMapping
{
    public Guid SourceStepId { get; set; }

    /// <summary>Dotted JSON path, for example <c>data.token</c> or <c>items[0].id</c>.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Body, Header, Query or Path.</summary>
    public string TargetKind { get; set; } = "Body";

    /// <summary>Target body path, header name, query name, or URL placeholder name.</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>Optional template; <c>{value}</c> is replaced with the extracted value.</summary>
    public string Template { get; set; } = "{value}";
}

public sealed class TestStepResult
{
    public Guid StepId { get; set; }

    public Guid RequestId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public string StatusText { get; set; } = string.Empty;

    public long ElapsedMs { get; set; }

    public int Attempts { get; set; } = 1;

    public bool Passed { get; set; }

    public bool Skipped { get; set; }

    public string? Error { get; set; }

    public string ResponsePreview { get; set; } = string.Empty;

    public List<TestAssertionResult> Assertions { get; } = new();

    public bool HasAssertions => Assertions.Count > 0;

    public string AttemptText => Attempts > 1 ? $"尝试 {Attempts} 次" : string.Empty;

    public string ResultLabel =>
        Skipped ? "跳过"
        : Passed ? "通过"
        : "失败";

    public BadgeTone ResultTone =>
        Skipped ? BadgeTone.Neutral
        : Passed ? BadgeTone.Success
        : BadgeTone.Danger;

    [JsonIgnore]
    public object ResultIcon =>
        new ArcGlyph
        {
            Data =
                Skipped ? ArcIcons.Minus
                : Passed ? ArcIcons.CircleCheck
                : ArcIcons.CircleX,
            Size = 12,
        };

    [JsonIgnore]
    public bool CanContinue => !Skipped && StepId != Guid.Empty;

    [JsonIgnore]
    public IRelayCommand? ContinueCommand { get; set; }
}

public sealed class TestAssertionResult
{
    public string Label { get; set; } = string.Empty;

    public bool Passed { get; set; }

    public string Detail { get; set; } = string.Empty;

    public string ResultLabel => Passed ? "通过" : "失败";

    public BadgeTone ResultTone => Passed ? BadgeTone.Success : BadgeTone.Danger;
}

public sealed class TestRunResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SuiteId { get; set; }

    public string SuiteName { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public int Total { get; set; }

    public int Passed { get; set; }

    public int Failed { get; set; }

    public int Skipped { get; set; }

    public bool Stopped { get; set; }

    /// <summary>The run was cancelled before every step finished.</summary>
    public bool Cancelled { get; set; }

    /// <summary>Steps that never ran because the run was stopped or cancelled.</summary>
    public int NotRun { get; set; }

    /// <summary>Set when preflight rejected the run before any request was sent.</summary>
    public string? Error { get; set; }

    public List<TestStepResult> Steps { get; set; } = new();

    public List<SuiteRunNodeState> NodeStates { get; set; } = new();

    public List<SuiteRunEdgeState> EdgeStates { get; set; } = new();

    public bool HasFailures => Failed > 0;

    public string WorkflowSummary =>
        NodeStates.Count == 0
            ? "按顺序执行"
            : $"{NodeStates.Count} 个节点 · {EdgeStates.Count} 条连线（{EdgeStates.Count(edge => edge.Active)} 条激活）";

    /// <summary>Pass count, skipped count and how many steps never ran.</summary>
    public string Outcome =>
        $"{Passed}/{Total} 通过"
        + (Skipped > 0 ? $" · {Skipped} 跳过" : string.Empty)
        + (NotRun > 0 ? $" · {NotRun} 步未运行" : string.Empty);

    public string Summary =>
        Error is not null
            ? $"预检失败 · {Error}"
            : (Cancelled ? "已取消 · " : string.Empty)
                + $"{Outcome} · {Timestamp.LocalDateTime:HH:mm:ss}";

    public override string ToString() => Summary;
}

/// <summary>Per-node state persisted with a run so history can be inspected after a restart.</summary>
public sealed class SuiteRunNodeState
{
    public Guid StepId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Pending, Running, Done, Failed, Skipped or NotRun.</summary>
    public string State { get; set; } = "Pending";

    public long ElapsedMs { get; set; }

    public string? Detail { get; set; }

    public string StateLabel =>
        State switch
        {
            "Done" => "通过",
            "Failed" => "失败",
            "Skipped" => "跳过",
            "NotRun" => "未运行",
            "Running" => "运行中",
            _ => "等待",
        };

    public BadgeTone StateTone =>
        State switch
        {
            "Done" => BadgeTone.Success,
            "Failed" => BadgeTone.Danger,
            _ => BadgeTone.Neutral,
        };

    public string ElapsedText => ElapsedMs > 0 ? $"{ElapsedMs} ms" : string.Empty;
}

/// <summary>Final state of one dependency edge in a persisted run.</summary>
public sealed class SuiteRunEdgeState
{
    public string SourceId { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;

    public bool Active { get; set; }
}
