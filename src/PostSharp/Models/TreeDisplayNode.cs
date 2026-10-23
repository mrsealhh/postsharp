using Avalonia;

namespace PostSharp.Models;

/// <summary>One visible row in the flattened sidebar tree: a collection header or a request.</summary>
public sealed class TreeDisplayNode
{
    public RequestCollection? Collection { get; init; }

    public RequestItem? Request { get; init; }

    public int Depth { get; init; }

    public bool IsCollection => Collection is not null;

    public bool IsRequest => Request is not null;

    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    public Thickness RequestIndent => new(Depth * 16 + 20, 0, 0, 0);
}
