namespace PostSharp.Models;

/// <summary>Persisted visual workflow for a suite. Nodes are steps; edges carry branch conditions.</summary>
public sealed class SuiteWorkflow
{
    public List<SuiteWorkflowNode> Nodes { get; } = new();

    public List<SuiteWorkflowEdge> Edges { get; } = new();

    public bool IsEmpty => Nodes.Count == 0;

    public bool HasStepGraph => Nodes.Any(node => node.StepId != Guid.Empty);
}

public sealed class SuiteWorkflowNode
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The suite step this node represents.</summary>
    public Guid StepId { get; set; }

    /// <summary>step keeps workflow nodes aligned with suite steps; collection/request are legacy authoring values.</summary>
    public string Kind { get; set; } = "step";

    public Guid CollectionId { get; set; }

    public Guid RequestId { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>All or Any: how multiple incoming branches combine.</summary>
    public string JoinMode { get; set; } = "All";
}

public sealed class SuiteWorkflowEdge
{
    public string SourceId { get; set; } = string.Empty;

    public string TargetId { get; set; } = string.Empty;

    /// <summary>Always, OnSuccess, OnFailure, OnSkipped or Expression.</summary>
    public string Condition { get; set; } = "Always";

    public SuiteCondition Rules { get; } = new();
}

/// <summary>Externally supplied graph definition used by MCP and tests.</summary>
public sealed record SuiteWorkflowSpec(
    IReadOnlyList<SuiteWorkflowNodeSpec> Nodes,
    IReadOnlyList<SuiteWorkflowEdgeSpec> Edges,
    int? MaxParallelism = null
);

/// <summary>One node in a suite workflow; stepId or stepIndex resolves the backing step.</summary>
public sealed record SuiteWorkflowNodeSpec(
    Guid? StepId,
    int? StepIndex,
    string? Id,
    double? X,
    double? Y,
    string? JoinMode
);

/// <summary>One directed branch between two workflow nodes.</summary>
public sealed record SuiteWorkflowEdgeSpec(
    string SourceId,
    string TargetId,
    string? Condition,
    SuiteConditionSpec? Rules = null
);

/// <summary>Structured condition attached to a workflow edge.</summary>
public sealed record SuiteConditionSpec(string? Mode, IReadOnlyList<SuiteConditionRuleSpec> Rules);

/// <summary>One rule inside an edge condition.</summary>
public sealed record SuiteConditionRuleSpec(
    string? Source,
    string? Path,
    string? Operator,
    string? Expected
);
