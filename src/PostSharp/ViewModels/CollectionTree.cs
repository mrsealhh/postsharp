using System.Collections.ObjectModel;
using PostSharp.Models;

namespace PostSharp.ViewModels;

/// <summary>
/// Rules of the collection tree shared by the main window and the recycle bin. The workspace keeps every collection
/// in one flat list whose order follows the tree, root first.
/// </summary>
public static class CollectionTree
{
    public const int MaxDepth = 5;

    /// <summary>How many levels a collection occupies, counting itself.</summary>
    public static int SubtreeHeight(RequestCollection collection) =>
        1 + (collection.Children.Count == 0 ? 0 : collection.Children.Max(SubtreeHeight));

    /// <summary>The collection followed by all of its sub-collections, top level first.</summary>
    public static IReadOnlyList<RequestCollection> SelfAndDescendants(RequestCollection collection)
    {
        var members = new List<RequestCollection> { collection };
        for (var index = 0; index < members.Count; index++)
        {
            members.AddRange(members[index].Children);
        }

        return members;
    }

    /// <summary>"Parent / Child" names from the top level down to the collection.</summary>
    public static string DescribeLocation(RequestCollection collection)
    {
        var names = new List<string>();
        for (RequestCollection? current = collection; current is not null; current = current.Parent)
        {
            names.Insert(0, current.Name);
        }

        return string.Join(" / ", names);
    }

    /// <summary>
    /// Rewrites the flat list in tree order for the given top-level order. Does nothing when the tree and the list
    /// disagree on how many collections exist, so a half-applied change never drops a collection.
    /// </summary>
    public static void SyncFlatOrder(
        ObservableCollection<RequestCollection> collections,
        IReadOnlyList<RequestCollection> roots
    )
    {
        var ordered = new List<RequestCollection>();
        foreach (var root in roots)
        {
            AddInTreeOrder(root, ordered);
        }

        if (ordered.Count != collections.Count)
        {
            return;
        }

        collections.Clear();
        foreach (var collection in ordered)
        {
            collections.Add(collection);
        }
    }

    private static void AddInTreeOrder(RequestCollection collection, List<RequestCollection> target)
    {
        target.Add(collection);
        foreach (var child in collection.Children)
        {
            AddInTreeOrder(child, target);
        }
    }
}
