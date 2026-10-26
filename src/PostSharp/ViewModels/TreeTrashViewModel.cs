using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;

namespace PostSharp.ViewModels;

/// <summary>
/// The recycle bin for collections and requests. Deleting from the tree or through MCP lands here first;
/// restoring puts the item back where it was, and only an explicit, confirmed purge removes it for good.
/// </summary>
/// <param name="restore">Puts a node back into the tree and returns the message to show.</param>
/// <param name="persist">Saves the workspace after the bin changed.</param>
/// <param name="toast">Shows a short confirmation.</param>
public partial class TreeTrashViewModel(
    Func<TrashedNode, string> restore,
    Action persist,
    Action<string> toast
) : ObservableObject
{
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

    /// <summary>Adds a node that was just deleted; the newest stays on top.</summary>
    public void Add(TrashedNode node)
    {
        Wire(node);
        Items.Insert(0, node);
        NotifyItemsChanged();
    }

    /// <summary>Adds a node read from the workspace file, keeping the stored order.</summary>
    public void Load(TrashedNode node)
    {
        Wire(node);
        Items.Add(node);
        NotifyItemsChanged();
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
        persist();
        toast("已永久删除");
    }

    [RelayCommand]
    private void CancelPurge() => ResetPurge();

    private void Wire(TrashedNode node)
    {
        node.RestoreCommand = new RelayCommand(() => Restore(node));
        node.PurgeCommand = new RelayCommand(() => RequestPurge(node));
    }

    private void Restore(TrashedNode node)
    {
        if (!Items.Remove(node))
        {
            return;
        }

        NotifyItemsChanged();
        toast(restore(node));
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
