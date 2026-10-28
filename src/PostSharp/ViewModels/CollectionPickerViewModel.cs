using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;

namespace PostSharp.ViewModels;

/// <summary>
/// The reusable collection browser used by the suite editor. It keeps the main window's collection tree
/// shape, adds search / favorites / expand-collapse, and exposes the checked requests in tree order.
/// </summary>
public sealed partial class CollectionPickerViewModel : ObservableObject
{
    private const int PageSize = 60;
    private readonly List<SuiteSourceCollection> _all = new();
    private readonly List<SuiteSourceCollection> _roots = new();
    private readonly List<SuiteSourceCollection> _filteredRows = new();
    private bool _syncing;
    private int _loadedRows;

    public ObservableCollection<SuiteSourceCollection> Rows { get; } = new();

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _mode = "全部";

    [ObservableProperty]
    private bool _showRequests = true;

    [ObservableProperty]
    private string _selectionSummary = "未选择";

    public event EventHandler? SelectionChanged;

    public IReadOnlyList<RequestItem> SelectedRequests =>
        _all.Where(row => row.IsRequest && row.IsSelected && row.Request is not null)
            .Select(row => row.Request!)
            .ToList();

    public int SelectedRequestCount => SelectedRequests.Count;

    public bool HasMore => _loadedRows < _filteredRows.Count;

    public int TotalRows => _filteredRows.Count;

