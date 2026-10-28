using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PostSharp.Models;

/// <summary>A selectable collection or request row in the suite builder's collection browser.</summary>
public sealed partial class SuiteSourceCollection : ObservableObject
{
    /// <summary>collection or request.</summary>
    public required string Kind { get; init; }

    public RequestCollection? Collection { get; init; }

    public RequestItem? Request { get; init; }

    public SuiteSourceCollection? Parent { get; init; }

    public List<SuiteSourceCollection> ChildItems { get; } = new();

    public int Depth { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded = true;

    public bool IsCollection => Kind == "collection";

    public bool IsRequest => Kind == "request";

    public bool IsFavorite =>
        IsCollection ? Collection?.IsFavorite == true : Request?.IsFavorite == true;

    public bool HasChildren =>
        Collection is not null && (Collection.Requests.Count > 0 || Collection.Children.Count > 0);

    public string Name => IsCollection ? Collection?.Name ?? "集合" : Request?.Name ?? "请求";

    public string RequestCountLabel =>
        IsCollection
            ? $"{Collection?.TotalRequestCount ?? 0} 个请求"
            : $"{Request?.Method} {Request?.Url}";

    public string KindLabel => IsCollection ? "集合" : "接口";

    public string SelectionTip =>
        IsCollection
            ? "勾选整个集合加入画布；再次点击取消整个集合"
            : "勾选该接口加入画布；再次点击取消";

    public string Method => Request?.Method ?? string.Empty;

    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    public IRelayCommand? ToggleCommand { get; set; }
}
