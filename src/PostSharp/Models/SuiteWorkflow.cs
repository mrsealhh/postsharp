namespace PostSharp.Models;

/// <summary>Persisted visual workflow for a suite.</summary>
public sealed class SuiteWorkflow
{
    public List<SuiteWorkflowNode> Nodes { get; } = new();

    public List<SuiteWorkflowEdge> Edges { get; } = new();
}

public sealed class SuiteWorkflowNode
{
    public string Id { get; set; } = string.Empty;

    /// <summary>collection or request.</summary>
    public string Kind { get; set; } = "request";

    public Guid CollectionId { get; set; }

    public Guid RequestId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }
}

public sealed class SuiteWorkflowEdge
{
    public string SourceId { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;
}
