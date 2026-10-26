using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;

namespace PostSharp.ViewModels;

/// <summary>
/// The recycle bin for collections and requests. Deleting from the tree or through MCP lands here first;
/// restoring puts the item back where it was, and only an explicit, confirmed purge removes it for good.
/// Trashed objects keep their ids, so suites that use them work again after a restore.
/// </summary>
/// <param name="collections">The workspace's flat collection list, in tree order.</param>
/// <param name="changed">Saves the workspace and refreshes the tree after the bin changed it.</param>
/// <param name="toast">Shows a short confirmation.</param>
public partial class TreeTrashViewModel(
    ObservableCollection<RequestCollection> collections,
    Action changed,
    Action<string> toast
) : ObservableObject
{
    /// <summary>The bin keeps the newest items; older ones are dropped so the workspace file stays small.</summary>
    public const int Capacity = 200;

    private TrashedNode? _pendingPurge;
    private bool _purgeAllPending;

    public ObservableCollection<TrashedNode> Items { get; } = new();

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isPurgeConfirmOpen;

    [ObservableProperty]
    private string _purgeTitle = string.Empty;

    [ObservableProperty]
    private string _purgeDescription = string.Empty;

    public bool HasItems => Items.Count > 0;

    public string ButtonTip =>
        HasItems
            ? $"集合回收站（{Items.Count} 项）：恢复已删除的集合和请求"
            : "集合回收站：删除的集合和请求会先进入这里";

    /// <summary>Adds a node read from the workspace file, keeping the stored order.</summary>
    public void Load(TrashedNode node)
    {
        Wire(node);
        Items.Add(node);
        NotifyItemsChanged();
    }

    /// <summary>
    /// Takes a request out of its collection into the bin; false when it is in no collection. The caller saves and
    /// moves the selection, since it knows whether the request was open.
    /// </summary>
    public bool TrashRequest(RequestItem request, string source)
    {
        if (request.Owner is not { } owner)
        {
            return false;
        }

        var node = new TrashedNode
        {
            Source = source,
            Request = request,
            OriginalParentId = owner.Id,
            OriginalLocation = CollectionTree.DescribeLocation(owner),
            OriginalPosition = owner.Requests.IndexOf(request),
        };

        owner.Requests.Remove(request);
        request.Owner = null;
        request.IsSelected = false;
        Add(node);
        return true;
    }

    /// <summary>Takes a collection, with its sub-collections and requests, out of the tree into the bin.</summary>
    public void TrashCollection(RequestCollection collection, string source)
    {
        var parent = collection.Parent;
        var position = parent is not null
            ? parent.Children.IndexOf(collection)
            : collections.Where(candidate => candidate.Parent is null).ToList().IndexOf(collection);

        var node = new TrashedNode
        {
            Source = source,
            Collection = collection,
            OriginalParentId = parent?.Id,
            OriginalLocation = parent is null ? "根目录" : CollectionTree.DescribeLocation(parent),
            OriginalPosition = position,
        };

        // Only the top of the subtree is detached: its sub-collections and requests stay linked for a restore.
        parent?.Children.Remove(collection);
        collection.Parent = null;
        foreach (var member in CollectionTree.SelfAndDescendants(collection))
        {
            collections.Remove(member);
            member.IsSelected = false;
        }

        Add(node);
    }

    [RelayCommand]
    private void Open() => IsOpen = true;

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void RequestEmpty()
    {
        if (!HasItems)
        {
            return;
        }

        _pendingPurge = null;
        _purgeAllPending = true;
        PurgeTitle = "清空集合回收站？";
        PurgeDescription = $"回收站中的 {Items.Count} 项将被永久删除，无法恢复。";
        IsPurgeConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmPurge()
    {
        if (_purgeAllPending)
        {
            Items.Clear();
        }
        else if (_pendingPurge is { } node)
        {
            Items.Remove(node);
        }

        ResetPurge();
        NotifyItemsChanged();
        changed();
        toast("已永久删除");
    }

    [RelayCommand]
    private void CancelPurge() => ResetPurge();

    private void Add(TrashedNode node)
    {
        Wire(node);
        Items.Insert(0, node);
        while (Items.Count > Capacity)
        {
            var oldest = Items[^1];
            Items.RemoveAt(Items.Count - 1);
            Log.Information(
                "集合回收站超过上限，自动永久删除最早的一项 {Kind} {Name} {DeletedAt}",
                oldest.KindLabel,
                oldest.Name,
                oldest.DeletedAt
            );
            toast(
                $"集合回收站超过 {Capacity} 项，已永久删除最早的{oldest.KindLabel}「{oldest.Name}」。"
            );
        }

        NotifyItemsChanged();
    }

    private void Wire(TrashedNode node)
    {
        node.RestoreCommand = new RelayCommand(() => Restore(node));
        node.PurgeCommand = new RelayCommand(() => RequestPurge(node));
    }

    private void Restore(TrashedNode node)
    {
        if (!Items.Contains(node))
        {
            return;
        }

        if (node.Collection is { } existingCollection && collections.Contains(existingCollection))
        {
            Items.Remove(node);
            NotifyItemsChanged();
            changed();
            toast($"集合「{existingCollection.Name}」已经在工作区中。");
            return;
        }

        if (
            node.Request is { } existingRequest
            && existingRequest.Owner is { } owner
            && owner.Requests.Contains(existingRequest)
        )
        {
            Items.Remove(node);
            NotifyItemsChanged();
            changed();
            toast($"请求「{existingRequest.Name}」已经在工作区中。");
            return;
        }

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
            return;
        }

        Items.Remove(node);
        NotifyItemsChanged();
        changed();
        toast(message);
    }

    /// <summary>Puts a collection back under its old parent, or at the top level when that parent is gone.</summary>
    private string RestoreCollection(TrashedNode node, RequestCollection collection)
    {
        var parent = node.OriginalParentId is { } parentId
            ? collections.FirstOrDefault(candidate => candidate.Id == parentId)
            : null;
        var members = CollectionTree.SelfAndDescendants(collection);

        if (
            parent is not null
            && parent.Depth + CollectionTree.SubtreeHeight(collection) <= CollectionTree.MaxDepth
        )
        {
            collection.Parent = parent;
            parent.Children.Insert(
                Math.Clamp(node.OriginalPosition, 0, parent.Children.Count),
                collection
            );
            parent.IsExpanded = true;
            foreach (var member in members)
            {
                collections.Add(member);
            }

            CollectionTree.SyncFlatOrder(
                collections,
                collections.Where(candidate => candidate.Parent is null).ToList()
            );
            return $"已恢复集合「{collection.Name}」到「{CollectionTree.DescribeLocation(parent)}」";
        }

        var roots = collections.Where(candidate => candidate.Parent is null).ToList();
        var rootPosition = node.OriginalParentId is null ? node.OriginalPosition : roots.Count;
        roots.Insert(Math.Clamp(rootPosition, 0, roots.Count), collection);
        foreach (var member in members)
        {
            collections.Add(member);
        }

        CollectionTree.SyncFlatOrder(collections, roots);
        return node.OriginalParentId is null
            ? $"已恢复集合「{collection.Name}」"
            : $"原位置「{node.OriginalLocation}」已不可用，集合「{collection.Name}」已恢复到根目录";
    }

    /// <summary>Puts a request back in its old collection, or in the first top-level one when that is gone.</summary>
    private string RestoreRequest(TrashedNode node, RequestItem request)
    {
        var target = node.OriginalParentId is { } ownerId
            ? collections.FirstOrDefault(candidate => candidate.Id == ownerId)
            : null;
        if (target is not null)
        {
            request.Owner = target;
            target.Requests.Insert(
                Math.Clamp(node.OriginalPosition, 0, target.Requests.Count),
                request
            );
            target.IsExpanded = true;
            return $"已恢复请求「{request.Name}」到「{CollectionTree.DescribeLocation(target)}」";
        }

        var fallback = collections.FirstOrDefault(candidate => candidate.Parent is null);
        if (fallback is null)
        {
            fallback = new RequestCollection { Name = "恢复的请求" };
            collections.Add(fallback);
        }

        request.Owner = fallback;
        fallback.Requests.Add(request);
        fallback.IsExpanded = true;
        return $"原集合「{node.OriginalLocation}」已不存在，请求「{request.Name}」已恢复到「{fallback.Name}」";
    }

    private void RequestPurge(TrashedNode node)
    {
        _pendingPurge = node;
        _purgeAllPending = false;
        PurgeTitle = $"永久删除{node.KindLabel}？";
        PurgeDescription = node.Collection is not null
            ? $"“{node.Name}”及其中 {node.RequestCount} 个请求将被永久删除，无法恢复。"
            : $"“{node.Name}”将被永久删除，无法恢复。";
        IsPurgeConfirmOpen = true;
    }

    private void ResetPurge()
    {
        _pendingPurge = null;
        _purgeAllPending = false;
        IsPurgeConfirmOpen = false;
    }

    private void NotifyItemsChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ButtonTip));
    }
}
