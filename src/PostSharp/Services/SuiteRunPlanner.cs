using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Builds run scopes for retry actions. A retry always keeps the dependencies that produced its
/// inputs, because response bodies from the previous run are not retained.
/// </summary>
public static class SuiteRunPlanner
{
    public static IReadOnlySet<Guid> SelectFailedWithDependencies(
        TestSuite suite,
        TestRunResult run
    )
    {
        var selected = run
            .Steps.Where(result => !result.Passed && !result.Skipped)
            .Select(result => result.StepId)
            .Where(stepId => suite.Steps.Any(step => step.Id == stepId))
            .ToHashSet();
        if (selected.Count == 0)
        {
            return selected;
        }

        var graph = BuildGraph(suite);
        AddAncestors(selected, graph.Incoming);
        AddMappingDependencies(selected, suite, graph.Incoming);
        return selected;
    }

    public static IReadOnlySet<Guid> SelectFromStepWithDependencies(TestSuite suite, Guid stepId)
    {
        var selected = new HashSet<Guid>();
        if (suite.Steps.All(step => step.Id != stepId))
        {
            return selected;
        }

        var graph = BuildGraph(suite);
        selected.Add(stepId);
        var queue = new Queue<Guid>();
        queue.Enqueue(stepId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var target in graph.Outgoing.GetValueOrDefault(current) ?? [])
            {
                if (selected.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        AddAncestors(selected, graph.Incoming);
        AddMappingDependencies(selected, suite, graph.Incoming);
        return selected;
    }

    /// <summary>Creates an ephemeral suite containing only the selected steps and their internal edges.</summary>
    public static TestSuite CreateSubset(TestSuite suite, IReadOnlySet<Guid> stepIds)
    {
        var subset = new TestSuite
        {
            Id = suite.Id,
            Name = suite.Name,
            StopOnError = suite.StopOnError,
            RunInParallel = false,
            ScheduleMinutes = 0,
            MaxParallelism = suite.MaxParallelism,
            LastSummary = suite.LastSummary,
        };
        foreach (var step in suite.Steps.Where(step => stepIds.Contains(step.Id)))
        {
            subset.Steps.Add(step);
        }

        if (suite.Workflow is { HasStepGraph: true } workflow)
        {
            var nodeIdsByStep = workflow
                .Nodes.GroupBy(node => node.StepId)
                .ToDictionary(group => group.Key, group => group.First().Id);
            var selectedNodeIds = new HashSet<string>(StringComparer.Ordinal);
            var subsetWorkflow = new SuiteWorkflow();
            foreach (var node in workflow.Nodes)
            {
                if (!stepIds.Contains(node.StepId))
                {
                    continue;
                }

                selectedNodeIds.Add(node.Id);
                subsetWorkflow.Nodes.Add(
                    new SuiteWorkflowNode
                    {
                        Id = node.Id,
                        StepId = node.StepId,
                        Kind = node.Kind,
                        CollectionId = node.CollectionId,
                        RequestId = node.RequestId,
                        X = node.X,
                        Y = node.Y,
                        JoinMode = node.JoinMode,
                    }
                );
            }

            foreach (var edge in workflow.Edges)
            {
                if (
                    !selectedNodeIds.Contains(edge.SourceId)
                    || !selectedNodeIds.Contains(edge.TargetId)
                )
                {
                    continue;
                }

                var copy = new SuiteWorkflowEdge
                {
                    SourceId = edge.SourceId,
                    TargetId = edge.TargetId,
                    Condition = edge.Condition,
                };
                copy.Rules.Mode = edge.Rules.Mode;
                foreach (var rule in edge.Rules.Rules)
                {
                    copy.Rules.Rules.Add(
                        new SuiteConditionRule
                        {
                            Source = rule.Source,
                            Path = rule.Path,
                            Operator = rule.Operator,
                            Expected = rule.Expected,
                        }
                    );
                }

                subsetWorkflow.Edges.Add(copy);
            }

            // A selected step that was not present in the saved graph still needs a node to run.
            foreach (var step in subset.Steps)
            {
                if (nodeIdsByStep.ContainsKey(step.Id))
                {
                    continue;
                }

                subsetWorkflow.Nodes.Add(
                    new SuiteWorkflowNode
                    {
                        Id = step.Id.ToString(),
                        StepId = step.Id,
                        Kind = "step",
                        X = 80,
                        Y = 60 + subsetWorkflow.Nodes.Count * 110,
                        JoinMode = step.JoinMode,
                    }
                );
            }

            subset.Workflow = subsetWorkflow;
        }
        else
        {
            subset.Workflow = SuiteWorkflowFactory.CreateLinear(subset.Steps);
        }

        return subset;
    }

    private static (
        IReadOnlyDictionary<Guid, List<Guid>> Incoming,
        IReadOnlyDictionary<Guid, List<Guid>> Outgoing
    ) BuildGraph(TestSuite suite)
    {
        var incoming = suite.Steps.ToDictionary(step => step.Id, _ => new List<Guid>());
        var outgoing = suite.Steps.ToDictionary(step => step.Id, _ => new List<Guid>());
        if (suite.Workflow is not { HasStepGraph: true } workflow)
        {
            for (var index = 1; index < suite.Steps.Count; index++)
            {
                var source = suite.Steps[index - 1];
                var target = suite.Steps[index];
                outgoing[source.Id].Add(target.Id);
                incoming[target.Id].Add(source.Id);
            }

            return (incoming, outgoing);
        }

        var nodeStepIds = workflow
            .Nodes.GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First().StepId,
                StringComparer.Ordinal
            );
        foreach (var edge in workflow.Edges)
        {
            if (
                !nodeStepIds.TryGetValue(edge.SourceId, out var sourceStepId)
                || !nodeStepIds.TryGetValue(edge.TargetId, out var targetStepId)
                || !incoming.ContainsKey(sourceStepId)
                || !incoming.ContainsKey(targetStepId)
                || sourceStepId == targetStepId
            )
            {
                continue;
            }

            outgoing[sourceStepId].Add(targetStepId);
            incoming[targetStepId].Add(sourceStepId);
        }

        return (incoming, outgoing);
    }

    private static void AddAncestors(
        ISet<Guid> selected,
        IReadOnlyDictionary<Guid, List<Guid>> incoming
    )
    {
        var queue = new Queue<Guid>(selected);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var source in incoming.GetValueOrDefault(current) ?? [])
            {
                if (selected.Add(source))
                {
                    queue.Enqueue(source);
                }
            }
        }
    }

    private static void AddMappingDependencies(
        ISet<Guid> selected,
        TestSuite suite,
        IReadOnlyDictionary<Guid, List<Guid>> incoming
    )
    {
        var queue = new Queue<Guid>(selected);
        while (queue.Count > 0)
        {
            var current = suite.Steps.FirstOrDefault(step => step.Id == queue.Dequeue());
            if (current is null)
            {
                continue;
            }

            foreach (var mapping in current.InputMappings)
            {
                if (mapping.SourceStepId == Guid.Empty || !selected.Add(mapping.SourceStepId))
                {
                    continue;
                }

                queue.Enqueue(mapping.SourceStepId);
                AddAncestors(selected, incoming);
            }
        }
    }
}
