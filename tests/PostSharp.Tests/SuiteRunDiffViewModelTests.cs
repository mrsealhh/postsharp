using PostSharp.Services;
using PostSharp.ViewModels;
using TKCode.Arc;

namespace PostSharp.Tests;

public sealed class SuiteRunDiffViewModelTests
{
    [Fact]
    public void ProjectsChangesAndSkipsUnchangedSteps()
    {
        var diff = CreateDiff([
            Step(SuiteRunChange.NewFailure, 200, 500, 100, 300),
            Step(SuiteRunChange.Fixed, 500, 200, 300, 100),
            Step(SuiteRunChange.Unchanged, 200, 200, 100, 104),
        ]);

        var viewModel = new SuiteRunDiffViewModel(diff);

        Assert.True(viewModel.HasChanges);
        Assert.Equal(1, viewModel.UnchangedCount);
        Assert.Equal(2, viewModel.ChangedRows.Count);
        Assert.Equal("新增失败", viewModel.ChangedRows[0].ChangeLabel);
        Assert.Equal(BadgeTone.Danger, viewModel.ChangedRows[0].Tone);
        Assert.Equal("已修复", viewModel.ChangedRows[1].ChangeLabel);
        Assert.Equal(BadgeTone.Success, viewModel.ChangedRows[1].Tone);
    }

    [Fact]
    public void IdenticalRunsReportNoChanges()
    {
        var diff = CreateDiff([Step(SuiteRunChange.Unchanged, 200, 200, 100, 100)]);

        var viewModel = new SuiteRunDiffViewModel(diff);

        Assert.False(viewModel.HasChanges);
        Assert.Contains("无显著变化", viewModel.UnchangedText, StringComparison.Ordinal);
    }

    [Fact]
    public void ElapsedDeltaShowsSignAndMissingStatuses()
    {
        var diff = CreateDiff([Step(SuiteRunChange.Slower, 200, 200, 100, 450)]);

        var row = Assert.Single(new SuiteRunDiffViewModel(diff).ChangedRows);

        Assert.Equal("200 → 200", row.StatusText);
        Assert.Contains("+350 ms", row.ElapsedText, StringComparison.Ordinal);

        var removed = new SuiteRunDiffViewModel(
            CreateDiff([Step(SuiteRunChange.Removed, 200, 0, 100, 0)])
        ).ChangedRows.Single();
        Assert.Equal("200 → —", removed.StatusText);
    }

    private static SuiteRunStepDiff Step(
        string change,
        int baseStatus,
        int targetStatus,
        long baseElapsed,
        long targetElapsed
    ) =>
        new(
            Guid.NewGuid(),
            "步骤",
            change,
            true,
            true,
            baseStatus,
            targetStatus,
            baseElapsed,
            targetElapsed,
            targetElapsed - baseElapsed,
            "明细"
        );

    private static SuiteRunDiff CreateDiff(IReadOnlyList<SuiteRunStepDiff> steps) =>
        new(
            "套件",
            Guid.NewGuid(),
            DateTimeOffset.Now.AddMinutes(-5),
            Guid.NewGuid(),
            DateTimeOffset.Now,
            0,
            0,
            0,
            0,
            0,
            steps.Count(step => step.Change == SuiteRunChange.Unchanged),
            0,
            0,
            "结论",
            steps,
            []
        );
}
