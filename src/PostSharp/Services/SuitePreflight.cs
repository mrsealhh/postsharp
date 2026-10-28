using System.Text.RegularExpressions;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Validates the authoring graph before a suite is saved or run. Errors block execution; warnings are
/// shown so users can decide whether a risky configuration is intentional.
/// </summary>
public static class SuitePreflight
{
    private static readonly HashSet<string> StepConditions = new(StringComparer.Ordinal)
    {
        "Always",
        "OnPreviousSuccess",
        "OnPreviousFailure",
        "Expression",
    };

    private static readonly HashSet<string> EdgeConditions = new(StringComparer.Ordinal)
    {
        "Always",
        "OnSuccess",
        "OnFailure",
        "OnSkipped",
        "Expression",
    };

    private static readonly HashSet<string> JoinModes = new(StringComparer.Ordinal)
    {
        "All",
        "Any",
    };

    private static readonly HashSet<string> MappingTargetKinds = new(StringComparer.Ordinal)
    {
        "Body",
        "Header",
        "Query",
        "Path",
    };

    private static readonly Regex VariablePattern = new(
        @"\{\{\s*([^{}]+?)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    public static SuiteValidationResult Validate(
        TestSuite suite,
        IEnumerable<RequestCollection> collections,
        EnvironmentModel? environment
    )
    {
        var issues = new List<SuiteValidationIssue>();
        var requests = FlattenRequests(collections).ToDictionary(request => request.Id);
        if (suite.Steps.Count == 0)
        {
            issues.Add(
                Issue("suite.empty", "套件没有步骤", "先从左侧勾选集合或接口，再保存或运行。")
            );
            return new SuiteValidationResult(issues);
        }

        var stepsById = new Dictionary<Guid, TestStep>();
        var stepIndexes = new Dictionary<Guid, int>();
        for (var index = 0; index < suite.Steps.Count; index++)
        {
            var step = suite.Steps[index];
            if (step.Id == Guid.Empty)
            {
                issues.Add(
                    StepIssue(
                        step,
                        "step.id",
                        "步骤缺少有效 ID",
                        "删除该节点后重新从左侧拖入接口。"
                    )
                );
                continue;
            }

            if (!stepsById.TryAdd(step.Id, step))
            {
                issues.Add(
                    StepIssue(
                        step,
                        "step.duplicate",
                        "步骤 ID 重复",
                        "删除重复节点后重新加入接口。"
                    )
                );
                continue;
            }

            stepIndexes[step.Id] = index;
        }

        var workflow = suite.Workflow;
        var nodesById = new Dictionary<string, SuiteWorkflowNode>(StringComparer.Ordinal);
        var nodeByStep = new Dictionary<Guid, SuiteWorkflowNode>();
        if (workflow is { Nodes.Count: > 0 })
        {
            foreach (var node in workflow.Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id))
                {
                    issues.Add(
                        Issue(
                            "workflow.node.id",
                            "工作流节点缺少连接 ID",
                            "删除该节点后重新加入接口。"
                        )
                    );
                    continue;
                }

                if (!nodesById.TryAdd(node.Id, node))
                {
                    issues.Add(
                        Issue(
                            "workflow.node.duplicate",
                            $"工作流节点 ID 重复：{node.Id}",
                            "删除重复节点后重新加入接口。"
                        )
                    );
                    continue;
                }

                if (node.StepId == Guid.Empty || !stepsById.TryGetValue(node.StepId, out var step))
                {
                    issues.Add(
                        Issue(
                            "workflow.node.dangling",
                            $"节点 {node.Id} 指向不存在的步骤",
                            "删除悬空节点后重新连接画布。"
                        )
                    );
                    continue;
                }

                if (!nodeByStep.TryAdd(node.StepId, node))
                {
                    issues.Add(
                        StepIssue(
                            step,
                            "workflow.node.stepDuplicate",
                            "同一个步骤被多个节点引用",
                            "保留一个节点并删除其余重复节点。"
                        )
                    );
                }

                if (node.JoinMode is not "All" and not "Any")
                {
                    issues.Add(
                        StepIssue(
                            step,
                            "workflow.node.joinMode",
                            $"汇合方式无效：{node.JoinMode}",
                            "改为「全部上游完成」或「任一上游到达」。"
                        )
                    );
                }
            }

