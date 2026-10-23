using CommunityToolkit.Mvvm.ComponentModel;

namespace PostSharp.Models;

public partial class EnvironmentModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "本地环境";

    public Guid Id { get; init; } = Guid.NewGuid();

    public EditableRowCollection Variables { get; } = new();

    public override string ToString() => Name;
}
