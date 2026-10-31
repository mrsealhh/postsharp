using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PostSharp.Models;

/// <summary>In-memory state for a long-running MCP operation (sequence, import or storage sync).</summary>
public sealed class McpJob
{
    public Guid Id { get; } = Guid.NewGuid();

    public string Kind { get; init; } = string.Empty;

    public string? IdempotencyKey { get; init; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    public string Status { get; set; } = "Running";

    public string Summary { get; set; } = "运行中…";

    public string? Error { get; set; }

    public int Completed { get; set; }

    public int Total { get; set; }

    public JsonNode? Result { get; set; }

    [JsonIgnore]
    public CancellationTokenSource Cancellation { get; } = new();

    [JsonIgnore]
    public object SyncRoot { get; } = new();
}
