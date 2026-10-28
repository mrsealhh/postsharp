using System.Collections.Concurrent;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>The result of one scheduled suite step; the response is kept for downstream mappings and branches.</summary>
public sealed record SuiteStepRunOutcome(TestStepResult Result, HttpResult? Response)
{
    public static SuiteStepRunOutcome Cancelled(TestStep step) =>
        new(
            new TestStepResult
            {
                StepId = step.Id,
                RequestId = step.RequestId,
                Name = step.RequestName,
                Method = step.Method,
                Url = step.Url,
                StatusText = "已取消",
                Error = "套件已取消",
                Passed = false,
            },
            null
        );
}

public sealed class SuiteWorkflowRunOutcome
{
    public List<TestStepResult> Steps { get; } = new();

    public List<SuiteRunNodeState> NodeStates { get; } = new();

    public List<SuiteRunEdgeState> EdgeStates { get; } = new();

    public bool Stopped { get; set; }

    public bool Cancelled { get; set; }
}

public enum SuiteWorkflowProgressKind
{
    StepStarted,
    StepCompleted,
    StepFailed,
    StepSkipped,
    StepNotRun,
    EdgeResolved,
}

/// <summary>A live run event for the canvas: one step changed state or one dependency was resolved.</summary>
public sealed record SuiteWorkflowProgress(
    SuiteWorkflowProgressKind Kind,
    Guid? StepId = null,
    Guid? SourceStepId = null,
    Guid? TargetStepId = null,
    bool? EdgeActive = null,
    long ElapsedMs = 0,
    string? Detail = null
);

