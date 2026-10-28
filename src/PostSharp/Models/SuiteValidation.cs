using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace PostSharp.Models;

public enum SuiteValidationSeverity
{
    Error,
    Warning,
}

/// <summary>A preflight problem that blocks a suite run or warns about a risky configuration.</summary>
public sealed class SuiteValidationIssue
{
    public string Code { get; init; } = string.Empty;

    public SuiteValidationSeverity Severity { get; init; }

    public Guid? StepId { get; init; }

    public string StepName { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Hint { get; init; } = string.Empty;

    public string DisplayMessage =>
        string.IsNullOrWhiteSpace(StepName) ? Message : $"{StepName}：{Message}";

    public bool IsError => Severity == SuiteValidationSeverity.Error;

    public bool CanLocate => StepId is not null;

    public string SeverityLabel => IsError ? "错误" : "提醒";

    public BadgeTone SeverityTone => IsError ? BadgeTone.Danger : BadgeTone.Warning;

    public IRelayCommand? LocateCommand { get; set; }
}

/// <summary>The complete result of validating a suite before save or run.</summary>
public sealed class SuiteValidationResult
{
    public SuiteValidationResult(IEnumerable<SuiteValidationIssue> issues)
    {
        Issues = issues.ToList();
    }

    public IReadOnlyList<SuiteValidationIssue> Issues { get; }

    public bool HasErrors => Issues.Any(issue => issue.IsError);

    public int ErrorCount => Issues.Count(issue => issue.IsError);

    public int WarningCount => Issues.Count(issue => !issue.IsError);

    public string Summary =>
        HasErrors
            ? $"预检未通过：{ErrorCount} 个错误"
                + (WarningCount > 0 ? $" · {WarningCount} 个提醒" : string.Empty)
        : WarningCount > 0 ? $"预检通过：{WarningCount} 个提醒"
        : "预检通过";
}
