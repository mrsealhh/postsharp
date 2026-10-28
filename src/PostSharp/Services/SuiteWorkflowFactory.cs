using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Builds the canonical DAG for suites created from an ordered step list.</summary>
public static class SuiteWorkflowFactory
{
    public static SuiteWorkflow CreateLinear(IReadOnlyList<TestStep> steps)
    {
        var workflow = new SuiteWorkflow();
        for (var index = 0; index < steps.Count; index++)
        {
            workflow.Nodes.Add(
                new SuiteWorkflowNode
                {
                    Id = steps[index].Id.ToString(),
                    StepId = steps[index].Id,
                    Kind = "step",
                    X = 80,
                    Y = 60 + index * 110,
                    JoinMode = steps[index].JoinMode,
                }
            );
        }

        var groups = new List<List<TestStep>>();
        foreach (var step in steps)
        {
            if (
                groups.Count > 0
                && step.ParallelGroup != 0
                && groups[^1][0].ParallelGroup == step.ParallelGroup
            )
            {
                groups[^1].Add(step);
            }
            else
            {
                groups.Add(new List<TestStep> { step });
            }
        }

        for (var index = 1; index < groups.Count; index++)
        {
            foreach (var source in groups[index - 1])
            {
                foreach (var target in groups[index])
                {
                    workflow.Edges.Add(
                        new SuiteWorkflowEdge
                        {
                            SourceId = source.Id.ToString(),
                            TargetId = target.Id.ToString(),
                            Condition = "Always",
                        }
                    );
                }
            }
        }

        return workflow;
    }

    public static SuiteWorkflow CreateFromSpec(
        IReadOnlyList<TestStep> steps,
        SuiteWorkflowSpec spec
    )
    {
        if (steps.Count == 0)
        {
            throw new InvalidOperationException("套件至少需要一个步骤。");
        }

        if (spec.Nodes.Count != steps.Count)
        {
            throw new InvalidOperationException(
                $"workflow.nodes 必须覆盖全部 {steps.Count} 个步骤，当前为 {spec.Nodes.Count} 个节点。"
            );
        }

        var workflow = new SuiteWorkflow();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var representedSteps = new HashSet<Guid>();
        foreach (var input in spec.Nodes)
        {
            var step = ResolveStep(steps, input);
            if (!representedSteps.Add(step.Id))
            {
                throw new InvalidOperationException(
                    $"步骤 {step.Id} 重复出现在 workflow.nodes 中。"
                );
            }

            var nodeId = string.IsNullOrWhiteSpace(input.Id) ? step.Id.ToString() : input.Id.Trim();
            if (!nodeIds.Add(nodeId))
            {
                throw new InvalidOperationException(
                    $"workflow.nodes 中存在重复节点 id：{nodeId}。"
                );
            }

            var stepIndex = 0;
            while (stepIndex < steps.Count && steps[stepIndex].Id != step.Id)
            {
                stepIndex++;
            }

            workflow.Nodes.Add(
                new SuiteWorkflowNode
                {
                    Id = nodeId,
                    StepId = step.Id,
                    Kind = "step",
                    X = input.X ?? 80,
                    Y = input.Y ?? 60 + (stepIndex * 110),
                    JoinMode = NormalizeJoinMode(input.JoinMode ?? step.JoinMode),
                }
            );
        }

        var edgeKeys = new HashSet<(string SourceId, string TargetId)>();
        foreach (var input in spec.Edges)
        {
            var sourceId = input.SourceId.Trim();
            var targetId = input.TargetId.Trim();
            if (!nodeIds.Contains(sourceId) || !nodeIds.Contains(targetId))
            {
                throw new InvalidOperationException(
                    $"workflow.edges 引用了不存在的节点：{sourceId} → {targetId}。"
                );
            }

            if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"workflow.edges 不允许自连：{sourceId}。");
            }

            if (!edgeKeys.Add((sourceId, targetId)))
            {
                throw new InvalidOperationException(
                    $"workflow.edges 存在重复连线：{sourceId} → {targetId}。"
                );
            }

