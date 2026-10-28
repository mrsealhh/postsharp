using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace PostSharp.Models;

/// <summary>One editable step in the suite builder.</summary>
public sealed partial class SuiteBuilderStep : ObservableObject
{
    public required TestStep Step { get; init; }

    [ObservableProperty]
    private string _delayText = "200";

    [ObservableProperty]
    private string _retryCountText = "0";

    [ObservableProperty]
    private string _retryDelayText = "500";

    [ObservableProperty]
    private string _condition = "Always";

    [ObservableProperty]
    private string _parallelGroupText = "0";

    [ObservableProperty]
    private string _joinMode = "All";

    [ObservableProperty]
    private bool _isSelected;

    public string RequestName => Step.RequestName;

    public string Method => Step.Method;

    public string Url => Step.Url;

    public BadgeTone MethodTone => Step.MethodTone;

    public string MappingLabel =>
        Step.InputMappings.Count == 0 ? "规则映射" : $"规则映射 ({Step.InputMappings.Count})";

    public string AssertionLabel =>
        Step.Assertions.Count == 0 ? "规则断言" : $"规则断言 ({Step.Assertions.Count})";

    public string ConditionDisplay =>
        Condition switch
        {
            "OnPreviousSuccess" => "上一步成功",
            "OnPreviousFailure" => "上一步失败",
            "Expression" => Step.ConditionRules.IsEmpty
                ? "条件表达式"
                : $"条件表达式：{Step.ConditionRules.Summary}",
            _ => "总是执行",
        };

    public string ConditionLabel =>
        Step.ConditionRules.IsEmpty ? "条件规则" : $"条件 ({Step.ConditionRules.Rules.Count})";

    public string DependencyLabel { get; set; } = "无上游";

    public void RefreshMappingLabel() => OnPropertyChanged(nameof(MappingLabel));

    public void RefreshAssertionLabel() => OnPropertyChanged(nameof(AssertionLabel));

    partial void OnConditionChanged(string value) => OnPropertyChanged(nameof(ConditionDisplay));

    public void RefreshConditionLabel()
    {
        OnPropertyChanged(nameof(ConditionLabel));
        OnPropertyChanged(nameof(ConditionDisplay));
    }

    public void RefreshDependencyLabel() => OnPropertyChanged(nameof(DependencyLabel));
}

/// <summary>A step that can be used as a mapping source.</summary>
public sealed record SuiteStepOption(Guid Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A selectable value with a Chinese display label.</summary>
public sealed record SuiteChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One editable input-mapping rule.</summary>
public sealed partial class InputMappingEditor : ObservableObject
{
    [ObservableProperty]
    private SuiteStepOption? _selectedSource;

    [ObservableProperty]
    private string _sourcePath = string.Empty;

    [ObservableProperty]
    private string _targetKind = "Body";

    [ObservableProperty]
    private string _targetPath = string.Empty;

    [ObservableProperty]
    private string _template = "{value}";

    [ObservableProperty]
    private string? _error;

    public IReadOnlyList<SuiteStepOption> SourceOptions { get; set; } =
        Array.Empty<SuiteStepOption>();

    public IRelayCommand? RemoveCommand { get; set; }
}

/// <summary>One editable condition rule row, shared by the list and workflow editors.</summary>
public sealed partial class ConditionRuleEditor : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPath))]
    private string _source = "StatusCode";

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsExpected))]
    private string _operator = "Equals";

    [ObservableProperty]
    private string _expected = string.Empty;

    [ObservableProperty]
    private SuiteChoice _selectedSourceChoice = new("StatusCode", "状态码");

    [ObservableProperty]
    private SuiteChoice _selectedOperatorChoice = new("Equals", "等于");

    [ObservableProperty]
    private string? _error;

    public IReadOnlyList<SuiteChoice> SourceOptions => SuiteConditionOptions.SourceChoices;

    public IReadOnlyList<SuiteChoice> OperatorOptions => SuiteConditionOptions.OperatorChoices;

    public bool NeedsPath => SuiteConditionOptions.NeedsPath(Source);

    public bool NeedsExpected => SuiteConditionOptions.NeedsExpected(Operator);

    public IRelayCommand? RemoveCommand { get; set; }

    public static ConditionRuleEditor FromModel(SuiteConditionRule rule)
    {
        var editor = new ConditionRuleEditor
        {
            Source = string.IsNullOrWhiteSpace(rule.Source) ? "StatusCode" : rule.Source,
            Path = rule.Path,
            Operator = string.IsNullOrWhiteSpace(rule.Operator) ? "Equals" : rule.Operator,
            Expected = rule.Expected,
        };
        editor.SelectedSourceChoice =
            editor.SourceOptions.FirstOrDefault(choice => choice.Value == editor.Source)
            ?? editor.SourceOptions[0];
        editor.SelectedOperatorChoice =
            editor.OperatorOptions.FirstOrDefault(choice => choice.Value == editor.Operator)
            ?? editor.OperatorOptions[0];
        return editor;
    }

    partial void OnSelectedSourceChoiceChanged(SuiteChoice value) => Source = value.Value;

    partial void OnSelectedOperatorChoiceChanged(SuiteChoice value) => Operator = value.Value;

    public SuiteConditionRule ToModel() =>
        new()
        {
            Source = Source,
            Path = Path.Trim(),
            Operator = Operator,
            Expected = Expected.Trim(),
        };
}

/// <summary>One editable assertion row in the suite builder.</summary>
public sealed partial class AssertionEditor : ObservableObject
{
    [ObservableProperty]
    private string _kind = "JSONPath";

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private string _expected = string.Empty;

    [ObservableProperty]
    private string? _error;

    public IRelayCommand? RemoveCommand { get; set; }
}