    public void LoadMore()
    {
        if (AppendPage())
        {
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(TotalRows));
        }
    }

    public void Load(IEnumerable<RequestCollection> collections)
    {
        foreach (var row in _all)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        _all.Clear();
        _roots.Clear();
        foreach (var collection in collections.Where(candidate => candidate.Parent is null))
        {
            AddCollectionRows(collection, 0, null);
        }

        RefreshRows();
    }

    public void SetSelection(IEnumerable<Guid> requestIds, bool raiseChange)
    {
        var selected = requestIds.ToHashSet();
        var expandChanged = false;
        _syncing = true;
        foreach (var row in _all)
        {
            if (row.IsRequest)
            {
                row.IsSelected = row.Request is not null && selected.Contains(row.Request.Id);
                if (row.IsSelected)
                {
                    for (var parent = row.Parent; parent is not null; parent = parent.Parent)
                    {
                        if (!parent.IsExpanded)
                        {
                            parent.IsExpanded = true;
                            expandChanged = true;
                        }
                    }
                }
            }
        }

        UpdateCollectionStates();
        _syncing = false;
        if (expandChanged)
        {
            RefreshRows();
        }

        UpdateSelectionSummary();
        if (raiseChange)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void ToggleAllCollections()
    {
        var expand = _all.Any(row => row.IsCollection && !row.IsExpanded);
        _syncing = true;
        foreach (var row in _all.Where(row => row.IsCollection))
        {
            row.IsExpanded = expand;
        }

        _syncing = false;
        RefreshRows();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _syncing = true;
        foreach (var row in _all.Where(row => row.IsRequest))
        {
            row.IsSelected = false;
        }

        UpdateCollectionStates();
        _syncing = false;
        UpdateSelectionSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFilterTextChanged(string value) => RefreshRows();

    partial void OnModeChanged(string value) => RefreshRows();

    partial void OnShowRequestsChanged(bool value) => RefreshRows();

    private void AddCollectionRows(
        RequestCollection collection,
        int depth,
        SuiteSourceCollection? parent
    )
    {
        var row = new SuiteSourceCollection
        {
            Kind = "collection",
            Collection = collection,
            Depth = depth,
            Parent = parent,
            IsExpanded = depth == 0,
        };
        row.ToggleCommand = new RelayCommand(() => row.IsExpanded = !row.IsExpanded);
        _all.Add(row);
        row.PropertyChanged += OnRowPropertyChanged;
        if (parent is null)
        {
            _roots.Add(row);
        }
        else
        {
            parent.ChildItems.Add(row);
        }

        foreach (var request in collection.Requests)
        {
            var child = new SuiteSourceCollection
            {
                Kind = "request",
                Request = request,
                Collection = collection,
                Depth = depth + 1,
                Parent = row,
            };
            row.ChildItems.Add(child);
            _all.Add(child);
            child.PropertyChanged += OnRowPropertyChanged;
        }

        foreach (var collectionChild in collection.Children)
        {
            AddCollectionRows(collectionChild, depth + 1, row);
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || sender is not SuiteSourceCollection row)
        {
            return;
        }

        if (e.PropertyName == nameof(SuiteSourceCollection.IsExpanded))
        {
            RefreshRows();
            return;
        }

        if (e.PropertyName != nameof(SuiteSourceCollection.IsSelected))
        {
            return;
        }

        _syncing = true;
        if (row.IsCollection)
        {
            foreach (var descendant in Descendants(row))
            {
                descendant.IsSelected = row.IsSelected;
            }
        }

        UpdateAncestors(row.Parent);
        _syncing = false;
        UpdateSelectionSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateAncestors(SuiteSourceCollection? parent)
    {
        while (parent is not null)
        {
            parent.IsSelected =
                parent.ChildItems.Count > 0 && parent.ChildItems.All(child => child.IsSelected);
            parent = parent.Parent;
        }
    }

    private void UpdateCollectionStates()
    {
        foreach (var row in _all.Where(row => row.IsCollection).Reverse())
        {
            row.IsSelected =
                row.ChildItems.Count > 0 && row.ChildItems.All(child => child.IsSelected);
        }
    }

    private static IEnumerable<SuiteSourceCollection> Descendants(SuiteSourceCollection row)
    {
        foreach (var child in row.ChildItems)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private void UpdateSelectionSummary()
    {
        var count = SelectedRequestCount;
        SelectionSummary = count == 0 ? "未选择" : $"已选 {count} 个请求";
    }

    private void RefreshRows()
    {
        _filteredRows.Clear();
        var searching = !string.IsNullOrWhiteSpace(FilterText);
        var favorites = Mode == "收藏";
        var forceOpen = searching || favorites;
        foreach (var root in _roots)
        {
            AddVisibleRows(root, forceOpen, searching, favorites);
        }

        _loadedRows = 0;
        Rows.Clear();
        AppendPage();
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(TotalRows));
        UpdateSelectionSummary();
    }

    private bool AppendPage()
    {
        var remaining = _filteredRows.Count - _loadedRows;
        if (remaining <= 0)
        {
            return false;
        }

        var take = Math.Min(PageSize, remaining);
        for (var index = 0; index < take; index++)
        {
            Rows.Add(_filteredRows[_loadedRows + index]);
        }

        _loadedRows += take;
        return true;
    }

    private void AddVisibleRows(
        SuiteSourceCollection row,
        bool forceOpen,
        bool searching,
        bool favorites
    )
    {
        if (!WouldShow(row, searching, favorites))
        {
            return;
        }

        _filteredRows.Add(row);
        if (row.IsCollection && (row.IsExpanded || forceOpen))
        {
            foreach (var child in row.ChildItems)
            {
                AddVisibleRows(child, forceOpen, searching, favorites);
            }
        }
    }

    private bool WouldShow(SuiteSourceCollection row, bool searching, bool favorites)
    {
        if (row.IsRequest)
        {
            if (!ShowRequests)
            {
                return false;
            }

            if (favorites && !row.IsFavorite && !IsUnderFavorite(row))
            {
                return false;
            }

            return !searching || Matches(row);
        }

        if (row.ChildItems.Any(child => WouldShow(child, searching, favorites)))
        {
            return true;
        }

        if (favorites)
        {
            return row.IsFavorite;
        }

        return !searching || Matches(row);
    }

    private static bool IsUnderFavorite(SuiteSourceCollection row)
    {
        for (var parent = row.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.IsFavorite)
            {
                return true;
            }
        }

        return false;
    }

    private bool Matches(SuiteSourceCollection row)
    {
        var text = FilterText.Trim();
        if (text.Length == 0)
        {
            return true;
        }

        if (row.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (row.IsRequest && row.Request is not null)
        {
            return row.Request.Url.Contains(text, StringComparison.OrdinalIgnoreCase)
                || row.Request.Method.Contains(text, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
