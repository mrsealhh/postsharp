using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PostSharp.Models;

/// <summary>A selectable collection in the automation suite builder.</summary>
public sealed partial class SuiteSourceCollection : ObservableObject
{
    public required RequestCollection Collection { get; init; }

    public int Depth { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    public string Name => Collection.Name;

    public string RequestCountLabel => $"{Collection.Requests.Count} 个请求";

    public Thickness Indent => new(Depth * 16, 0, 0, 0);
}
