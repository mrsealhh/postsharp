using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Deep-copies a collection subtree for the tree context menu. Every node gets a new id, auth is
/// copied into independent objects and suites keep pointing at the original requests.
/// </summary>
public static class CollectionDuplicator
{
    public static RequestCollection Duplicate(RequestCollection source)
    {
        var copy = CloneSubtree(source);
        copy.Name = $"{source.Name} 副本";
        copy.IsExpanded = true;
        return copy;
    }

    private static RequestCollection CloneSubtree(RequestCollection source)
    {
        var copy = new RequestCollection
        {
            Name = source.Name,
            IsFavorite = false,
            IsExpanded = source.IsExpanded,
        };
        copy.Auth.CopyFrom(source.Auth.Clone());

        foreach (var request in source.Requests)
        {
            var requestCopy = request.Clone();
            requestCopy.Owner = copy;
            copy.Requests.Add(requestCopy);
        }

        foreach (var child in source.Children)
        {
            var childCopy = CloneSubtree(child);
            childCopy.Parent = copy;
            copy.Children.Add(childCopy);
        }

        return copy;
    }
}
