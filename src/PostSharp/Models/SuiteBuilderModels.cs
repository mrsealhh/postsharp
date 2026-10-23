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

    public string RequestName => Step.RequestName;

    public string Method => Step.Method;

    public string Url => Step.Url;

    public BadgeTone MethodTone => Step.MethodTone;

    public string MappingLabel => Step.InputMappings.Count == 0
        ? "映射"
        : $"映射 ({Step.InputMappings.Count})";

    public IRelayCommand? EditMappingsCommand { get; set; }

    public void RefreshMappingLabel() => OnPropertyChanged(nameof(MappingLabel));
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

    public IReadOnlyList<SuiteStepOption> SourceOptions { get; set; } = Array.Empty<SuiteStepOption>();

    public IRelayCommand? RemoveCommand { get; set; }
}