            var condition = NormalizeEdgeCondition(input.Condition);
            var edge = new SuiteWorkflowEdge
            {
                SourceId = sourceId,
                TargetId = targetId,
                Condition = condition,
            };
            if (input.Rules is { } rules)
            {
                edge.Rules.Mode = NormalizeConditionMode(rules.Mode);
                foreach (var rule in rules.Rules)
                {
                    edge.Rules.Rules.Add(BuildConditionRule(rule));
                }
            }

            if (condition == "Expression" && edge.Rules.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"连线 {sourceId} → {targetId} 使用 Expression 时必须至少配置一条规则。"
                );
            }

            workflow.Edges.Add(edge);
        }

        return workflow;
    }

    public static void EnsureLinear(TestSuite suite)
    {
        if (suite.Workflow is { HasStepGraph: true })
        {
            return;
        }

        suite.Workflow = CreateLinear(suite.Steps);
    }

    private static TestStep ResolveStep(IReadOnlyList<TestStep> steps, SuiteWorkflowNodeSpec input)
    {
        TestStep? resolved = null;
        if (input.StepId is { } stepId)
        {
            resolved =
                steps.FirstOrDefault(candidate => candidate.Id == stepId)
                ?? throw new InvalidOperationException(
                    $"workflow.nodes 引用了不存在的步骤：{stepId}。"
                );
        }

        if (input.StepIndex is { } stepIndex)
        {
            if (stepIndex < 0 || stepIndex >= steps.Count)
            {
                throw new InvalidOperationException(
                    $"workflow.nodes.stepIndex={stepIndex} 超出步骤范围 0-{steps.Count - 1}。"
                );
            }

            var indexed = steps[stepIndex];
            if (resolved is not null && resolved.Id != indexed.Id)
            {
                throw new InvalidOperationException(
                    $"workflow.nodes 的 stepId 与 stepIndex={stepIndex} 指向不同的步骤。"
                );
            }

            resolved = indexed;
        }

        return resolved
            ?? throw new InvalidOperationException(
                "workflow.nodes 的每一项都必须提供 stepId 或 stepIndex。"
            );
    }

    private static SuiteConditionRule BuildConditionRule(SuiteConditionRuleSpec input)
    {
        var source = string.IsNullOrWhiteSpace(input.Source) ? "StatusCode" : input.Source.Trim();
        if (
            !SuiteConditionOptions.Sources.Any(candidate =>
                string.Equals(candidate, source, StringComparison.Ordinal)
            )
        )
        {
            throw new InvalidOperationException($"workflow.edges.rules 不支持来源：{source}。");
        }

        var @operator = string.IsNullOrWhiteSpace(input.Operator)
            ? "Equals"
            : input.Operator.Trim();
        if (
            !SuiteConditionOptions.Operators.Any(candidate =>
                string.Equals(candidate, @operator, StringComparison.Ordinal)
            )
        )
        {
            throw new InvalidOperationException(
                $"workflow.edges.rules 不支持操作符：{@operator}。"
            );
        }

        return new SuiteConditionRule
        {
            Source = source,
            Path = input.Path?.Trim() ?? string.Empty,
            Operator = @operator,
            Expected = input.Expected?.Trim() ?? string.Empty,
        };
    }

    private static string NormalizeJoinMode(string? value) =>
        string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase) ? "Any" : "All";

    private static string NormalizeConditionMode(string? value) =>
        string.Equals(value, "Any", StringComparison.OrdinalIgnoreCase)
            ? SuiteConditionOptions.Any
            : SuiteConditionOptions.All;

    private static string NormalizeEdgeCondition(string? value)
    {
        var condition = string.IsNullOrWhiteSpace(value) ? "Always" : value.Trim();
        return condition switch
        {
            "Always" or "OnSuccess" or "OnFailure" or "OnSkipped" or "Expression" => condition,
            _ => throw new InvalidOperationException($"workflow.edges 不支持条件：{condition}。"),
        };
    }
}
