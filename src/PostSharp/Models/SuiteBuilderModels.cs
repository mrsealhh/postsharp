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

    public IReadOnlyList<string> ConditionOptions { get; } =
        new[] { "Always", "OnPreviousSuccess", "OnPreviousFailure" };

    public string RequestName => Step.RequestName;

    public string Method => Step.Method;

    public string Url => Step.Url;

    public BadgeTone MethodTone => Step.MethodTone;

    public string MappingLabel =>
        Step.InputMappings.Count == 0 ? "映射" : $"映射 ({Step.InputMappings.Count})";

    public string AssertionLabel =>
        Step.Assertions.Count == 0 ? "断言" : $"断言 ({Step.Assertions.Count})";

    public IRelayCommand? EditMappingsCommand { get; set; }

    public IRelayCommand? EditAssertionsCommand { get; set; }

    public void RefreshMappingLabel() => OnPropertyChanged(nameof(MappingLabel));

    public void RefreshAssertionLabel() => OnPropertyChanged(nameof(AssertionLabel));
}

/// <summary>A step that can be used as a mapping source.</summary>
public sealed record SuiteStepOption(Guid Id, string Label)
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
