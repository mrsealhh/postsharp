using Avalonia;

namespace PostSharp.Models;

/// <summary>One visible row in the flattened sidebar tree: a collection header or a request.</summary>
public abstract class TreeDisplayNode
{
    private protected TreeDisplayNode(
        RequestCollection? collection,
        RequestItem? request,
        int depth
    )
    {
        Collection = collection;
        Request = request;
        Depth = depth;
    }

    public RequestCollection? Collection { get; }

    public RequestItem? Request { get; }

    public int Depth { get; }

    public abstract Thickness Indent { get; }
}

/// <summary>A collection header row; it owns its expand / collapse chevron and actions.</summary>
public sealed class TreeCollectionNode : TreeDisplayNode
{
    public TreeCollectionNode(RequestCollection collection, int depth)
        : base(collection, null, depth) { }

    public override Thickness Indent => new(Depth * 16, 0, 0, 0);
}

/// <summary>A request row, indented one step under its collection.</summary>
public sealed class TreeRequestNode : TreeDisplayNode
{
    public TreeRequestNode(RequestItem request, int depth)
        : base(null, request, depth) { }

    public override Thickness Indent => new(Depth * 16 + 20, 0, 0, 0);
}
