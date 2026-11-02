using TKCode.Arc;

namespace PostSharp.Services;

/// <summary>把流水线的只读图转换成 Arc 工作流画布可直接渲染的图。</summary>
public static class PipelineWorkflowMapper
{
    public static WorkflowGraph ToWorkflowGraph(PipelineGraph graph)
    {
        var workflow = new WorkflowGraph { AllowCycles = true };
        var nodes = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            var card = new WorkflowNode(node.Id, node.Label)
            {
                Kind = node.Kind switch
                {
                    "loop" => "循环",
                    "parallel" => "并发",
                    _ => "接口",
                },
                Description = node.Url is null ? string.Empty : $"{node.Method} {node.Url}",
            };
            nodes[node.Id] = card;
            workflow.Nodes.Add(card);
        }

        foreach (var edge in graph.Edges)
        {
            if (
                nodes.TryGetValue(edge.From, out var source)
                && nodes.TryGetValue(edge.To, out var target)
            )
            {
                ConnectOnce(workflow, source, target);
            }
        }

        foreach (
            var group in graph
                .Nodes.Where(node => node.ParentId is not null)
                .GroupBy(node => node.ParentId!)
        )
        {
            if (!nodes.TryGetValue(group.Key, out var parent))
            {
                continue;
            }

            var parentKind = graph.Nodes.First(node => node.Id == group.Key).Kind;
            var children = group
                .OrderBy(node => node.Order)
                .Select(node => nodes.TryGetValue(node.Id, out var card) ? card : null)
                .Where(card => card is not null)
                .Cast<WorkflowNode>()
                .ToList();
            if (parentKind == "loop")
            {
                // 循环体按顺序串起来，看起来像一条循环链。
                WorkflowNode? previous = null;
                foreach (var child in children)
                {
                    if (previous is null)
                    {
                        ConnectOnce(workflow, parent, child);
                    }
                    else
                    {
                        ConnectOnce(workflow, previous, child);
                    }

                    previous = child;
                }
            }
            else
            {
                foreach (var child in children)
                {
                    ConnectOnce(workflow, parent, child);
                }
            }
        }

        return workflow;
    }

    private static void ConnectOnce(
        WorkflowGraph workflow,
        WorkflowNode source,
        WorkflowNode target
    )
    {
        if (!workflow.OutgoingEdges(source).Any(edge => ReferenceEquals(edge.Target, target)))
        {
            workflow.Connect(source, target);
        }
    }
}
