using System.Text.Json.Serialization;

namespace PostSharp.Models;

/// <summary>In-memory state for an MCP-triggered suite run. Completed runs are persisted in the suite history.</summary>
public sealed class SuiteRunJob
{
    public Guid Id { get; } = Guid.NewGuid();

    public Guid SuiteId { get; init; }

    public string SuiteName { get; init; } = string.Empty;

    public int TotalSteps { get; init; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Running, Cancelling, Completed, Failed or Cancelled.</summary>
    public string Status { get; set; } = "Running";

    public string Summary { get; set; } = "运行中…";

    public string? Error { get; set; }

    public Guid? RunId { get; set; }

    public int CompletedSteps { get; set; }

    public List<SuiteValidationIssue> ValidationIssues { get; } = new();

    public List<SuiteRunNodeState> NodeStates { get; } = new();

    public List<SuiteRunEdgeState> EdgeStates { get; } = new();

    [JsonIgnore]
    public CancellationTokenSource Cancellation { get; } = new();

    [JsonIgnore]
    public object SyncRoot { get; } = new();
}
