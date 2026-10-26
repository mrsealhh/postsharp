using PostSharp.Models;

namespace PostSharp.ViewModels;

/// <summary>Moving tree nodes into the collection recycle bin and back.</summary>
public partial class MainWindowViewModel
{
    /// <summary>Moves a request to the recycle bin. Returns false when it is not in any collection.</summary>
    public bool DeleteRequest(Guid requestId, string source)
    {
        var request = Collections
            .SelectMany(collection => collection.Requests)
            .FirstOrDefault(candidate => candidate.Id == requestId);
        if (request is null)
        {
            return false;
        }

        var wasOpen =
            ReferenceEquals(Editing, request) || ReferenceEquals(SelectedRequest, request);
        TrashRequest(request, source);
        if (wasOpen)
        {
            SelectFirstRequestOrBlank();
        }

        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        return true;
    }

    private void TrashRequest(RequestItem request, string source)
    {
        if (request.Owner is not { } owner)
        {
            return;
        }

        var node = new TrashedNode
        {
            Source = source,
            Request = request,
            OriginalParentId = owner.Id,
            OriginalLocation = DescribeLocation(owner),
            OriginalPosition = owner.Requests.IndexOf(request),
        };

        owner.Requests.Remove(request);
        request.Owner = null;
        request.IsSelected = false;
        TreeTrash.Add(node);
    }

    private void TrashCollection(RequestCollection collection, string source)
    {
        var parent = collection.Parent;
        var position = parent is not null
            ? parent.Children.IndexOf(collection)
            : Collections.Where(candidate => candidate.Parent is null).ToList().IndexOf(collection);

        var node = new TrashedNode
        {
            Source = source,
            Collection = collection,
            OriginalParentId = parent?.Id,
            OriginalLocation = parent is null ? "根目录" : DescribeLocation(parent),
            OriginalPosition = position,
        };

        // Only the top of the subtree is detached: its sub-collections and requests stay linked for a restore.
        parent?.Children.Remove(collection);
        collection.Parent = null;
        foreach (var member in SelfAndDescendants(collection))
        {
            Collections.Remove(member);
            member.IsSelected = false;
        }

        TreeTrash.Add(node);
    }

    /// <summary>Puts a node back in its original place, or at the top level when that place is gone.</summary>
    private string RestoreTrashedNode(TrashedNode node)
    {
        string message;
        if (node.Collection is { } collection)
        {
            message = RestoreCollection(node, collection);
        }
        else if (node.Request is { } request)
        {
            message = RestoreRequest(node, request);
        }
        else
        {
            return "回收站中的这一项已无内容可恢复";
        }

        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        return message;
    }

    private string RestoreCollection(TrashedNode node, RequestCollection collection)
    {
        var parent = node.OriginalParentId is { } parentId
            ? Collections.FirstOrDefault(candidate => candidate.Id == parentId)
            : null;
        var fitsUnderParent =
            parent is not null
            && parent.Depth + GetCollectionSubtreeHeight(collection) <= MaxCollectionDepth;

        var members = SelfAndDescendants(collection);
        if (parent is not null && fitsUnderParent)
        {
            collection.Parent = parent;
            parent.Children.Insert(
                Math.Clamp(node.OriginalPosition, 0, parent.Children.Count),
                collection
            );
            parent.IsExpanded = true;
            foreach (var member in members)
            {
                Collections.Add(member);
            }

            SyncRootCollections();
            return $"已恢复集合「{collection.Name}」到「{DescribeLocation(parent)}」";
        }

        var roots = Collections.Where(candidate => candidate.Parent is null).ToList();
        var rootPosition = node.OriginalParentId is null ? node.OriginalPosition : roots.Count;
        roots.Insert(Math.Clamp(rootPosition, 0, roots.Count), collection);
        foreach (var member in members)
        {
            Collections.Add(member);
        }

        SyncFlatCollectionOrder(roots);
        return node.OriginalParentId is null
            ? $"已恢复集合「{collection.Name}」"
            : $"原位置「{node.OriginalLocation}」已不可用，集合「{collection.Name}」已恢复到根目录";
    }

    private string RestoreRequest(TrashedNode node, RequestItem request)
    {
        var target = node.OriginalParentId is { } ownerId
            ? Collections.FirstOrDefault(candidate => candidate.Id == ownerId)
            : null;
        if (target is not null)
        {
            request.Owner = target;
            target.Requests.Insert(
                Math.Clamp(node.OriginalPosition, 0, target.Requests.Count),
                request
            );
            target.IsExpanded = true;
            return $"已恢复请求「{request.Name}」到「{DescribeLocation(target)}」";
        }

        var fallback = Collections.FirstOrDefault(candidate => candidate.Parent is null);
        if (fallback is null)
        {
            fallback = new RequestCollection { Name = "恢复的请求" };
            Collections.Add(fallback);
        }

        request.Owner = fallback;
        fallback.Requests.Add(request);
        fallback.IsExpanded = true;
        return $"原集合「{node.OriginalLocation}」已不存在，请求「{request.Name}」已恢复到「{fallback.Name}」";
    }

    private static List<RequestCollection> SelfAndDescendants(RequestCollection collection)
    {
        var members = new List<RequestCollection> { collection };
        for (var index = 0; index < members.Count; index++)
        {
            members.AddRange(members[index].Children);
        }

        return members;
    }

    private static string DescribeLocation(RequestCollection collection)
    {
        var names = new List<string>();
        for (RequestCollection? current = collection; current is not null; current = current.Parent)
        {
            names.Insert(0, current.Name);
        }

        return string.Join(" / ", names);
    }
}
