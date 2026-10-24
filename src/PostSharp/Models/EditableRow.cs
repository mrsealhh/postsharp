using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PostSharp.Models;

/// <summary>One editable key/value line used by params, headers and environment variables.</summary>
public partial class KeyValueRow : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>Set by the owning collection so a row can remove itself from a data template.</summary>
    public IRelayCommand? RemoveCommand { get; set; }
}

/// <summary>
/// A key/value row list that always keeps one empty line at the bottom, the way Postman does.
/// Rows with an empty key are ignored when the request is sent.
/// </summary>
public sealed class EditableRowCollection : ObservableCollection<KeyValueRow>
{
    private bool _suspend;

    public EditableRowCollection()
    {
        AddEmptyCore();
    }

    public IEnumerable<KeyValueRow> ActiveRows =>
        this.Where(row => row.IsEnabled && !string.IsNullOrWhiteSpace(row.Key));

    public void AddRow(string key, string value, bool isEnabled = true)
    {
        _suspend = true;
        var row = new KeyValueRow
        {
            Key = key,
            Value = value,
            IsEnabled = isEnabled,
        };
        if (Count > 0 && IsBlank(this[^1]))
        {
            Insert(Count - 1, row);
        }
        else
        {
            Add(row);
        }

        _suspend = false;
    }

    /// <summary>Adds the trailing blank line when the last row has content.</summary>
    public void EnsureTrailingRow()
    {
        if (Count == 0 || !IsBlank(this[^1]))
        {
            AddEmptyCore();
        }
    }

    private static bool IsBlank(KeyValueRow row) =>
        string.IsNullOrWhiteSpace(row.Key) && string.IsNullOrWhiteSpace(row.Value);

    private void AddEmptyCore()
    {
        var row = new KeyValueRow();
        Add(row);
    }

    private void RemoveRow(KeyValueRow row)
    {
        if (Count == 1)
        {
            row.Key = string.Empty;
            row.Value = string.Empty;
            return;
        }

        Remove(row);
        if (Count == 1 && IsBlank(this[0]))
        {
            return;
        }

        EnsureTrailingRow();
    }

    protected override void InsertItem(int index, KeyValueRow item)
    {
        item.RemoveCommand ??= new RelayCommand(() => RemoveRow(item));
        base.InsertItem(index, item);
        item.PropertyChanged += OnRowPropertyChanged;
    }

    protected override void RemoveItem(int index)
    {
        this[index].PropertyChanged -= OnRowPropertyChanged;
        base.RemoveItem(index);
    }

    protected override void ClearItems()
    {
        foreach (var item in this)
        {
            item.PropertyChanged -= OnRowPropertyChanged;
        }

        base.ClearItems();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suspend || sender is not KeyValueRow row)
        {
            return;
        }

        if (e.PropertyName is not (nameof(KeyValueRow.Key) or nameof(KeyValueRow.Value)))
        {
            return;
        }

        if (Count > 0 && ReferenceEquals(this[^1], row) && !IsBlank(row))
        {
            AddEmptyCore();
        }
    }
}
