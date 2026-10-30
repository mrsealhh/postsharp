using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Stable change codes for one step between two runs.</summary>
public static class SuiteRunChange
{
    public const string NewFailure = "NewFailure";
    public const string Fixed = "Fixed";
    public const string StillFailing = "StillFailing";
    public const string StatusChanged = "StatusChanged";
    public const string Slower = "Slower";
    public const string Faster = "Faster";
    public const string Skipped = "Skipped";
    public const string Unchanged = "Unchanged";
    public const string Removed = "Removed";
    public const string Added = "Added";
}

public sealed record SuiteRunStepDiff(
    Guid StepId,
    string Name,
    string Change,
    bool? BasePassed,
    bool? TargetPassed,
    int BaseStatus,
    int TargetStatus,
    long BaseElapsedMs,
    long TargetElapsedMs,
    long ElapsedDeltaMs,
    string Detail
);

public sealed record SuiteRunNodeDiff(
    Guid StepId,
    string Name,
    string BaseState,
    string TargetState,
    long BaseElapsedMs,
    long TargetElapsedMs,
    string Detail
);

/// <summary>Structured comparison of two runs of the same suite.</summary>
public sealed record SuiteRunDiff(
    string SuiteName,
    Guid BaseRunId,
    DateTimeOffset BaseTimestamp,
    Guid TargetRunId,
    DateTimeOffset TargetTimestamp,
    int NewFailures,
    int Fixed,
    int StatusChanged,
    int Slower,
    int Faster,
    int Unchanged,
    int NotInTarget,
    int NewInTarget,
    string Conclusion,
    IReadOnlyList<SuiteRunStepDiff> Steps,
    IReadOnlyList<SuiteRunNodeDiff> Nodes
);

/// <summary>
/// Compares two completed runs step by step. Steps are matched by their stable id, so a reordered
/// suite still reports the same node as changed; status and elapsed regressions are classified
/// separately from pass/fail changes.
/// </summary>
public static class SuiteRunDiffService
{
    public const int MinimumElapsedDeltaMs = 50;

    public static SuiteRunDiff Compare(
        TestRunResult baseRun,
        TestRunResult targetRun,
        int elapsedRegressionPercent = 20
    )
    {
        var baseSteps = Index(baseRun.Steps);
        var targetSteps = Index(targetRun.Steps);
        var steps = new List<SuiteRunStepDiff>();
        foreach (var (key, baseStep) in baseSteps)
        {
            if (!targetSteps.TryGetValue(key, out var targetStep))
            {
                steps.Add(Diff(SuiteRunChange.Removed, baseStep, null, "目标运行中没有该步骤"));
                continue;
            }

            steps.Add(CompareStep(baseStep, targetStep, elapsedRegressionPercent));
        }

        foreach (var (key, targetStep) in targetSteps)
        {
            if (!baseSteps.ContainsKey(key))
            {
                steps.Add(Diff(SuiteRunChange.Added, null, targetStep, "基准运行中没有该步骤"));
            }
        }

        var nodes = CompareNodes(baseRun, targetRun);
        var newFailures = steps.Count(step => step.Change == SuiteRunChange.NewFailure);
        var fixedCount = steps.Count(step => step.Change == SuiteRunChange.Fixed);
        var statusChanged = steps.Count(step => step.Change == SuiteRunChange.StatusChanged);
        var slower = steps.Count(step => step.Change == SuiteRunChange.Slower);
        var faster = steps.Count(step => step.Change == SuiteRunChange.Faster);
        var notInTarget = steps.Count(step => step.Change == SuiteRunChange.Removed);
        var newInTarget = steps.Count(step => step.Change == SuiteRunChange.Added);
        var unchanged = steps.Count(step => step.Change == SuiteRunChange.Unchanged);

        return new SuiteRunDiff(
            targetRun.SuiteName,
            baseRun.Id,
            baseRun.Timestamp,
            targetRun.Id,
            targetRun.Timestamp,
            newFailures,
            fixedCount,
            statusChanged,
            slower,
            faster,
            unchanged,
            notInTarget,
            newInTarget,
            Conclusion(
                newFailures,
                fixedCount,
                statusChanged,
                slower,
                faster,
                notInTarget,
                newInTarget
            ),
            steps,
            nodes
        );
    }

