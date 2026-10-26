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
}
