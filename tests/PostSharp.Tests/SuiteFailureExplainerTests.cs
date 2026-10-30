using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class SuiteFailureExplainerTests
{
    private static TestRunResult Run(params TestStepResult[] steps) =>
        new()
        {
            SuiteId = Guid.NewGuid(),
            SuiteName = "登录回归",
            Total = steps.Length,
            Failed = steps.Count(step => !step.Passed && !step.Skipped),
            Passed = steps.Count(step => step.Passed && !step.Skipped),
            Skipped = steps.Count(step => step.Skipped),
            Steps = steps.ToList(),
        };

    [Fact]
    public void PreflightFailurePointsAtValidateSuite()
    {
        var run = Run();
        run.Error = "套件预检失败：步骤 2 引用了不存在的请求";

        var explanation = SuiteFailureExplainer.Explain(run);

        Assert.False(explanation.Passed);
        var finding = Assert.Single(explanation.Findings);
        Assert.Equal("Preflight", finding.Category);
        Assert.Contains("validate_suite", explanation.NextAction, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusMismatchNamesTheStepAndNextToolCall()
    {
        var stepId = Guid.NewGuid();
        var run = Run(
            new TestStepResult
            {
                StepId = stepId,
                Name = "创建订单",
                Passed = false,
                StatusCode = 500,
                ElapsedMs = 120,
                Error = "期望状态 201，实际 500",
            }
        );

        var explanation = SuiteFailureExplainer.Explain(run);

        Assert.False(explanation.Passed);
        var finding = explanation.Findings[0];
        Assert.Equal(stepId, finding.StepId);
        Assert.Equal("创建订单", finding.StepName);
        Assert.Equal("Status", finding.Category);
        Assert.Contains("期望状态 201，实际 500", finding.Message, StringComparison.Ordinal);
        Assert.Contains(
            "continue_suite_from_step",
            explanation.NextAction,
            StringComparison.Ordinal
        );
        Assert.Contains(stepId.ToString(), explanation.NextAction, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedAssertionKeepsTheAssertionDetail()
    {
        var run = Run(
            new TestStepResult
            {
                StepId = Guid.NewGuid(),
                Name = "查询订单",
                Passed = false,
                StatusCode = 200,
                Error = "实际 false",
                Assertions =
                {
                    new TestAssertionResult
                    {
                        Label = "JSONPath data.paid = true",
                        Passed = false,
                        Detail = "实际 false",
                    },
                },
            }
        );

        var explanation = SuiteFailureExplainer.Explain(run);

        var finding = explanation.Findings[0];
        Assert.Equal("Assertion", finding.Category);
        Assert.Contains("JSONPath data.paid = true", finding.Message, StringComparison.Ordinal);
        Assert.Equal("实际 false", finding.Evidence);
    }

    [Fact]
    public void NetworkFailureAndSkippedDependencyAreSeparated()
    {
        var run = Run(
            new TestStepResult
            {
                StepId = Guid.NewGuid(),
                Name = "健康检查",
                Passed = false,
                StatusCode = 0,
                Error = "连接被拒绝",
            },
            new TestStepResult
            {
                StepId = Guid.NewGuid(),
                Name = "后续步骤",
                Skipped = true,
            }
        );

        var explanation = SuiteFailureExplainer.Explain(run);

        Assert.Equal(2, explanation.Findings.Count);
        Assert.Contains(explanation.Findings, finding => finding.Category == "Network");
        Assert.Contains(explanation.Findings, finding => finding.Category == "Dependency");
    }

    [Fact]
    public void PassingRunHasNoFindings()
    {
        var run = Run(
            new TestStepResult
            {
                StepId = Guid.NewGuid(),
                Name = "健康检查",
                Passed = true,
                StatusCode = 200,
            }
        );

        var explanation = SuiteFailureExplainer.Explain(run);

        Assert.True(explanation.Passed);
        Assert.Empty(explanation.Findings);
        Assert.Contains("通过", explanation.Conclusion, StringComparison.Ordinal);
    }

    [Fact]
    public void NotRunStepsAreReportedWhenTheRunStops()
    {
        var run = Run(
            new TestStepResult
            {
                StepId = Guid.NewGuid(),
                Name = "失败步骤",
                Passed = false,
                StatusCode = 500,
                Error = "期望状态 200，实际 500",
            }
        );
        run.NotRun = 3;
        run.Stopped = true;

        var explanation = SuiteFailureExplainer.Explain(run);

        Assert.Contains(
            explanation.Findings,
            finding =>
                finding.Category == "Dependency"
                && finding.Message.Contains("3 个步骤没有运行", StringComparison.Ordinal)
        );
    }
}
