using System.Globalization;
using PostSharp.Services;
using TKCode.Arc;

namespace PostSharp.ViewModels;

/// <summary>Display-ready projection of <see cref="SuiteRunDiff"/> for the run comparison dialog.</summary>
public sealed class SuiteRunDiffViewModel
{
    public SuiteRunDiffViewModel(SuiteRunDiff diff)
    {
        Diff = diff;
        ChangedRows = diff
            .Steps.Where(step => step.Change != SuiteRunChange.Unchanged)
            .Select(Row)
            .ToArray();
        UnchangedCount = diff.Steps.Count(step => step.Change == SuiteRunChange.Unchanged);
        NodeRows = diff
            .Nodes.Select(node => new SuiteRunNodeDiffRow(
                node.Name,
                $"{node.BaseState} → {node.TargetState}",
                $"{node.BaseElapsedMs} → {node.TargetElapsedMs} ms",
                node.Detail
            ))
            .ToArray();
    }

    public SuiteRunDiff Diff { get; }

    public IReadOnlyList<SuiteRunDiffRow> ChangedRows { get; }

    public IReadOnlyList<SuiteRunNodeDiffRow> NodeRows { get; }

    public int UnchangedCount { get; }

    public bool HasChanges => ChangedRows.Count > 0;

    public bool HasNodes => NodeRows.Count > 0;

    public string HeaderLine =>
        $"{Diff.SuiteName} · {Diff.BaseTimestamp:MM-dd HH:mm:ss} → {Diff.TargetTimestamp:MM-dd HH:mm:ss}";

    public string UnchangedText =>
        UnchangedCount == 0 ? "所有步骤都有变化" : $"另有 {UnchangedCount} 个步骤无显著变化";

    private static SuiteRunDiffRow Row(SuiteRunStepDiff step) =>
        new(
            step.Name,
            Label(step.Change),
            Tone(step.Change),
            step.Detail,
            $"{DisplayStatus(step.BaseStatus)} → {DisplayStatus(step.TargetStatus)}",
            Elapsed(step)
        );

    private static string Label(string change) =>
        change switch
        {
            SuiteRunChange.NewFailure => "新增失败",
            SuiteRunChange.Fixed => "已修复",
            SuiteRunChange.StillFailing => "仍然失败",
            SuiteRunChange.StatusChanged => "状态码变化",
            SuiteRunChange.Slower => "耗时回归",
            SuiteRunChange.Faster => "耗时改善",
            SuiteRunChange.Skipped => "已跳过",
            SuiteRunChange.Removed => "目标未运行",
            SuiteRunChange.Added => "目标新增",
            _ => "无变化",
        };

    private static BadgeTone Tone(string change) =>
        change switch
        {
            SuiteRunChange.NewFailure or SuiteRunChange.StillFailing => BadgeTone.Danger,
            SuiteRunChange.Fixed or SuiteRunChange.Faster => BadgeTone.Success,
            SuiteRunChange.StatusChanged or SuiteRunChange.Slower => BadgeTone.Warning,
            SuiteRunChange.Added => BadgeTone.Info,
            _ => BadgeTone.Neutral,
        };

    private static string DisplayStatus(int statusCode) =>
        statusCode > 0 ? statusCode.ToString(CultureInfo.InvariantCulture) : "—";

    private static string Elapsed(SuiteRunStepDiff step)
    {
        var delta = step.ElapsedDeltaMs;
        var deltaText =
            delta == 0 ? string.Empty
            : delta > 0 ? $"（+{delta} ms）"
            : $"（{delta} ms）";
        return $"{step.BaseElapsedMs} → {step.TargetElapsedMs} ms{deltaText}";
    }
}

public sealed record SuiteRunDiffRow(
    string Name,
    string ChangeLabel,
    BadgeTone Tone,
    string Detail,
    string StatusText,
    string ElapsedText
);

public sealed record SuiteRunNodeDiffRow(
    string Name,
    string StateText,
    string ElapsedText,
    string Detail
);
