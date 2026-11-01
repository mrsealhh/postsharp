using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;
using TKCode.Arc;

namespace PostSharp.ViewModels;

/// <summary>One selectable HAR entry in the recording session dialog.</summary>
public sealed class RecordingEntryViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public RecordingEntryViewModel(HarSessionEntry entry, bool isSelected, Action selectionChanged)
    {
        Entry = entry;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged;
    }

    public HarSessionEntry Entry { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged();
            }
        }
    }

    public string IndexText => Entry.Index.ToString(CultureInfo.InvariantCulture);

    public string Method => Entry.Method;

    public string Url => Entry.Url;

    public string DisplayUrl => Entry.DisplayUrl;

    public string Name => Entry.DisplayName;

    public string StatusText =>
        Entry.Status > 0 ? Entry.Status.ToString(CultureInfo.InvariantCulture) : "—";

    public BadgeTone StatusTone =>
        Entry.Status switch
        {
            >= 200 and < 300 => BadgeTone.Success,
            >= 300 and < 400 => BadgeTone.Info,
            >= 400 and < 500 => BadgeTone.Warning,
            >= 500 => BadgeTone.Danger,
            _ => BadgeTone.Neutral,
        };

    public string TimeText =>
        Entry.ElapsedMs > 0
            ? $"{Entry.ElapsedMs.ToString("0", CultureInfo.InvariantCulture)} ms"
            : string.Empty;

    public string MetaText =>
        string.IsNullOrWhiteSpace(Entry.ResourceType) ? Entry.MimeType
        : string.IsNullOrWhiteSpace(Entry.MimeType) ? Entry.ResourceType
        : $"{Entry.ResourceType} · {Entry.MimeType}";

    public bool IsStatic => Entry.IsStatic;
}

/// <summary>Filters, selects and imports HAR entries as a recorded session.</summary>
public sealed partial class RecordingSessionViewModel : ObservableObject
{
    public const string AllMethods = "全部方法";
    public const string AllStatuses = "全部状态";
    public const string SuccessStatuses = "2xx 成功";
    public const string ClientErrorStatuses = "4xx 客户端错误";
    public const string ServerErrorStatuses = "5xx 服务端错误";
    public const string NoStatus = "无响应";

    private readonly Action<IReadOnlyList<HarSessionEntry>, bool> _apply;
    private readonly Action _close;
    private readonly List<RecordingEntryViewModel> _entries;
    private string _filterText = string.Empty;
    private string _selectedMethod = AllMethods;
    private string _selectedStatus = AllStatuses;
    private bool _includeStaticResources;
    private string _error = string.Empty;

    public RecordingSessionViewModel(
        IReadOnlyList<HarSessionEntry> entries,
        Action<IReadOnlyList<HarSessionEntry>, bool> apply,
        Action close
    )
    {
        _apply = apply;
        _close = close;
        _entries = entries
            .Select(entry => new RecordingEntryViewModel(
                entry,
                !entry.IsStatic,
                OnSelectionChanged
            ))
            .ToList();
        MethodOptions = new[] { AllMethods }
            .Concat(
                entries
                    .Select(entry => entry.Method)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(method => method, StringComparer.Ordinal)
            )
            .ToArray();
        StatusOptions =
        [
            AllStatuses,
            SuccessStatuses,
            ClientErrorStatuses,
            ServerErrorStatuses,
            NoStatus,
        ];
        IsTruncated = entries.Count >= HarSessionService.MaxEntries;
        Refresh();
    }

    public IReadOnlyList<RecordingEntryViewModel> Entries => _entries;

    public ObservableCollection<RecordingEntryViewModel> VisibleEntries { get; } = new();

    public IReadOnlyList<string> MethodOptions { get; }

    public IReadOnlyList<string> StatusOptions { get; }

    public bool IsTruncated { get; }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                Refresh();
            }
        }
    }

    public string SelectedMethod
    {
        get => _selectedMethod;
        set
        {
            if (SetProperty(ref _selectedMethod, value))
            {
                Refresh();
            }
        }
    }

    public string SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value))
            {
                Refresh();
            }
        }
    }

    public bool IncludeStaticResources
    {
        get => _includeStaticResources;
        set
        {
            if (SetProperty(ref _includeStaticResources, value))
            {
                Refresh();
            }
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    public int TotalCount => _entries.Count;

    public int VisibleCount => VisibleEntries.Count;

    public int SelectedCount => _entries.Count(entry => entry.IsSelected);

    public bool CanImport => SelectedCount > 0;

    public bool HasVisibleEntries => VisibleCount > 0;

    public string Summary =>
        $"共 {TotalCount} 条 · 显示 {VisibleCount} 条 · 已选 {SelectedCount} 条";

    public string LimitNote =>
        IsTruncated
            ? $"HAR 条目较多，仅显示前 {HarSessionService.MaxEntries} 条，请先精简或分批导入。"
            : string.Empty;

    public bool HasLimitNote => IsTruncated;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var entry in VisibleEntries)
        {
            entry.IsSelected = true;
        }

        Error = string.Empty;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var entry in _entries)
        {
            entry.IsSelected = false;
        }

        Error = string.Empty;
    }

    [RelayCommand]
    private void ImportSelected() => Apply(generateSuite: false);

    [RelayCommand]
    private void ImportAndGenerateSuite() => Apply(generateSuite: true);

    [RelayCommand]
    private void Close() => _close();

    private void Apply(bool generateSuite)
    {
        var selected = _entries
            .Where(entry => entry.IsSelected)
            .Select(entry => entry.Entry)
            .ToList();
        if (selected.Count == 0)
        {
            Error = "至少选择一个请求后再导入。";
            return;
        }

        Error = string.Empty;
        _apply(selected, generateSuite);
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanImport));
        OnPropertyChanged(nameof(Summary));
        if (SelectedCount > 0)
        {
            Error = string.Empty;
        }
    }

    private void Refresh()
    {
        if (!IncludeStaticResources)
        {
            foreach (var entry in _entries.Where(entry => entry.IsStatic && entry.IsSelected))
            {
                entry.IsSelected = false;
            }
        }

        VisibleEntries.Clear();
        foreach (var entry in _entries)
        {
            if (!IncludeStaticResources && entry.IsStatic)
            {
                continue;
            }

            if (!Matches(entry))
            {
                continue;
            }

            VisibleEntries.Add(entry);
        }

        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(HasVisibleEntries));
        OnPropertyChanged(nameof(Summary));
    }

    private bool Matches(RecordingEntryViewModel entry)
    {
        if (
            !string.IsNullOrWhiteSpace(FilterText)
            && !entry.Url.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase)
            && !entry.Method.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        if (
            SelectedMethod != AllMethods
            && !string.Equals(entry.Method, SelectedMethod, StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        return SelectedStatus switch
        {
            SuccessStatuses => entry.Entry.Status is >= 200 and < 300,
            ClientErrorStatuses => entry.Entry.Status is >= 400 and < 500,
            ServerErrorStatuses => entry.Entry.Status >= 500,
            NoStatus => entry.Entry.Status == 0,
            _ => true,
        };
    }
}
