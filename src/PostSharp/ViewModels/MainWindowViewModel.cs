using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;
using PostSharp.Services;
using TKCode.Arc;

namespace PostSharp.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly WorkspaceStore _store = new();
    private readonly AuditStore _auditStore = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly HttpRequestService _http = new();
    private readonly MockApiServer _mockServer = new();
    private readonly Dictionary<RequestCollection, bool> _expanded = new();
    private readonly Dictionary<Guid, TestRunResult> _suiteResults = new();

    private const int AuditPreviewLimit = 2000;
    private const int ResponsePreviewLimit = 8 * 1024;
    private const int DetailPageSize = 16 * 1024;

    private int _auditLimit = 500;
    private int _largeResponseThreshold = 64 * 1024;

    private object? _pendingDelete;
    private HistoryEntry? _pendingHistoryTrash;
    private HistoryEntry? _pendingHistoryPurge;
    private bool _trashAllPending;
    private bool _purgeAllPending;
    private CancellationTokenSource? _sendCts;
    private bool _initializing = true;

    public MainWindowViewModel()
    {
        Settings = _settingsStore.Load();
        Settings.PropertyChanged += OnSettingsChanged;
        _http.TimeoutSeconds = Settings.RequestTimeoutSeconds;
        _http.AutoFormatResponse = Settings.AutoFormatResponse;
        _auditLimit = Math.Max(20, Settings.AuditLimit);
        _largeResponseThreshold = Math.Max(8, Settings.LargeResponseThresholdKb) * 1024;
        ApplyTheme();

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
        SelectedSuite ??= Suites.FirstOrDefault();
        IsDraft = firstRequest is null;
        _initializing = false;

        MockStatus = _mockServer.IsRunning
            ? $"Mock 服务运行中 · {_mockServer.BaseUrl}"
            : "Mock 服务未启动";

        _mockServer.CustomHandler = new McpServer(new McpWorkspaceAdapter(this)).HandleHttpAsync;

        History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
        RecycledHistory.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRecycledHistory));
            OnPropertyChanged(nameof(RecycleBinLabel));
        };
        Suites.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SuiteCount));
        Audit.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AuditCount));
            RefreshAuditView();
        };

        foreach (var entry in _auditStore.Load().Take(_auditLimit))
        {
            Audit.Add(entry);
        }

        RefreshAuditView();
        BindEditingTracking();
        RefreshDerivedState();

        Persist();
    }

    public ObservableCollection<RequestCollection> Collections { get; } = new();

    public ObservableCollection<EnvironmentModel> Environments { get; } = new();

    public ObservableCollection<HistoryEntry> History { get; } = new();

    public ObservableCollection<HistoryEntry> RecycledHistory { get; } = new();

    public ObservableCollection<AuditEntry> Audit { get; } = new();

    public ObservableCollection<AuditEntry> VisibleAudit { get; } = new();

    public ObservableCollection<TestSuite> Suites { get; } = new();

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
    private bool _isManagementOpen;

    [ObservableProperty]
    private string _auditFilterText = string.Empty;

    [ObservableProperty]
    private string _auditSourceFilter = "全部";

    [ObservableProperty]
    private AuditEntry? _selectedAuditEntry;

    [ObservableProperty]
    private TestSuite? _selectedSuite;

    [ObservableProperty]
    private TestRunResult? _selectedSuiteResult;

    [ObservableProperty]
    private bool _isRunningSuite;

    [ObservableProperty]
    private bool _isBodySingleLine;

    [ObservableProperty]
    private string _bodyPreview = string.Empty;

    [ObservableProperty]
    private string _curlPreview = string.Empty;

    [ObservableProperty]
    private bool _hasHeaderWarning;

    [ObservableProperty]
    private string _headerWarningText = string.Empty;

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
    private bool _isHistoryTrashConfirmOpen;

    [ObservableProperty]
    private string _historyTrashConfirmTitle = "移入回收站？";

    [ObservableProperty]
    private string _historyTrashConfirmDescription = string.Empty;

    [ObservableProperty]
    private bool _isHistoryTrashOpen;

    [ObservableProperty]
    private bool _isHistoryPurgeConfirmOpen;

    [ObservableProperty]
    private string _historyPurgeTitle = "永久删除？";

    [ObservableProperty]
    private string _historyPurgeDescription = string.Empty;

    [ObservableProperty]
    private bool _isToastOpen;

    [ObservableProperty]
    private string _toastTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowJsonResponse))]
    [NotifyPropertyChangedFor(nameof(ShowRawResponse))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyResponse))]
    [NotifyPropertyChangedFor(nameof(ShowLargeResponseBanner))]
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
    [NotifyPropertyChangedFor(nameof(ShowJsonResponse))]
    [NotifyPropertyChangedFor(nameof(ShowRawResponse))]
    [NotifyPropertyChangedFor(nameof(ShowLargeResponseBanner))]
    private bool _isLargeResponse;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSseContent))]
    [NotifyPropertyChangedFor(nameof(ShowSseThinking))]
    [NotifyPropertyChangedFor(nameof(ShowSseRaw))]
    [NotifyPropertyChangedFor(nameof(SseSummary))]
    private bool _isSseResponse;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SseSummary))]
    [NotifyPropertyChangedFor(nameof(HasSseThinking))]
    private string _sseThinking = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SseSummary))]
    [NotifyPropertyChangedFor(nameof(HasSseContent))]
    private string _sseContent = string.Empty;

    [ObservableProperty]
    private string _sseRaw = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SseSummary))]
    private int _sseEventCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSseContent))]
    [NotifyPropertyChangedFor(nameof(ShowSseThinking))]
    [NotifyPropertyChangedFor(nameof(ShowSseRaw))]
    private string _sseViewMode = "内容";

    [ObservableProperty]
    private int _responseTabIndex;

    [ObservableProperty]
    private string _responseBody = string.Empty;

    [ObservableProperty]
    private string _fullResponseBody = string.Empty;

    [ObservableProperty]
    private string _responseClipboardText = string.Empty;

    [ObservableProperty]
    private string _largeResponseNotice = string.Empty;

    [ObservableProperty]
    private string _responseStatusText = string.Empty;

    [ObservableProperty]
    private string _responseTimeText = string.Empty;

    [ObservableProperty]
    private string _responseSizeText = string.Empty;

    [ObservableProperty]
    private string _responseContentType = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseStatusIcon))]
    private BadgeTone _responseTone = BadgeTone.Neutral;

    [ObservableProperty]
    private string _mockStatus = string.Empty;

    public AppSettings Settings { get; private set; } = new();

    public event EventHandler? AppearanceChanged;

    public IReadOnlyList<string> ThemeOptions { get; } = new[] { "跟随系统", "浅色", "深色" };

    public IReadOnlyList<string> UiFontOptions { get; } = new[] { "Inter", "系统默认", "微软雅黑" };

    public IReadOnlyList<string> CodeFontOptions { get; } = new[] { "Geist Mono", "Consolas", "Cascadia Mono" };

    public IReadOnlyList<string> CodeFontSizeOptions { get; } = new[] { "12", "13", "14", "16" };

    public IReadOnlyList<int> TimeoutOptions { get; } = new[] { 10, 30, 60, 120, 300 };

    public IReadOnlyList<int> LargeResponseThresholdOptions { get; } = new[] { 32, 64, 128, 256 };

    public IReadOnlyList<int> AuditLimitOptions { get; } = new[] { 100, 500, 1000, 2000 };

    [ObservableProperty]
    private string _managementTab = "审计";

    public bool IsAuditTab => ManagementTab == "审计";

    public bool IsAutomationTab => ManagementTab == "自动化";

    public bool IsGuideTab => ManagementTab == "指南";

    public bool IsSettingsTab => ManagementTab == "设置";

    partial void OnManagementTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsAuditTab));
        OnPropertyChanged(nameof(IsAutomationTab));
        OnPropertyChanged(nameof(IsGuideTab));
        OnPropertyChanged(nameof(IsSettingsTab));
    }

    [RelayCommand]
    private void OpenGuide()
    {
        ManagementTab = "指南";
        IsManagementOpen = true;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        ManagementTab = "设置";
        IsManagementOpen = true;
    }

    [RelayCommand]
    private void SetManagementTab(string tab)
    {
        if (tab is "审计" or "自动化" or "指南" or "设置")
        {
            ManagementTab = tab;
        }
    }

    [RelayCommand]
    private void LoadGuideRequest(string key)
    {
        var name = key switch
        {
            "health" => "健康检查",
            "sse" => "AI 流式输出 (SSE)",
            "upload" => "上传文件",
            _ => key
        };

        var request = Collections.SelectMany(collection => collection.Requests)
            .FirstOrDefault(candidate => candidate.Name == name);

        if (request is null)
        {
            ShowToast($"示例请求「{name}」不存在");
            return;
        }

        IsManagementOpen = false;
        OpenRequest(request);
        ShowToast($"已载入示例：{request.Name}");
    }

    [RelayCommand]
    private void OpenEnvironmentFromGuide()
    {
        IsManagementOpen = false;
        IsEnvironmentDialogOpen = true;
    }

    [RelayCommand]
    private void OpenMcpFromGuide()
    {
        IsManagementOpen = false;
        IsMcpDialogOpen = true;
    }

    [RelayCommand]
    private void ResetSettings()
    {
        var defaults = new AppSettings();
        Settings.Theme = defaults.Theme;
        Settings.UiFont = defaults.UiFont;
        Settings.CodeFont = defaults.CodeFont;
        Settings.CodeFontSize = defaults.CodeFontSize;
        Settings.RequestTimeoutSeconds = defaults.RequestTimeoutSeconds;
        Settings.DefaultBodyMode = defaults.DefaultBodyMode;
        Settings.AutoFormatResponse = defaults.AutoFormatResponse;
        Settings.LargeResponseThresholdKb = defaults.LargeResponseThresholdKb;
        Settings.AuditLimit = defaults.AuditLimit;
        ShowToast("设置已恢复默认");
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        _http.TimeoutSeconds = Math.Clamp(Settings.RequestTimeoutSeconds, 5, 600);
        _http.AutoFormatResponse = Settings.AutoFormatResponse;
        _largeResponseThreshold = Math.Max(8, Settings.LargeResponseThresholdKb) * 1024;
        _auditLimit = Math.Max(20, Settings.AuditLimit);

        while (Audit.Count > _auditLimit)
        {
            Audit.RemoveAt(Audit.Count - 1);
        }

        if (e.PropertyName == nameof(AppSettings.Theme))
        {
            ApplyTheme();
        }

        _settingsStore.Save(Settings);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTheme()
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = Settings.Theme switch
        {
            "浅色" => ThemeVariant.Light,
            "深色" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    public bool HasSelection => SelectedRequest is not null || SelectedCollection is not null;

    public bool IsRequestSelected => SelectedRequest is not null;

    public bool HasHistory => History.Count > 0;

    public bool HasRecycledHistory => RecycledHistory.Count > 0;

    public string RecycleBinLabel => RecycledHistory.Count > 0
        ? $"回收站 ({RecycledHistory.Count})"
        : "回收站";

    public bool HasSelectedAuditEntry => SelectedAuditEntry is not null;

    public bool HasSelectedSuite => SelectedSuite is not null;

    public bool HasSelectedSuiteResult => SelectedSuiteResult is not null;

    public int AuditCount => Audit.Count;

    public int SuiteCount => Suites.Count;

    public bool ShowJsonResponse => HasResponse && IsJsonResponse && !IsLargeResponse;

    public bool ShowRawResponse => HasResponse && !ShowJsonResponse;

    public bool ShowEmptyResponse => !HasResponse && !HasResponseError;

    public bool ShowResponseError => HasResponseError;

    public bool ShowLargeResponseBanner => HasResponse && IsLargeResponse;

    public IReadOnlyList<string> SseViewOptions { get; } = new[] { "内容", "思考过程", "原始事件" };

    public bool ShowSseContent => IsSseResponse && SseViewMode == "内容";

    public bool ShowSseThinking => IsSseResponse && SseViewMode == "思考过程";

    public bool ShowSseRaw => IsSseResponse && SseViewMode == "原始事件";

    public bool HasSseContent => SseContent.Length > 0;

    public bool HasSseThinking => SseThinking.Length > 0;

    public bool IsMultipartBody => Editing.BodyMode == "Multipart";

    public bool IsTextBody => !IsMultipartBody;

    public bool HasUploadFile => !string.IsNullOrWhiteSpace(Editing.UploadFilePath);

    public string SseSummary => IsSseResponse
        ? $"SSE 流 · {SseEventCount} 个事件 · 内容 {SseContent.Length} 字 · 思考 {SseThinking.Length} 字"
        : "当前响应不是 SSE 流";

    public object ResponseStatusIcon => new ArcGlyph
    {
        Data = ResponseTone switch
        {
            BadgeTone.Success => ArcIcons.CircleCheck,
            BadgeTone.Warning => ArcIcons.TriangleAlert,
            BadgeTone.Danger => ArcIcons.CircleX,
            _ => ArcIcons.Info
        },
        Size = 12
    };

    public string DetailPageSizeText => $"{DetailPageSize / 1024} KB/页";

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
        BindEditingTracking();
        RefreshDerivedState();
    }

    partial void OnFilterTextChanged(string value) => NotifyTreeChanged();

    partial void OnActiveEnvironmentChanged(EnvironmentModel? value)
    {
        if (!_initializing)
        {
            Persist();
        }

        RefreshDerivedState();
    }

    partial void OnSelectedAuditEntryChanged(AuditEntry? value) =>
        OnPropertyChanged(nameof(HasSelectedAuditEntry));

    partial void OnSelectedSuiteChanged(TestSuite? value)
    {
        OnPropertyChanged(nameof(HasSelectedSuite));
        SelectedSuiteResult = value is not null && _suiteResults.TryGetValue(value.Id, out var result)
            ? result
            : null;
    }

    partial void OnSelectedSuiteResultChanged(TestRunResult? value) =>
        OnPropertyChanged(nameof(HasSelectedSuiteResult));

    partial void OnAuditFilterTextChanged(string value) => RefreshAuditView();

    partial void OnAuditSourceFilterChanged(string value) => RefreshAuditView();

    partial void OnSseViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowSseContent));
        OnPropertyChanged(nameof(ShowSseThinking));
        OnPropertyChanged(nameof(ShowSseRaw));
    }

    partial void OnIsManagementOpenChanged(bool value)
    {
        if (value)
        {
            RefreshAuditView();
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsSending)
        {
            return;
        }

        IsSending = true;
        _sendCts = new CancellationTokenSource();
        ComposedRequest? composed = null;

        try
        {
            HttpResult result;
            try
            {
                composed = RequestComposer.Compose(Editing, ActiveEnvironment);
                result = await _http.SendAsync(composed, _sendCts.Token);
            }
            catch (Exception exception)
            {
                result = HttpRequestService.Failure(exception.Message);
            }

            ApplyResponse(result);

            if (result.HasResponse)
            {
                AddHistory(Editing, result);
            }

            RecordAudit(Editing, composed, result, "UI", null, null);
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
            BodyMode = Settings.DefaultBodyMode,
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
        RefreshDerivedState();
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
        if (History.Count == 0)
        {
            ShowToast("历史记录为空");
            return;
        }

        _trashAllPending = true;
        _pendingHistoryTrash = null;
        HistoryTrashConfirmTitle = "清空历史记录？";
        HistoryTrashConfirmDescription =
            $"将把 {History.Count} 条记录移入回收站，可在回收站恢复或永久删除。";
        IsHistoryTrashConfirmOpen = true;
    }

    [RelayCommand]
    private void OpenHistoryTrash() => IsHistoryTrashOpen = true;

    [RelayCommand]
    private void CloseHistoryTrash() => IsHistoryTrashOpen = false;

    private void RequestDeleteHistory(HistoryEntry entry)
    {
        _trashAllPending = false;
        _pendingHistoryTrash = entry;
        HistoryTrashConfirmTitle = "移入回收站？";
        HistoryTrashConfirmDescription =
            $"“{entry.Name}”将被移入回收站，可在回收站恢复或永久删除。";
        IsHistoryTrashConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmHistoryTrash()
    {
        if (_trashAllPending)
        {
            for (var index = History.Count - 1; index >= 0; index--)
            {
                MoveToRecycleBin(History[index]);
            }

            History.Clear();
            ShowToast("历史记录已移入回收站");
        }
        else if (_pendingHistoryTrash is { } entry)
        {
            History.Remove(entry);
            MoveToRecycleBin(entry);
            ShowToast("已移入回收站");
        }

        _pendingHistoryTrash = null;
        _trashAllPending = false;
        IsHistoryTrashConfirmOpen = false;
        Persist();
    }

    [RelayCommand]
    private void CancelHistoryTrash()
    {
        _pendingHistoryTrash = null;
        _trashAllPending = false;
        IsHistoryTrashConfirmOpen = false;
    }

    private void MoveToRecycleBin(HistoryEntry entry)
    {
        entry.RestoreCommand = new RelayCommand(() => RestoreFromRecycleBin(entry));
        entry.PurgeCommand = new RelayCommand(() => RequestPurgeHistory(entry));
        RecycledHistory.Insert(0, entry);
        while (RecycledHistory.Count > 100)
        {
            RecycledHistory.RemoveAt(RecycledHistory.Count - 1);
        }
    }

    private void RestoreFromRecycleBin(HistoryEntry entry)
    {
        if (!RecycledHistory.Remove(entry))
        {
            return;
        }

        WireHistoryEntry(entry);
        History.Insert(0, entry);
        while (History.Count > 40)
        {
            History.RemoveAt(History.Count - 1);
        }

        Persist();
        ShowToast("已从回收站恢复");
    }

    private void RequestPurgeHistory(HistoryEntry entry)
    {
        _purgeAllPending = false;
        _pendingHistoryPurge = entry;
        HistoryPurgeTitle = "永久删除？";
        HistoryPurgeDescription = $"“{entry.Name}”将从回收站永久删除，无法恢复。";
        IsHistoryPurgeConfirmOpen = true;
    }

    [RelayCommand]
    private void EmptyHistoryTrash()
    {
        if (RecycledHistory.Count == 0)
        {
            ShowToast("回收站为空");
            return;
        }

        _purgeAllPending = true;
        _pendingHistoryPurge = null;
        HistoryPurgeTitle = "清空回收站？";
        HistoryPurgeDescription = "回收站中的所有记录将被永久删除，无法恢复。";
        IsHistoryPurgeConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmHistoryPurge()
    {
        if (_purgeAllPending)
        {
            RecycledHistory.Clear();
            ShowToast("回收站已清空");
        }
        else if (_pendingHistoryPurge is { } entry)
        {
            RecycledHistory.Remove(entry);
            ShowToast("已永久删除");
        }

        _pendingHistoryPurge = null;
        _purgeAllPending = false;
        IsHistoryPurgeConfirmOpen = false;
        Persist();
    }

    [RelayCommand]
    private void CancelHistoryPurge()
    {
        _pendingHistoryPurge = null;
        _purgeAllPending = false;
        IsHistoryPurgeConfirmOpen = false;
    }

    private void WireHistoryEntry(HistoryEntry entry)
    {
        entry.OpenCommand = new RelayCommand(() => LoadHistory(entry));
        entry.DeleteCommand = new RelayCommand(() => RequestDeleteHistory(entry));
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
        RefreshDerivedState();
    }

    public void RecordExternalSend(
        RequestItem request,
        ComposedRequest? composed,
        HttpResult result,
        string source,
        string? tool,
        string? client)
    {
        AddHistory(request, result);
        RecordAudit(request, composed, result, source, tool, client);
        Persist();
    }

    public void NotifyUploadChanged()
    {
        RefreshDerivedState();
        Persist();
    }

    private static string BuildMultipartPreview(RequestItem request)
    {
        if (!string.Equals(request.BodyMode, "Multipart", StringComparison.OrdinalIgnoreCase))
        {
            return request.Body;
        }

        var lines = request.FormFields.ActiveRows
            .Select(row => $"{row.Key}: {row.Value}")
            .ToList();
        if (!string.IsNullOrWhiteSpace(request.UploadFilePath))
        {
            lines.Add($"[file] {request.UploadFieldName}: {request.UploadFilePath}");
        }

        return string.Join("\n", lines);
    }

    private void RecordAudit(
        RequestItem request,
        ComposedRequest? composed,
        HttpResult result,
        string source,
        string? tool,
        string? client)
    {
        var requestHeaders = composed is not null
            ? string.Join(
                "\n",
                composed.Headers
                    .Select(pair => $"{pair.Key}: {pair.Value}")
                    .Concat(composed.ContentType is null
                        ? Array.Empty<string>()
                        : new[] { $"Content-Type: {composed.ContentType}" }))
            : string.Join("\n", request.Headers.ActiveRows.Select(row => $"{row.Key}: {row.Value}"));

        var entry = new AuditEntry
        {
            Source = source,
            Tool = tool,
            Client = client,
            RequestId = request.Id,
            RequestName = request.Name,
            Method = composed?.Method ?? request.Method,
            Url = composed?.Url ?? request.Url,
            Environment = composed?.EnvironmentName ?? ActiveEnvironment?.Name,
            StatusCode = result.StatusCode,
            StatusText = result.ReasonPhrase,
            ElapsedMs = result.ElapsedMs,
            SizeBytes = result.SizeBytes,
            IsSuccess = result.HasResponse && result.StatusCode < 400,
            Error = result.Error,
            Curl = composed is null ? null : RequestComposer.BuildCurl(composed),
            RequestHeaders = Truncate(requestHeaders, AuditPreviewLimit),
            RequestBodyPreview = Truncate(
                composed?.Body ?? BuildMultipartPreview(request),
                AuditPreviewLimit),
            ResponseHeaders = Truncate(
                string.Join("\n", result.Headers.Select(header => $"{header.Key}: {header.Value}")),
                AuditPreviewLimit),
            ResponseBodyPreview = Truncate(result.Body, AuditPreviewLimit),
            ResponseContentType = result.MediaType,
            IsSse = result.IsSse,
            SseEventCount = result.SseEventCount,
            AiThinking = result.SseThinking,
            AiContent = result.SseContent
        };

        Audit.Insert(0, entry);
        while (Audit.Count > _auditLimit)
        {
            Audit.RemoveAt(Audit.Count - 1);
        }

        _auditStore.Save(Audit);
    }

    private void RefreshAuditView()
    {
        var query = AuditFilterText.Trim();
        IEnumerable<AuditEntry> entries = Audit;

        if (AuditSourceFilter is "UI" or "MCP")
        {
            entries = entries.Where(entry => entry.Source == AuditSourceFilter);
        }

        if (query.Length > 0)
        {
            entries = entries.Where(entry =>
                entry.RequestName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Url.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (entry.Client?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (entry.Tool?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        VisibleAudit.Clear();
        foreach (var entry in entries.Take(300))
        {
            VisibleAudit.Add(entry);
        }
    }

    [RelayCommand]
    private void OpenManagement() => IsManagementOpen = true;

    [RelayCommand]
    private void CloseManagement() => IsManagementOpen = false;

    [RelayCommand]
    private void ClearAudit()
    {
        Audit.Clear();
        _auditStore.Save(Audit);
        ShowToast("审计记录已清空");
    }

    [RelayCommand]
    private void OpenSelectedAuditRequest()
    {
        if (SelectedAuditEntry?.RequestId is not { } id)
        {
            ShowToast("这条审计记录没有关联请求");
            return;
        }

        var request = Collections.SelectMany(collection => collection.Requests)
            .FirstOrDefault(candidate => candidate.Id == id);
        if (request is null)
        {
            ShowToast("原请求已被删除");
            return;
        }

        IsManagementOpen = false;
        OpenRequest(request);
    }

    [RelayCommand]
    private void CreateSuiteFromCollection()
    {
        var collection = SelectedCollection ?? Editing.Owner ?? Collections.FirstOrDefault();
        if (collection is null || collection.Requests.Count == 0)
        {
            ShowToast("当前集合没有可加入套件的请求");
            return;
        }

        var suite = new TestSuite
        {
            Name = $"{collection.Name} · 回归测试",
            StopOnError = true
        };

        foreach (var request in collection.Requests)
        {
            suite.Steps.Add(new TestStep
            {
                RequestId = request.Id,
                RequestName = request.Name,
                Method = request.Method,
                Url = request.Url
            });
        }

        Suites.Add(suite);
        SelectedSuite = suite;
        Persist();
        ShowToast("已创建自动化套件");
    }

    [RelayCommand]
    private void DeleteSelectedSuite()
    {
        if (SelectedSuite is not { } suite)
        {
            return;
        }

        Suites.Remove(suite);
        _suiteResults.Remove(suite.Id);
        SelectedSuite = Suites.FirstOrDefault();
        Persist();
        ShowToast("套件已删除");
    }

    [RelayCommand]
    private async Task RunSelectedSuiteAsync()
    {
        if (SelectedSuite is not { } suite || IsRunningSuite)
        {
            return;
        }

        try
        {
            IsRunningSuite = true;
            var result = await RunSuiteCoreAsync(
                suite,
                "UI",
                $"suite:{suite.Name}",
                null,
                CancellationToken.None);
            SelectedSuiteResult = result;
            ShowToast($"套件完成：{result.Passed}/{result.Total} 通过");
        }
        finally
        {
            IsRunningSuite = false;
        }
    }

    public async Task<TestRunResult> RunSuiteForMcpAsync(
        Guid suiteId,
        string? client,
        CancellationToken cancellationToken)
    {
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var suite = Suites.FirstOrDefault(candidate => candidate.Id == suiteId)
                        ?? throw new KeyNotFoundException($"没有找到 id 为 {suiteId} 的套件。");
            return await RunSuiteCoreAsync(
                suite,
                "MCP",
                "run_suite",
                client,
                cancellationToken);
        });
    }

    private async Task<TestRunResult> RunSuiteCoreAsync(
        TestSuite suite,
        string source,
        string tool,
        string? client,
        CancellationToken cancellationToken)
    {
        var steps = new List<TestStepResult>();
        var stopped = false;

        foreach (var step in suite.Steps)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                break;
            }

            var request = Collections.SelectMany(collection => collection.Requests)
                .FirstOrDefault(candidate => candidate.Id == step.RequestId);

            if (request is null)
            {
                steps.Add(new TestStepResult
                {
                    RequestId = step.RequestId,
                    Name = step.RequestName,
                    Method = step.Method,
                    StatusText = "请求已删除",
                    Error = "请求已删除",
                    Passed = false
                });

                if (suite.StopOnError)
                {
                    stopped = true;
                    break;
                }

                continue;
            }

            ComposedRequest? composed = null;
            HttpResult result;
            try
            {
                composed = RequestComposer.Compose(request, ActiveEnvironment);
                result = await _http.SendAsync(composed, cancellationToken);
            }
            catch (Exception exception)
            {
                result = HttpRequestService.Failure(exception.Message);
            }

            AddHistory(request, result);
            RecordAudit(request, composed, result, source, tool, client);

            var statusMatches = step.ExpectedStatus is null
                ? result.StatusCode is >= 200 and < 400
                : result.StatusCode == step.ExpectedStatus;
            var bodyMatches = string.IsNullOrWhiteSpace(step.BodyContains)
                || result.Body.Contains(step.BodyContains, StringComparison.OrdinalIgnoreCase);
            var passed = result.HasResponse && statusMatches && bodyMatches;
            var error = result.Error;
            if (passed is false && error is null)
            {
                error = !statusMatches
                    ? $"期望状态 {step.ExpectedStatus?.ToString() ?? "2xx/3xx"}，实际 {result.StatusCode}"
                    : $"响应不包含 \"{step.BodyContains}\"";
            }

            steps.Add(new TestStepResult
            {
                RequestId = request.Id,
                Name = request.Name,
                Method = request.Method,
                StatusCode = result.StatusCode,
                StatusText = result.ReasonPhrase,
                ElapsedMs = result.ElapsedMs,
                Passed = passed,
                Error = error,
                ResponsePreview = Truncate(result.Body, 500)
            });

            if (!passed && suite.StopOnError)
            {
                stopped = true;
                break;
            }
        }

        var run = new TestRunResult
        {
            SuiteId = suite.Id,
            SuiteName = suite.Name,
            Total = steps.Count,
            Passed = steps.Count(step => step.Passed),
            Failed = steps.Count(step => !step.Passed),
            Stopped = stopped,
            Steps = steps
        };

        _suiteResults[suite.Id] = run;
        suite.LastSummary = run.Summary;
        if (SelectedSuite?.Id == suite.Id)
        {
            SelectedSuiteResult = run;
        }

        Persist();
        return run;
    }

    [RelayCommand]
    private void FormatBody()
    {
        var body = Editing.Body;
        if (string.IsNullOrWhiteSpace(body))
        {
            ShowToast("请求体为空");
            return;
        }

        var trimmed = body.TrimStart();
        try
        {
            if (trimmed[0] is '{' or '[')
            {
                using var document = System.Text.Json.JsonDocument.Parse(body);
                Editing.Body = System.Text.Json.JsonSerializer.Serialize(
                    document.RootElement,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                ShowToast("JSON 已格式化");
            }
            else if (trimmed[0] == '<')
            {
                Editing.Body = System.Xml.Linq.XDocument.Parse(body).ToString();
                ShowToast("XML 已格式化");
            }
            else
            {
                ShowToast("当前内容不是可格式化的 JSON/XML");
            }
        }
        catch (Exception exception)
        {
            ShowToast($"格式化失败：{exception.Message}");
        }

        RefreshDerivedState();
    }

    [RelayCommand]
    private void MinifyBody()
    {
        var body = Editing.Body;
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            Editing.Body = System.Text.Json.JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            ShowToast("JSON 已压缩为单行");
        }
        catch (Exception)
        {
            ShowToast("只有 JSON 请求体可以压缩为单行");
        }

        RefreshDerivedState();
    }

    [RelayCommand]
    private void AddJsonContentType()
    {
        Editing.Headers.AddRow("Content-Type", "application/json");
        RefreshDerivedState();
    }

    [RelayCommand]
    private void AddAcceptJson()
    {
        Editing.Headers.AddRow("Accept", "application/json");
        RefreshDerivedState();
    }

    [RelayCommand]
    private void AddBearerToken()
    {
        Editing.Headers.AddRow("Authorization", "Bearer {{token}}");
        RefreshDerivedState();
    }

    private void BindEditingTracking()
    {
        if (_trackedEditing is not null)
        {
            _trackedEditing.PropertyChanged -= OnTrackedEditingPropertyChanged;
        }

        if (_trackedParams is not null)
        {
            _trackedParams.CollectionChanged -= OnTrackedRowsCollectionChanged;
        }

        if (_trackedHeaders is not null)
        {
            _trackedHeaders.CollectionChanged -= OnTrackedRowsCollectionChanged;
        }

        foreach (var row in _trackedRows)
        {
            row.PropertyChanged -= OnTrackedRowPropertyChanged;
        }

        _trackedRows.Clear();
        _trackedEditing = Editing;
        _trackedParams = Editing.Params;
        _trackedHeaders = Editing.Headers;

        _trackedEditing.PropertyChanged += OnTrackedEditingPropertyChanged;
        _trackedParams.CollectionChanged += OnTrackedRowsCollectionChanged;
        _trackedHeaders.CollectionChanged += OnTrackedRowsCollectionChanged;

        foreach (var row in _trackedParams.Concat(_trackedHeaders))
        {
            row.PropertyChanged += OnTrackedRowPropertyChanged;
            _trackedRows.Add(row);
        }
    }

    private RequestItem? _trackedEditing;
    private EditableRowCollection? _trackedParams;
    private EditableRowCollection? _trackedHeaders;
    private readonly List<KeyValueRow> _trackedRows = new();

    private void OnTrackedEditingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RequestItem.Url)
            or nameof(RequestItem.Method)
            or nameof(RequestItem.Body)
            or nameof(RequestItem.BodyMode)
            or nameof(RequestItem.UploadFilePath)
            or nameof(RequestItem.UploadFieldName))
        {
            RefreshDerivedState();
        }
    }

    private void OnTrackedRowsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        BindEditingTracking();
        RefreshDerivedState();
    }

    private void OnTrackedRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyValueRow.Key)
            or nameof(KeyValueRow.Value)
            or nameof(KeyValueRow.IsEnabled))
        {
            RefreshDerivedState();
        }
    }

    private void RefreshDerivedState()
    {
        var body = Editing.Body ?? string.Empty;
        BodyPreview = string.IsNullOrWhiteSpace(body)
            ? "请求体为空"
            : Truncate(string.Join(" ", body.Split(
                new[] { '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries)), 260);

        OnPropertyChanged(nameof(IsMultipartBody));
        OnPropertyChanged(nameof(IsTextBody));
        OnPropertyChanged(nameof(HasUploadFile));

        var duplicates = Editing.Headers.ActiveRows
            .Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .GroupBy(row => row.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        HasHeaderWarning = duplicates.Count > 0;
        HeaderWarningText = HasHeaderWarning
            ? $"重复请求头：{string.Join("、", duplicates)}；发送时会按行依次附加。"
            : string.Empty;

        CurlPreview = RequestComposer.BuildCurl(Editing, ActiveEnvironment);
    }

    private static string Truncate(string text, int limit)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= limit)
        {
            return text ?? string.Empty;
        }

        return text[..limit] + "\n…（已截断）";
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
            FullResponseBody = string.Empty;
            ResponseClipboardText = string.Empty;
            LargeResponseNotice = string.Empty;
            IsJsonResponse = false;
            IsLargeResponse = false;
            IsSseResponse = false;
            SseThinking = string.Empty;
            SseContent = string.Empty;
            SseRaw = string.Empty;
            SseEventCount = 0;
            ResponseTabIndex = 0;
            return;
        }

        var body = result.Body;
        var isLarge = body.Length > _largeResponseThreshold;
        var preview = isLarge ? TakePreview(body, ResponsePreviewLimit) : body;

        HasResponse = true;
        HasResponseError = false;
        ResponseError = null;
        ResponseBody = preview;
        FullResponseBody = body;
        ResponseClipboardText = body;
        IsJsonResponse = result.IsJson;
        IsLargeResponse = isLarge;
        IsSseResponse = result.IsSse;
        SseThinking = result.SseThinking;
        SseContent = result.SseContent;
        SseRaw = result.SseRaw;
        SseEventCount = result.SseEventCount;
        SseViewMode = "内容";
        ResponseTabIndex = result.IsSse ? 2 : 0;
        LargeResponseNotice = isLarge
            ? $"响应体 {FormatSize(result.SizeBytes)}，已折叠显示前 {ResponsePreviewLimit / 1024} KB。完整内容可在详情页分页查看或复制。"
            : string.Empty;
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
        FullResponseBody = string.Empty;
        ResponseClipboardText = string.Empty;
        LargeResponseNotice = string.Empty;
        IsJsonResponse = false;
        IsLargeResponse = false;
        IsSseResponse = false;
        SseThinking = string.Empty;
        SseContent = string.Empty;
        SseRaw = string.Empty;
        SseEventCount = 0;
        ResponseTabIndex = 0;
        ResponseStatusText = string.Empty;
        ResponseTimeText = string.Empty;
        ResponseSizeText = string.Empty;
        ResponseContentType = string.Empty;
        ResponseHeaders.Clear();
    }

    private static string TakePreview(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        var preview = text[..limit];
        return char.IsHighSurrogate(preview[^1]) ? preview[..^1] : preview;
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

        WireHistoryEntry(entry);
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

        _store.Save(WorkspaceStore.Capture(
            Collections,
            Environments,
            History,
            RecycledHistory,
            Suites,
            ActiveEnvironment?.Id));
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
            WireHistoryEntry(entry);
            History.Add(entry);
        }

        foreach (var historyDto in workspace.RecycledHistory)
        {
            var entry = WorkspaceStore.ToHistory(historyDto);
            entry.RestoreCommand = new RelayCommand(() => RestoreFromRecycleBin(entry));
            entry.PurgeCommand = new RelayCommand(() => RequestPurgeHistory(entry));
            RecycledHistory.Add(entry);
        }

        foreach (var suiteDto in workspace.Suites)
        {
            var suite = new TestSuite
            {
                Id = suiteDto.Id == Guid.Empty ? Guid.NewGuid() : suiteDto.Id,
                Name = suiteDto.Name,
                StopOnError = suiteDto.StopOnError,
                LastSummary = string.IsNullOrWhiteSpace(suiteDto.LastSummary) ? "尚未运行" : suiteDto.LastSummary
            };

            foreach (var stepDto in suiteDto.Steps)
            {
                suite.Steps.Add(new TestStep
                {
                    RequestId = stepDto.RequestId,
                    RequestName = stepDto.RequestName,
                    Method = stepDto.Method,
                    Url = stepDto.Url,
                    ExpectedStatus = stepDto.ExpectedStatus,
                    BodyContains = stepDto.BodyContains
                });
            }

            Suites.Add(suite);
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

        var large = new RequestItem
        {
            Name = "大响应 (200 KB)",
            Method = "GET",
            Url = "{{baseUrl}}/api/large?kb=200",
            Owner = collection
        };

        var sse = new RequestItem
        {
            Name = "AI 流式输出 (SSE)",
            Method = "GET",
            Url = "{{baseUrl}}/api/ai/stream",
            Owner = collection
        };
        sse.Headers.AddRow("Accept", "text/event-stream");

        var upload = new RequestItem
        {
            Name = "上传文件",
            Method = "POST",
            Url = "{{baseUrl}}/api/upload",
            BodyMode = "Multipart",
            UploadFieldName = "file",
            Owner = collection
        };
        upload.FormFields.AddRow("note", "PostSharp upload demo");

        collection.Requests.Add(health);
        collection.Requests.Add(users);
        collection.Requests.Add(echo);
        collection.Requests.Add(orders);
        collection.Requests.Add(slow);
        collection.Requests.Add(error);
        collection.Requests.Add(large);
        collection.Requests.Add(sse);
        collection.Requests.Add(upload);
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

        var smokeSuite = new TestSuite
        {
            Name = "内置 Mock 冒烟测试",
            StopOnError = true
        };

        smokeSuite.Steps.Add(new TestStep
        {
            RequestId = health.Id,
            RequestName = health.Name,
            Method = health.Method,
            Url = health.Url,
            ExpectedStatus = 200
        });
        smokeSuite.Steps.Add(new TestStep
        {
            RequestId = users.Id,
            RequestName = users.Name,
            Method = users.Method,
            Url = users.Url,
            ExpectedStatus = 200
        });
        smokeSuite.Steps.Add(new TestStep
        {
            RequestId = echo.Id,
            RequestName = echo.Name,
            Method = echo.Method,
            Url = echo.Url,
            ExpectedStatus = 200
        });
        smokeSuite.Steps.Add(new TestStep
        {
            RequestId = orders.Id,
            RequestName = orders.Name,
            Method = orders.Method,
            Url = orders.Url,
            ExpectedStatus = 201
        });

        Suites.Add(smokeSuite);
        SelectedSuite = smokeSuite;
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
