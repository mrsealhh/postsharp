using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class SuiteRunDiffTests
{
    private static TestStepResult Step(
        Guid id,
        string name,
        bool passed,
        int status,
        long elapsed
    ) =>
        new()
        {
            StepId = id,
            Name = name,
            Passed = passed,
            StatusCode = status,
            ElapsedMs = elapsed,
        };

    [Fact]
    public void ClassifiesNewFailuresFixesStatusAndElapsedChanges()
    {
        var slowerId = Guid.NewGuid();
        var fixedId = Guid.NewGuid();
        var statusId = Guid.NewGuid();
        var newFailureId = Guid.NewGuid();
        var removedId = Guid.NewGuid();
        var addedId = Guid.NewGuid();

        var baseRun = new TestRunResult
        {
            SuiteName = "回归",
            Steps =
            {
                Step(slowerId, "slow", true, 200, 100),
                Step(fixedId, "fixed", false, 500, 50),
                Step(statusId, "status", true, 200, 30),
                Step(newFailureId, "broken", true, 200, 40),
                Step(removedId, "removed", true, 200, 20),
            },
            NodeStates =
            {
                new SuiteRunNodeState
                {
                    StepId = newFailureId,
                    Name = "broken",
                    State = "Done",
                    ElapsedMs = 40,
                },
            },
        };
        var targetRun = new TestRunResult
        {
            SuiteName = "回归",
            Steps =
            {
                Step(slowerId, "slow", true, 200, 400),
                Step(fixedId, "fixed", true, 200, 50),
                Step(statusId, "status", true, 404, 30),
                Step(newFailureId, "broken", false, 500, 40),
                Step(addedId, "added", true, 200, 10),
            },
            NodeStates =
            {
                new SuiteRunNodeState
                {
                    StepId = newFailureId,
                    Name = "broken",
                    State = "Failed",
                    ElapsedMs = 40,
                },
            },
        };

        var diff = SuiteRunDiffService.Compare(baseRun, targetRun);

        Assert.Equal(1, diff.NewFailures);
        Assert.Equal(1, diff.Fixed);
        Assert.Equal(1, diff.StatusChanged);
        Assert.Equal(1, diff.Slower);
        Assert.Equal(0, diff.Faster);
        Assert.Equal(1, diff.NotInTarget);
        Assert.Equal(1, diff.NewInTarget);
        Assert.Contains(
            diff.Steps,
            step =>
                step.StepId == slowerId
                && step.Change == SuiteRunChange.Slower
                && step.ElapsedDeltaMs == 300
        );
        Assert.Contains(
            diff.Steps,
            step => step.StepId == newFailureId && step.Change == SuiteRunChange.NewFailure
        );
        Assert.Contains("新增失败 1 步", diff.Conclusion, StringComparison.Ordinal);
        Assert.Single(diff.Nodes);
        Assert.Equal("Done → Failed", diff.Nodes[0].Detail);
    }

    [Fact]
    public void IdenticalRunsReportNoChanges()
    {
        var id = Guid.NewGuid();
        var baseRun = new TestRunResult
        {
            SuiteName = "稳定",
            Steps = { Step(id, "health", true, 200, 100) },
        };
        var targetRun = new TestRunResult
        {
            SuiteName = "稳定",
            Steps = { Step(id, "health", true, 200, 120) },
        };

        var diff = SuiteRunDiffService.Compare(baseRun, targetRun);

        Assert.Equal(1, diff.Unchanged);
        Assert.Equal(0, diff.Slower);
        Assert.Contains("结果一致", diff.Conclusion, StringComparison.Ordinal);
    }

    [Fact]
    public void ThresholdUsesTheLargerOfAbsoluteAndPercentValues()
    {
        var id = Guid.NewGuid();
        var baseRun = new TestRunResult
        {
            SuiteName = "阈值",
            Steps = { Step(id, "fast", true, 200, 10) },
        };
        var targetRun = new TestRunResult
        {
            SuiteName = "阈值",
            Steps = { Step(id, "fast", true, 200, 70) },
        };

        var diff = SuiteRunDiffService.Compare(baseRun, targetRun, elapsedRegressionPercent: 5);

        Assert.Equal(1, diff.Slower);
    }
}