            foreach (var step in suite.Steps)
            {
                if (!nodeByStep.ContainsKey(step.Id))
                {
                    issues.Add(
                        StepIssue(
                            step,
                            "workflow.node.missing",
                            "步骤没有对应的工作流节点",
                            "把步骤重新拖入画布，或删除该步骤。"
                        )
                    );
                }
            }
        }
        else if (suite.Steps.Count > 1)
        {
            issues.Add(
                Issue(
                    "workflow.missing",
                    "未保存画布工作流，将按步骤顺序执行",
                    "打开编排器并保存画布，才能使用分支和汇合。",
                    severity: SuiteValidationSeverity.Warning
                )
            );
        }

        var outgoing = stepsById.Keys.ToDictionary(stepId => stepId, _ => new List<Guid>());
        var incoming = stepsById.Keys.ToDictionary(stepId => stepId, _ => new List<Guid>());
        var edgeKeys = new HashSet<(string Source, string Target)>();
        if (workflow is { Nodes.Count: > 0 })
        {
            foreach (var edge in workflow.Edges)
            {
                if (
                    !nodesById.TryGetValue(edge.SourceId, out var source)
                    || !nodesById.TryGetValue(edge.TargetId, out var target)
                )
                {
                    issues.Add(
                        Issue(
                            "workflow.edge.dangling",
                            $"连线 {edge.SourceId} → {edge.TargetId} 指向不存在的节点",
                            "删除这条连线后重新连接两个节点。"
                        )
                    );
                    continue;
                }

                if (source.StepId == target.StepId)
                {
                    issues.Add(
                        StepIssue(
                            stepsById[target.StepId],
                            "workflow.edge.self",
                            "节点不能连接到自己",
                            "删除自环连线。"
                        )
                    );
                    continue;
                }

                if (!edgeKeys.Add((edge.SourceId, edge.TargetId)))
                {
                    issues.Add(
                        StepIssue(
                            stepsById[target.StepId],
                            "workflow.edge.duplicate",
                            "两个节点之间存在重复连线",
                            "删除多余的连线，只保留一条分支条件。"
                        )
                    );
                    continue;
                }

                if (!EdgeConditions.Contains(edge.Condition))
                {
                    issues.Add(
                        StepIssue(
                            stepsById[target.StepId],
                            "workflow.edge.condition",
                            $"分支条件无效：{edge.Condition}",
                            "改为总是、成功、失败、跳过或条件表达式。"
                        )
                    );
                }

                if (edge.Condition == "Expression" && edge.Rules.IsEmpty)
                {
                    issues.Add(
                        StepIssue(
                            stepsById[target.StepId],
                            "workflow.edge.expression",
                            "分支条件为「条件表达式」但没有规则",
                            "选中连线并添加至少一条条件规则。"
                        )
                    );
                }

                ValidateCondition(edge.Rules, stepsById[target.StepId], "分支条件", issues);

                if (
                    outgoing.TryGetValue(source.StepId, out var targets)
                    && incoming.TryGetValue(target.StepId, out var sources)
                )
                {
                    targets.Add(target.StepId);
                    sources.Add(source.StepId);
                }
            }
        }
        else
        {
            for (var index = 1; index < suite.Steps.Count; index++)
            {
                var source = suite.Steps[index - 1];
                var target = suite.Steps[index];
                outgoing[source.Id].Add(target.Id);
                incoming[target.Id].Add(source.Id);
            }
        }

        if (workflow is { Nodes.Count: > 0 } && HasCycle(stepsById.Keys, outgoing))
        {
            var cycleSteps = FindResidualSteps(stepsById.Keys, outgoing)
                .Select(stepId => stepsById[stepId].RequestName)
                .Take(4)
                .ToList();
            issues.Add(
                Issue(
                    "workflow.cycle",
                    $"工作流存在环，涉及：{string.Join("、", cycleSteps)}",
                    "删除环中的一条连线后重新运行预检。",
                    cycleSteps.Count > 0
                        ? suite.Steps.FirstOrDefault(step => step.RequestName == cycleSteps[0])?.Id
                        : null
                )
            );
        }

        var ancestors = BuildAncestors(stepsById.Keys, incoming);
        foreach (var step in suite.Steps)
        {
            ValidateStep(step, requests, environment, issues);
            ValidateMappings(step, stepsById, stepIndexes, ancestors, workflow is not null, issues);
            ValidateAssertions(step, issues);
            ValidateCondition(step.ConditionRules, step, "步骤条件", issues);
        }

        return new SuiteValidationResult(issues);
    }

    private static void ValidateStep(
        TestStep step,
        IReadOnlyDictionary<Guid, RequestItem> requests,
        EnvironmentModel? environment,
        ICollection<SuiteValidationIssue> issues
    )
    {
        if (step.RequestId == Guid.Empty || !requests.ContainsKey(step.RequestId))
        {
            issues.Add(
                StepIssue(
                    step,
                    "step.requestMissing",
                    "引用的请求已删除或不存在",
                    "删除该步骤，或重新从左侧选择接口加入画布。"
                )
            );
        }

        if (!StepConditions.Contains(step.Condition))
        {
            issues.Add(
                StepIssue(
                    step,
                    "step.condition",
                    $"步骤条件无效：{step.Condition}",
                    "改为总是执行、上一步成功、上一步失败或条件表达式。"
                )
            );
        }

        if (step.ExpectedStatus is < 100 or > 599)
        {
            issues.Add(
                StepIssue(
                    step,
                    "step.expectedStatus",
                    "期望状态码不在 100–599 范围内",
                    "清空期望状态码，或填写有效的 HTTP 状态码。"
                )
            );
        }

        if (
            environment is null && VariablePattern.Matches(step.Url).Any()
            || environment is not null
                && VariablePattern
                    .Matches(step.Url)
                    .Any(match =>
                        !environment.Variables.ActiveRows.Any(row =>
                            row.IsEnabled
                            && row.Key.Equals(
                                match.Groups[1].Value.Trim(),
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                    )
        )
        {
            issues.Add(
                StepIssue(
                    step,
                    "environment.missingVariable",
                    "请求 URL 引用了当前环境没有的变量",
                    environment is null
                        ? "在顶部选择环境，或把 URL 改成直接地址。"
                        : "在环境变量中补充缺失的变量，或换用包含该变量的环境。",
                    SuiteValidationSeverity.Warning
                )
            );
        }
    }

    private static void ValidateMappings(
        TestStep step,
        IReadOnlyDictionary<Guid, TestStep> stepsById,
        IReadOnlyDictionary<Guid, int> stepIndexes,
        IReadOnlyDictionary<Guid, HashSet<Guid>> ancestors,
        bool hasWorkflow,
        ICollection<SuiteValidationIssue> issues
    )
    {
        foreach (var mapping in step.InputMappings)
        {
            if (
                mapping.SourceStepId == Guid.Empty
                || !stepsById.TryGetValue(mapping.SourceStepId, out var source)
            )
            {
                issues.Add(
                    StepIssue(
                        step,
                        "mapping.sourceMissing",
                        "映射来源步骤不存在",
                        "重新选择映射来源，或删除这条映射。"
                    )
                );
                continue;
            }

            if (mapping.SourceStepId == step.Id)
            {
                issues.Add(
                    StepIssue(
                        step,
                        "mapping.self",
                        "映射来源不能是当前步骤",
                        "选择当前步骤的上游节点。"
                    )
                );
                continue;
            }

            if (!MappingTargetKinds.Contains(mapping.TargetKind))
            {
                issues.Add(
                    StepIssue(
                        step,
                        "mapping.targetKind",
                        $"映射目标类型无效：{mapping.TargetKind}",
                        "改为请求体、请求头、查询参数或 URL 路径。"
                    )
                );
            }

            if (string.IsNullOrWhiteSpace(mapping.TargetPath))
            {
                issues.Add(
                    StepIssue(
                        step,
                        "mapping.targetPath",
                        "映射目标字段为空",
                        "填写目标字段，例如 data.userId 或 Authorization。"
                    )
                );
            }

            if (string.IsNullOrWhiteSpace(mapping.SourcePath))
            {
                issues.Add(
                    StepIssue(
                        step,
                        "mapping.sourcePath",
                        $"映射来源「{source.RequestName}」的字段为空",
                        "填写来源 JSON 路径，例如 data.token。"
                    )
                );
            }

            var isUpstream =
                hasWorkflow && ancestors.TryGetValue(step.Id, out var upstream)
                    ? upstream.Contains(mapping.SourceStepId)
                    : stepIndexes.TryGetValue(mapping.SourceStepId, out var sourceIndex)
                        && stepIndexes.TryGetValue(step.Id, out var targetIndex)
                        && sourceIndex < targetIndex;
            if (!isUpstream)
            {
                issues.Add(
                    StepIssue(
                        step,
                        "mapping.notUpstream",
                        $"映射来源「{source.RequestName}」不是当前步骤的上游",
                        "把来源节点连接到当前节点之前，或改用上游节点。"
                    )
                );
            }
        }
    }

    private static void ValidateAssertions(TestStep step, ICollection<SuiteValidationIssue> issues)
    {
        foreach (var assertion in step.Assertions)
        {
            if (assertion.Kind is not "JSONPath" and not "Header")
            {
                issues.Add(
                    StepIssue(
                        step,
                        "assertion.kind",
                        $"断言类型无效：{assertion.Kind}",
                        "改为 JSONPath 或响应头断言。"
                    )
                );
            }

            if (string.IsNullOrWhiteSpace(assertion.Path))
            {
                issues.Add(
                    StepIssue(
                        step,
                        "assertion.path",
                        "断言字段为空",
                        "填写 JSONPath 或响应头名称。"
                    )
                );
            }

            if (string.IsNullOrWhiteSpace(assertion.Expected))
            {
                issues.Add(
                    StepIssue(
                        step,
                        "assertion.expected",
                        "断言期望值为空",
                        "填写期望值，或删除这条断言。"
                    )
                );
            }
        }
    }

    private static void ValidateCondition(
        SuiteCondition condition,
        TestStep step,
        string context,
        ICollection<SuiteValidationIssue> issues
    )
    {
        if (condition.Mode is not "All" and not "Any")
        {
            issues.Add(
                StepIssue(
                    step,
                    "condition.mode",
                    $"{context}的组合方式无效：{condition.Mode}",
                    "改为全部满足或任一满足。"
                )
            );
        }

        foreach (var rule in condition.Rules)
        {
            if (!SuiteConditionOptions.Sources.Contains(rule.Source))
            {
                issues.Add(
                    StepIssue(
                        step,
                        "condition.source",
                        $"{context}的判断对象无效：{rule.Source}",
                        "改为状态码、响应字段、响应头、耗时、步骤结果或环境变量。"
                    )
                );
            }

            if (!SuiteConditionOptions.Operators.Contains(rule.Operator))
            {
                issues.Add(
                    StepIssue(
                        step,
                        "condition.operator",
                        $"{context}的比较方式无效：{rule.Operator}",
                        "重新选择比较方式。"
                    )
                );
            }

            if (
                SuiteConditionOptions.NeedsPath(rule.Source) && string.IsNullOrWhiteSpace(rule.Path)
            )
            {
                issues.Add(
                    StepIssue(
                        step,
                        "condition.path",
                        $"{context}缺少字段或变量名",
                        "填写字段路径、响应头名称或环境变量名。"
                    )
                );
            }

            if (
                SuiteConditionOptions.NeedsExpected(rule.Operator)
                && string.IsNullOrWhiteSpace(rule.Expected)
            )
            {
                issues.Add(
                    StepIssue(
                        step,
                        "condition.expected",
                        $"{context}缺少期望值",
                        "填写用于比较的期望值。"
                    )
                );
            }
        }
    }

    private static IReadOnlyDictionary<Guid, HashSet<Guid>> BuildAncestors(
        IEnumerable<Guid> stepIds,
        IReadOnlyDictionary<Guid, List<Guid>> incoming
    )
    {
        var result = stepIds.ToDictionary(stepId => stepId, _ => new HashSet<Guid>());
        foreach (var stepId in result.Keys)
        {
            var queue = new Queue<Guid>();
            queue.Enqueue(stepId);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!incoming.TryGetValue(current, out var sources))
                {
                    continue;
                }

                foreach (var source in sources)
                {
                    if (result[stepId].Add(source))
                    {
                        queue.Enqueue(source);
                    }
                }
            }
        }

        return result;
    }

    private static bool HasCycle(
        IEnumerable<Guid> stepIds,
        IReadOnlyDictionary<Guid, List<Guid>> outgoing
    ) => FindResidualSteps(stepIds, outgoing).Count > 0;

    private static IReadOnlyList<Guid> FindResidualSteps(
        IEnumerable<Guid> stepIds,
        IReadOnlyDictionary<Guid, List<Guid>> outgoing
    )
    {
        var ids = stepIds.ToList();
        var indegree = ids.ToDictionary(stepId => stepId, _ => 0);
        foreach (var targets in outgoing.Values)
        {
            foreach (var target in targets)
            {
                indegree[target]++;
            }
        }

        var queue = new Queue<Guid>(ids.Where(stepId => indegree[stepId] == 0));
        var visited = new HashSet<Guid>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current) || !outgoing.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var target in targets)
            {
                indegree[target]--;
                if (indegree[target] == 0)
                {
                    queue.Enqueue(target);
                }
            }
        }

        return ids.Where(stepId => !visited.Contains(stepId)).ToList();
    }

    private static IReadOnlyList<RequestItem> FlattenRequests(
        IEnumerable<RequestCollection> collections
    )
    {
        var requests = new List<RequestItem>();
        foreach (var collection in collections)
        {
            AddRequests(collection, requests);
        }

        return requests;
    }

    private static void AddRequests(RequestCollection collection, ICollection<RequestItem> target)
    {
        foreach (var request in collection.Requests)
        {
            target.Add(request);
        }

        foreach (var child in collection.Children)
        {
            AddRequests(child, target);
        }
    }

    private static SuiteValidationIssue StepIssue(
        TestStep step,
        string code,
        string message,
        string hint,
        SuiteValidationSeverity severity = SuiteValidationSeverity.Error
    ) => Issue(code, message, hint, step.Id, step.RequestName, severity);

    private static SuiteValidationIssue Issue(
        string code,
        string message,
        string hint,
        Guid? stepId = null,
        string stepName = "",
        SuiteValidationSeverity severity = SuiteValidationSeverity.Error
    ) =>
        new()
        {
            Code = code,
            Severity = severity,
            StepId = stepId,
            StepName = stepName,
            Message = message,
            Hint = hint,
        };
}
