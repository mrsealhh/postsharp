using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PostSharp.ViewModels;

/// <summary>
/// Exposes a source collection page by page. The view only creates the rows that have been scrolled into
/// view, and the source collection change is coalesced so filtering does not rebuild the page many times.
/// </summary>
public sealed class PagedCollection<T> : ObservableObject
{
    private readonly ObservableCollection<T> _source;
    private readonly int _pageSize;
    private int _loaded;
    private bool _refreshPending;

    public PagedCollection(ObservableCollection<T> source, int pageSize = 40)
    {
        _source = source;
        _pageSize = Math.Max(1, pageSize);
        _source.CollectionChanged += OnSourceChanged;
        RefreshCore();
    }

    public ObservableCollection<T> Items { get; } = new();

    public bool HasMore => _loaded < _source.Count;

    public int TotalCount => _source.Count;

    public int LoadedCount => _loaded;

    public void LoadMore()
    {
        if (AppendPage())
        {
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(LoadedCount));
        }
    }

    /// <summary>Loads pages until the item at the given source index is present.</summary>
    public void EnsureIndex(int index)
    {
        if (index < 0)
        {
            return;
        }

        var changed = false;
        while (_loaded <= index && AppendPage())
        {
            changed = true;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(LoadedCount));
        }
    }

    public void Refresh() => RefreshCore();

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_refreshPending)
        {
            return;
        }

        _refreshPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _refreshPending = false;
                RefreshCore();
            },
            DispatcherPriority.Background
        );
    }

    private void RefreshCore()
    {
        _loaded = 0;
        Items.Clear();
        AppendPage();
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(LoadedCount));
    }

    private bool AppendPage()
    {
        var remaining = _source.Count - _loaded;
        if (remaining <= 0)
        {
            return false;
        }

        var take = Math.Min(_pageSize, remaining);
        for (var index = 0; index < take; index++)
        {
            Items.Add(_source[_loaded + index]);
        }

        _loaded += take;
        return true;
    }
}
