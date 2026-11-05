using System.Text;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed record PipelineGraphNode(
    string Id,
    string Kind,
    string Label,
    string? Method,
    string? Url,
    string? ParentId,
    int Order
);

public sealed record PipelineGraphEdge(string From, string To);

/// <summary>流水线的只读视图：节点/边 + Mermaid/DOT 文本，供 CLI、MCP 和文档渲染。</summary>
public sealed record PipelineGraph(
    string Name,
    int StepCount,
    int CaseCount,
    IReadOnlyList<PipelineGraphNode> Nodes,
    IReadOnlyList<PipelineGraphEdge> Edges,
    string Mermaid,
    string Dot,
    IReadOnlyList<PipelineIssue> Issues
);

/// <summary>把流水线节点树摊平成带层级的图，不执行任何请求。</summary>
public static class PipelineVisualization
{
    public static PipelineGraph Build(
        AutomationPipeline pipeline,
        IReadOnlyList<PipelineIssue> issues
    )
    {
        var nodes = new List<PipelineGraphNode>();
        var edges = new List<PipelineGraphEdge>();
        var mermaid = new StringBuilder("flowchart TD\n");
        var dot = new StringBuilder("digraph pipeline {\n  rankdir=TB;\n");
        var nextId = 0;

        EmitGroup(
            pipeline.Steps,
            parentId: null,
            nodes,
            edges,
            mermaid,
            dot,
            ref nextId,
            pipeline.MaxParallelism,
            chainSiblings: true
        );

        dot.Append("}\n");

        return new PipelineGraph(
            pipeline.Name,
            CountSteps(pipeline.Steps),
            pipeline.Data.Count,
            nodes,
            edges,
            mermaid.ToString(),
            dot.ToString(),
            issues
        );
    }

    private static (string? Entry, string? Exit) EmitGroup(
        IReadOnlyList<PipelineNode> steps,
        string? parentId,
        List<PipelineGraphNode> nodes,
        List<PipelineGraphEdge> edges,
        StringBuilder mermaid,
        StringBuilder dot,
        ref int nextId,
        int maxParallelism,
        bool chainSiblings
    )
    {
        string? entry = null;
        string? previous = null;
        foreach (var step in steps)
        {
            var (nodeEntry, nodeExit) = EmitNode(
                step,
                parentId,
                nodes,
                edges,
                mermaid,
                dot,
                ref nextId,
                maxParallelism
            );
            entry ??= nodeEntry;
            if (chainSiblings && previous is not null && nodeEntry is not null)
            {
                edges.Add(new PipelineGraphEdge(previous, nodeEntry));
                mermaid.Append($"  {previous} --> {nodeEntry}\n");
                dot.Append($"  {previous} -> {nodeEntry};\n");
            }

            previous = nodeExit ?? nodeEntry;
        }

        return (entry, previous);
    }

    private static (string? Entry, string? Exit) EmitNode(
        PipelineNode node,
        string? parentId,
        List<PipelineGraphNode> nodes,
        List<PipelineGraphEdge> edges,
        StringBuilder mermaid,
        StringBuilder dot,
        ref int nextId,
        int maxParallelism
    )
    {
        if (node.Kind == PipelineNodeKind.Loop)
        {
            var id = $"loop{nextId++}";
            var label = $"循环 {node.LoopAlias} ∈ {node.LoopOver}";
            nodes.Add(new PipelineGraphNode(id, "loop", label, null, null, parentId, nodes.Count));
            mermaid.Append($"  subgraph {id}[\"{Escape(label)}\"]\n    direction TB\n");
            dot.Append($"  subgraph cluster_{id} {{\n    label=\"{EscapeDot(label)}\";\n");
            EmitGroup(
                node.Children,
                id,
                nodes,
                edges,
                mermaid,
                dot,
                ref nextId,
                maxParallelism,
                chainSiblings: true
            );
            mermaid.Append("  end\n");
            dot.Append("  }\n");
            return (id, id);
        }

        if (node.Kind == PipelineNodeKind.Parallel)
        {
            var id = $"par{nextId++}";
            var parallelism = node.Parallelism > 0 ? node.Parallelism : maxParallelism;
            var label = $"并发组 max={parallelism}";
            nodes.Add(
                new PipelineGraphNode(id, "parallel", label, null, null, parentId, nodes.Count)
            );
            mermaid.Append($"  subgraph {id}[\"{Escape(label)}\"]\n    direction TB\n");
            dot.Append($"  subgraph cluster_{id} {{\n    label=\"{EscapeDot(label)}\";\n");
            EmitGroup(
                node.Children,
                id,
                nodes,
                edges,
                mermaid,
                dot,
                ref nextId,
                maxParallelism,
                chainSiblings: false
            );
            mermaid.Append("  end\n");
            dot.Append("  }\n");
            return (id, id);
        }

        var requestId = $"n{nextId++}";
        var requestLabel = BuildRequestLabel(node);
        nodes.Add(
            new PipelineGraphNode(
                requestId,
                "request",
                requestLabel,
                node.Method,
                node.Url,
                parentId,
                nodes.Count
            )
        );
        mermaid.Append($"  {requestId}[\"{Escape(requestLabel)}\"]\n");
        dot.Append($"  {requestId} [label=\"{EscapeDot(requestLabel)}\", shape=box];\n");
        return (requestId, requestId);
    }

    private static string BuildRequestLabel(PipelineNode node)
    {
        var name = node.Name;
        if (
            string.IsNullOrWhiteSpace(name)
            || string.Equals(name, node.Url, StringComparison.Ordinal)
        )
        {
            return string.IsNullOrWhiteSpace(node.Url) ? node.Method : $"{node.Method} {node.Url}";
        }

        return string.IsNullOrWhiteSpace(node.Url) ? name : $"{name}<br/>{node.Method} {node.Url}";
    }

    private static string Escape(string text) =>
        text.Replace("\"", "'", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal);

    private static string EscapeDot(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("<br/>", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static int CountSteps(IReadOnlyList<PipelineNode> steps) =>
        steps.Sum(step => step.Kind == PipelineNodeKind.Request ? 1 : CountSteps(step.Children));
}
