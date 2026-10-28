using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class SuiteWorkflowTests
{
    [Fact]
    public void StepExpectationMentionsConditionAndParallelGroup()
    {
        var step = new TestStep { Condition = "OnPreviousSuccess", ParallelGroup = 2 };

        Assert.Contains("OnPreviousSuccess", step.ExpectationText);
        Assert.Contains("并行组 2", step.ExpectationText);
    }

    [Fact]
    public void SuiteDefaultsToSequentialAndNoSchedule()
    {
        var suite = new TestSuite();

        Assert.False(suite.RunInParallel);
        Assert.Equal(0, suite.ScheduleMinutes);
    }

    [Fact]
    public void DetectsPowerShellImportKind()
    {
        Assert.Equal(
            ImportFileKind.PowerShell,
            ImportFileDetector.Detect("request.ps1", "Invoke-RestMethod -Uri 'https://example.com'")
        );
    }

    [Fact]
    public async Task FailureBranchRunsAndSuccessBranchSkips()
    {
        var suite = new TestSuite { StopOnError = true };
        var root = AddStep(suite, "root");
        var success = AddStep(suite, "success");
        var failure = AddStep(suite, "failure");
        var workflow = BuildWorkflow(suite);
        Connect(workflow, root, success, "OnSuccess");
        Connect(workflow, root, failure, "OnFailure");

        var progress = new List<SuiteWorkflowProgress>();
        var outcome = await SuiteWorkflowScheduler.RunAsync(
            suite,
            workflow,
            null,
            (step, _, _) => Complete(step, passed: step.Id != root.Id),
            CancellationToken.None,
            progress.Add
        );

        Assert.True(Find(outcome, success).Skipped);
        Assert.True(Find(outcome, failure).Passed);
        Assert.False(Find(outcome, failure).Skipped);
        Assert.True(Find(outcome, root).Error is not null);

        var edgeStates = progress
            .Where(item => item.Kind == SuiteWorkflowProgressKind.EdgeResolved)
            .ToDictionary(item => (item.SourceStepId, item.TargetStepId), item => item.EdgeActive);
        Assert.Equal(false, edgeStates[(root.Id, success.Id)]);
        Assert.Equal(true, edgeStates[(root.Id, failure.Id)]);
    }

    [Fact]
    public async Task SkippedStepDoesNotActivateAlwaysBranch()
    {
        var suite = new TestSuite { StopOnError = true };
        var root = AddStep(suite, "root");
        root.Condition = "OnPreviousFailure";
        var always = AddStep(suite, "always");
        var skipped = AddStep(suite, "skipped");
        var workflow = BuildWorkflow(suite);
        Connect(workflow, root, always, "Always");
        Connect(workflow, root, skipped, "OnSkipped");

        var outcome = await SuiteWorkflowScheduler.RunAsync(
            suite,
            workflow,
            null,
            (step, _, _) => Complete(step, passed: true),
            CancellationToken.None
        );

        Assert.True(Find(outcome, root).Skipped);
        Assert.True(Find(outcome, always).Skipped);
        Assert.False(Find(outcome, skipped).Skipped);
        Assert.True(Find(outcome, skipped).Passed);
    }

    [Fact]
    public async Task AnyJoinDoesNotWaitForAnInactiveBranch()
    {
        var suite = new TestSuite { StopOnError = false };
        var root = AddStep(suite, "root");
        var success = AddStep(suite, "success");
        var failure = AddStep(suite, "failure");
        var join = AddStep(suite, "join");
        join.JoinMode = "Any";
        var workflow = BuildWorkflow(suite);
        Connect(workflow, root, success, "OnSuccess");
        Connect(workflow, root, failure, "OnFailure");
        Connect(workflow, success, join, "Always");
        Connect(workflow, failure, join, "Always");

        var outcome = await SuiteWorkflowScheduler.RunAsync(
            suite,
            workflow,
            null,
            (step, _, _) => Complete(step, passed: step.Id != root.Id),
            CancellationToken.None
        );

        Assert.True(Find(outcome, join).Passed);
        Assert.False(Find(outcome, join).Skipped);
    }

    [Fact]
    public async Task StoppedRunReportsPendingStepsAsNotRun()
    {
        var suite = new TestSuite { StopOnError = true };
        var root = AddStep(suite, "root");
        var downstream = AddStep(suite, "downstream");
        var unrelated = AddStep(suite, "unrelated");
        var unrelatedChild = AddStep(suite, "unrelated-child");
        var workflow = BuildWorkflow(suite);
        Connect(workflow, root, downstream, "OnSuccess");
        Connect(workflow, unrelated, unrelatedChild, "OnSuccess");

        var progress = new List<SuiteWorkflowProgress>();
        var outcome = await SuiteWorkflowScheduler.RunAsync(
            suite,
            workflow,
            null,
            (step, _, _) => Complete(step, passed: step.Id == unrelated.Id),
            CancellationToken.None,
            progress.Add
        );

        Assert.True(Find(outcome, downstream).Skipped);
        Assert.Contains(
            progress,
            item =>
                item.Kind == SuiteWorkflowProgressKind.StepNotRun
                && item.StepId == unrelatedChild.Id
        );
    }

    [Fact]
    public async Task RunSnapshotKeepsNodeAndEdgeStates()
    {
        var suite = new TestSuite { StopOnError = true };
        var root = AddStep(suite, "root");
        var success = AddStep(suite, "success");
        var failure = AddStep(suite, "failure");
        var workflow = BuildWorkflow(suite);
        Connect(workflow, root, success, "OnSuccess");
        Connect(workflow, root, failure, "OnFailure");

        var outcome = await SuiteWorkflowScheduler.RunAsync(
            suite,
            workflow,
            null,
            (step, _, _) => Complete(step, passed: step.Id != root.Id),
            CancellationToken.None
        );

        Assert.Equal(
            "Failed",
            Assert.Single(outcome.NodeStates, state => state.StepId == root.Id).State
        );
        Assert.Equal(
            "Skipped",
            Assert.Single(outcome.NodeStates, state => state.StepId == success.Id).State
        );
        Assert.Equal(
            "Done",
            Assert.Single(outcome.NodeStates, state => state.StepId == failure.Id).State
        );
        Assert.True(
            Assert.Single(outcome.EdgeStates, edge => edge.TargetId == failure.Id.ToString()).Active
        );
        Assert.False(
            Assert.Single(outcome.EdgeStates, edge => edge.TargetId == success.Id.ToString()).Active
        );
    }

    private static TestStep AddStep(TestSuite suite, string name)
    {
        var step = new TestStep { RequestName = name, Url = $"https://example.test/{name}" };
        suite.Steps.Add(step);
        return step;
    }

    private static SuiteWorkflow BuildWorkflow(TestSuite suite)
    {
        var workflow = new SuiteWorkflow();
        foreach (var step in suite.Steps)
        {
            workflow.Nodes.Add(
                new SuiteWorkflowNode
                {
                    Id = step.Id.ToString(),
                    StepId = step.Id,
                    JoinMode = step.JoinMode,
                    X = suite.Steps.IndexOf(step) * 340,
                    Y = 0,
                }
            );
        }

        return workflow;
    }

    private static void Connect(
        SuiteWorkflow workflow,
        TestStep source,
        TestStep target,
        string condition
    )
    {
        workflow.Edges.Add(
            new SuiteWorkflowEdge
            {
                SourceId = source.Id.ToString(),
                TargetId = target.Id.ToString(),
                Condition = condition,
            }
        );
    }

    private static Task<SuiteStepRunOutcome> Complete(TestStep step, bool passed) =>
        Task.FromResult(
            new SuiteStepRunOutcome(
                new TestStepResult
                {
                    StepId = step.Id,
                    RequestId = step.RequestId,
                    Name = step.RequestName,
                    Method = step.Method,
                    Url = step.Url,
                    Passed = passed,
                    StatusText = passed ? "200 OK" : "500 失败",
                    Error = passed ? null : "模拟失败",
                },
                null
            )
        );

    private static TestStepResult Find(SuiteWorkflowRunOutcome outcome, TestStep step) =>
        Assert.Single(outcome.Steps, result => result.StepId == step.Id);
}