    private static SuiteRunStepDiff CompareStep(
        TestStepResult baseStep,
        TestStepResult targetStep,
        int elapsedRegressionPercent
    )
    {
        var delta = targetStep.ElapsedMs - baseStep.ElapsedMs;
        if (targetStep.Skipped)
        {
            return Diff(
                SuiteRunChange.Skipped,
                baseStep,
                targetStep,
                baseStep.Skipped ? "两次都跳过" : "本次被跳过"
            );
        }

        if (!targetStep.Passed && !targetStep.Skipped)
        {
            return Diff(
                baseStep.Passed && !baseStep.Skipped
                    ? SuiteRunChange.NewFailure
                    : SuiteRunChange.StillFailing,
                baseStep,
                targetStep,
                targetStep.Error ?? "步骤失败"
            );
        }

        if (!baseStep.Passed && !baseStep.Skipped)
        {
            return Diff(SuiteRunChange.Fixed, baseStep, targetStep, "上次失败，本次通过");
        }

        if (baseStep.StatusCode != targetStep.StatusCode)
        {
            return Diff(
                SuiteRunChange.StatusChanged,
                baseStep,
                targetStep,
                $"状态码 {baseStep.StatusCode} → {targetStep.StatusCode}"
            );
        }

        var threshold = Math.Max(
            MinimumElapsedDeltaMs,
            (long)
                Math.Ceiling(
                    baseStep.ElapsedMs * Math.Clamp(elapsedRegressionPercent, 0, 1000) / 100d
                )
        );
        if (delta >= threshold)
        {
            return Diff(
                SuiteRunChange.Slower,
                baseStep,
                targetStep,
                $"耗时增加 {delta} ms（{baseStep.ElapsedMs} → {targetStep.ElapsedMs} ms）"
            );
        }

        if (-delta >= threshold)
        {
            return Diff(
                SuiteRunChange.Faster,
                baseStep,
                targetStep,
                $"耗时减少 {-delta} ms（{baseStep.ElapsedMs} → {targetStep.ElapsedMs} ms）"
            );
        }

        return Diff(SuiteRunChange.Unchanged, baseStep, targetStep, "结果和耗时无显著变化");
    }

    private static SuiteRunStepDiff Diff(
        string change,
        TestStepResult? baseStep,
        TestStepResult? targetStep,
        string detail
    )
    {
        var current = targetStep ?? baseStep;
        return new SuiteRunStepDiff(
            current?.StepId ?? Guid.Empty,
            current?.Name ?? string.Empty,
            change,
            baseStep is null ? null : baseStep.Passed,
            targetStep is null ? null : targetStep.Passed,
            baseStep?.StatusCode ?? 0,
            targetStep?.StatusCode ?? 0,
            baseStep?.ElapsedMs ?? 0,
            targetStep?.ElapsedMs ?? 0,
            (targetStep?.ElapsedMs ?? 0) - (baseStep?.ElapsedMs ?? 0),
            detail
        );
    }

    private static IReadOnlyList<SuiteRunNodeDiff> CompareNodes(
        TestRunResult baseRun,
        TestRunResult targetRun
    )
    {
        var baseNodes = baseRun.NodeStates.ToDictionary(node => node.StepId, node => node);
        var diffs = new List<SuiteRunNodeDiff>();
        foreach (var targetNode in targetRun.NodeStates)
        {
            if (!baseNodes.TryGetValue(targetNode.StepId, out var baseNode))
            {
                continue;
            }

            if (
                string.Equals(baseNode.State, targetNode.State, StringComparison.Ordinal)
                && baseNode.ElapsedMs == targetNode.ElapsedMs
            )
            {
                continue;
            }

            diffs.Add(
                new SuiteRunNodeDiff(
                    targetNode.StepId,
                    targetNode.Name,
                    baseNode.State,
                    targetNode.State,
                    baseNode.ElapsedMs,
                    targetNode.ElapsedMs,
                    $"{baseNode.State} → {targetNode.State}"
                )
            );
        }

        return diffs;
    }

    private static string Conclusion(
        int newFailures,
        int fixedCount,
        int statusChanged,
        int slower,
        int faster,
        int notInTarget,
        int newInTarget
    )
    {
        var parts = new List<string>();
        if (newFailures > 0)
        {
            parts.Add($"新增失败 {newFailures} 步");
        }

        if (fixedCount > 0)
        {
            parts.Add($"修复 {fixedCount} 步");
        }

        if (statusChanged > 0)
        {
            parts.Add($"状态码变化 {statusChanged} 步");
        }

        if (slower > 0)
        {
            parts.Add($"耗时回归 {slower} 步");
        }

        if (faster > 0)
        {
            parts.Add($"耗时改善 {faster} 步");
        }

        if (notInTarget > 0)
        {
            parts.Add($"目标运行缺少 {notInTarget} 步");
        }

        if (newInTarget > 0)
        {
            parts.Add($"目标运行新增 {newInTarget} 步");
        }

        return parts.Count == 0
            ? "两次运行结果一致，未发现失败、状态码或耗时变化。"
            : string.Join("；", parts) + "。";
    }

    private static Dictionary<string, TestStepResult> Index(IReadOnlyList<TestStepResult> steps)
    {
        var index = new Dictionary<string, TestStepResult>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            index[Key(step)] = step;
        }

        return index;
    }

    private static string Key(TestStepResult step) =>
        step.StepId != Guid.Empty
            ? step.StepId.ToString("N")
            : $"{step.RequestId:N}|{step.Name}|{step.Method}|{step.Url}";
}