/// <summary>
/// Runs a suite graph with parallel branches, conditional edges and skip propagation.
/// </summary>
public static class SuiteWorkflowScheduler
{
    public static async Task<SuiteWorkflowRunOutcome> RunAsync(
        TestSuite suite,
        SuiteWorkflow workflow,
        EnvironmentModel? environment,
        Func<
            TestStep,
            IReadOnlyDictionary<Guid, HttpResult>,
            CancellationToken,
            Task<SuiteStepRunOutcome>
        > runner,
        CancellationToken cancellationToken,
        Action<SuiteWorkflowProgress>? progress = null
    )
    {
        var outcome = new SuiteWorkflowRunOutcome();
        var stepsById = suite.Steps.ToDictionary(step => step.Id);
        var stepIdByNode = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var nodesByStep = new Dictionary<Guid, SuiteWorkflowNode>();
        foreach (var node in workflow.Nodes)
        {
            if (node.StepId == Guid.Empty || !stepsById.ContainsKey(node.StepId))
            {
                continue;
            }

            if (!nodesByStep.TryAdd(node.StepId, node))
            {
                continue;
            }

            stepIdByNode[node.Id] = node.StepId;
        }

        if (nodesByStep.Count == 0)
        {
            return outcome;
        }

        var incoming = nodesByStep.Keys.ToDictionary(
            stepId => stepId,
            _ => new List<(Guid SourceStepId, SuiteWorkflowEdge Edge)>()
        );
        foreach (var edge in workflow.Edges)
        {
            if (
                !stepIdByNode.TryGetValue(edge.SourceId, out var sourceStepId)
                || !stepIdByNode.TryGetValue(edge.TargetId, out var targetStepId)
                || !incoming.TryGetValue(targetStepId, out var list)
            )
            {
                continue;
            }

            if (sourceStepId == targetStepId)
            {
                continue;
            }

            list.Add((sourceStepId, edge));
        }

        var outgoing = nodesByStep.Keys.ToDictionary(stepId => stepId, _ => new List<Guid>());
        var outgoingEdges = nodesByStep.Keys.ToDictionary(
            stepId => stepId,
            _ => new List<SuiteWorkflowEdge>()
        );
        foreach (var (targetStepId, branches) in incoming)
        {
            foreach (var branch in branches)
            {
                outgoing[branch.SourceStepId].Add(targetStepId);
                outgoingEdges[branch.SourceStepId].Add(branch.Edge);
            }
        }

        var pending = new HashSet<Guid>(nodesByStep.Keys);
        var running = new Dictionary<Guid, Task<SuiteStepRunOutcome>>();
        var states = new Dictionary<Guid, StepState>();
        var reportedEdges = new HashSet<string>(StringComparer.Ordinal);
        var responses = new ConcurrentDictionary<Guid, HttpResult>();
        var nodeStates = nodesByStep.Keys.ToDictionary(
            stepId => stepId,
            stepId => new SuiteRunNodeState
            {
                StepId = stepId,
                Name = stepsById[stepId].RequestName,
                State = "Pending",
            }
        );
        var edgeStates = new Dictionary<(string Source, string Target), SuiteRunEdgeState>();
        foreach (var edge in workflow.Edges)
        {
            if (
                !stepIdByNode.ContainsKey(edge.SourceId) || !stepIdByNode.ContainsKey(edge.TargetId)
            )
            {
                continue;
            }

            edgeStates.TryAdd(
                (edge.SourceId, edge.TargetId),
                new SuiteRunEdgeState
                {
                    SourceId = edge.SourceId,
                    TargetId = edge.TargetId,
                    Active = false,
                }
            );
        }
        var maxParallelism = suite.MaxParallelism;
        var stopping = false;
        var stopped = false;
        var cancelled = false;
        var startedAny = false;
        var allowedAfterFailure = new HashSet<Guid>();

        void AllowDescendants(Guid stepId)
        {
            var queue = new Queue<Guid>();
            queue.Enqueue(stepId);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var target in outgoing[current])
                {
                    if (allowedAfterFailure.Add(target))
                    {
                        queue.Enqueue(target);
                    }
                }
            }
        }

        void ReportEdges(Guid stepId, StepState state)
        {
            foreach (var edge in outgoingEdges[stepId])
            {
                if (!stepIdByNode.TryGetValue(edge.TargetId, out var targetStepId))
                {
                    continue;
                }

                var active = IsEdgeActive(edge, state, environment);
                reportedEdges.Add($"{edge.SourceId}|{edge.TargetId}");
                if (edgeStates.TryGetValue((edge.SourceId, edge.TargetId), out var edgeState))
                {
                    edgeState.Active = active;
                }

                progress?.Invoke(
                    new SuiteWorkflowProgress(
                        SuiteWorkflowProgressKind.EdgeResolved,
                        SourceStepId: stepId,
                        TargetStepId: targetStepId,
                        EdgeActive: active
                    )
                );
            }
        }

        void SkipNode(TestStep step, string reason)
        {
            var skipped = CreateSkippedResult(step, reason);
            var state = new StepState(new SuiteStepRunOutcome(skipped, null), DateTimeOffset.Now);
            states[step.Id] = state;
            outcome.Steps.Add(skipped);
            pending.Remove(step.Id);
            startedAny = true;
            nodeStates[step.Id].State = "Skipped";
            nodeStates[step.Id].Detail = reason;
            progress?.Invoke(
                new SuiteWorkflowProgress(
                    SuiteWorkflowProgressKind.StepSkipped,
                    StepId: step.Id,
                    Detail: reason
                )
            );
            ReportEdges(step.Id, state);
            if (stopped)
            {
                AllowDescendants(step.Id);
            }
        }

        while (pending.Count > 0 || running.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                stopping = true;
            }

            startedAny = false;
            foreach (var stepId in OrderPending(pending, suite))
            {
                if (stopping)
                {
                    break;
                }

                if (stopped && !allowedAfterFailure.Contains(stepId))
                {
                    continue;
                }

                if (maxParallelism > 0 && running.Count >= maxParallelism)
                {
                    break;
                }

                var step = stepsById[stepId];
                var node = nodesByStep[stepId];
                var branches = incoming[stepId];
                var ready = IsReady(node.JoinMode, branches, states, environment);
                if (!ready)
                {
                    continue;
                }

                var activeBranches = branches
                    .Where(branch => states.ContainsKey(branch.SourceStepId))
                    .Where(branch =>
                        IsEdgeActive(branch.Edge, states[branch.SourceStepId], environment)
                    )
                    .ToList();
                if (branches.Count > 0 && activeBranches.Count == 0)
                {
                    SkipNode(step, "上游分支条件不满足");
                    continue;
                }

                if (!IsStepConditionActive(step, activeBranches, states, environment))
                {
                    SkipNode(step, "步骤条件不满足");
                    continue;
                }

                pending.Remove(stepId);
                startedAny = true;
                nodeStates[stepId].State = "Running";
                progress?.Invoke(
                    new SuiteWorkflowProgress(SuiteWorkflowProgressKind.StepStarted, StepId: stepId)
                );
                running[stepId] = RunStepAsync(
                    step,
                    activeBranches,
                    states,
                    responses,
                    runner,
                    cancellationToken
                );
            }

            if (running.Count == 0)
            {
                if (!startedAny)
                {
                    break;
                }

                continue;
            }

            var completedTask = await Task.WhenAny(running.Values);
            var completedStepId = running
                .First(pair => ReferenceEquals(pair.Value, completedTask))
                .Key;
            running.Remove(completedStepId);
            SuiteStepRunOutcome completedOutcome;
            try
            {
                completedOutcome = await completedTask;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                stopping = true;
                continue;
            }

            if (completedOutcome.Result.StatusText == "已取消")
            {
                cancelled = true;
                stopping = true;
                pending.Add(completedStepId);
                continue;
            }

            var completedState = new StepState(completedOutcome, DateTimeOffset.Now);
            states[completedStepId] = completedState;
            if (completedOutcome.Response is not null)
            {
                responses[completedStepId] = completedOutcome.Response;
            }

            outcome.Steps.Add(completedOutcome.Result);
            nodeStates[completedStepId].State =
                completedOutcome.Result.Skipped ? "Skipped"
                : completedOutcome.Result.Passed ? "Done"
                : "Failed";
            nodeStates[completedStepId].ElapsedMs = completedOutcome.Result.ElapsedMs;
            nodeStates[completedStepId].Detail = completedOutcome.Result.Passed
                ? completedOutcome.Result.StatusText
                : completedOutcome.Result.Error;
            var completedKind =
                completedOutcome.Result.Skipped ? SuiteWorkflowProgressKind.StepSkipped
                : completedOutcome.Result.Passed ? SuiteWorkflowProgressKind.StepCompleted
                : SuiteWorkflowProgressKind.StepFailed;
            progress?.Invoke(
                new SuiteWorkflowProgress(
                    completedKind,
                    StepId: completedStepId,
                    ElapsedMs: completedOutcome.Result.ElapsedMs,
                    Detail: completedOutcome.Result.Passed
                        ? completedOutcome.Result.StatusText
                        : completedOutcome.Result.Error
                )
            );
            ReportEdges(completedStepId, completedState);
            if (
                !completedOutcome.Result.Passed
                && !completedOutcome.Result.Skipped
                && suite.StopOnError
            )
            {
                stopped = true;
                AllowDescendants(completedStepId);
            }
        }

        var notRunReason =
            cancelled ? "已取消，未执行"
            : stopped ? "运行已停止，未执行"
            : "未执行";
        foreach (var stepId in pending)
        {
            nodeStates[stepId].State = "NotRun";
            nodeStates[stepId].Detail = notRunReason;
            progress?.Invoke(
                new SuiteWorkflowProgress(
                    SuiteWorkflowProgressKind.StepNotRun,
                    StepId: stepId,
                    Detail: notRunReason
                )
            );
        }

        foreach (var edge in workflow.Edges)
        {
            if (reportedEdges.Contains($"{edge.SourceId}|{edge.TargetId}"))
            {
                continue;
            }

            if (
                !stepIdByNode.TryGetValue(edge.SourceId, out var sourceStepId)
                || !stepIdByNode.TryGetValue(edge.TargetId, out var targetStepId)
            )
            {
                continue;
            }

            progress?.Invoke(
                new SuiteWorkflowProgress(
                    SuiteWorkflowProgressKind.EdgeResolved,
                    SourceStepId: sourceStepId,
                    TargetStepId: targetStepId,
                    EdgeActive: false
                )
            );
        }

        outcome.Cancelled = cancelled;
        outcome.Stopped = stopped || cancelled;
        outcome.Steps.Sort(
            (left, right) => StepOrder(suite, left).CompareTo(StepOrder(suite, right))
        );
        outcome.NodeStates.AddRange(
            nodeStates.Values.OrderBy(state => StepOrder(suite, state.StepId))
        );
        outcome.EdgeStates.AddRange(edgeStates.Values);
        return outcome;
    }

    private static IReadOnlyList<Guid> OrderPending(HashSet<Guid> pending, TestSuite suite) =>
        pending
            .OrderBy(
                stepId => suite.Steps.FirstIndex(step => step.Id == stepId),
                Comparer<int>.Default
            )
            .ToList();

    private static int StepOrder(TestSuite suite, TestStepResult result) =>
        StepOrder(suite, result.StepId);

    private static int StepOrder(TestSuite suite, Guid stepId)
    {
        var index = suite.Steps.FirstIndex(step => step.Id == stepId);
        return index < 0 ? int.MaxValue : index;
    }

    private static bool IsReady(
        string joinMode,
        IReadOnlyList<(Guid SourceStepId, SuiteWorkflowEdge Edge)> branches,
        IReadOnlyDictionary<Guid, StepState> states,
        EnvironmentModel? environment
    )
    {
        if (branches.Count == 0)
        {
            return true;
        }

        if (joinMode == "Any")
        {
            if (
                branches.Any(branch =>
                    states.TryGetValue(branch.SourceStepId, out var state)
                    && IsEdgeActive(branch.Edge, state, environment)
                )
            )
            {
                return true;
            }

            return branches.All(branch => states.ContainsKey(branch.SourceStepId));
        }

        return branches.All(branch => states.ContainsKey(branch.SourceStepId));
    }

    private static bool IsStepConditionActive(
        TestStep step,
        IReadOnlyList<(Guid SourceStepId, SuiteWorkflowEdge Edge)> activeBranches,
        IReadOnlyDictionary<Guid, StepState> states,
        EnvironmentModel? environment
    )
    {
        if (step.Condition == "Always")
        {
            return true;
        }

        if (activeBranches.Count == 0)
        {
            return step.Condition switch
            {
                "OnPreviousSuccess" => true,
                "OnPreviousFailure" => false,
                "Expression" => SuiteConditionEvaluator.Evaluate(
                    step.ConditionRules,
                    null,
                    null,
                    environment
                ),
                _ => true,
            };
        }

        var context = states[activeBranches[0].SourceStepId];
        var result = context.Outcome.Result;
        return step.Condition switch
        {
            "OnPreviousSuccess" => activeBranches.All(branch =>
            {
                var source = states[branch.SourceStepId].Outcome.Result;
                return source.Passed && !source.Skipped;
            }),
            "OnPreviousFailure" => activeBranches.Any(branch =>
            {
                var source = states[branch.SourceStepId].Outcome.Result;
                return !source.Passed && !source.Skipped;
            }),
            "Expression" => SuiteConditionEvaluator.Evaluate(
                step.ConditionRules,
                context.Outcome.Response,
                result,
                environment
            ),
            _ => true,
        };
    }

    private static bool IsEdgeActive(
        SuiteWorkflowEdge edge,
        StepState source,
        EnvironmentModel? environment
    )
    {
        var result = source.Outcome.Result;
        if (result.Skipped)
        {
            return edge.Condition == "OnSkipped";
        }

        return edge.Condition switch
        {
            "OnSuccess" => result.Passed,
            "OnFailure" => !result.Passed,
            "OnSkipped" => false,
            "Expression" => SuiteConditionEvaluator.Evaluate(
                edge.Rules,
                source.Outcome.Response,
                result,
                environment
            ),
            _ => true,
        };
    }

    private static async Task<SuiteStepRunOutcome> RunStepAsync(
        TestStep step,
        IReadOnlyList<(Guid SourceStepId, SuiteWorkflowEdge Edge)> activeBranches,
        IReadOnlyDictionary<Guid, StepState> states,
        IReadOnlyDictionary<Guid, HttpResult> responses,
        Func<
            TestStep,
            IReadOnlyDictionary<Guid, HttpResult>,
            CancellationToken,
            Task<SuiteStepRunOutcome>
        > runner,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.Now;
        var wait = activeBranches
            .Select(branch =>
                states[branch.SourceStepId].CompletedAt.AddMilliseconds(step.DelayMs) - now
            )
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        if (wait > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(wait, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return SuiteStepRunOutcome.Cancelled(step);
            }
        }

        try
        {
            return await runner(step, responses, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return SuiteStepRunOutcome.Cancelled(step);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "套件步骤调度异常 {StepName}", step.RequestName);
            return new SuiteStepRunOutcome(
                new TestStepResult
                {
                    StepId = step.Id,
                    RequestId = step.RequestId,
                    Name = step.RequestName,
                    Method = step.Method,
                    Url = step.Url,
                    StatusText = "步骤异常",
                    Error = exception.Message,
                    Passed = false,
                },
                null
            );
        }
    }

    private static TestStepResult CreateSkippedResult(TestStep step, string reason) =>
        new()
        {
            StepId = step.Id,
            RequestId = step.RequestId,
            Name = step.RequestName,
            Method = step.Method,
            Url = step.Url,
            Passed = true,
            Skipped = true,
            StatusText = "已跳过",
            Error = reason,
        };

    private sealed record StepState(SuiteStepRunOutcome Outcome, DateTimeOffset CompletedAt);
}

internal static class SuiteListExtensions
{
    public static int FirstIndex<T>(this IList<T> items, Func<T, bool> predicate)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (predicate(items[index]))
            {
                return index;
            }
        }

        return -1;
    }
}
