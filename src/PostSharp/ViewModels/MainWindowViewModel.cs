using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;
using PostSharp.Services;
using TKCode.Arc;

namespace PostSharp.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly WorkspaceStore _store = new();
    private readonly HttpRequestService _http = new();
    private readonly MockApiServer _mockServer = new();
    private readonly Dictionary<RequestCollection, bool> _expanded = new();

    private object? _pendingDelete;
    private CancellationTokenSource? _sendCts;
    private bool _initializing = true;

    public MainWindowViewModel()
    {
        _mockServer.Start();
        LoadWorkspace();

        if (Collections.Count == 0)
        {
            SeedWorkspace();
        }

        ActiveEnvironment ??= Environments.FirstOrDefault();
        if (ActiveEnvironment is null)
        {
            var environment = new EnvironmentModel { Name = "本地环境" };
            environment.Variables.AddRow("baseUrl", _mockServer.BaseUrl);
            environment.Variables.EnsureTrailingRow();
            Environments.Add(environment);
            ActiveEnvironment = environment;
        }

        var firstRequest = Collections.SelectMany(collection => collection.Requests).FirstOrDefault();
        Editing = firstRequest ?? new RequestItem { Name = "新建请求" };
        SelectedCollection = firstRequest?.Owner;
        SelectedRequest = firstRequest;
        IsDraft = firstRequest is null;
        _initializing = false;

        MockStatus = _mockServer.IsRunning
            ? $"Mock 服务运行中 · {_mockServer.BaseUrl}"
            : "Mock 服务未启动";

        _mockServer.CustomHandler = new McpServer(new McpWorkspaceAdapter(this)).HandleHttpAsync;

        History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));

        Persist();
    }

    public ObservableCollection<RequestCollection> Collections { get; } = new();

    public ObservableCollection<EnvironmentModel> Environments { get; } = new();

    public ObservableCollection<HistoryEntry> History { get; } = new();

    public ObservableCollection<ResponseHeaderView> ResponseHeaders { get; } = new();

    public IReadOnlyList<string> Methods { get; } = new[]
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"
    };

    public event EventHandler? TreeChanged;

    [ObservableProperty]
    private RequestItem _editing = new();

    [ObservableProperty]
    private RequestCollection? _selectedCollection;

    [ObservableProperty]
    private RequestItem? _selectedRequest;

    [ObservableProperty]
    private EnvironmentModel? _activeEnvironment;

    [ObservableProperty]
    private bool _isDraft;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _treeIsEmpty;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private bool _isEnvironmentDialogOpen;

    [ObservableProperty]
    private bool _isMcpDialogOpen;

    [ObservableProperty]
    private bool _isRenameDialogOpen;

    [ObservableProperty]
    private string _renameDialogTitle = "重命名";

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    private bool _isDeleteDialogOpen;

    [ObservableProperty]
    private string _deleteDialogTitle = "删除？";

    [ObservableProperty]
    private string _deleteDialogDescription = string.Empty;

    [ObservableProperty]
    private bool _isToastOpen;

    [ObservableProperty]
    private string _toastTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJsonResponse))]
    [NotifyPropertyChangedFor(nameof(ShowRawResponse))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyResponse))]
    private bool _hasResponse;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyResponse))]
    [NotifyPropertyChangedFor(nameof(ShowResponseError))]
    private bool _hasResponseError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJsonResponse))]
    [NotifyPropertyChangedFor(nameof(ShowRawResponse))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyResponse))]
    private string? _responseError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJsonResponse))]
    [NotifyPropertyChangedFor(nameof(ShowRawResponse))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyResponse))]
    private bool _isJsonResponse;

    [ObservableProperty]
    private string _responseBody = string.Empty;

    [ObservableProperty]
    private string _responseStatusText = string.Empty;

    [ObservableProperty]
    private string _responseTimeText = string.Empty;

    [ObservableProperty]
    private string _responseSizeText = string.Empty;

    [ObservableProperty]
    private string _responseContentType = string.Empty;

    [ObservableProperty]
    private BadgeTone _responseTone = BadgeTone.Neutral;

    [ObservableProperty]
    private string _mockStatus = string.Empty;

    public bool HasSelection => SelectedRequest is not null || SelectedCollection is not null;

    public bool IsRequestSelected => SelectedRequest is not null;

    public bool HasHistory => History.Count > 0;

    public bool ShowJsonResponse => HasResponse && IsJsonResponse;

    public bool ShowRawResponse => HasResponse && !IsJsonResponse;

    public bool ShowEmptyResponse => !HasResponse && !HasResponseError;

    public bool ShowResponseError => HasResponseError;

    public string McpEndpoint => _mockServer.IsRunning
        ? $"{_mockServer.BaseUrl}/mcp"
        : "MCP 服务未启动";

    public string McpClientConfig => $$"""
                                       {
                                         "mcpServers": {
                                           "postsharp": {
                                             "url": "{{(_mockServer.IsRunning ? _mockServer.BaseUrl : "http://127.0.0.1:5199")}}/mcp"
                                           }
                                         }
                                       }
                                       """;

    public string McpToolSummary =>
        "list_collections · get_request · create_request · update_request · delete_request · "
        + "send_request · run_sequence · list_history · list_environments · set_variable";

    public string EditorHint => IsDraft
        ? "草稿 · 保存后加入集合"
        : "Ctrl+S 保存 · Ctrl+Enter 发送";

    partial void OnEditingChanged(RequestItem value)
    {
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(EditorHint));
    }

    partial void OnFilterTextChanged(string value) => NotifyTreeChanged();

    partial void OnActiveEnvironmentChanged(EnvironmentModel? value)
    {
        if (!_initializing)
        {
            Persist();
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsSending)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Editing.Url))
        {
            HasResponse = false;
            HasResponseError = true;
            ResponseError = "请先输入请求地址，例如 {{baseUrl}}/api/health。";
            ResponseTone = BadgeTone.Danger;
            ResponseStatusText = "请求失败";
            return;
        }

        IsSending = true;
        _sendCts = new CancellationTokenSource();

        try
        {
            var result = await _http.SendAsync(Editing, ActiveEnvironment, _sendCts.Token);
            ApplyResponse(result);

            if (result.HasResponse)
            {
                AddHistory(Editing, result);
            }

            Persist();
        }
        finally
        {
            IsSending = false;
            _sendCts?.Dispose();
            _sendCts = null;
        }
    }

    [RelayCommand]
    private void CancelSend() => _sendCts?.Cancel();

    [RelayCommand]
    private void Save()
    {
        if (Editing.Owner is null)
        {
            var target = SelectedCollection ?? Collections.FirstOrDefault();
            if (target is null)
            {
                target = new RequestCollection { Name = "我的集合" };
                Collections.Add(target);
            }

            Editing.Owner = target;
            if (!target.Requests.Contains(Editing))
            {
                target.Requests.Add(Editing);
            }

            SelectedCollection = target;
            SelectedRequest = Editing;
            IsDraft = false;
        }

        Persist();
        NotifyTreeChanged();
        ShowToast("请求已保存");
    }

    [RelayCommand]
    private void AddRequest()
    {
        var target = SelectedCollection ?? Editing.Owner ?? Collections.FirstOrDefault();
        if (target is null)
        {
            target = new RequestCollection { Name = "我的集合" };
            Collections.Add(target);
        }

        var request = new RequestItem
        {
            Name = $"新建请求 {target.Requests.Count + 1}",
            Method = "GET",
            Url = ActiveEnvironment is not null ? "{{baseUrl}}/api/health" : "https://api.example.com/health",
            Owner = target
        };

        target.Requests.Add(request);
        _expanded[target] = true;
        OpenRequest(request);
        Persist();
        NotifyTreeChanged();
        ShowToast("已创建请求");
    }

    [RelayCommand]
    private void AddCollection()
    {
        var collection = new RequestCollection { Name = $"新建集合 {Collections.Count + 1}" };
        Collections.Add(collection);
        _expanded[collection] = true;
        SelectedCollection = collection;
        SelectedRequest = null;
        IsDraft = Editing.Owner is null;
        NotifySelectionChanged();
        Persist();
        NotifyTreeChanged();
        ShowToast("已创建集合");
    }

    [RelayCommand]
    private void DuplicateSelectedRequest()
    {
        if (SelectedRequest is not { } source || source.Owner is null)
        {
            ShowToast("请先选择一个已保存的请求");
            return;
        }

        var copy = source.Clone();
        copy.Name = $"{source.Name} 副本";
        copy.Owner = source.Owner;
        source.Owner.Requests.Add(copy);
        _expanded[source.Owner] = true;
        OpenRequest(copy);
        Persist();
        NotifyTreeChanged();
        ShowToast("已复制请求");
    }

    [RelayCommand]
    private void CopySelectedUrl()
    {
        if (SelectedRequest is { } request)
        {
            _ = CopyTextAsync(request.Url, "已复制 URL");
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedRequest is not null)
        {
            _pendingDelete = SelectedRequest;
            DeleteDialogTitle = "删除请求？";
            DeleteDialogDescription = $"“{SelectedRequest.Name}”将从集合中删除。此操作无法撤销。";
        }
        else if (SelectedCollection is not null)
        {
            _pendingDelete = SelectedCollection;
            DeleteDialogTitle = "删除集合？";
            DeleteDialogDescription =
                $"“{SelectedCollection.Name}”及其 {SelectedCollection.Requests.Count} 个请求将被删除。此操作无法撤销。";
        }
        else
        {
            ShowToast("请先选择集合或请求");
            return;
        }

        IsDeleteDialogOpen = true;
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        if (_pendingDelete is RequestItem request)
        {
            request.Owner?.Requests.Remove(request);
            if (ReferenceEquals(Editing, request))
            {
                SelectFirstRequestOrBlank();
            }
        }
        else if (_pendingDelete is RequestCollection collection)
        {
            Collections.Remove(collection);
            _expanded.Remove(collection);
            if (ReferenceEquals(Editing.Owner, collection))
            {
                SelectFirstRequestOrBlank();
            }
        }

        _pendingDelete = null;
        IsDeleteDialogOpen = false;
        Persist();
        NotifyTreeChanged();
        ShowToast("已删除");
    }

    [RelayCommand]
    private void CancelDelete()
    {
        _pendingDelete = null;
        IsDeleteDialogOpen = false;
    }

    [RelayCommand]
    private void RenameSelected()
    {
        if (SelectedRequest is not null)
        {
            _pendingDelete = SelectedRequest;
            RenameDialogTitle = "重命名请求";
            RenameText = SelectedRequest.Name;
        }
        else if (SelectedCollection is not null)
        {
            _pendingDelete = SelectedCollection;
            RenameDialogTitle = "重命名集合";
            RenameText = SelectedCollection.Name;
        }
        else
        {
            ShowToast("请先选择集合或请求");
            return;
        }

        IsRenameDialogOpen = true;
    }

    [RelayCommand]
    private void ApplyRename()
    {
        var name = RenameText.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            RenameText = "未命名";
            return;
        }

        switch (_pendingDelete)
        {
            case RequestItem request:
                request.Name = name;
                break;
            case RequestCollection collection:
                collection.Name = name;
                break;
        }

        _pendingDelete = null;
        IsRenameDialogOpen = false;
        Persist();
        NotifyTreeChanged();
        ShowToast("名称已更新");
    }

    [RelayCommand]
    private void CancelRename()
    {
        _pendingDelete = null;
        IsRenameDialogOpen = false;
    }

    [RelayCommand]
    private void OpenEnvironmentDialog() => IsEnvironmentDialogOpen = true;

    [RelayCommand]
    private void CloseMcpDialog() => IsMcpDialogOpen = false;

    [RelayCommand]
    private void CloseEnvironmentDialog()
    {
        IsEnvironmentDialogOpen = false;
        Persist();
        ShowToast("环境变量已保存");
    }

    [RelayCommand]
    private void CancelEnvironmentDialog() => IsEnvironmentDialogOpen = false;

    [RelayCommand]
    private void AddEnvironment()
    {
        var environment = new EnvironmentModel { Name = $"新环境 {Environments.Count + 1}" };
        environment.Variables.AddRow("baseUrl", _mockServer.BaseUrl);
        environment.Variables.EnsureTrailingRow();
        Environments.Add(environment);
        ActiveEnvironment = environment;
        Persist();
    }

    [RelayCommand]
    private void RemoveEnvironment()
    {
        if (ActiveEnvironment is null || Environments.Count <= 1)
        {
            ShowToast("至少保留一个环境");
            return;
        }

        var index = Environments.IndexOf(ActiveEnvironment);
        Environments.Remove(ActiveEnvironment);
        ActiveEnvironment = Environments[Math.Clamp(index, 0, Environments.Count - 1)];
        Persist();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        Persist();
        ShowToast("历史记录已清空");
    }

    public void OpenRequest(RequestItem request)
    {
        var changed = !ReferenceEquals(Editing, request);
        Editing = request;
        SelectedRequest = request;
        SelectedCollection = request.Owner;
        IsDraft = request.Owner is null;

        if (changed)
        {
            ClearResponse();
        }

        NotifySelectionChanged();
    }

    public void SelectCollection(RequestCollection collection)
    {
        SelectedCollection = collection;
        SelectedRequest = null;
        NotifySelectionChanged();
    }

    public void ToggleCollection(RequestCollection collection, bool expanded) => _expanded[collection] = expanded;

    public bool IsExpanded(RequestCollection collection) =>
        _expanded.TryGetValue(collection, out var expanded) && expanded;

    public void LoadHistory(HistoryEntry entry)
    {
        Editing = entry.ToRequest();
        SelectedRequest = null;
        SelectedCollection = null;
        IsDraft = true;
        ClearResponse();
        NotifySelectionChanged();
        NotifyTreeChanged();
        ShowToast("已载入历史请求，保存后可加入集合");
    }

    public void SaveNow() => Persist();

    public async Task CopyTextAsync(string text, string toast)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow: { } window })
        {
            return;
        }

        var clipboard = window.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
        ShowToast(toast);
    }

    public void NotifyWorkspaceChanged()
    {
        Persist();
        NotifyTreeChanged();
    }

    public void RecordExternalSend(RequestItem request, HttpResult result)
    {
        AddHistory(request, result);
        Persist();
    }

    public void AfterExternalDelete(RequestItem request)
    {
        if (!ReferenceEquals(Editing, request))
        {
            return;
        }

        Editing = new RequestItem { Name = "新建请求" };
        SelectedRequest = null;
        SelectedCollection = null;
        IsDraft = true;
        ClearResponse();
        NotifySelectionChanged();
    }

    public void Shutdown()
    {
        Persist();
        _mockServer.Dispose();
    }

    private void ApplyResponse(HttpResult result)
    {
        ResponseHeaders.Clear();

        if (!result.HasResponse)
        {
            HasResponse = false;
            HasResponseError = true;
            ResponseError = result.Error;
            ResponseTone = BadgeTone.Danger;
            ResponseStatusText = "请求失败";
            ResponseTimeText = string.Empty;
            ResponseSizeText = string.Empty;
            ResponseContentType = string.Empty;
            ResponseBody = string.Empty;
            IsJsonResponse = false;
            return;
        }

        HasResponse = true;
        HasResponseError = false;
        ResponseError = null;
        ResponseBody = result.Body;
        IsJsonResponse = result.IsJson;
        ResponseStatusText = string.IsNullOrWhiteSpace(result.ReasonPhrase)
            ? result.StatusCode.ToString()
            : $"{result.StatusCode} {result.ReasonPhrase}";
        ResponseTimeText = $"耗时 {result.ElapsedMs} ms";
        ResponseSizeText = $"大小 {FormatSize(result.SizeBytes)}";
        ResponseContentType = string.IsNullOrWhiteSpace(result.MediaType)
            ? "未知内容类型"
            : result.MediaType;
        ResponseTone = result.StatusCode switch
        {
            >= 200 and < 300 => BadgeTone.Success,
            >= 300 and < 400 => BadgeTone.Info,
            >= 400 and < 500 => BadgeTone.Warning,
            _ => BadgeTone.Danger
        };

        foreach (var header in result.Headers)
        {
            ResponseHeaders.Add(header);
        }
    }

    private void ClearResponse()
    {
        HasResponse = false;
        HasResponseError = false;
        ResponseError = null;
        ResponseBody = string.Empty;
        IsJsonResponse = false;
        ResponseStatusText = string.Empty;
        ResponseTimeText = string.Empty;
        ResponseSizeText = string.Empty;
        ResponseContentType = string.Empty;
        ResponseHeaders.Clear();
    }

    private void AddHistory(RequestItem request, HttpResult result)
    {
        var entry = new HistoryEntry
        {
            Name = request.Name,
            Method = request.Method,
            Url = request.Url,
            BodyMode = request.BodyMode,
            Body = request.Body,
            StatusCode = result.StatusCode,
            DurationMs = result.ElapsedMs,
            Params = request.Params.ActiveRows
                .Select(row => new RowSnapshot(row.Key, row.Value, row.IsEnabled))
                .ToList(),
            Headers = request.Headers.ActiveRows
                .Select(row => new RowSnapshot(row.Key, row.Value, row.IsEnabled))
                .ToList()
        };

        entry.OpenCommand = new RelayCommand(() => LoadHistory(entry));
        History.Insert(0, entry);
        while (History.Count > 40)
        {
            History.RemoveAt(History.Count - 1);
        }
    }

    private void SelectFirstRequestOrBlank()
    {
        var request = Collections.SelectMany(collection => collection.Requests).FirstOrDefault();
        if (request is not null)
        {
            OpenRequest(request);
        }
        else
        {
            Editing = new RequestItem { Name = "新建请求" };
            SelectedRequest = null;
            SelectedCollection = null;
            IsDraft = true;
            ClearResponse();
            NotifySelectionChanged();
        }
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsRequestSelected));
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(EditorHint));
    }

    private void NotifyTreeChanged() => TreeChanged?.Invoke(this, EventArgs.Empty);

    private void ShowToast(string title)
    {
        ToastTitle = title;
        IsToastOpen = true;
    }

    private void Persist()
    {
        if (_initializing)
        {
            return;
        }

        _store.Save(WorkspaceStore.Capture(Collections, Environments, History, ActiveEnvironment?.Id));
    }

    private void LoadWorkspace()
    {
        var workspace = _store.Load();
        if (workspace is null)
        {
            return;
        }

        foreach (var collectionDto in workspace.Collections)
        {
            var collection = new RequestCollection
            {
                Id = collectionDto.Id == Guid.Empty ? Guid.NewGuid() : collectionDto.Id,
                Name = collectionDto.Name
            };

            foreach (var requestDto in collectionDto.Requests)
            {
                var request = WorkspaceStore.ToModel(requestDto);
                request.Owner = collection;
                collection.Requests.Add(request);
            }

            Collections.Add(collection);
        }

        foreach (var environmentDto in workspace.Environments)
        {
            var environment = new EnvironmentModel
            {
                Id = environmentDto.Id == Guid.Empty ? Guid.NewGuid() : environmentDto.Id,
                Name = environmentDto.Name
            };

            foreach (var row in environmentDto.Variables)
            {
                environment.Variables.AddRow(row.Key, row.Value, row.IsEnabled);
            }

            environment.Variables.EnsureTrailingRow();
            Environments.Add(environment);
        }

        foreach (var historyDto in workspace.History)
        {
            var entry = WorkspaceStore.ToHistory(historyDto);
            entry.OpenCommand = new RelayCommand(() => LoadHistory(entry));
            History.Add(entry);
        }

        ActiveEnvironment = Environments.FirstOrDefault(
            environment => environment.Id == workspace.ActiveEnvironmentId)
            ?? Environments.FirstOrDefault();
    }

    private void SeedWorkspace()
    {
        var environment = new EnvironmentModel { Name = "本地 Mock 环境" };
        environment.Variables.AddRow("baseUrl", _mockServer.BaseUrl);
        environment.Variables.AddRow("token", "demo-token-123");
        environment.Variables.EnsureTrailingRow();
        Environments.Add(environment);
        ActiveEnvironment = environment;

        var collection = new RequestCollection { Name = "内置 Mock 示例" };

        var health = new RequestItem
        {
            Name = "健康检查",
            Method = "GET",
            Url = "{{baseUrl}}/api/health",
            Owner = collection
        };

        var users = new RequestItem
        {
            Name = "用户列表",
            Method = "GET",
            Url = "{{baseUrl}}/api/users",
            Owner = collection
        };
        users.Params.AddRow("page", "1");

        var echo = new RequestItem
        {
            Name = "回显请求",
            Method = "POST",
            Url = "{{baseUrl}}/api/echo",
            BodyMode = "Json",
            Body = """
                   {
                     "name": "PostSharp",
                     "message": "你好，接口",
                     "tags": ["mock", "demo"]
                   }
                   """,
            Owner = collection
        };
        echo.Headers.AddRow("X-Demo-Token", "{{token}}");

        var orders = new RequestItem
        {
            Name = "创建订单",
            Method = "POST",
            Url = "{{baseUrl}}/api/orders",
            BodyMode = "Json",
            Body = """
                   {
                     "flight": "MU5101",
                     "cabin": "Y",
                     "amount": 1280
                   }
                   """,
            Owner = collection
        };

        var slow = new RequestItem
        {
            Name = "慢响应",
            Method = "GET",
            Url = "{{baseUrl}}/api/slow?ms=1200",
            Owner = collection
        };

        var error = new RequestItem
        {
            Name = "模拟错误",
            Method = "GET",
            Url = "{{baseUrl}}/api/error",
            Owner = collection
        };

        collection.Requests.Add(health);
        collection.Requests.Add(users);
        collection.Requests.Add(echo);
        collection.Requests.Add(orders);
        collection.Requests.Add(slow);
        collection.Requests.Add(error);
        Collections.Add(collection);
        _expanded[collection] = true;

        var auth = new RequestCollection { Name = "示例：带鉴权头" };
        var me = new RequestItem
        {
            Name = "读取用户详情",
            Method = "GET",
            Url = "{{baseUrl}}/api/users/1",
            Owner = auth
        };
        me.Headers.AddRow("Authorization", "Bearer {{token}}");
        auth.Requests.Add(me);
        Collections.Add(auth);
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
            _ => $"{bytes / (1024d * 1024d):0.##} MB"
        };
    }
}
