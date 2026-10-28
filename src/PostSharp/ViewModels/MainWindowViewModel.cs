using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;
using PostSharp.Services;
using TKCode.Arc;

namespace PostSharp.ViewModels;

/// <summary>Where a dragged tree row should be inserted relative to the row under the pointer.</summary>
public enum TreeDropPosition
{
    Before,
    After,
    Inside,
}

public partial class MainWindowViewModel : ObservableObject, IImportHost
{
    private readonly WorkspaceStore _store = new();
    private readonly SuiteStore _suiteStore = new();
    private readonly AuditStore _auditStore = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly HttpRequestService _http = new();
    private readonly AuthorizationService _authorizationService = new();
    private readonly MockApiServer _mockServer = new();
    private readonly AuditScrubber _auditScrubber;

    private const int AuditPreviewLimit = 2000;
    private const int ResponsePreviewLimit = 8 * 1024;
    private const int DetailPageSize = 16 * 1024;
    private const int MaxCollectionDepth = 5;
    private const string SideBySideLayout = "SideBySide";
    private const string StackedLayout = "Stacked";

    private static readonly JsonSerializerOptions MappingJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private int _auditLimit;
    private int _largeResponseThreshold = 64 * 1024;

    private object? _pendingDelete;
    private HistoryEntry? _pendingHistoryTrash;
    private HistoryEntry? _pendingHistoryPurge;
    private bool _trashAllPending;
    private bool _purgeAllPending;
    private AuditEntry? _pendingAuditPurge;
    private bool _auditPurgeAllPending;
    private readonly Dictionary<Guid, CancellationTokenSource> _sendCancellations = new();
    private CancellationTokenSource? _suiteCts;
    private readonly ConcurrentDictionary<Guid, SuiteRunJob> _suiteRunJobs = new();
    private CancellationTokenSource? _updateCts;
    private CancellationTokenSource? _openApiSyncCts;
    private CancellationTokenSource? _openApiSyncRequestCts;
    private ComposedRequest? _lastComposedRequest;
    private SseResumeSnapshot? _sseResume;
    private readonly DispatcherTimer _suiteScheduleTimer = new()
    {
        Interval = TimeSpan.FromSeconds(30),
    };
    private readonly Dictionary<Guid, DateTimeOffset> _lastScheduledRuns = new();
    private bool _initializing = true;

    public MainWindowViewModel()
    {
        AuditPage = new PagedCollection<AuditEntry>(VisibleAudit);
        TreePage = new PagedCollection<TreeDisplayNode>(TreeNodes, 80);
        TreeTrash = new TreeTrashViewModel(Collections, OnTreeTrashChanged, ShowToast);
        _auditScrubber = new AuditScrubber(
            Audit,
            AuditTrash,
            Environments,
            PersistAudit,
            _auditStore.DeleteBackups,
            DataNotice
        );
        Settings = _settingsStore.Load();
        FontService.Initialize(Settings);
        AppLog.Reconfigure(Settings.LogDirectory, Settings.LogLevel);
        Log.Information(
            "设置已加载 {SettingsFile}，主题 {Theme}，日志目录 {LogDirectory}，日志级别 {LogLevel}",
            _settingsStore.FilePath,
            Settings.Theme,
            Settings.LogDirectory,
            Settings.LogLevel
        );
        Settings.PropertyChanged += OnSettingsChanged;
        IsWorkspaceSideBySide = Settings.WorkspaceLayout != StackedLayout;
        _http.TimeoutSeconds = Settings.RequestTimeoutSeconds;
        _http.AutoFormatResponse = Settings.AutoFormatResponse;
        _http.SseThinkingPath = Settings.SseThinkingPath;
        _http.SseContentPath = Settings.SseContentPath;
        _auditLimit = Math.Max(0, Settings.AuditLimit);
        _largeResponseThreshold = Math.Max(8, Settings.LargeResponseThresholdKb) * 1024;

        _mockServer.Start();
        if (SettingsStore.StartupProblem is { } settingsProblem)
        {
            DataNotice.Report(settingsProblem, "设置文件 settings.json ", "已使用默认设置");
        }

        var pluginIssues = StoragePluginRegistry.Current.Refresh();
        if (pluginIssues.Count > 0)
        {
            DataNotice.Warn(
                $"存储插件加载有问题：{string.Join("；", pluginIssues.Take(3))}"
                    + (pluginIssues.Count > 3 ? " 等。" : string.Empty)
                    + $" 请检查插件目录 {AppPaths.StoragePluginDirectory} 后重新扫描。"
            );
        }

        var workspaceLoad = _store.Load();
        DataNotice.Report(workspaceLoad, "工作区文件 workspace.json ", "已用示例工作区启动");
        if (workspaceLoad.Value is { } workspace)
        {
            LoadWorkspace(workspace);
        }
        else
        {
            SeedWorkspace();
        }

        var suiteLoad = _suiteStore.Load();
        DataNotice.Report(suiteLoad, "套件文档 ", "套件列表从空白开始");
        if (StorageCoordinator.Current.Config.BlockedRemoteCategories.Count > 0)
        {
            DataNotice.Warn(
                "远程存储暂时不可用，已暂停相关分类的自动回写；本地数据仍会保存。请在设置中测试连接后执行同步或切换。"
            );
        }
        if (suiteLoad.Value is { } storedSuites)
        {
            Suites.Clear();
            foreach (var suiteDto in storedSuites)
            {
                var suite = WorkspaceStore.ToSuiteModel(suiteDto);
                WireSuiteRunCommands(suite);
                Suites.Add(suite);
            }
        }
        else if (Suites.Count > 0)
        {
            // Workspaces written before suites had their own storage still contain the list.
            _suiteStore.Save(Suites);
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

        var firstRequest = Collections
            .SelectMany(collection => collection.Requests)
            .FirstOrDefault();
        Editing = firstRequest ?? new RequestItem { Name = "新建请求" };
        SelectedCollection = firstRequest?.Owner;
        SelectedRequest = firstRequest;
        SelectedSuite ??= Suites.FirstOrDefault();
        IsDraft = firstRequest is null;
        _initializing = false;
        RefreshRequestTabOptions();

        MockStatus = _mockServer.IsRunning
            ? $"Mock 服务运行中 · {_mockServer.BaseUrl}"
            : "Mock 服务未启动";

        _mockServer.CustomHandler = new McpServer(new McpWorkspaceAdapter(this)).HandleHttpAsync;

        History.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(HistoryCountText));
        };
        RecycledHistory.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRecycledHistory));
            OnPropertyChanged(nameof(RecycleBinLabel));
        };
        Suites.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SuiteCount));
            OnPropertyChanged(nameof(HasSuites));
        };
        SelectedSuiteValidationIssues.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSuiteValidationIssues));
            OnPropertyChanged(nameof(SuiteValidationSummary));
        };
        Audit.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(AuditCount));
            RefreshAuditView();
        };
        AuditTrash.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAuditTrash));
            OnPropertyChanged(nameof(AuditTrashLabel));
        };

        var auditLoad = _auditStore.Load();
        var auditTrashLoad = _auditStore.LoadTrash();
        const string auditCopyHint = "数据库文件可能包含审计内容，请勿分享给其他人。";
        DataNotice.Report(
            auditLoad,
            "审计数据库 postsharp.db ",
            "审计列表从空白开始",
            auditCopyHint
        );
        DataNotice.Report(
            auditTrashLoad,
            "审计回收站（postsharp.db）",
            "审计回收站从空白开始",
            auditCopyHint
        );

        var loadedAudit = auditLoad.Value ?? new List<AuditEntry>();
        foreach (var entry in _auditLimit > 0 ? loadedAudit.Take(_auditLimit) : loadedAudit)
        {
            WireAuditEntry(entry);
            Audit.Add(entry);
        }

        foreach (var entry in auditTrashLoad.Value ?? new List<AuditEntry>())
        {
            WireAuditTrashEntry(entry);
            AuditTrash.Add(entry);
        }

        // Records written before secrets were masked still hold credentials.
        ScrubAuditSecrets();

        Import = new ImportViewModel(this);
        RefreshAuditView();
        BindEditingTracking();
        RefreshAuthState();
        RefreshTree();
        UpdateSelectionFlags();
        _suiteScheduleTimer.Tick += OnSuiteScheduleTick;
        _suiteScheduleTimer.Start();

        HasPendingUpdate = UpdateService.PendingVersion is not null;
        UpdateService.PendingChanged += OnPendingUpdateChanged;
        _updateCts = new CancellationTokenSource();
        _ = RunUpdateLoopAsync(_updateCts.Token);
        _openApiSyncCts = new CancellationTokenSource();
        _ = RunOpenApiAutoSyncAsync(_openApiSyncCts.Token);

        Persist();
    }

    public ObservableCollection<RequestCollection> Collections { get; } = new();

    public DataNoticeViewModel DataNotice { get; } = new();

    public TreeTrashViewModel TreeTrash { get; }

    public ObservableCollection<TreeDisplayNode> TreeNodes { get; } = new();

    public PagedCollection<TreeDisplayNode> TreePage { get; }

    public ObservableCollection<EnvironmentModel> Environments { get; } = new();

    public ObservableCollection<HistoryEntry> History { get; } = new();

    public ObservableCollection<HistoryEntry> RecycledHistory { get; } = new();

    public ObservableCollection<AuditEntry> Audit { get; } = new();

    public ObservableCollection<AuditEntry> AuditTrash { get; } = new();

    public ObservableCollection<AuditEntry> VisibleAudit { get; } = new();

    public PagedCollection<AuditEntry> AuditPage { get; }

    public ObservableCollection<TestSuite> Suites { get; } = new();

    public ObservableCollection<ResponseHeaderView> ResponseHeaders { get; } = new();

    public ImportViewModel Import { get; }

    public IReadOnlyList<string> Methods { get; } =
        new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    public IReadOnlyList<CodeLanguageOption> CodeLanguages => CodeSampleService.Languages;

    public bool IsCompactLayout => Settings.UiLayout == UiLayouts.Compact;

    public bool IsWrapTabs => Settings.TabOverflow == TabOverflows.Wrap;

    public ObservableCollection<RequestTabOption> RequestTabOptions { get; } = new();

    [ObservableProperty]
    private int _requestTabIndex;

    [ObservableProperty]
    private CodeLanguageOption _selectedCodeLanguage = CodeSampleService.Languages[0];

    [ObservableProperty]
    private string _codePreview = string.Empty;

    [ObservableProperty]
    private bool _hasOpenApiDoc;

    [ObservableProperty]
    private string _openApiDocMarkdown = string.Empty;

    [ObservableProperty]
    private string _openApiDocSourceText = string.Empty;

    [ObservableProperty]
    private bool _hasOpenApiSource;

    [ObservableProperty]
    private bool _hasOpenApiSourceSelected;

    [ObservableProperty]
    private string _openApiAutoSyncActionText = "开启自动同步";

    [ObservableProperty]
    private bool _isSyncingOpenApi;

    public string CodeFileName => SelectedCodeLanguage.FileName;

    public string CodeLanguageHighlight => SelectedCodeLanguage.Highlight;

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
    private string _treeFilterMode = "全部";

    [ObservableProperty]
    private bool _treeIsEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTransformSse))]
    private bool _isSending;

    [ObservableProperty]
    private double _uploadProgressValue;

    [ObservableProperty]
    private string _uploadProgressText = string.Empty;

    [ObservableProperty]
    private bool _isUploading;

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
    [NotifyPropertyChangedFor(nameof(CanRerunSelectedRun))]
    private TestRunResult? _selectedSuiteResult;

    public ObservableCollection<SuiteValidationIssue> SelectedSuiteValidationIssues { get; } =
        new();

    [ObservableProperty]
    private bool _isSuiteValidationVisible;

    [ObservableProperty]
    private bool _isRunningSuite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRequestBodyJsonViewer))]
    [NotifyPropertyChangedFor(nameof(ShowRequestBodyEditor))]
    private bool _isRequestBodyJsonView;

    [ObservableProperty]
    private string _bodyPreview = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportCurl))]
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
    private bool _isHistoryExpanded;

    [ObservableProperty]
    private bool _isHistoryPurgeConfirmOpen;

    [ObservableProperty]
    private string _historyPurgeTitle = "永久删除？";

    [ObservableProperty]
    private string _historyPurgeDescription = string.Empty;

    [ObservableProperty]
    private bool _isAuditTrashConfirmOpen;

    [ObservableProperty]
    private string _auditTrashConfirmTitle = "清空审计记录？";

    [ObservableProperty]
    private string _auditTrashConfirmDescription = string.Empty;

    [ObservableProperty]
    private bool _isAuditTrashOpen;

    [ObservableProperty]
    private bool _isAuditPurgeConfirmOpen;

    [ObservableProperty]
    private string _auditPurgeTitle = "永久删除？";

    [ObservableProperty]
    private string _auditPurgeDescription = string.Empty;

    public bool CanExportCurl =>
        IsRequestSelected
        && !string.IsNullOrWhiteSpace(Editing.Url)
        && !CurlPreview.StartsWith('#');

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
    private bool _isResponseJsonView = true;

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
    [NotifyPropertyChangedFor(nameof(CanTransformSse))]
    private bool _isSseResponse;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SseSummary))]
    [NotifyPropertyChangedFor(nameof(HasSseThinking))]
    [NotifyPropertyChangedFor(nameof(SseThinkingTextWidth))]
    [NotifyPropertyChangedFor(nameof(CanTransformSse))]
    private string _sseThinking = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SseSummary))]
    [NotifyPropertyChangedFor(nameof(HasSseContent))]
    [NotifyPropertyChangedFor(nameof(SseContentTextWidth))]
    [NotifyPropertyChangedFor(nameof(CanTransformSse))]
    private string _sseContent = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SseRawTextWidth))]
    [NotifyPropertyChangedFor(nameof(CanTransformSse))]
    private string _sseRaw = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SseSummary))]
    private int _sseEventCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResumeSse))]
    private string _sseLastEventId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResumeSse))]
    private bool _sseCanResume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseCollapseLabel))]
    private bool _isResponseCollapsed;

    public string ResponseCollapseLabel => IsResponseCollapsed ? "展开响应" : "收起响应";

    [ObservableProperty]
    private bool _isWorkspaceSideBySide;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private bool _hasPendingUpdate;

    public string UpdateButtonText =>
        HasPendingUpdate ? "安装"
        : IsCheckingUpdates ? "检查中"
        : "更新";

    public string UpdateButtonTooltip =>
        HasPendingUpdate ? $"安装已下载的 {UpdateService.PendingVersion ?? "新版本"}"
        : IsCheckingUpdates ? "正在检查更新…"
        : "检查更新";

    public string WorkspaceLayoutMode
    {
        get => IsWorkspaceSideBySide ? SideBySideLayout : StackedLayout;
        set
        {
            var layout = value == SideBySideLayout ? SideBySideLayout : StackedLayout;
            IsWorkspaceSideBySide = layout == SideBySideLayout;
            Settings.WorkspaceLayout = layout;
        }
    }

    public bool CanResumeSse => SseCanResume && !string.IsNullOrWhiteSpace(SseLastEventId);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSseContent))]
    [NotifyPropertyChangedFor(nameof(ShowSseThinking))]
    [NotifyPropertyChangedFor(nameof(ShowSseRaw))]
    [NotifyPropertyChangedFor(nameof(CanTransformSse))]
    private string _sseViewMode = "内容";

    [ObservableProperty]
    private int _responseTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResponseTextWidth))]
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

    [ObservableProperty]
    private string _managementTab = "审计";

    public bool IsAuditTab => ManagementTab == "审计";

    public bool IsAutomationTab => ManagementTab == "自动化";

    public bool IsGuideTab => ManagementTab == "指南";

    partial void OnManagementTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsAuditTab));
        OnPropertyChanged(nameof(IsAutomationTab));
        OnPropertyChanged(nameof(IsGuideTab));
    }

    [RelayCommand]
    private void OpenGuide()
    {
        ManagementTab = "指南";
        IsManagementOpen = true;
    }

    [RelayCommand]
    private void SetManagementTab(string tab)
    {
        if (tab is "审计" or "自动化" or "指南")
        {
            ManagementTab = tab;
        }
    }

    public CollectionImportOutcome ApplyEnvironmentImport(EnvironmentImportResult result)
    {
        var environmentName =
            ApplyImportedEnvironment(result.Environment) ?? result.Environment.Name;
        return new CollectionImportOutcome(environmentName, 0, 0, environmentName);
    }

    public void CompleteImport()
    {
        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        RefreshDerivedState();
    }

    public CollectionImportOutcome ApplyCollectionImport(ImportResult result)
    {
        var root = new RequestCollection
        {
            Name = string.IsNullOrWhiteSpace(result.Root.Name) ? "导入的集合" : result.Root.Name,
            IsExpanded = true,
        };
        Collections.Insert(0, root);

        RequestItem? firstRequest = null;
        var requestCount = 0;
        var folderCount = 0;
        AddImportedCollectionContent(
            root,
            result.Root,
            1,
            string.Empty,
            ref firstRequest,
            ref requestCount,
            ref folderCount
        );

        var environmentName = ApplyImportedEnvironment(result.Environment);
        SyncRootCollections();
        if (firstRequest is not null)
        {
            OpenRequest(firstRequest);
        }
        else
        {
            SelectedCollection = root;
            SelectedRequest = null;
        }

        return new CollectionImportOutcome(
            result.Root.Name,
            requestCount,
            folderCount,
            environmentName
        );
    }

    public RequestItem ApplyCurlImport(CurlImportResult result)
    {
        var target =
            SelectedCollection
            ?? Editing.Owner
            ?? Collections.FirstOrDefault(collection => collection.Parent is null)
            ?? Collections.FirstOrDefault();
        if (target is null)
        {
            target = new RequestCollection { Name = "导入的 cURL 请求" };
            Collections.Add(target);
        }

        var request = AddImportedRequest(target, result.Request, string.Empty);
        SyncRootCollections();
        OpenRequest(request);
        return request;
    }

    private void AddImportedCollectionContent(
        RequestCollection target,
        ImportedCollection source,
        int depth,
        string namePrefix,
        ref RequestItem? firstRequest,
        ref int requestCount,
        ref int folderCount
    )
    {
        foreach (var imported in source.Requests)
        {
            var request = AddImportedRequest(target, imported, namePrefix);
            firstRequest ??= request;
            requestCount++;
        }

        foreach (var child in source.Children)
        {
            if (depth >= CollectionTree.MaxDepth)
            {
                AddImportedCollectionContent(
                    target,
                    child,
                    depth,
                    namePrefix + child.Name + " / ",
                    ref firstRequest,
                    ref requestCount,
                    ref folderCount
                );
                continue;
            }

            var collection = new RequestCollection
            {
                Name = child.Name,
                Parent = target,
                IsExpanded = true,
            };
            target.Children.Add(collection);
            Collections.Add(collection);
            folderCount++;
            AddImportedCollectionContent(
                collection,
                child,
                depth + 1,
                string.Empty,
                ref firstRequest,
                ref requestCount,
                ref folderCount
            );
        }
    }

    private static RequestItem AddImportedRequest(
        RequestCollection target,
        ImportedRequest imported,
        string namePrefix
    )
    {
        var request = new RequestItem
        {
            Name = namePrefix + imported.Name,
            Method = imported.Method,
            Url = imported.Url,
            TimeoutSeconds = imported.TimeoutSeconds,
            BodyMode = imported.BodyMode,
            Body = imported.Body,
            UploadFilePath = imported.UploadFilePath,
            UploadFieldName = string.IsNullOrWhiteSpace(imported.UploadFieldName)
                ? "file"
                : imported.UploadFieldName,
            Owner = target,
        };

        foreach (var field in imported.Params)
        {
            request.Params.AddRow(field.Key, field.Value, field.IsEnabled);
        }

        foreach (var field in imported.Headers)
        {
            request.Headers.AddRow(field.Key, field.Value, field.IsEnabled);
        }

        foreach (var field in imported.FormFields)
        {
            request.FormFields.AddRow(field.Key, field.Value, field.IsEnabled);
        }

        request.Params.EnsureTrailingRow();
        request.Headers.EnsureTrailingRow();
        request.FormFields.EnsureTrailingRow();
        target.Requests.Add(request);
        return request;
    }

    public string? ApplyImportedEnvironment(ImportedEnvironment? imported)
    {
        if (imported is null || imported.Variables.Count == 0)
        {
            return null;
        }

        var environment =
            Environments.FirstOrDefault(candidate =>
                candidate.Name.Equals(imported.Name, StringComparison.OrdinalIgnoreCase)
            ) ?? new EnvironmentModel { Name = imported.Name };
        if (!Environments.Contains(environment))
        {
            Environments.Add(environment);
        }

        foreach (var variable in imported.Variables)
        {
            if (
                environment.Variables.Any(row =>
                    row.Key.Equals(variable.Key, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                continue;
            }

            environment.Variables.AddRow(variable.Key, variable.Value, variable.IsEnabled);
        }

        environment.Variables.EnsureTrailingRow();
        ActiveEnvironment = environment;
        return environment.Name;
    }

    public ExportDocument? BuildPostmanExport()
    {
        var collection = SelectedCollection ?? SelectedRequest?.Owner;
        if (collection is not null)
        {
            return new ExportDocument(
                PostmanExportService.ExportCollection(collection),
                PostmanExportService.SuggestFileName(collection.Name)
            );
        }

        var roots = Collections.Where(candidate => candidate.Parent is null).ToList();
        if (roots.Count == 0)
        {
            return null;
        }

        return new ExportDocument(
            PostmanExportService.ExportWorkspace(roots),
            PostmanExportService.SuggestFileName("PostSharp 工作区")
        );
    }

    public ExportDocument? BuildCurlExport()
    {
        if (!CanExportCurl)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(Editing.Name) ? "request" : Editing.Name.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(
            name.Select(character => invalid.Contains(character) ? '-' : character).ToArray()
        )
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrEmpty(safeName))
        {
            safeName = "request";
        }

        return new ExportDocument(CurlPreview, $"{safeName}.sh");
    }

    public ExportDocument? BuildPowerShellExport()
    {
        if (!CanExportCurl)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(Editing.Name) ? "request" : Editing.Name.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(
            name.Select(character => invalid.Contains(character) ? '-' : character).ToArray()
        )
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrEmpty(safeName))
        {
            safeName = "request";
        }

        return new ExportDocument(
            RequestComposer.BuildPowerShell(Editing, ActiveEnvironment),
            $"{safeName}.ps1"
        );
    }

    public ExportDocument? BuildCodeExport()
    {
        if (string.IsNullOrWhiteSpace(CodePreview))
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(Editing.Name) ? "request" : Editing.Name.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(
            name.Select(character => invalid.Contains(character) ? '-' : character).ToArray()
        )
            .Trim()
            .TrimEnd('.');
        if (string.IsNullOrEmpty(safeName))
        {
            safeName = "request";
        }

        var extension = Path.GetExtension(SelectedCodeLanguage.FileName);
        return new ExportDocument(CodePreview, $"{safeName}{extension}");
    }

    public CollectionImportOutcome ApplyOpenApiImport(OpenApiImportDocument document)
    {
        EnsureOpenApiEnvironments(document);
        var root = new RequestCollection { Name = document.Title, IsExpanded = true };
        if (
            Uri.TryCreate(document.Source, UriKind.Absolute, out var sourceUri)
            && (sourceUri.Scheme == Uri.UriSchemeHttp || sourceUri.Scheme == Uri.UriSchemeHttps)
        )
        {
            root.OpenApiSource = new OpenApiSource
            {
                Url = sourceUri.ToString(),
                DocumentTitle = document.Title,
                Version = document.Version,
                ETag = document.ETag ?? string.Empty,
                LastModifiedHeader = document.LastModifiedHeader ?? string.Empty,
                ContentHash = document.ContentHash ?? string.Empty,
                ImportedAt = DateTimeOffset.Now,
                LastSyncedAt = DateTimeOffset.Now,
                LastRequestCount = document.RequestCount,
                SelectedServerIndex = document.SelectedServerIndex,
            };
        }

        Collections.Insert(0, root);

        var groupCount = 0;
        var requestCount = 0;
        RequestItem? firstRequest = null;

        foreach (var group in document.Groups)
        {
            var target = root;
            if (group.Name != "未分组")
            {
                target = new RequestCollection
                {
                    Name = group.Name,
                    Parent = root,
                    IsExpanded = true,
                };
                root.Children.Add(target);
                Collections.Add(target);
                groupCount++;
            }

            foreach (var imported in group.Requests)
            {
                var request = OpenApiRequestFactory.Create(imported);
                request.Owner = target;
                target.Requests.Add(request);
                firstRequest ??= request;
                requestCount++;
            }
        }

        SyncRootCollections();
        if (firstRequest is not null)
        {
            OpenRequest(firstRequest);
        }
        else
        {
            SelectedCollection = root;
            SelectedRequest = null;
        }

        return new CollectionImportOutcome(document.Title, requestCount, groupCount, null);
    }

    private void EnsureOpenApiEnvironments(OpenApiImportDocument document)
    {
        var targets = OpenApiEnvironmentService.Resolve(document);
        EnvironmentModel? first = null;
        foreach (var target in targets)
        {
            var environment = Environments.FirstOrDefault(candidate =>
                candidate.Variables.Any(row =>
                    string.Equals(row.Key, "baseUrl", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(row.Value, target.BaseUrl, StringComparison.OrdinalIgnoreCase)
                )
            );
            if (environment is null)
            {
                var name = target.Name;
                for (
                    var suffix = 2;
                    Environments.Any(candidate => candidate.Name == name);
                    suffix++
                )
                {
                    name = $"{target.Name} ({suffix})";
                }

                environment = new EnvironmentModel { Name = name };
                environment.Variables.AddRow("baseUrl", target.BaseUrl);
                environment.Variables.EnsureTrailingRow();
                Environments.Add(environment);
            }

            first ??= environment;
        }

        if (first is not null)
        {
            ActiveEnvironment = first;
        }
    }

    [RelayCommand]
    private Task SyncSelectedOpenApi() => SyncOpenApiAsync(SelectedCollection, silent: false);

    [RelayCommand]
    private void CancelOpenApiSync() => _openApiSyncRequestCts?.Cancel();

    [RelayCommand]
    private void ToggleSelectedOpenApiAutoSync()
    {
        var root = RootCollectionOf(SelectedCollection ?? Editing.Owner);
        if (root?.OpenApiSource is not { CanSync: true } source)
        {
            ShowToast("当前集合不是从 OpenAPI URL 导入的，无法自动同步。");
            return;
        }

        source.AutoSync = !source.AutoSync;
        Persist();
        RefreshOpenApiSelectionState();
        RefreshOpenApiDocState();
        ShowToast(source.AutoSync ? "已开启自动同步" : "已关闭自动同步");
    }

    /// <summary>
    /// Downloads the source document and merges it into the imported collection. Downloads run off the UI
    /// thread; the merge, persistence and notifications are marshalled back to it. Removed operations go to
    /// the collection recycle bin, so a bad upstream change is reversible.
    /// </summary>
    public async Task SyncOpenApiAsync(
        RequestCollection? collection,
        bool silent,
        CancellationToken cancellationToken = default
    )
    {
        var root = RootCollectionOf(collection ?? SelectedCollection ?? Editing.Owner);
        if (root?.OpenApiSource is not { CanSync: true } source)
        {
            if (!silent)
            {
                ShowToast("当前集合不是从 OpenAPI URL 导入的，没有可同步的来源。");
            }

            return;
        }

        if (IsSyncingOpenApi)
        {
            if (!silent)
            {
                ShowToast("正在同步 OpenAPI 文档，请稍候。");
            }

            return;
        }

        CancellationTokenSource? requestCts = null;
        if (!silent)
        {
            IsSyncingOpenApi = true;
            _openApiSyncRequestCts?.Cancel();
            _openApiSyncRequestCts?.Dispose();
            requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _openApiSyncRequestCts = requestCts;
        }

        var syncToken = requestCts?.Token ?? cancellationToken;
        try
        {
            Log.Information("开始同步 OpenAPI 文档 {Url}", source.Url);
            var result = await OpenApiImportService.FetchAsync(
                source.Url,
                source.ETag,
                source.LastModifiedHeader,
                syncToken,
                knownContentHash: source.ContentHash
            );
            if (result.NotModified)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    source.ETag = result.ETag;
                    source.LastModifiedHeader = result.LastModified;
                    source.LastSyncedAt = DateTimeOffset.Now;
                    Persist();
                    if (!silent)
                    {
                        RefreshOpenApiDocState();
                        ShowToast($"OpenAPI「{root.Name}」已是最新");
                    }
                });
                return;
            }

            if (result.Document is not { } document)
            {
                throw new InvalidOperationException("OpenAPI 文档没有返回内容。");
            }

            if (
                source.ContentHash.Length > 0
                && string.Equals(source.ContentHash, result.ContentHash, StringComparison.Ordinal)
            )
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    source.ETag = result.ETag;
                    source.LastModifiedHeader = result.LastModified;
                    source.LastSyncedAt = DateTimeOffset.Now;
                    Persist();
                    if (!silent)
                    {
                        RefreshOpenApiDocState();
                        ShowToast($"OpenAPI「{root.Name}」已是最新");
                    }
                });
                return;
            }

            if (
                source.SelectedServerIndex >= 0
                && source.SelectedServerIndex < document.Servers.Count
            )
            {
                document = document.WithServer(source.SelectedServerIndex);
            }

            var applied = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var syncResult = OpenApiSyncService.Apply(
                    root,
                    document,
                    Collections.Add,
                    request => TreeTrash.TrashRequest(request, "OpenAPI 同步")
                );
                source.ETag = result.ETag;
                source.LastModifiedHeader = result.LastModified;
                source.ContentHash = result.ContentHash;
                source.LastSyncedAt = DateTimeOffset.Now;
                source.LastRequestCount = document.RequestCount;
                source.DocumentTitle = document.Title;
                source.Version = document.Version;

                SyncRootCollections();
                if (!Collections.SelectMany(candidate => candidate.Requests).Contains(Editing))
                {
                    SelectFirstRequestOrBlank();
                }

                Persist();
                NotifyTreeChanged();
                NotifySelectionChanged();
                RefreshOpenApiDocState();
                return syncResult;
            });

            Log.Information(
                "OpenAPI 同步完成 {Url}：新增 {Added}，更新 {Updated}，移除 {Removed}，未变 {Unchanged}",
                source.Url,
                applied.Added,
                applied.Updated,
                applied.Removed,
                applied.Unchanged
            );
            if (!silent || applied.Changed)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ShowToast($"OpenAPI「{root.Name}」{applied.Summary}")
                );
            }
        }
        catch (OperationCanceledException) when (syncToken.IsCancellationRequested)
        {
            Log.Information("OpenAPI 同步已取消 {Url}", source.Url);
            if (!silent)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ShowToast("已取消 OpenAPI 同步"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "同步 OpenAPI 文档失败 {Url}", source.Url);
            await Dispatcher.UIThread.InvokeAsync(() =>
                DataNotice.Warn(
                    $"OpenAPI「{root.Name}」同步失败：{exception.Message} 请检查文档地址、网络和访问权限后重试。"
                )
            );
        }
        finally
        {
            if (!silent)
            {
                requestCts?.Dispose();
                if (ReferenceEquals(_openApiSyncRequestCts, requestCts))
                {
                    _openApiSyncRequestCts = null;
                }

                await Dispatcher.UIThread.InvokeAsync(() => IsSyncingOpenApi = false);
            }
        }
    }

    private async Task RunOpenApiAutoSyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            var roots = await Dispatcher.UIThread.InvokeAsync(() =>
                Collections
                    .Where(candidate =>
                        candidate.Parent is null
                        && candidate.OpenApiSource is { AutoSync: true, CanSync: true }
                    )
                    .ToList()
            );
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SyncOpenApiAsync(root, silent: true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "自动同步 OpenAPI 文档时意外终止");
        }
        finally
        {
            // 自动同步会一次性重建几百个请求和它们的 schema 文档，峰值过后立刻把这段内存还回去。
            HeapTrim.Request();
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
            _ => key,
        };

        var request = Collections
            .SelectMany(collection => collection.Requests)
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

    public SettingsViewModel CreateSettingsViewModel() => new(Settings, _settingsStore);

    public AuthorizationViewModel CreateAuthorizationViewModel(
        RequestAuth auth,
        string targetLabel,
        Func<string> targetDetailFactory
    ) => new(auth, targetLabel, targetDetailFactory);

    private RequestCollection? AuthCollectionTarget =>
        SelectedRequest is null ? SelectedCollection : null;

    /// <summary>树里选中的是集合而不是请求；此时授权标签页编辑集合自己的配置。</summary>
    public bool IsCollectionSelected => AuthCollectionTarget is not null;

    /// <summary>授权标签页与工具栏钥匙按钮当前编辑的那份配置：选中的集合或当前请求。</summary>
    public RequestAuth EditingAuth =>
        AuthCollectionTarget is { } collection ? collection.Auth : Editing.Auth;

    /// <summary>当前编辑对象通过继承拿到授权的集合；自己配置了授权或一路继承时为 null。</summary>
    public RequestCollection? InheritedAuthSource { get; private set; }

    /// <summary>「编辑所属 / 上级集合授权」按钮要打开的集合；没有可跳转的上级时为 null。</summary>
    public RequestCollection? EnclosingCollectionAuthTarget
    {
        get
        {
            if (AuthCollectionTarget is not null)
            {
                return InheritedAuthSource;
            }

            return Editing.Owner;
        }
    }

    public bool HasEnclosingCollectionAuth => EnclosingCollectionAuthTarget is not null;

    public string EnclosingCollectionAuthLabel =>
        AuthCollectionTarget is not null ? "编辑上级集合授权" : "编辑所属集合授权";

    public string AuthScopeTitle { get; private set; } = string.Empty;

    public string AuthScopeDetail { get; private set; } = string.Empty;

    public string AuthScopeSummary { get; private set; } = string.Empty;

    public void RefreshAuthState()
    {
        InheritedAuthSource = AuthCollectionTarget is { } collection
            ? AuthResolver.FindInheritSource(collection)
            : AuthResolver.FindInheritSource(Editing);

        AuthScopeTitle = BuildAuthScopeTitle();
        AuthScopeDetail = BuildAuthScopeDetail();
        AuthScopeSummary = BuildAuthScopeSummary();
        OnPropertyChanged(nameof(InheritedAuthSource));
        OnPropertyChanged(nameof(EnclosingCollectionAuthTarget));
        OnPropertyChanged(nameof(HasEnclosingCollectionAuth));
        OnPropertyChanged(nameof(EnclosingCollectionAuthLabel));
        OnPropertyChanged(nameof(AuthScopeTitle));
        OnPropertyChanged(nameof(AuthScopeDetail));
        OnPropertyChanged(nameof(AuthScopeSummary));
        RefreshDerivedState();
    }

    /// <summary>授权窗口与标签页共用这段说明：写清保存后到底影响谁。</summary>
    public string DescribeCollectionAuthTarget(RequestCollection collection)
    {
        if (!collection.Auth.IsInherit)
        {
            return $"只作用于集合「{collection.Name}」及其下级中仍选择「继承」的请求：{AuthResolver.Describe(collection.Auth)}。";
        }

        return AuthResolver.FindInheritSource(collection) is { } source
            ? $"当前继承上级集合「{source.Name}」的授权：{AuthResolver.Describe(source.Auth)}。在这里配置后只覆盖本集合及其下级。"
            : "该集合当前未配置授权；在这里配置后只影响本集合及其下级，其他集合不受影响。";
    }

    public string DescribeRequestAuthTarget(RequestItem request)
    {
        if (!request.Auth.IsInherit)
        {
            return $"只影响请求「{request.Name}」：{AuthResolver.Describe(request.Auth)}。";
        }

        return AuthResolver.FindInheritSource(request) is { } source
            ? $"请求「{request.Name}」当前继承集合「{source.Name}」的授权：{AuthResolver.Describe(source.Auth)}。在这里配置后只覆盖这个请求。"
            : $"请求「{request.Name}」当前不附加认证；在这里配置后只影响这个请求。";
    }

    /// <summary>授权窗口关闭后调用：把窗口里改过的配置落盘，并刷新摘要与 cURL。</summary>
    public void OnAuthorizationDialogClosed()
    {
        Persist();
        RefreshAuthState();
    }

    /// <summary>发送前解析这次请求的授权；OAuth 2.0 令牌过期时先续期。</summary>
    public async Task<AuthInjection?> ResolveAuthAsync(
        RequestItem request,
        CancellationToken cancellationToken = default
    )
    {
        var auth = AuthResolver.Resolve(request);
        if (auth is null)
        {
            return null;
        }

        if (auth.AuthType == AuthKinds.OAuth2 && auth.OAuth2 is { } profile)
        {
            if (profile.IsTokenExpired && profile.CanRefresh)
            {
                var refreshed = await _authorizationService.EnsureFreshAsync(
                    profile,
                    cancellationToken
                );
                if (!refreshed && profile.IsTokenExpired)
                {
                    throw new OidcException("授权令牌已过期且刷新失败，请重新登录后再发送。");
                }
            }
            else if (profile.IsTokenExpired || !profile.HasAccessToken)
            {
                throw new OidcException("授权令牌不可用，请先在授权配置中登录。");
            }

            return AuthorizationService.BuildInjection(profile, auth.Target);
        }

        return AuthorizationService.BuildInjection(auth);
    }

    /// <summary>同步版本，给 cURL 预览这类不能等待令牌刷新的地方用。</summary>
    public AuthInjection? ResolveAuth(RequestItem request)
    {
        var auth = AuthResolver.Resolve(request);
        return auth is null ? null : AuthorizationService.BuildInjection(auth);
    }

    private string BuildAuthScopeTitle()
    {
        if (AuthCollectionTarget is { } collection)
        {
            if (!collection.Auth.IsInherit)
            {
                return $"集合「{collection.Name}」· 本级授权";
            }

            return InheritedAuthSource is { } source
                ? $"集合「{collection.Name}」· 继承自「{source.Name}」"
                : $"集合「{collection.Name}」· 未配置授权";
        }

        if (!Editing.Auth.IsInherit)
        {
            return $"请求「{Editing.Name}」· 本级授权";
        }

        return InheritedAuthSource is { } inherited
            ? $"请求「{Editing.Name}」· 继承自集合「{inherited.Name}」"
            : $"请求「{Editing.Name}」· 未配置授权";
    }

    private string BuildAuthScopeDetail()
    {
        if (AuthCollectionTarget is { } collection)
        {
            if (!collection.Auth.IsInherit)
            {
                return $"只作用于该集合及其下级中仍选择「继承」的请求：{AuthResolver.Describe(collection.Auth)}。";
            }

            return InheritedAuthSource is { } source
                ? $"本集合未覆盖，使用上级集合「{source.Name}」的授权：{AuthResolver.Describe(source.Auth)}；该集合下未单独配置的请求共用这份授权。"
                : "该集合与其下级都未配置授权，发送时不附加认证；选择一种认证类型即可为本集合配置。";
        }

        if (!Editing.Auth.IsInherit)
        {
            return $"只影响「{Editing.Name}」：{AuthResolver.Describe(Editing.Auth)}。";
        }

        return InheritedAuthSource is { } inherited
            ? $"{AuthResolver.Describe(inherited.Auth)}；该集合下未单独配置的请求共用这份授权，修改集合授权会影响这些请求。"
            : "发送时不附加 Authorization 头或令牌参数；选择一种认证类型即可为这个请求单独配置。";
    }

    private string BuildAuthScopeSummary()
    {
        if (AuthCollectionTarget is { } collection)
        {
            var resolved = AuthResolver.Resolve(collection);
            if (resolved is null)
            {
                return $"集合「{collection.Name}」：不附加认证";
            }

            return InheritedAuthSource is { } source
                ? $"集合「{collection.Name}」继承「{source.Name}」：{AuthResolver.Describe(resolved)}"
                : $"集合「{collection.Name}」：{AuthResolver.Describe(resolved)}";
        }

        var requestAuth = AuthResolver.Resolve(Editing);
        if (requestAuth is null)
        {
            return $"请求「{Editing.Name}」：不附加认证";
        }

        return AuthResolver.FindInheritSource(Editing) is { } inherited
            ? $"请求「{Editing.Name}」继承「{inherited.Name}」：{AuthResolver.Describe(requestAuth)}"
            : $"请求「{Editing.Name}」：{AuthResolver.Describe(requestAuth)}";
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        _http.TimeoutSeconds = Math.Clamp(Settings.RequestTimeoutSeconds, 5, 600);
        _http.AutoFormatResponse = Settings.AutoFormatResponse;
        _http.SseThinkingPath = Settings.SseThinkingPath;
        _http.SseContentPath = Settings.SseContentPath;
        _largeResponseThreshold = Math.Max(8, Settings.LargeResponseThresholdKb) * 1024;
        _auditLimit = Math.Max(0, Settings.AuditLimit);

        if (_auditLimit > 0)
        {
            while (Audit.Count > _auditLimit)
            {
                Audit.RemoveAt(Audit.Count - 1);
            }
        }

        PersistAudit();

        if (
            e.PropertyName == nameof(AppSettings.Theme)
            || e.PropertyName == nameof(AppSettings.CornerScale)
        )
        {
            _ = ThemeService.ApplyAsync(Settings.Theme, cornerScale: Settings.CornerScale);
        }

        if (
            e.PropertyName == nameof(AppSettings.UiScale)
            || e.PropertyName == nameof(AppSettings.UiLayout)
            || e.PropertyName == nameof(AppSettings.TabOverflow)
        )
        {
            OnPropertyChanged(nameof(IsCompactLayout));
            OnPropertyChanged(nameof(IsWrapTabs));
            UiScaleService.ApplyToAll(Settings.UiScale);
        }

        if (
            e.PropertyName == nameof(AppSettings.LogDirectory)
            || e.PropertyName == nameof(AppSettings.LogLevel)
        )
        {
            AppLog.Reconfigure(Settings.LogDirectory, Settings.LogLevel);
        }

        if (e.PropertyName == nameof(AppSettings.FontDirectories))
        {
            FontService.Reload(Settings.FontDirectories);
        }

        if (_settingsStore.Save(Settings) == SaveResult.Failed)
        {
            DataNotice.Warn(
                "设置保存失败：无法写入 settings.json，修改只在本次运行中有效。请检查数据目录是否可写、settings.json 是否被其他程序占用。"
            );
        }

        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HasSelection => SelectedRequest is not null || SelectedCollection is not null;

    public bool IsRequestSelected => SelectedRequest is not null;

    public bool HasOpenDialog =>
        IsEnvironmentDialogOpen
        || IsMcpDialogOpen
        || IsRenameDialogOpen
        || IsDeleteDialogOpen
        || IsHistoryTrashConfirmOpen
        || IsHistoryTrashOpen
        || IsHistoryPurgeConfirmOpen
        || IsAuditTrashConfirmOpen
        || IsAuditTrashOpen
        || IsAuditPurgeConfirmOpen
        || Import.IsImportDialogOpen
        || TreeTrash.IsOpen
        || TreeTrash.IsPurgeConfirmOpen;

    /// <summary>Enter in a confirmation or rename dialog; buttons keep their own activation.</summary>
    public bool TryConfirmActiveDialog()
    {
        if (IsDeleteDialogOpen)
        {
            ConfirmDeleteCommand.Execute(null);
            return true;
        }

        if (IsRenameDialogOpen)
        {
            ApplyRenameCommand.Execute(null);
            return true;
        }

        if (IsHistoryTrashConfirmOpen)
        {
            ConfirmHistoryTrashCommand.Execute(null);
            return true;
        }

        if (IsHistoryPurgeConfirmOpen)
        {
            ConfirmHistoryPurgeCommand.Execute(null);
            return true;
        }

        if (IsAuditTrashConfirmOpen)
        {
            ConfirmAuditTrashCommand.Execute(null);
            return true;
        }

        if (IsAuditPurgeConfirmOpen)
        {
            ConfirmAuditPurgeCommand.Execute(null);
            return true;
        }

        if (TreeTrash.IsPurgeConfirmOpen)
        {
            TreeTrash.ConfirmPurgeCommand.Execute(null);
            return true;
        }

        return false;
    }

    /// <summary>Esc closes the top-most overlay and applies its cancel semantics.</summary>
    public bool TryCancelActiveDialog()
    {
        if (Import.IsImportDialogOpen)
        {
            Import.CloseImportDialogCommand.Execute(null);
            return true;
        }

        if (IsHistoryPurgeConfirmOpen)
        {
            CancelHistoryPurgeCommand.Execute(null);
            return true;
        }

        if (IsAuditPurgeConfirmOpen)
        {
            CancelAuditPurgeCommand.Execute(null);
            return true;
        }

        if (TreeTrash.IsPurgeConfirmOpen)
        {
            TreeTrash.CancelPurgeCommand.Execute(null);
            return true;
        }

        if (IsHistoryTrashConfirmOpen)
        {
            CancelHistoryTrashCommand.Execute(null);
            return true;
        }

        if (IsAuditTrashConfirmOpen)
        {
            CancelAuditTrashCommand.Execute(null);
            return true;
        }

        if (IsDeleteDialogOpen)
        {
            CancelDeleteCommand.Execute(null);
            return true;
        }

        if (IsRenameDialogOpen)
        {
            CancelRenameCommand.Execute(null);
            return true;
        }

        if (IsEnvironmentDialogOpen)
        {
            CancelEnvironmentDialogCommand.Execute(null);
            return true;
        }

        if (IsMcpDialogOpen)
        {
            CloseMcpDialogCommand.Execute(null);
            return true;
        }

        if (IsHistoryTrashOpen)
        {
            CloseHistoryTrashCommand.Execute(null);
            return true;
        }

        if (IsAuditTrashOpen)
        {
            CloseAuditTrashCommand.Execute(null);
            return true;
        }

        if (TreeTrash.IsOpen)
        {
            TreeTrash.CloseCommand.Execute(null);
            return true;
        }

        return false;
    }

    public bool HasHistory => History.Count > 0;

    public string HistoryCountText => History.Count == 0 ? "最近记录" : $"最近 {History.Count} 条";

    public string TreeEmptyText =>
        TreeFilterMode == "收藏" ? "还没有收藏的集合或请求" : "没有匹配的请求";

    public bool HasRecycledHistory => RecycledHistory.Count > 0;

    public string RecycleBinLabel =>
        RecycledHistory.Count > 0 ? $"回收站 ({RecycledHistory.Count})" : "回收站";

    public bool HasSelectedAuditEntry => SelectedAuditEntry is not null;

    public bool HasSelectedSuite => SelectedSuite is not null;

    public bool HasSelectedSuiteResult => SelectedSuiteResult is not null;

    public bool CanRerunSelectedRun => SelectedSuiteResult?.HasFailures == true;

    public bool HasSuiteValidationIssues => SelectedSuiteValidationIssues.Count > 0;

    public string SuiteValidationSummary =>
        !HasSuiteValidationIssues ? string.Empty
        : SelectedSuiteValidationIssues.Any(issue => issue.IsError)
            ? $"预检未通过：{SelectedSuiteValidationIssues.Count(issue => issue.IsError)} 个错误"
        : $"预检通过：{SelectedSuiteValidationIssues.Count} 个提醒";

    public int AuditCount => Audit.Count;

    public bool HasAuditTrash => AuditTrash.Count > 0;

    public string AuditTrashLabel =>
        AuditTrash.Count > 0 ? $"审计回收站 ({AuditTrash.Count})" : "审计回收站";

    public int SuiteCount => Suites.Count;

    public bool HasSuites => Suites.Count > 0;

    public bool ShowJsonResponse =>
        HasResponse && IsJsonResponse && !IsLargeResponse && IsResponseJsonView;

    public bool ShowRawResponse => HasResponse && !ShowJsonResponse;

    public double ResponseTextWidth => EstimateTextWidth(ResponseBody);

    public double SseContentTextWidth => EstimateTextWidth(SseContent);

    public double SseThinkingTextWidth => EstimateTextWidth(SseThinking);

    public double SseRawTextWidth => EstimateTextWidth(SseRaw);

    public bool ShowEmptyResponse => !HasResponse && !HasResponseError;

    public bool ShowResponseError => HasResponseError;

    public bool ShowLargeResponseBanner => HasResponse && IsLargeResponse;

    public IReadOnlyList<string> SseViewOptions { get; } = new[] { "内容", "思考过程", "原始事件" };

    public bool ShowSseContent => IsSseResponse && SseViewMode == "内容";

    public bool ShowSseThinking => IsSseResponse && SseViewMode == "思考过程";

    public bool ShowSseRaw => IsSseResponse && SseViewMode == "原始事件";

    public bool HasSseContent => SseContent.Length > 0;

    public bool HasSseThinking => SseThinking.Length > 0;

    public bool CanTransformSse =>
        IsSseResponse
        && !IsSending
        && (
            SseViewMode switch
            {
                "内容" => SseContent.Length > 0,
                "思考过程" => SseThinking.Length > 0,
                "原始事件" => SseRaw.Length > 0,
                _ => false,
            }
        );

    private static double EstimateTextWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 600;
        }

        var longest = text.Split('\n').Max(line => line.Length);
        return Math.Clamp(longest * 12.0, 600, 80000);
    }

    public bool IsMultipartBody => Editing.BodyMode == "Multipart";

    public bool IsJsonBody => Editing.BodyMode == "Json";

    public bool IsTextBody => !IsMultipartBody;

    public bool ShowRequestBodyJsonViewer => IsJsonBody && IsRequestBodyJsonView;

    public bool ShowRequestBodyEditor => IsTextBody && !ShowRequestBodyJsonViewer;

    public bool HasUploadFile => Editing.UploadFiles.Count > 0;

    public string SseSummary =>
        IsSseResponse
            ? $"SSE 流 · {SseEventCount} 个事件 · 内容 {SseContent.Length} 字 · 思考 {SseThinking.Length} 字"
            : "当前响应不是 SSE 流";

    public object ResponseStatusIcon =>
        new ArcGlyph
        {
            Data = ResponseTone switch
            {
                BadgeTone.Success => ArcIcons.CircleCheck,
                BadgeTone.Warning => ArcIcons.TriangleAlert,
                BadgeTone.Danger => ArcIcons.CircleX,
                _ => ArcIcons.Info,
            },
            Size = 12,
        };

    public string DetailPageSizeText => $"{DetailPageSize / 1024} KB/页";

    public string McpEndpoint =>
        _mockServer.IsRunning ? $"{_mockServer.BaseUrl}/mcp" : "MCP 服务未启动";

    public string McpClientConfig =>
        $$"""
            {
              "mcpServers": {
                "postsharp": {
                  "url": "{{(
                _mockServer.IsRunning ? _mockServer.BaseUrl : "http://127.0.0.1:5199"
            )}}/mcp"
                }
              }
            }
            """;

    public string McpToolSummary =>
        "集合/请求：list_collections、create_collection、update_collection、move_collection、delete_collection、"
        + "get_request、create_request、update_request、move_request、delete_request、duplicate_request；"
        + "套件：list_suites、get_suite、create_suite、create_suite_from_collections、update_suite、run_suite、delete_suite；"
        + "环境与历史：list_environments、create_environment、rename_environment、delete_environment、activate_environment、set_variable、delete_variable、"
        + "list_history、clear_history、list_history_trash、restore_history、purge_history、empty_history_trash；"
        + "审计/回收站：list_audit、get_audit、clear_audit、list_audit_trash、restore_audit、purge_audit、empty_audit_trash、"
        + "list_collection_trash、restore_collection_trash、purge_collection_trash、empty_collection_trash；"
        + "设置/存储：get_settings、update_settings、check_updates、get_storage_config、upsert_storage_profile、delete_storage_profile、set_storage_binding、test_storage_profile、sync_storage、switch_storage；"
        + "导入导出：import_content、export_workspace、send_request、run_sequence、get_curl。";

    public string EditorHint =>
        IsCollectionSelected ? "已选中集合 · 「授权」标签页正在配置集合授权"
        : IsDraft ? "草稿 · 保存后加入集合"
        : "Ctrl+S 保存 · Ctrl+Enter 发送";

    partial void OnEditingChanged(RequestItem value)
    {
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(EditorHint));
        BindEditingTracking();
        OnPropertyChanged(nameof(EditingAuth));
        RefreshAuthState();
        RefreshOpenApiDocState();
        RefreshCodePreview();
    }

    partial void OnSelectedCodeLanguageChanged(CodeLanguageOption value)
    {
        OnPropertyChanged(nameof(CodeFileName));
        OnPropertyChanged(nameof(CodeLanguageHighlight));
        RefreshCodePreview();
    }

    partial void OnHasOpenApiDocChanged(bool value) => RefreshRequestTabOptions();

    partial void OnRequestTabIndexChanged(int value)
    {
        foreach (var option in RequestTabOptions)
        {
            option.IsSelected = option.Index == value;
        }
    }

    [RelayCommand]
    private void SelectRequestTab(int index) => RequestTabIndex = index;

    private void RefreshRequestTabOptions()
    {
        var current = RequestTabIndex;
        RequestTabOptions.Clear();
        RequestTabOptions.Add(new RequestTabOption(0, "参数"));
        RequestTabOptions.Add(new RequestTabOption(1, "请求头"));
        RequestTabOptions.Add(new RequestTabOption(2, "授权"));
        RequestTabOptions.Add(new RequestTabOption(3, "请求体"));
        RequestTabOptions.Add(new RequestTabOption(4, "cURL"));
        if (HasOpenApiDoc)
        {
            RequestTabOptions.Add(new RequestTabOption(5, "接口文档"));
        }

        RequestTabOptions.Add(new RequestTabOption(6, "调用代码"));
        RequestTabIndex =
            RequestTabOptions.FirstOrDefault(option => option.Index == current)?.Index
            ?? RequestTabOptions[0].Index;
        foreach (var option in RequestTabOptions)
        {
            option.IsSelected = option.Index == RequestTabIndex;
        }
    }

    partial void OnSelectedCollectionChanged(RequestCollection? value) => OnAuthScopeChanged();

    partial void OnSelectedRequestChanged(RequestItem? value) => OnAuthScopeChanged();

    private void OnAuthScopeChanged()
    {
        BindAuthTracking();
        OnPropertyChanged(nameof(IsCollectionSelected));
        OnPropertyChanged(nameof(EditingAuth));
        OnPropertyChanged(nameof(EditorHint));
        RefreshAuthState();
        RefreshOpenApiSelectionState();
    }

    private void RefreshOpenApiSelectionState()
    {
        var root = RootCollectionOf(SelectedCollection ?? Editing.Owner);
        HasOpenApiSourceSelected = root?.OpenApiSource is { CanSync: true };
        OpenApiAutoSyncActionText = root?.OpenApiSource is { AutoSync: true }
            ? "关闭自动同步"
            : "开启自动同步";
    }

    partial void OnFilterTextChanged(string value) => NotifyTreeChanged();

    partial void OnTreeFilterModeChanged(string value)
    {
        OnPropertyChanged(nameof(TreeEmptyText));
        NotifyTreeChanged();
    }

    partial void OnActiveEnvironmentChanged(EnvironmentModel? value)
    {
        if (!_initializing)
        {
            Persist();
        }

        RefreshDerivedState();
    }

    partial void OnSelectedAuditEntryChanged(AuditEntry? value)
    {
        foreach (var entry in Audit)
        {
            entry.IsSelected = ReferenceEquals(entry, value);
        }

        OnPropertyChanged(nameof(HasSelectedAuditEntry));
    }

    partial void OnSelectedSuiteChanged(TestSuite? value)
    {
        foreach (var suite in Suites)
        {
            suite.IsSelected = ReferenceEquals(suite, value);
        }

        ClearSuiteValidation();
        if (value is not null)
        {
            WireSuiteRunCommands(value);
        }

        OnPropertyChanged(nameof(HasSelectedSuite));
        SelectedSuiteResult = value?.Runs.FirstOrDefault();
    }

    partial void OnSelectedSuiteResultChanged(TestRunResult? value)
    {
        OnPropertyChanged(nameof(HasSelectedSuiteResult));
        OnPropertyChanged(nameof(CanRerunSelectedRun));
    }

    partial void OnAuditFilterTextChanged(string value) => RefreshAuditView();

    partial void OnAuditSourceFilterChanged(string value) => RefreshAuditView();

    partial void OnSseViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowSseContent));
        OnPropertyChanged(nameof(ShowSseThinking));
        OnPropertyChanged(nameof(ShowSseRaw));
        OnPropertyChanged(nameof(CanTransformSse));
    }

    partial void OnIsWorkspaceSideBySideChanged(bool value) =>
        OnPropertyChanged(nameof(WorkspaceLayoutMode));

    partial void OnIsCheckingUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(UpdateButtonText));
        OnPropertyChanged(nameof(UpdateButtonTooltip));
    }

    partial void OnHasPendingUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(UpdateButtonText));
        OnPropertyChanged(nameof(UpdateButtonTooltip));
    }

    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (HasPendingUpdate)
        {
            if (!UpdateService.TryApplyPendingUpdate())
            {
                HasPendingUpdate = UpdateService.PendingVersion is not null;
                DataNotice.Warn("无法启动更新器，请检查更新目录权限或重新下载更新。");
                return;
            }

            Log.Information("用户确认安装已下载更新，正在退出以安装新版本");
            (
                Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
            )?.Shutdown();
            return;
        }

        await CheckForUpdatesAsync();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdates)
        {
            return;
        }

        IsCheckingUpdates = true;
        try
        {
            var result = await UpdateService.CheckAndStageAsync(
                Settings,
                force: true,
                CancellationToken.None
            );
            HasPendingUpdate =
                result.Status == UpdateCheckStatus.Staged
                || UpdateService.PendingVersion is not null;
            if (result.Status == UpdateCheckStatus.Failed)
            {
                DataNotice.Warn(result.Message);
            }
            else
            {
                ShowToast(result.Message);
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "手动检查更新失败");
            DataNotice.Warn("检查更新失败，请确认网络可以访问 GitHub 后重试。");
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    partial void OnIsManagementOpenChanged(bool value)
    {
        if (value)
        {
            RefreshAuditView();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        var request = Editing;
        if (request.IsSending)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _sendCancellations[request.Id] = cancellation;
        request.IsSending = true;
        IsSending = true;
        if (ReferenceEquals(Editing, request))
        {
            IsUploading = false;
            UploadProgressValue = 0;
            UploadProgressText = string.Empty;
            _sseResume = null;
            SseCanResume = false;
            SseLastEventId = string.Empty;
        }

        var secrets = SecretMasker.CollectSecrets(Environments);
        ComposedRequest? stored = null;

        try
        {
            HttpResult result;
            try
            {
                var auth = await ResolveAuthAsync(request, cancellation.Token);
                var composed = RequestComposer.Compose(request, ActiveEnvironment, auth);
                if (ReferenceEquals(Editing, request))
                {
                    _lastComposedRequest = composed;
                }

                // Masked before the await: the request and environment can be edited while it is on its way.
                stored = SecretMasker.MaskComposed(request, ActiveEnvironment, composed, secrets);
                var sseProgress = new Progress<SseProgress>(progress =>
                {
                    if (ReferenceEquals(Editing, request))
                    {
                        ApplySseProgress(progress);
                    }
                });
                var uploadProgress = new Progress<UploadProgress>(progress =>
                {
                    if (ReferenceEquals(Editing, request))
                    {
                        ApplyUploadProgress(progress);
                    }
                });
                result = await _http.SendAsync(
                    composed,
                    stored.Url,
                    cancellation.Token,
                    request.TimeoutSeconds,
                    sseProgress,
                    uploadProgress
                );
                if (ReferenceEquals(Editing, request))
                {
                    result = MergeSseResume(result);
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception, "UI 发送请求失败 {RequestName}", request.Name);
                result = HttpRequestService.Failure(exception.Message);
            }

            if (ReferenceEquals(Editing, request))
            {
                ApplyResponse(result);
            }

            if (result.HasResponse)
            {
                if (ReferenceEquals(Editing, request))
                {
                    IsResponseCollapsed = false;
                    if (Settings.WorkspaceLayout is null)
                    {
                        IsWorkspaceSideBySide = true;
                    }
                }

                AddHistory(request, result);
            }

            RecordAudit(request, stored, result, "UI", null, null, secrets);
            Persist();
        }
        finally
        {
            _sendCancellations.Remove(request.Id);
            request.IsSending = false;
            IsSending = _sendCancellations.Count > 0;
            if (ReferenceEquals(Editing, request))
            {
                IsUploading = false;
            }

            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void CancelSend()
    {
        if (_sendCancellations.TryGetValue(Editing.Id, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    [RelayCommand]
    private void ToggleResponseCollapsed()
    {
        IsResponseCollapsed = !IsResponseCollapsed;
    }

    [RelayCommand]
    private async Task ResumeSseAsync()
    {
        var request = Editing;
        if (request.IsSending || !CanResumeSse || _lastComposedRequest is not { } composed)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _sendCancellations[request.Id] = cancellation;
        request.IsSending = true;
        IsSending = true;
        _sseResume = new SseResumeSnapshot(SseRaw, SseThinking, SseContent, SseEventCount);
        SseCanResume = false;
        try
        {
            var sseProgress = new Progress<SseProgress>(progress =>
            {
                if (ReferenceEquals(Editing, request))
                {
                    ApplySseProgress(progress);
                }
            });
            var secrets = SecretMasker.CollectSecrets(Environments);
            var stored = RequestComposer.MaskAuthorization(
                SecretMasker.MaskRequest(composed, SecretMasker.CollectAllSecrets(Environments))
            );
            var logUrl = stored.Url;
            var result = await _http.SendAsync(
                composed,
                logUrl,
                cancellation.Token,
                request.TimeoutSeconds,
                sseProgress,
                lastEventId: SseLastEventId
            );
            if (ReferenceEquals(Editing, request))
            {
                result = MergeSseResume(result);
                ApplyResponse(result);
                if (result.HasResponse)
                {
                    IsResponseCollapsed = false;
                    if (Settings.WorkspaceLayout is null)
                    {
                        IsWorkspaceSideBySide = true;
                    }
                }
            }

            if (result.HasResponse)
            {
                AddHistory(request, result);
            }

            RecordAudit(request, stored, result, "UI", null, null, secrets);
            Persist();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "继续接收 SSE 失败 {RequestName}", request.Name);
            if (ReferenceEquals(Editing, request))
            {
                ApplyResponse(HttpRequestService.Failure(exception.Message));
            }
        }
        finally
        {
            _sendCancellations.Remove(request.Id);
            request.IsSending = false;
            IsSending = _sendCancellations.Count > 0;
            if (ReferenceEquals(Editing, request))
            {
                _sseResume = null;
            }

            cancellation.Dispose();
        }
    }

    private HttpResult MergeSseResume(HttpResult result)
    {
        if (_sseResume is not { } resume || !result.IsSse)
        {
            return result;
        }

        return result with
        {
            SseRaw = resume.Raw + result.SseRaw,
            SseThinking = resume.Thinking + result.SseThinking,
            SseContent = resume.Content + result.SseContent,
            SseEventCount = resume.Count + result.SseEventCount,
        };
    }

    private sealed record SseResumeSnapshot(string Raw, string Thinking, string Content, int Count);

    [RelayCommand]
    private void Save()
    {
        if (Editing.Owner is null)
        {
            EnsureTreeShowsNewNode();
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
        var target =
            SelectedCollection
            ?? Editing.Owner
            ?? Collections.FirstOrDefault(collection => collection.Parent is null)
            ?? Collections.FirstOrDefault();
        if (target is null)
        {
            target = new RequestCollection { Name = "我的集合" };
            Collections.Add(target);
        }

        AddRequestToCollection(target);
    }

    public void AddRequestToCollection(RequestCollection target)
    {
        EnsureTreeShowsNewNode();
        var request = new RequestItem
        {
            Name = $"新建请求 {target.Requests.Count + 1}",
            Method = "GET",
            Url = ActiveEnvironment is not null
                ? "{{baseUrl}}/api/health"
                : "https://api.example.com/health",
            BodyMode = Settings.DefaultBodyMode,
            Owner = target,
        };

        target.Requests.Add(request);
        target.IsExpanded = true;
        OpenRequest(request);
        Persist();
        NotifyTreeChanged();
        ShowToast("已创建请求");
    }

    public void AddSubcollection(RequestCollection parent)
    {
        EnsureTreeShowsNewNode();
        if (parent.Depth >= CollectionTree.MaxDepth)
        {
            ShowToast($"集合最多支持 {CollectionTree.MaxDepth} 级");
            return;
        }

        var collection = new RequestCollection
        {
            Name = $"新建集合 {parent.Children.Count + 1}",
            Parent = parent,
        };

        parent.Children.Add(collection);
        Collections.Add(collection);
        parent.IsExpanded = true;
        SelectedCollection = collection;
        SelectedRequest = null;
        NotifySelectionChanged();
        Persist();
        NotifyTreeChanged();
        ShowToast("已创建二级集合");
    }

    public RequestCollection CreateCollection(
        string name,
        Guid? parentId,
        bool favorite,
        int? position
    )
    {
        RequestCollection? parent = null;
        if (parentId is { } requestedParent)
        {
            parent =
                Collections.FirstOrDefault(candidate => candidate.Id == requestedParent)
                ?? throw new KeyNotFoundException($"没有找到 id 为 {requestedParent} 的父集合。");
        }

        if (parent is not null && parent.Depth >= CollectionTree.MaxDepth)
        {
            throw new InvalidOperationException($"集合最多支持 {CollectionTree.MaxDepth} 级。");
        }

        var collection = new RequestCollection
        {
            Name = string.IsNullOrWhiteSpace(name) ? "MCP 集合" : name.Trim(),
            Parent = parent,
            IsFavorite = favorite,
        };

        Collections.Add(collection);
        if (parent is not null)
        {
            parent.Children.Add(collection);
            parent.IsExpanded = true;
            MoveCollectionWithin(parent.Children, collection, position);
            SyncRootCollections();
        }
        else
        {
            var roots = Collections.Where(candidate => candidate.Parent is null).ToList();
            MoveCollectionWithin(roots, collection, position);
            SyncFlatCollectionOrder(roots);
        }

        Persist();
        NotifyTreeChanged();
        return collection;
    }

    public RequestCollection UpdateCollection(
        Guid id,
        string? name,
        Guid? parentId,
        bool changeParent,
        bool? favorite,
        int? position
    )
    {
        var collection =
            Collections.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new KeyNotFoundException($"没有找到 id 为 {id} 的集合。");

        if (!string.IsNullOrWhiteSpace(name))
        {
            collection.Name = name.Trim();
        }

        if (favorite is { } isFavorite)
        {
            collection.IsFavorite = isFavorite;
        }

        List<RequestCollection>? explicitRootOrder = null;
        if (changeParent)
        {
            RequestCollection? target = null;
            if (parentId is { } requestedParent)
            {
                target =
                    Collections.FirstOrDefault(candidate => candidate.Id == requestedParent)
                    ?? throw new KeyNotFoundException(
                        $"没有找到 id 为 {requestedParent} 的目标集合。"
                    );
            }

            if (
                ReferenceEquals(target, collection)
                || (target is not null && IsSelfOrDescendant(target, collection))
            )
            {
                throw new InvalidOperationException("不能把集合移动到自身或后代集合。");
            }

            if (
                target is not null
                && target.Depth + CollectionTree.SubtreeHeight(collection) > CollectionTree.MaxDepth
            )
            {
                throw new InvalidOperationException($"集合最多支持 {CollectionTree.MaxDepth} 级。");
            }

            if (!ReferenceEquals(collection.Parent, target))
            {
                collection.Parent?.Children.Remove(collection);
                collection.Parent = target;
                target?.Children.Add(collection);
                if (target is not null)
                {
                    target.IsExpanded = true;
                    MoveCollectionWithin(target.Children, collection, position);
                }
                else if (position is not null)
                {
                    var roots = Collections.Where(candidate => candidate.Parent is null).ToList();
                    MoveCollectionWithin(roots, collection, position);
                    explicitRootOrder = roots;
                }
            }
            else if (target is not null && position is not null)
            {
                MoveCollectionWithin(target.Children, collection, position);
            }
            else if (target is null && position is not null)
            {
                var roots = Collections.Where(candidate => candidate.Parent is null).ToList();
                MoveCollectionWithin(roots, collection, position);
                explicitRootOrder = roots;
            }
        }
        else if (position is not null)
        {
            if (collection.Parent is { } parent)
            {
                MoveCollectionWithin(parent.Children, collection, position);
            }
            else
            {
                var roots = Collections.Where(candidate => candidate.Parent is null).ToList();
                MoveCollectionWithin(roots, collection, position);
                explicitRootOrder = roots;
            }
        }

        if (explicitRootOrder is not null)
        {
            SyncFlatCollectionOrder(explicitRootOrder);
        }
        else
        {
            SyncRootCollections();
        }

        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        RefreshAuthState();
        return collection;
    }

    /// <summary>
    /// Moves a collection and everything under it to the recycle bin. Returns false when it does not exist.
    /// </summary>
    public bool DeleteCollection(Guid id, string source)
    {
        var collection = Collections.FirstOrDefault(candidate => candidate.Id == id);
        if (collection is null)
        {
            return false;
        }

        var editingRemoved =
            Editing.Owner is not null && IsSelfOrDescendant(Editing.Owner, collection);
        var selectedRemoved =
            SelectedCollection is not null && IsSelfOrDescendant(SelectedCollection, collection);
        TreeTrash.TrashCollection(collection, source);

        if (editingRemoved)
        {
            SelectedCollection = null;
            SelectedRequest = null;
            SelectFirstRequestOrBlank();
        }
        else if (selectedRemoved)
        {
            SelectedCollection = Editing.Owner;
            SelectedRequest = Editing.Owner is null ? null : Editing;
        }

        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        return true;
    }

    /// <summary>Moves a request to the recycle bin. Returns false when it is not in the tree.</summary>
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
        if (!TreeTrash.TrashRequest(request, source))
        {
            return false;
        }

        if (wasOpen)
        {
            SelectFirstRequestOrBlank();
        }

        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        return true;
    }

    private void OnTreeTrashChanged()
    {
        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
    }

    public RequestItem MoveRequest(Guid requestId, Guid targetCollectionId, int? position)
    {
        var request =
            Collections
                .SelectMany(collection => collection.Requests)
                .FirstOrDefault(candidate => candidate.Id == requestId)
            ?? throw new KeyNotFoundException($"没有找到 id 为 {requestId} 的请求。");
        var target =
            Collections.FirstOrDefault(candidate => candidate.Id == targetCollectionId)
            ?? throw new KeyNotFoundException($"没有找到 id 为 {targetCollectionId} 的集合。");

        if (!ReferenceEquals(request.Owner, target))
        {
            request.Owner?.Requests.Remove(request);
            request.Owner = target;
            target.Requests.Add(request);
        }

        if (position is { } requestedPosition)
        {
            var current = target.Requests.IndexOf(request);
            var index = Math.Clamp(requestedPosition, 0, target.Requests.Count - 1);
            if (current >= 0 && current != index)
            {
                target.Requests.Move(current, index);
            }
        }

        target.IsExpanded = true;
        if (ReferenceEquals(SelectedRequest, request))
        {
            SelectedCollection = target;
        }

        RefreshAuthState();
        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        return request;
    }

    private void SyncRootCollections()
    {
        var roots = Collections.Where(candidate => candidate.Parent is null).ToList();
        SyncFlatCollectionOrder(roots);
    }

    private static void MoveCollectionWithin(
        IList<RequestCollection> siblings,
        RequestCollection collection,
        int? position
    )
    {
        if (position is not { } requestedPosition)
        {
            return;
        }

        var current = siblings.IndexOf(collection);
        if (current < 0)
        {
            return;
        }

        var index = Math.Clamp(requestedPosition, 0, siblings.Count - 1);
        if (index == current)
        {
            return;
        }

        siblings.RemoveAt(current);
        siblings.Insert(index, collection);
    }

    [RelayCommand]
    private void AddCollection()
    {
        EnsureTreeShowsNewNode();
        var collection = new RequestCollection { Name = $"新建集合 {Collections.Count + 1}" };
        Collections.Add(collection);
        collection.IsExpanded = true;
        SelectedCollection = collection;
        SelectedRequest = null;
        IsDraft = Editing.Owner is null;
        NotifySelectionChanged();
        Persist();
        NotifyTreeChanged();
        ShowToast("已创建集合");
    }

    [RelayCommand]
    private void ToggleAllCollections()
    {
        var expand = !Collections.Any(collection => collection.IsExpanded);
        foreach (var collection in Collections)
        {
            collection.IsExpanded = expand;
        }

        NotifyTreeChanged();
        ShowToast(expand ? "已展开全部集合" : "已折叠全部集合");
    }

    private void EnsureTreeShowsNewNode()
    {
        if (TreeFilterMode == "收藏")
        {
            TreeFilterMode = "全部";
        }
    }

    [RelayCommand]
    private void DuplicateSelectedRequest()
    {
        if (SelectedRequest is not { } source || source.Owner is null)
        {
            ShowToast("请先选择一个已保存的请求");
            return;
        }

        EnsureTreeShowsNewNode();
        var copy = source.Clone();
        copy.Name = $"{source.Name} 副本";
        copy.Owner = source.Owner;
        source.Owner.Requests.Add(copy);
        source.Owner.IsExpanded = true;
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
        if (SelectedRequest is { } request)
        {
            RequestDeleteNode(request);
            return;
        }

        if (SelectedCollection is { } collection)
        {
            RequestDeleteNode(collection);
            return;
        }

        ShowToast("请先选择集合或请求");
    }

    public void RequestDeleteNode(object node)
    {
        switch (node)
        {
            case RequestItem request:
                _pendingDelete = request;
                DeleteDialogTitle = "删除请求？";
                DeleteDialogDescription =
                    $"“{request.Name}”将移入集合回收站，可在回收站中恢复到原位置。";
                break;
            case RequestCollection collection:
                _pendingDelete = collection;
                DeleteDialogTitle = "删除集合？";
                DeleteDialogDescription =
                    $"“{collection.Name}”及其子集合、共 {collection.TotalRequestCount} 个请求将移入集合回收站，可在回收站中恢复到原位置。";
                break;
            default:
                ShowToast("请先选择集合或请求");
                return;
        }

        IsDeleteDialogOpen = true;
    }

    public void ToggleFavoriteNode(object node)
    {
        switch (node)
        {
            case RequestItem request:
                request.IsFavorite = !request.IsFavorite;
                ShowToast(request.IsFavorite ? "已收藏请求" : "已取消收藏");
                break;
            case RequestCollection collection:
                collection.IsFavorite = !collection.IsFavorite;
                ShowToast(collection.IsFavorite ? "已收藏集合" : "已取消收藏");
                break;
            default:
                return;
        }

        Persist();
        NotifyTreeChanged();
    }

    public bool CanMoveTreeNode(TreeDisplayNode source, RequestCollection target)
    {
        if (source.Request is { } request)
        {
            return !ReferenceEquals(request.Owner, target);
        }

        if (source.Collection is { } collection)
        {
            return !ReferenceEquals(collection, target)
                && !ReferenceEquals(collection.Parent, target)
                && !IsSelfOrDescendant(target, collection)
                && target.Depth + CollectionTree.SubtreeHeight(collection)
                    <= CollectionTree.MaxDepth;
        }

        return false;
    }

    public bool CanMoveTreeNode(
        TreeDisplayNode source,
        TreeDisplayNode target,
        TreeDropPosition position
    )
    {
        if (ReferenceEquals(source, target) || target.Collection is null && target.Request is null)
        {
            return false;
        }

        if (source.Request is { } request)
        {
            if (target.Request is { } targetRequest)
            {
                return targetRequest.Owner is not null && !ReferenceEquals(request, targetRequest);
            }

            return target.Collection is not null;
        }

        if (
            source.Collection is not { } collection
            || target.Collection is not { } targetCollection
        )
        {
            return false;
        }

        if (
            ReferenceEquals(collection, targetCollection)
            || IsSelfOrDescendant(targetCollection, collection)
        )
        {
            return false;
        }

        var parent =
            position == TreeDropPosition.Inside ? targetCollection : targetCollection.Parent;

        return parent is null
            || (
                !IsSelfOrDescendant(parent, collection)
                && parent.Depth + CollectionTree.SubtreeHeight(collection)
                    <= CollectionTree.MaxDepth
            );
    }

    public bool CanMoveNode(object node, int delta)
    {
        return node switch
        {
            RequestItem request when request.Owner is { } owner => owner.Requests.IndexOf(request)
                + delta
                is var index
                && index >= 0
                && index < owner.Requests.Count,
            RequestCollection collection => CanMoveCollection(collection, delta),
            _ => false,
        };
    }

    public void MoveNode(object node, int delta)
    {
        if (node is RequestItem request && request.Owner is { } owner)
        {
            var index = owner.Requests.IndexOf(request);
            var target = index + delta;
            if (index < 0 || target < 0 || target >= owner.Requests.Count)
            {
                return;
            }

            owner.Requests.Move(index, target);
        }
        else if (node is RequestCollection collection)
        {
            MoveCollection(collection, delta);
        }
        else
        {
            return;
        }

        Persist();
        NotifyTreeChanged();
    }

    private bool CanMoveCollection(RequestCollection collection, int delta)
    {
        IList<RequestCollection> siblings = collection.Parent is { } parent
            ? parent.Children
            : Collections.Where(candidate => candidate.Parent is null).ToList();
        var index = siblings.IndexOf(collection);
        var target = index + delta;
        return index >= 0 && target >= 0 && target < siblings.Count;
    }

    private void MoveCollection(RequestCollection collection, int delta)
    {
        List<RequestCollection> roots;
        if (collection.Parent is { } parent)
        {
            var index = parent.Children.IndexOf(collection);
            var target = index + delta;
            if (index >= 0 && target >= 0 && target < parent.Children.Count)
            {
                parent.Children.Move(index, target);
            }

            roots = Collections.Where(candidate => candidate.Parent is null).ToList();
        }
        else
        {
            roots = Collections.Where(candidate => candidate.Parent is null).ToList();
            var index = roots.IndexOf(collection);
            var target = index + delta;
            if (index >= 0 && target >= 0 && target < roots.Count)
            {
                var item = roots[index];
                roots.RemoveAt(index);
                roots.Insert(target, item);
            }
        }

        SyncFlatCollectionOrder(roots);
    }

    private void SyncFlatCollectionOrder(IReadOnlyList<RequestCollection> roots) =>
        CollectionTree.SyncFlatOrder(Collections, roots);

    public bool MoveTreeNode(TreeDisplayNode source, RequestCollection target)
    {
        if (!CanMoveTreeNode(source, target))
        {
            return false;
        }

        if (source.Request is { } request)
        {
            request.Owner?.Requests.Remove(request);
            request.Owner = target;
            target.Requests.Add(request);
            target.IsExpanded = true;

            if (ReferenceEquals(SelectedRequest, request))
            {
                SelectedCollection = target;
            }

            ShowToast("已移动到集合");
        }
        else if (source.Collection is { } collection)
        {
            collection.Parent?.Children.Remove(collection);
            collection.Parent = target;
            target.Children.Add(collection);
            target.IsExpanded = true;
            ShowToast("已移动集合");
        }
        else
        {
            return false;
        }

        SyncRootCollections();
        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        RefreshAuthState();
        return true;
    }

    public bool MoveTreeNode(
        TreeDisplayNode source,
        TreeDisplayNode target,
        TreeDropPosition position
    )
    {
        if (!CanMoveTreeNode(source, target, position))
        {
            return false;
        }

        var moved = source switch
        {
            { Request: { } request } => MoveRequestNode(request, target, position),
            { Collection: { } collection } => MoveCollectionNode(collection, target, position),
            _ => false,
        };

        if (!moved)
        {
            return false;
        }

        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        RefreshAuthState();
        return true;
    }

    private bool MoveRequestNode(
        RequestItem request,
        TreeDisplayNode target,
        TreeDropPosition position
    )
    {
        RequestCollection destination;
        int index;

        if (target.Request is { } targetRequest)
        {
            if (targetRequest.Owner is not { } owner)
            {
                return false;
            }

            destination = owner;
            var targetIndex = destination.Requests.IndexOf(targetRequest);
            if (targetIndex < 0)
            {
                return false;
            }

            index = targetIndex + (position == TreeDropPosition.After ? 1 : 0);
        }
        else if (target.Collection is { } collection)
        {
            destination = collection;
            index = position switch
            {
                TreeDropPosition.Before => 0,
                _ => collection.Requests.Count,
            };
        }
        else
        {
            return false;
        }

        var oldOwner = request.Owner;
        var oldIndex = oldOwner?.Requests.IndexOf(request) ?? -1;
        if (ReferenceEquals(oldOwner, destination))
        {
            if (oldIndex >= 0)
            {
                if (index > oldIndex)
                {
                    index--;
                }

                destination.Requests.Move(
                    oldIndex,
                    Math.Clamp(index, 0, destination.Requests.Count - 1)
                );
            }
        }
        else
        {
            oldOwner?.Requests.Remove(request);
            request.Owner = destination;
            destination.Requests.Insert(Math.Clamp(index, 0, destination.Requests.Count), request);
        }

        destination.IsExpanded = true;
        if (ReferenceEquals(SelectedRequest, request))
        {
            SelectedCollection = destination;
        }

        ShowToast(ReferenceEquals(oldOwner, destination) ? "已调整请求顺序" : "已移动请求");
        return true;
    }

    private bool MoveCollectionNode(
        RequestCollection collection,
        TreeDisplayNode target,
        TreeDropPosition position
    )
    {
        if (target.Collection is not { } targetCollection)
        {
            return false;
        }

        var oldParent = collection.Parent;
        RequestCollection? newParent;
        int index;

        if (position == TreeDropPosition.Inside)
        {
            newParent = targetCollection;
            index = targetCollection.Children.Count;
        }
        else
        {
            newParent = targetCollection.Parent;
            var siblings = newParent is null
                ? Collections.Where(candidate => candidate.Parent is null).ToList()
                : newParent.Children.ToList();
            var targetIndex = siblings.IndexOf(targetCollection);
            if (targetIndex < 0)
            {
                return false;
            }

            index = targetIndex + (position == TreeDropPosition.After ? 1 : 0);
        }

        if (ReferenceEquals(oldParent, newParent))
        {
            var siblings = newParent is null
                ? Collections.Where(candidate => candidate.Parent is null).ToList()
                : newParent.Children.ToList();
            var oldIndex = siblings.IndexOf(collection);
            if (oldIndex < 0)
            {
                return false;
            }

            if (index > oldIndex)
            {
                index--;
            }

            if (newParent is null)
            {
                siblings.RemoveAt(oldIndex);
                siblings.Insert(Math.Clamp(index, 0, siblings.Count), collection);
                SyncFlatCollectionOrder(siblings);
            }
            else
            {
                newParent.Children.Move(
                    oldIndex,
                    Math.Clamp(index, 0, newParent.Children.Count - 1)
                );
            }
        }
        else
        {
            oldParent?.Children.Remove(collection);
            collection.Parent = newParent;

            if (newParent is not null)
            {
                newParent.Children.Insert(
                    Math.Clamp(index, 0, newParent.Children.Count),
                    collection
                );
                newParent.IsExpanded = true;
            }
            else
            {
                var roots = Collections.Where(candidate => candidate.Parent is null).ToList();
                roots.Remove(collection);
                roots.Insert(Math.Clamp(index, 0, roots.Count), collection);
                SyncFlatCollectionOrder(roots);
            }
        }

        ShowToast(ReferenceEquals(oldParent, newParent) ? "已调整集合顺序" : "已移动集合");
        SyncRootCollections();
        return true;
    }

    [RelayCommand]
    private void ConfirmDelete()
    {
        var deleted = _pendingDelete switch
        {
            RequestItem request => DeleteRequest(request.Id, "UI"),
            RequestCollection collection => DeleteCollection(collection.Id, "UI"),
            _ => false,
        };

        _pendingDelete = null;
        IsDeleteDialogOpen = false;
        ShowToast(deleted ? "已移入集合回收站" : "没有找到要删除的集合或请求，它可能已被删除");
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
        if (SelectedRequest is { } request)
        {
            RequestRenameNode(request);
            return;
        }

        if (SelectedCollection is { } collection)
        {
            RequestRenameNode(collection);
            return;
        }

        ShowToast("请先选择集合或请求");
    }

    public void RequestRenameNode(object node)
    {
        switch (node)
        {
            case RequestItem request:
                OpenRequest(request);
                _pendingDelete = request;
                RenameDialogTitle = "重命名请求";
                RenameText = request.Name;
                break;
            case RequestCollection collection:
                SelectCollection(collection);
                _pendingDelete = collection;
                RenameDialogTitle = "重命名集合";
                RenameText = collection.Name;
                break;
            default:
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
        RefreshAuthState();
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

    // Edits apply as they are typed, and the dialog also closes on Esc or a click outside it, so a variable marked
    // secret here is masked in the audit whichever way it closes.
    partial void OnIsEnvironmentDialogOpenChanged(bool value)
    {
        if (!value)
        {
            ScrubAuditSecrets();
        }
    }

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
    private void ToggleHistoryExpanded() => IsHistoryExpanded = !IsHistoryExpanded;

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
        while (History.Count > 100)
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

    private void SelectHistoryEntry(HistoryEntry entry)
    {
        foreach (var candidate in History)
        {
            candidate.IsSelected = ReferenceEquals(candidate, entry);
        }
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

    public void ToggleCollection(RequestCollection collection, bool expanded)
    {
        collection.IsExpanded = expanded;
        NotifyTreeChanged();
    }

    public bool IsExpanded(RequestCollection collection) => collection.IsExpanded;

    private static bool IsSelfOrDescendant(RequestCollection child, RequestCollection ancestor)
    {
        for (var current = child; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    public void LoadHistory(HistoryEntry entry)
    {
        SelectHistoryEntry(entry);
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
        if (
            Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
        )
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

    /// <summary>Records a send made outside the window; <paramref name="stored"/> is already masked.</summary>
    public void RecordExternalSend(
        RequestItem request,
        ComposedRequest? stored,
        HttpResult result,
        string source,
        string? tool,
        string? client,
        IReadOnlyList<string> secrets
    )
    {
        AddHistory(request, result);
        RecordAudit(request, stored, result, source, tool, client, secrets);
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

        var lines = request.FormFields.ActiveRows.Select(row => $"{row.Key}: {row.Value}").ToList();
        if (request.UploadFiles.Count > 0)
        {
            lines.AddRange(
                request.UploadFiles.Select(file =>
                    $"[file] {file.FieldName}: {file.FilePath} ({file.SizeText})"
                )
            );
        }
        else if (!string.IsNullOrWhiteSpace(request.UploadFilePath))
        {
            lines.Add($"[file] {request.UploadFieldName}: {request.UploadFilePath}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Stores a record of a send and returns it. <paramref name="stored"/> is the sent request already masked with
    /// <see cref="SecretMasker.MaskComposed"/> before the send, or null when nothing was sent.
    /// </summary>
    private AuditEntry RecordAudit(
        RequestItem request,
        ComposedRequest? stored,
        HttpResult result,
        string source,
        string? tool,
        string? client,
        IReadOnlyList<string> secrets
    )
    {
        // The audit file and MCP readers must never see credentials, so everything stored is masked first.
        var requestHeaders = stored is not null
            ? string.Join(
                "\n",
                stored
                    .Headers.Select(pair => $"{pair.Key}: {pair.Value}")
                    .Concat(
                        stored.ContentType is null
                            ? Array.Empty<string>()
                            : new[] { $"Content-Type: {stored.ContentType}" }
                    )
            )
            : string.Join(
                "\n",
                request.Headers.ActiveRows.Select(row =>
                    $"{row.Key}: {SecretMasker.MaskHeaderValue(row.Key, row.Value, secrets)}"
                )
            );

        var entry = new AuditEntry
        {
            Source = source,
            Tool = tool,
            Client = client,
            RequestId = request.Id,
            RequestName = request.Name,
            Method = stored?.Method ?? request.Method,
            Url = stored?.Url ?? SecretMasker.MaskSecrets(request.Url, secrets),
            Environment = stored?.EnvironmentName ?? ActiveEnvironment?.Name,
            StatusCode = result.StatusCode,
            StatusText = SecretMasker.MaskSecrets(result.ReasonPhrase, secrets),
            ElapsedMs = result.ElapsedMs,
            SizeBytes = result.SizeBytes,
            IsSuccess = result.HasResponse && result.StatusCode < 400,
            Error = result.Error is null ? null : SecretMasker.MaskSecrets(result.Error, secrets),
            Curl = stored is null ? null : AuditEntry.LimitCurl(RequestComposer.BuildCurl(stored)),
            RequestHeaders = Truncate(requestHeaders, AuditPreviewLimit),
            RequestBodyPreview = Truncate(
                stored?.Body ?? SecretMasker.MaskSecrets(BuildMultipartPreview(request), secrets),
                AuditPreviewLimit
            ),
            ResponseHeaders = Truncate(
                SecretMasker.MaskHeaderLines(
                    string.Join(
                        "\n",
                        result.Headers.Select(header => $"{header.Key}: {header.Value}")
                    ),
                    secrets
                ),
                AuditPreviewLimit
            ),
            ResponseBodyPreview = SecretMasker.MaskPreview(result.Body, AuditPreviewLimit, secrets),
            ResponseContentType = SecretMasker.MaskSecrets(result.MediaType, secrets),
            IsSse = result.IsSse,
            SseEventCount = result.SseEventCount,
            AiThinking = AuditEntry.LimitAiText(
                SecretMasker.MaskSecrets(result.SseThinking, secrets)
            ),
            AiContent = AuditEntry.LimitAiText(
                SecretMasker.MaskSecrets(result.SseContent, secrets)
            ),
        };

        WireAuditEntry(entry);
        Audit.Insert(0, entry);
        if (_auditLimit > 0)
        {
            while (Audit.Count > _auditLimit)
            {
                Audit.RemoveAt(Audit.Count - 1);
            }
        }

        PersistAudit();
        return entry;
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
                || (entry.Tool?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            );
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
        if (Audit.Count == 0)
        {
            ShowToast("审计记录为空");
            return;
        }

        AuditTrashConfirmTitle = "清空审计记录？";
        AuditTrashConfirmDescription =
            $"将把 {Audit.Count} 条审计记录移入审计回收站，可在回收站恢复或永久删除。";
        IsAuditTrashConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmAuditTrash()
    {
        for (var index = Audit.Count - 1; index >= 0; index--)
        {
            MoveAuditToTrash(Audit[index]);
        }

        Audit.Clear();
        IsAuditTrashConfirmOpen = false;
        PersistAudit();
        ShowToast("审计记录已移入回收站");
    }

    [RelayCommand]
    private void CancelAuditTrash() => IsAuditTrashConfirmOpen = false;

    [RelayCommand]
    private void OpenAuditTrash() => IsAuditTrashOpen = true;

    [RelayCommand]
    private void CloseAuditTrash() => IsAuditTrashOpen = false;

    private void DeleteAuditEntry(AuditEntry entry)
    {
        if (!Audit.Remove(entry))
        {
            return;
        }

        MoveAuditToTrash(entry);
        if (ReferenceEquals(SelectedAuditEntry, entry))
        {
            SelectedAuditEntry = Audit.FirstOrDefault();
        }

        PersistAudit();
        ShowToast("审计记录已移入回收站");
    }

    private void MoveAuditToTrash(AuditEntry entry)
    {
        WireAuditTrashEntry(entry);
        AuditTrash.Insert(0, entry);
        while (AuditTrash.Count > 1000)
        {
            AuditTrash.RemoveAt(AuditTrash.Count - 1);
        }
    }

    private void WireAuditEntry(AuditEntry entry)
    {
        entry.DeleteCommand = new RelayCommand(() => DeleteAuditEntry(entry));
    }

    private void WireAuditTrashEntry(AuditEntry entry)
    {
        entry.DeleteCommand = null;
        entry.RestoreCommand = new RelayCommand(() => RestoreAudit(entry));
        entry.PurgeCommand = new RelayCommand(() => RequestPurgeAudit(entry));
    }

    private void RestoreAudit(AuditEntry entry)
    {
        if (!AuditTrash.Remove(entry))
        {
            return;
        }

        WireAuditEntry(entry);
        Audit.Insert(0, entry);
        PersistAudit();
        ShowToast("审计记录已恢复");
    }

    private void RequestPurgeAudit(AuditEntry entry)
    {
        _auditPurgeAllPending = false;
        _pendingAuditPurge = entry;
        AuditPurgeTitle = "永久删除？";
        AuditPurgeDescription = $"“{entry.RequestName}”将从审计回收站永久删除，无法恢复。";
        IsAuditPurgeConfirmOpen = true;
    }

    [RelayCommand]
    private void EmptyAuditTrash()
    {
        if (AuditTrash.Count == 0)
        {
            ShowToast("审计回收站为空");
            return;
        }

        _auditPurgeAllPending = true;
        _pendingAuditPurge = null;
        AuditPurgeTitle = "清空审计回收站？";
        AuditPurgeDescription = "审计回收站中的所有记录将被永久删除，无法恢复。";
        IsAuditPurgeConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmAuditPurge()
    {
        if (_auditPurgeAllPending)
        {
            AuditTrash.Clear();
            ShowToast("审计回收站已清空");
        }
        else if (_pendingAuditPurge is { } entry)
        {
            AuditTrash.Remove(entry);
            ShowToast("审计记录已永久删除");
        }

        _pendingAuditPurge = null;
        _auditPurgeAllPending = false;
        IsAuditPurgeConfirmOpen = false;
        PersistAudit();
    }

    [RelayCommand]
    private void CancelAuditPurge()
    {
        _pendingAuditPurge = null;
        _auditPurgeAllPending = false;
        IsAuditPurgeConfirmOpen = false;
    }

    /// <summary>Returns false when either audit file could not be written.</summary>
    /// <summary>
    /// Writes audit records to the SQLite database. Failed when either table could not be written.
    /// </summary>
    private SaveResult PersistAudit()
    {
        var saved = _auditStore.Save(Audit);
        var trashSaved = _auditStore.SaveTrash(AuditTrash);
        if (saved == SaveResult.Failed || trashSaved == SaveResult.Failed)
        {
            DataNotice.Warn(
                "审计记录保存失败：无法写入审计数据库 postsharp.db，新的审计记录暂时只在界面中。请检查数据目录是否可写；下次发送请求时会自动重试。"
            );
            return SaveResult.Failed;
        }

        return saved == SaveResult.Saved && trashSaved == SaveResult.Saved
            ? SaveResult.Saved
            : SaveResult.Blocked;
    }

    /// <summary>Masks stored audit records again when a variable may have become secret.</summary>
    public void ScrubAuditSecrets() => _auditScrubber.Scrub();

    public SaveResult SaveSettingsForMcp()
    {
        var saved = _settingsStore.Save(Settings);
        Persist();
        return saved;
    }

    public EnvironmentModel CreateEnvironmentForMcp(string name)
    {
        var environment = new EnvironmentModel
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? $"新环境 {Environments.Count + 1}"
                : name.Trim(),
        };
        environment.Variables.AddRow("baseUrl", _mockServer.BaseUrl);
        environment.Variables.EnsureTrailingRow();
        Environments.Add(environment);
        ActiveEnvironment = environment;
        NotifyWorkspaceChanged();
        return environment;
    }

    public EnvironmentModel RenameEnvironmentForMcp(Guid id, string name)
    {
        var environment =
            Environments.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new KeyNotFoundException($"没有找到 id 为 {id} 的环境。");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("环境名称不能为空。");
        }

        environment.Name = name.Trim();
        NotifyWorkspaceChanged();
        return environment;
    }

    public bool ActivateEnvironmentForMcp(Guid id)
    {
        var environment = Environments.FirstOrDefault(candidate => candidate.Id == id);
        if (environment is null)
        {
            return false;
        }

        ActiveEnvironment = environment;
        NotifyWorkspaceChanged();
        return true;
    }

    public bool DeleteEnvironmentForMcp(Guid id)
    {
        if (Environments.Count <= 1)
        {
            throw new InvalidOperationException("至少保留一个环境。");
        }

        var environment = Environments.FirstOrDefault(candidate => candidate.Id == id);
        if (environment is null)
        {
            return false;
        }

        Environments.Remove(environment);
        if (ReferenceEquals(ActiveEnvironment, environment))
        {
            ActiveEnvironment = Environments.FirstOrDefault();
        }

        NotifyWorkspaceChanged();
        return true;
    }

    public bool DeleteVariableForMcp(Guid? environmentId, string key)
    {
        var environment = environmentId is { } requested
            ? Environments.FirstOrDefault(candidate => candidate.Id == requested)
            : ActiveEnvironment;
        if (environment is null)
        {
            return false;
        }

        var row = environment.Variables.FirstOrDefault(candidate =>
            candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
        );
        if (row is null)
        {
            return false;
        }

        environment.Variables.Remove(row);
        environment.Variables.EnsureTrailingRow();
        NotifyWorkspaceChanged();
        return true;
    }

    public int MoveAllHistoryToTrash()
    {
        var count = History.Count;
        for (var index = History.Count - 1; index >= 0; index--)
        {
            MoveToRecycleBin(History[index]);
        }

        History.Clear();
        if (count > 0)
        {
            Persist();
        }

        return count;
    }

    public bool DeleteHistoryForMcp(DateTimeOffset timestamp)
    {
        var entry = History.FirstOrDefault(candidate => candidate.Timestamp == timestamp);
        if (entry is null)
        {
            return false;
        }

        History.Remove(entry);
        MoveToRecycleBin(entry);
        Persist();
        return true;
    }

    public bool RestoreHistoryForMcp(DateTimeOffset timestamp)
    {
        var entry = RecycledHistory.FirstOrDefault(candidate => candidate.Timestamp == timestamp);
        if (entry is null)
        {
            return false;
        }

        RestoreFromRecycleBin(entry);
        return true;
    }

    public bool PurgeHistoryForMcp(DateTimeOffset timestamp)
    {
        var entry = RecycledHistory.FirstOrDefault(candidate => candidate.Timestamp == timestamp);
        if (entry is null)
        {
            return false;
        }

        RecycledHistory.Remove(entry);
        Persist();
        return true;
    }

    public int EmptyHistoryTrashForMcp()
    {
        var count = RecycledHistory.Count;
        RecycledHistory.Clear();
        if (count > 0)
        {
            Persist();
        }

        return count;
    }

    public int MoveAllAuditToTrash()
    {
        var count = Audit.Count;
        for (var index = Audit.Count - 1; index >= 0; index--)
        {
            MoveAuditToTrash(Audit[index]);
        }

        Audit.Clear();
        if (count > 0)
        {
            PersistAudit();
        }

        return count;
    }

    public bool DeleteAuditForMcp(Guid id)
    {
        var entry = Audit.FirstOrDefault(candidate => candidate.Id == id);
        if (entry is null)
        {
            return false;
        }

        DeleteAuditEntry(entry);
        return true;
    }

    public bool RestoreAuditForMcp(Guid id)
    {
        var entry = AuditTrash.FirstOrDefault(candidate => candidate.Id == id);
        if (entry is null)
        {
            return false;
        }

        RestoreAudit(entry);
        return true;
    }

    public bool PurgeAuditForMcp(Guid id)
    {
        var entry = AuditTrash.FirstOrDefault(candidate => candidate.Id == id);
        if (entry is null)
        {
            return false;
        }

        AuditTrash.Remove(entry);
        PersistAudit();
        return true;
    }

    public int EmptyAuditTrashForMcp()
    {
        var count = AuditTrash.Count;
        AuditTrash.Clear();
        if (count > 0)
        {
            PersistAudit();
        }

        return count;
    }

    [RelayCommand]
    private void OpenSelectedAuditRequest()
    {
        if (SelectedAuditEntry?.RequestId is not { } id)
        {
            ShowToast("这条审计记录没有关联请求");
            return;
        }

        var request = Collections
            .SelectMany(collection => collection.Requests)
            .FirstOrDefault(candidate => candidate.Id == id);
        if (request is null)
        {
            ShowToast("原请求已被删除");
            return;
        }

        IsManagementOpen = false;
        OpenRequest(request);
    }

    /// <summary>Raised when the suite editor should open; the window owns the editor window lifetime.</summary>
    public event EventHandler<RequestCollection?>? SuiteEditorRequested;

    public WorkflowEditorViewModel CreateWorkflowEditorViewModel(
        TestSuite? draft = null,
        TestSuite? existing = null
    ) =>
        new(Collections, draft)
        {
            SuiteRunner = RunSuiteDraftAsync,
            ValidationEnvironment = ActiveEnvironment,
            RunRecorder = existing is null
                ? null
                : result => RecordEditorRunAsync(existing, result),
        };

    public TestSuite CreateSuiteEditorDraft(RequestCollection? defaultCollection)
    {
        var suite = new TestSuite
        {
            Name = $"套件 {Suites.Count + 1}",
            StopOnError = true,
            ScheduleMinutes = 0,
            MaxParallelism = 4,
        };
        if (defaultCollection is null)
        {
            return suite;
        }

        foreach (var request in FlattenCollectionRequests(defaultCollection))
        {
            suite.Steps.Add(
                new TestStep
                {
                    RequestId = request.Id,
                    RequestName = request.Name,
                    Method = request.Method,
                    Url = request.Url,
                    DelayMs = 200,
                }
            );
        }

        suite.Workflow = SuiteWorkflowFactory.CreateLinear(suite.Steps);
        return suite;
    }

    public TestSuite CreateSuiteDraftForEditing(TestSuite suite) => CloneSuite(suite);

    public void ApplySuiteEditorResult(TestSuite incoming, TestSuite? existing)
    {
        if (existing is null)
        {
            Suites.Add(incoming);
            SelectedSuite = incoming;
            SelectedSuiteResult = null;
            Persist();
            ShowToast($"已创建套件（{incoming.Steps.Count} 个步骤）");
            return;
        }

        existing.Name = incoming.Name;
        existing.StopOnError = incoming.StopOnError;
        existing.RunInParallel = incoming.RunInParallel;
        existing.ScheduleMinutes = incoming.ScheduleMinutes;
        existing.MaxParallelism = incoming.MaxParallelism;
        existing.Steps.Clear();
        existing.Steps.AddRange(incoming.Steps);
        existing.Workflow = incoming.Workflow;
        foreach (var run in incoming.Runs)
        {
            if (existing.Runs.All(candidate => candidate.Id != run.Id))
            {
                existing.Runs.Insert(0, run);
            }
        }

        while (existing.Runs.Count > TestSuite.RunHistoryLimit)
        {
            existing.Runs.RemoveAt(existing.Runs.Count - 1);
        }

        WireSuiteRunCommands(existing);
        SelectedSuite = null;
        SelectedSuite = existing;
        Persist();
        ShowToast($"套件已更新（{existing.Steps.Count} 个步骤）");
    }

    [RelayCommand]
    private void OpenSuiteBuilder() => SuiteEditorRequested?.Invoke(this, null);

    /// <summary>Opens the suite editor with a specific collection preselected (used by the tree context menu).</summary>
    public void OpenSuiteBuilderForCollection(RequestCollection collection)
    {
        if (collection.TotalRequestCount > 200)
        {
            ShowToast(
                $"集合「{collection.Name}」包含 {collection.TotalRequestCount} 个请求，请先在左侧展开选择具体接口"
            );
            SuiteEditorRequested?.Invoke(this, null);
            return;
        }

        SuiteEditorRequested?.Invoke(this, collection);
    }

    private static IReadOnlyList<RequestItem> FlattenCollectionRequests(
        RequestCollection collection
    )
    {
        var requests = new List<RequestItem>();
        requests.AddRange(collection.Requests);
        foreach (var child in collection.Children)
        {
            requests.AddRange(FlattenCollectionRequests(child));
        }

        return requests;
    }

    private static TestSuite CloneSuite(TestSuite source)
    {
        var clone = new TestSuite
        {
            Name = source.Name,
            StopOnError = source.StopOnError,
            RunInParallel = source.RunInParallel,
            ScheduleMinutes = source.ScheduleMinutes,
            MaxParallelism = source.MaxParallelism,
            LastSummary = source.LastSummary,
        };
        foreach (var step in source.Steps)
        {
            var copy = new TestStep
            {
                Id = step.Id,
                RequestId = step.RequestId,
                RequestName = step.RequestName,
                Method = step.Method,
                Url = step.Url,
                DelayMs = step.DelayMs,
                RetryCount = step.RetryCount,
                RetryDelayMs = step.RetryDelayMs,
                Condition = step.Condition,
                JoinMode = step.JoinMode,
                ParallelGroup = step.ParallelGroup,
                ExpectedStatus = step.ExpectedStatus,
                BodyContains = step.BodyContains,
            };
            foreach (var rule in step.ConditionRules.Rules)
            {
                copy.ConditionRules.Rules.Add(
                    new SuiteConditionRule
                    {
                        Source = rule.Source,
                        Path = rule.Path,
                        Operator = rule.Operator,
                        Expected = rule.Expected,
                    }
                );
            }

            copy.ConditionRules.Mode = step.ConditionRules.Mode;
            foreach (var mapping in step.InputMappings)
            {
                copy.InputMappings.Add(
                    new InputMapping
                    {
                        SourceStepId = mapping.SourceStepId,
                        SourcePath = mapping.SourcePath,
                        TargetKind = mapping.TargetKind,
                        TargetPath = mapping.TargetPath,
                        Template = mapping.Template,
                    }
                );
            }

            foreach (var assertion in step.Assertions)
            {
                copy.Assertions.Add(
                    new TestAssertion
                    {
                        Kind = assertion.Kind,
                        Path = assertion.Path,
                        Expected = assertion.Expected,
                    }
                );
            }

            clone.Steps.Add(copy);
        }

        if (source.Workflow is { } workflow)
        {
            clone.Workflow = new SuiteWorkflow();
            foreach (var node in workflow.Nodes)
            {
                clone.Workflow.Nodes.Add(
                    new SuiteWorkflowNode
                    {
                        Id = node.Id,
                        StepId = node.StepId,
                        Kind = node.Kind,
                        CollectionId = node.CollectionId,
                        RequestId = node.RequestId,
                        X = node.X,
                        Y = node.Y,
                        JoinMode = node.JoinMode,
                    }
                );
            }

            foreach (var edge in workflow.Edges)
            {
                var copy = new SuiteWorkflowEdge
                {
                    SourceId = edge.SourceId,
                    TargetId = edge.TargetId,
                    Condition = edge.Condition,
                };
                copy.Rules.Mode = edge.Rules.Mode;
                foreach (var rule in edge.Rules.Rules)
                {
                    copy.Rules.Rules.Add(
                        new SuiteConditionRule
                        {
                            Source = rule.Source,
                            Path = rule.Path,
                            Operator = rule.Operator,
                            Expected = rule.Expected,
                        }
                    );
                }

                clone.Workflow.Edges.Add(copy);
            }
        }

        return clone;
    }

    public SuiteValidationResult ValidateSuiteForMcp(Guid suiteId)
    {
        var suite =
            Suites.FirstOrDefault(candidate => candidate.Id == suiteId)
            ?? throw new KeyNotFoundException($"没有找到 id 为 {suiteId} 的套件。");
        return ValidateSuite(suite);
    }

    private SuiteValidationResult ValidateSuite(TestSuite suite) =>
        SuitePreflight.Validate(suite, Collections, ActiveEnvironment);

    private void ShowSuiteValidation(SuiteValidationResult validation)
    {
        SelectedSuiteValidationIssues.Clear();
        foreach (var issue in validation.Issues)
        {
            SelectedSuiteValidationIssues.Add(issue);
        }

        IsSuiteValidationVisible = validation.Issues.Count > 0;
        OnPropertyChanged(nameof(SuiteValidationSummary));
    }

    private bool EnsureSuiteValid(TestSuite suite)
    {
        var validation = ValidateSuite(suite);
        ShowSuiteValidation(validation);
        if (!validation.HasErrors)
        {
            return true;
        }

        ShowToast($"{validation.Summary}，请先处理预检问题");
        return false;
    }

    private void ClearSuiteValidation()
    {
        SelectedSuiteValidationIssues.Clear();
        IsSuiteValidationVisible = false;
    }

    [RelayCommand]
    private void DismissSuiteValidation() => IsSuiteValidationVisible = false;

    private static TestRunResult PreflightFailure(
        TestSuite suite,
        SuiteValidationResult validation
    ) =>
        new()
        {
            SuiteId = suite.Id,
            SuiteName = suite.Name,
            Total = 0,
            NotRun = suite.Steps.Count,
            Stopped = true,
            Error = validation.Summary,
        };

    private void WireSuiteRunCommands(TestSuite suite)
    {
        foreach (var run in suite.Runs)
        {
            foreach (var result in run.Steps)
            {
                result.ContinueCommand = new AsyncRelayCommand(() =>
                    ContinueFromStepAsync(suite, result)
                );
            }
        }
    }

    private Task RecordEditorRunAsync(TestSuite suite, TestRunResult result)
    {
        if (result.Error is not null)
        {
            return Task.CompletedTask;
        }

        result.SuiteId = suite.Id;
        result.SuiteName = suite.Name;
        if (suite.Runs.All(run => run.Id != result.Id))
        {
            suite.Runs.Insert(0, result);
            while (suite.Runs.Count > TestSuite.RunHistoryLimit)
            {
                suite.Runs.RemoveAt(suite.Runs.Count - 1);
            }
        }

        WireSuiteRunCommands(suite);
        suite.LastSummary = result.Summary;
        if (SelectedSuite?.Id == suite.Id)
        {
            SelectedSuiteResult = result;
        }

        Persist();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void DeleteSelectedSuite()
    {
        if (SelectedSuite is not { } suite)
        {
            return;
        }

        Suites.Remove(suite);
        SelectedSuite = Suites.FirstOrDefault();
        ClearSuiteValidation();
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

        if (HasActiveSuiteRunJob())
        {
            ShowToast("MCP 后台任务正在运行，请等待或先取消");
            return;
        }

        if (!EnsureSuiteValid(suite))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _suiteCts = cancellation;
        try
        {
            IsRunningSuite = true;
            var result = await RunSuiteCoreAsync(
                suite,
                "UI",
                $"suite:{suite.Name}",
                null,
                cancellation.Token
            );
            SelectedSuiteResult = result;
            ShowToast(
                result.Cancelled ? $"套件已取消：{result.Outcome}" : $"套件完成：{result.Outcome}"
            );
        }
        finally
        {
            IsRunningSuite = false;
            _suiteCts = null;
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private async Task RerunFailedAsync()
    {
        if (SelectedSuite is not { } suite || SelectedSuiteResult is not { } run || IsRunningSuite)
        {
            return;
        }

        var stepIds = SuiteRunPlanner.SelectFailedWithDependencies(suite, run);
        if (stepIds.Count == 0)
        {
            ShowToast("本次运行没有失败步骤");
            return;
        }

        await RunSuiteSubsetAsync(suite, stepIds, "重跑失败及依赖");
    }

    private async Task ContinueFromStepAsync(TestSuite suite, TestStepResult stepResult)
    {
        if (IsRunningSuite)
        {
            ShowToast("套件正在运行，请等待当前运行结束");
            return;
        }

        var stepIds = SuiteRunPlanner.SelectFromStepWithDependencies(suite, stepResult.StepId);
        if (stepIds.Count == 0)
        {
            ShowToast("该步骤已从套件中移除，无法继续运行");
            return;
        }

        await RunSuiteSubsetAsync(suite, stepIds, $"从「{stepResult.Name}」继续");
    }

    private async Task RunSuiteSubsetAsync(
        TestSuite owner,
        IReadOnlySet<Guid> stepIds,
        string actionLabel
    )
    {
        if (HasActiveSuiteRunJob())
        {
            ShowToast("MCP 后台任务正在运行，请等待或先取消");
            return;
        }

        var subset = SuiteRunPlanner.CreateSubset(owner, stepIds);
        if (subset.Steps.Count == 0)
        {
            ShowToast("所选步骤已不存在，无法运行");
            return;
        }

        if (!EnsureSuiteValid(subset))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _suiteCts = cancellation;
        try
        {
            IsRunningSuite = true;
            var result = await RunSuiteCoreAsync(
                subset,
                "UI",
                "suite:retry",
                null,
                cancellation.Token,
                forceWorkflow: true,
                historySuite: owner
            );
            SelectedSuiteResult = result;
            ShowToast(
                result.Cancelled
                    ? $"{actionLabel}已取消：{result.Outcome}"
                    : $"{actionLabel}完成：{result.Outcome}"
            );
        }
        finally
        {
            IsRunningSuite = false;
            _suiteCts = null;
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void CancelSuiteRun() => _suiteCts?.Cancel();

    public string RunSuiteButtonText => IsRunningSuite ? "运行中…" : "运行套件";

    partial void OnIsRunningSuiteChanged(bool value) =>
        OnPropertyChanged(nameof(RunSuiteButtonText));

    public async Task<TestRunResult> RunSuiteForMcpAsync(
        Guid suiteId,
        string? client,
        CancellationToken cancellationToken
    )
    {
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var suite =
                Suites.FirstOrDefault(candidate => candidate.Id == suiteId)
                ?? throw new KeyNotFoundException($"没有找到 id 为 {suiteId} 的套件。");
            var validation = ValidateSuite(suite);
            ShowSuiteValidation(validation);
            if (validation.HasErrors)
            {
                throw new InvalidOperationException(
                    $"{validation.Summary}，请先在桌面端修复套件。"
                );
            }

            return await RunSuiteCoreAsync(suite, "MCP", "run_suite", client, cancellationToken);
        });
    }

    public async Task<TestRunResult> RerunFailedSuiteForMcpAsync(
        Guid suiteId,
        Guid runId,
        string? client,
        CancellationToken cancellationToken
    )
    {
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var suite =
                Suites.FirstOrDefault(candidate => candidate.Id == suiteId)
                ?? throw new KeyNotFoundException($"没有找到 id 为 {suiteId} 的套件。");
            var run =
                suite.Runs.FirstOrDefault(candidate => candidate.Id == runId)
                ?? throw new KeyNotFoundException($"套件中没有 id 为 {runId} 的运行记录。");
            var stepIds = SuiteRunPlanner.SelectFailedWithDependencies(suite, run);
            if (stepIds.Count == 0)
            {
                throw new InvalidOperationException("本次运行没有失败步骤。");
            }

            return await RunSuiteSubsetForMcpAsync(
                suite,
                stepIds,
                "rerun_failed_suite",
                client,
                cancellationToken
            );
        });
    }

    public async Task<TestRunResult> ContinueSuiteFromStepForMcpAsync(
        Guid suiteId,
        Guid runId,
        Guid stepId,
        string? client,
        CancellationToken cancellationToken
    )
    {
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var suite =
                Suites.FirstOrDefault(candidate => candidate.Id == suiteId)
                ?? throw new KeyNotFoundException($"没有找到 id 为 {suiteId} 的套件。");
            var run =
                suite.Runs.FirstOrDefault(candidate => candidate.Id == runId)
                ?? throw new KeyNotFoundException($"套件中没有 id 为 {runId} 的运行记录。");
            if (run.Steps.All(step => step.StepId != stepId))
            {
                throw new KeyNotFoundException($"运行记录中没有 id 为 {stepId} 的步骤。");
            }

            var stepIds = SuiteRunPlanner.SelectFromStepWithDependencies(suite, stepId);
            if (stepIds.Count == 0)
            {
                throw new InvalidOperationException("该步骤已从套件中移除，无法继续运行。");
            }

            return await RunSuiteSubsetForMcpAsync(
                suite,
                stepIds,
                "continue_suite_from_step",
                client,
                cancellationToken
            );
        });
    }

    private async Task<TestRunResult> RunSuiteSubsetForMcpAsync(
        TestSuite owner,
        IReadOnlySet<Guid> stepIds,
        string tool,
        string? client,
        CancellationToken cancellationToken
    )
    {
        if (IsRunningSuite)
        {
            throw new InvalidOperationException("桌面端正在运行套件，请等待当前运行结束。");
        }

        var subset = SuiteRunPlanner.CreateSubset(owner, stepIds);
        if (subset.Steps.Count == 0)
        {
            throw new InvalidOperationException("所选步骤已不存在，无法运行。");
        }

        var validation = ValidateSuite(subset);
        if (validation.HasErrors)
        {
            throw new InvalidOperationException($"{validation.Summary}，请先在桌面端修复套件。");
        }

        return await RunSuiteCoreAsync(
            subset,
            "MCP",
            tool,
            client,
            cancellationToken,
            forceWorkflow: true,
            historySuite: owner
        );
    }

    public SuiteRunJob StartSuiteRunForMcp(Guid suiteId, string? client)
    {
        var suite =
            Suites.FirstOrDefault(candidate => candidate.Id == suiteId)
            ?? throw new KeyNotFoundException($"没有找到 id 为 {suiteId} 的套件。");
        if (IsRunningSuite)
        {
            throw new InvalidOperationException("桌面端正在运行套件，请等待当前运行结束。");
        }

        if (
            _suiteRunJobs.Values.Any(job =>
                job.SuiteId == suiteId && (job.Status == "Running" || job.Status == "Cancelling")
            )
        )
        {
            throw new InvalidOperationException("该套件已有一个 MCP 运行任务，请先等待或取消。");
        }

        SuiteWorkflowFactory.EnsureLinear(suite);
        var job = new SuiteRunJob
        {
            SuiteId = suite.Id,
            SuiteName = suite.Name,
            TotalSteps = suite.Steps.Count,
        };
        foreach (var step in suite.Steps)
        {
            job.NodeStates.Add(
                new SuiteRunNodeState
                {
                    StepId = step.Id,
                    Name = step.RequestName,
                    State = "Pending",
                }
            );
        }

        _suiteRunJobs[job.Id] = job;
        _ = RunSuiteJobAsync(job, suite, client);
        PruneSuiteRunJobs();
        return job;
    }

    public SuiteRunJob GetSuiteRunJobForMcp(Guid jobId)
    {
        return _suiteRunJobs.TryGetValue(jobId, out var job)
            ? job
            : throw new KeyNotFoundException($"没有找到 id 为 {jobId} 的运行任务。");
    }

    public bool CancelSuiteRunJobForMcp(Guid jobId)
    {
        if (!_suiteRunJobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        lock (job.SyncRoot)
        {
            if (job.Status != "Running" && job.Status != "Cancelling")
            {
                return false;
            }

            job.Status = "Cancelling";
            job.Summary = "正在取消…";
        }

        job.Cancellation.Cancel();
        return true;
    }

    private async Task RunSuiteJobAsync(SuiteRunJob job, TestSuite suite, string? client)
    {
        try
        {
            var result = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var validation = ValidateSuite(suite);
                lock (job.SyncRoot)
                {
                    job.ValidationIssues.Clear();
                    job.ValidationIssues.AddRange(validation.Issues);
                }

                if (validation.HasErrors)
                {
                    return PreflightFailure(suite, validation);
                }

                return await RunSuiteCoreAsync(
                    suite,
                    "MCP",
                    "start_suite_run",
                    client,
                    job.Cancellation.Token,
                    progress => ApplySuiteRunProgress(job, progress),
                    forceWorkflow: true
                );
            });

            lock (job.SyncRoot)
            {
                job.RunId = result.Error is null ? result.Id : null;
                job.Error = result.Error;
                job.Summary = result.Summary;
                job.CompletedSteps = result.Total;
                job.NodeStates.Clear();
                job.NodeStates.AddRange(result.NodeStates);
                job.EdgeStates.Clear();
                job.EdgeStates.AddRange(result.EdgeStates);
                job.Status =
                    result.Cancelled ? "Cancelled"
                    : result.Error is not null || result.Failed > 0 ? "Failed"
                    : "Completed";
                job.CompletedAt = DateTimeOffset.Now;
            }
        }
        catch (OperationCanceledException)
        {
            lock (job.SyncRoot)
            {
                job.Status = "Cancelled";
                job.Summary = "已取消";
                job.CompletedAt = DateTimeOffset.Now;
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "MCP 套件任务失败 {SuiteName}", suite.Name);
            lock (job.SyncRoot)
            {
                job.Status = "Failed";
                job.Error = exception.Message;
                job.Summary = "运行失败";
                job.CompletedAt = DateTimeOffset.Now;
            }
        }
        finally
        {
            PruneSuiteRunJobs();
        }
    }

    private void ApplySuiteRunProgress(SuiteRunJob job, SuiteWorkflowProgress progress)
    {
        lock (job.SyncRoot)
        {
            switch (progress.Kind)
            {
                case SuiteWorkflowProgressKind.StepStarted:
                case SuiteWorkflowProgressKind.StepCompleted:
                case SuiteWorkflowProgressKind.StepFailed:
                case SuiteWorkflowProgressKind.StepSkipped:
                case SuiteWorkflowProgressKind.StepNotRun:
                {
                    if (progress.StepId is not { } stepId)
                    {
                        break;
                    }

                    var node = job.NodeStates.FirstOrDefault(candidate =>
                        candidate.StepId == stepId
                    );
                    if (node is null)
                    {
                        node = new SuiteRunNodeState { StepId = stepId, Name = "步骤" };
                        job.NodeStates.Add(node);
                    }

                    node.State = progress.Kind switch
                    {
                        SuiteWorkflowProgressKind.StepStarted => "Running",
                        SuiteWorkflowProgressKind.StepCompleted => "Done",
                        SuiteWorkflowProgressKind.StepFailed => "Failed",
                        SuiteWorkflowProgressKind.StepSkipped => "Skipped",
                        _ => "NotRun",
                    };
                    node.ElapsedMs = progress.ElapsedMs;
                    node.Detail = progress.Detail;
                    job.CompletedSteps = job.NodeStates.Count(candidate =>
                        candidate.State is "Done" or "Failed" or "Skipped" or "NotRun"
                    );
                    job.Summary = $"正在运行 · {job.CompletedSteps}/{job.TotalSteps} 步完成";
                    break;
                }

                case SuiteWorkflowProgressKind.EdgeResolved:
                {
                    if (
                        progress.SourceStepId is not { } sourceStepId
                        || progress.TargetStepId is not { } targetStepId
                    )
                    {
                        break;
                    }

                    var sourceId = sourceStepId.ToString();
                    var targetId = targetStepId.ToString();
                    var edge = job.EdgeStates.FirstOrDefault(candidate =>
                        candidate.SourceId == sourceId && candidate.TargetId == targetId
                    );
                    if (edge is null)
                    {
                        edge = new SuiteRunEdgeState { SourceId = sourceId, TargetId = targetId };
                        job.EdgeStates.Add(edge);
                    }

                    edge.Active = progress.EdgeActive == true;
                    break;
                }
            }
        }
    }

    private bool HasActiveSuiteRunJob() =>
        _suiteRunJobs.Values.Any(job =>
        {
            lock (job.SyncRoot)
            {
                return job.Status == "Running" || job.Status == "Cancelling";
            }
        });

    private void PruneSuiteRunJobs()
    {
        var stale = _suiteRunJobs
            .Values.Where(job =>
            {
                lock (job.SyncRoot)
                {
                    return job.CompletedAt is not null;
                }
            })
            .OrderBy(job => job.StartedAt)
            .Take(Math.Max(0, _suiteRunJobs.Count - 20))
            .ToList();
        foreach (var job in stale)
        {
            _suiteRunJobs.TryRemove(job.Id, out _);
        }
    }

    public async Task<TestRunResult> RunSuiteDraftAsync(
        TestSuite suite,
        Action<SuiteWorkflowProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        var validation = ValidateSuite(suite);
        if (validation.HasErrors)
        {
            return PreflightFailure(suite, validation);
        }

        return await RunSuiteCoreAsync(
            suite,
            "UI",
            "suite:editor",
            null,
            cancellationToken,
            progress,
            forceWorkflow: true
        );
    }

    private async Task<TestRunResult> RunSuiteCoreAsync(
        TestSuite suite,
        string source,
        string tool,
        string? client,
        CancellationToken cancellationToken,
        Action<SuiteWorkflowProgress>? progress = null,
        bool forceWorkflow = false,
        TestSuite? historySuite = null
    )
    {
        var validation = ValidateSuite(suite);
        if (validation.HasErrors)
        {
            return PreflightFailure(suite, validation);
        }

        if (
            suite.Workflow is { Nodes.Count: > 0 } workflow
            && workflow.Nodes.Any(node => suite.Steps.Any(step => step.Id == node.StepId))
        )
        {
            return await RunSuiteWorkflowAsync(
                suite,
                workflow,
                source,
                tool,
                client,
                cancellationToken,
                progress,
                historySuite
            );
        }

        if (
            !forceWorkflow
            && suite.RunInParallel
            && suite.Steps.All(step => step.InputMappings.Count == 0 && step.Condition == "Always")
        )
        {
            return await RunSuiteInParallelAsync(
                suite,
                source,
                tool,
                client,
                cancellationToken,
                historySuite
            );
        }

        var steps = new List<TestStepResult>();
        var stopped = false;
        var cancelled = false;
        var stepResults = new Dictionary<Guid, HttpResult>();

        for (var index = 0; index < suite.Steps.Count; index++)
        {
            var step = suite.Steps[index];

            if (!ShouldRunStep(step, steps.LastOrDefault(), stepResults, ActiveEnvironment))
            {
                steps.Add(CreateSkippedStepResult(step));
                continue;
            }

            if (index > 0 && suite.Steps[index - 1].DelayMs > 0)
            {
                try
                {
                    await Task.Delay(
                        Math.Clamp(suite.Steps[index - 1].DelayMs, 0, 60_000),
                        cancellationToken
                    );
                }
                catch (OperationCanceledException exception)
                {
                    Log.Debug(exception, "套件运行被取消 {SuiteName}", suite.Name);
                    stopped = true;
                    cancelled = true;
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                cancelled = true;
                break;
            }

            var execution = await ExecuteSuiteStepAsync(
                suite,
                step,
                stepResults,
                applyMappings: true,
                source,
                tool,
                client,
                cancellationToken
            );
            if (execution.Result.StatusText == "已取消")
            {
                stopped = true;
                cancelled = true;
                break;
            }

            if (execution.Response is not null)
            {
                stepResults[step.Id] = execution.Response;
            }

            steps.Add(execution.Result);
            if (!execution.Result.Passed && suite.StopOnError)
            {
                stopped = true;
                break;
            }
        }

        return CompleteSuiteRun(suite, steps, stopped, cancelled, historySuite);
    }

    private TestRunResult CompleteSuiteRun(
        TestSuite suite,
        List<TestStepResult> steps,
        bool stopped,
        bool cancelled,
        TestSuite? historySuite = null,
        IReadOnlyList<SuiteRunNodeState>? nodeStates = null,
        IReadOnlyList<SuiteRunEdgeState>? edgeStates = null
    )
    {
        var owner = historySuite ?? suite;
        var run = new TestRunResult
        {
            SuiteId = owner.Id,
            SuiteName = owner.Name,
            Total = steps.Count,
            Passed = steps.Count(step => step.Passed && !step.Skipped),
            Failed = steps.Count(step => !step.Passed && !step.Skipped),
            Skipped = steps.Count(step => step.Skipped),
            Stopped = stopped,
            Cancelled = cancelled,
            NotRun = suite.Steps.Count - steps.Count,
            Steps = steps,
            NodeStates = nodeStates?.ToList() ?? new List<SuiteRunNodeState>(),
            EdgeStates = edgeStates?.ToList() ?? new List<SuiteRunEdgeState>(),
        };

        WireSuiteRunCommands(owner);
        foreach (var result in run.Steps)
        {
            result.ContinueCommand = new AsyncRelayCommand(() =>
                ContinueFromStepAsync(owner, result)
            );
        }

        owner.Runs.Insert(0, run);
        while (owner.Runs.Count > TestSuite.RunHistoryLimit)
        {
            owner.Runs.RemoveAt(owner.Runs.Count - 1);
        }

        owner.LastSummary = run.Summary;
        if (SelectedSuite?.Id == owner.Id)
        {
            SelectedSuiteResult = run;
        }

        Persist();
        return run;
    }

    private async Task<TestRunResult> RunSuiteInParallelAsync(
        TestSuite suite,
        string source,
        string tool,
        string? client,
        CancellationToken cancellationToken,
        TestSuite? historySuite = null
    )
    {
        Log.Warning("套件启用并行执行，步骤延迟和映射依赖将被忽略 {SuiteName}", suite.Name);
        var tasks = suite.Steps.Select(step =>
            ExecuteSuiteStepAsync(
                suite,
                step,
                null,
                applyMappings: false,
                source,
                tool,
                client,
                cancellationToken
            )
        );
        var outcomes = await Task.WhenAll(tasks);
        var cancelled = outcomes.Any(outcome => outcome.Result.StatusText == "已取消");
        return CompleteSuiteRun(
            suite,
            outcomes
                .Where(outcome => outcome.Result.StatusText != "已取消")
                .Select(outcome => outcome.Result)
                .ToList(),
            false,
            cancelled,
            historySuite
        );
    }

    private async Task<TestRunResult> RunSuiteWorkflowAsync(
        TestSuite suite,
        SuiteWorkflow workflow,
        string source,
        string tool,
        string? client,
        CancellationToken cancellationToken,
        Action<SuiteWorkflowProgress>? progress = null,
        TestSuite? historySuite = null
    )
    {
        var outcome = await SuiteWorkflowScheduler.RunAsync(
            suite,
            workflow,
            ActiveEnvironment,
            (step, responses, token) =>
                ExecuteSuiteStepAsync(
                    suite,
                    step,
                    responses,
                    applyMappings: true,
                    source,
                    tool,
                    client,
                    token
                ),
            cancellationToken,
            progress
        );
        return CompleteSuiteRun(
            suite,
            outcome.Steps,
            outcome.Stopped,
            outcome.Cancelled,
            historySuite,
            outcome.NodeStates,
            outcome.EdgeStates
        );
    }

    private async Task<SuiteStepRunOutcome> ExecuteSuiteStepAsync(
        TestSuite suite,
        TestStep step,
        IReadOnlyDictionary<Guid, HttpResult>? stepResults,
        bool applyMappings,
        string source,
        string tool,
        string? client,
        CancellationToken cancellationToken
    )
    {
        var request = Collections
            .SelectMany(collection => collection.Requests)
            .FirstOrDefault(candidate => candidate.Id == step.RequestId);
        if (request is null)
        {
            return new SuiteStepRunOutcome(
                new TestStepResult
                {
                    StepId = step.Id,
                    RequestId = step.RequestId,
                    Name = step.RequestName,
                    Method = step.Method,
                    Url = step.Url,
                    StatusText = "请求已删除",
                    Error = "请求已删除",
                    Passed = false,
                },
                null
            );
        }

        var secrets = SecretMasker.CollectSecrets(Environments);
        var effectiveRequest =
            applyMappings && step.InputMappings.Count > 0
                ? request.Clone(copyAuthorization: false)
                : request;

        if (applyMappings && step.InputMappings.Count > 0 && stepResults is not null)
        {
            string? mappingError;
            try
            {
                mappingError = ApplyInputMappings(effectiveRequest, step, stepResults);
            }
            catch (Exception exception)
            {
                Log.Error(
                    exception,
                    "套件入参映射失败 {SuiteName} {StepName}",
                    suite.Name,
                    step.RequestName
                );
                mappingError = $"入参映射失败：{exception.Message}";
            }

            if (mappingError is not null)
            {
                var failure = HttpRequestService.Failure(mappingError);
                AddHistory(effectiveRequest, failure);
                RecordAudit(effectiveRequest, null, failure, source, tool, client, secrets);
                return new SuiteStepRunOutcome(
                    new TestStepResult
                    {
                        StepId = step.Id,
                        RequestId = request.Id,
                        Name = request.Name,
                        Method = request.Method,
                        Url = request.Url,
                        StatusText = "入参映射失败",
                        Error = mappingError,
                        Passed = false,
                    },
                    null
                );
            }
        }

        ComposedRequest? composed = null;
        ComposedRequest? stored = null;
        HttpResult result = HttpRequestService.Failure("步骤未执行。");
        var attempts = 0;
        var passed = false;
        var assertionResults = new List<TestAssertionResult>();
        string? error = null;

        while (true)
        {
            attempts++;
            try
            {
                var auth = await ResolveAuthAsync(effectiveRequest, cancellationToken);
                composed = RequestComposer.Compose(effectiveRequest, ActiveEnvironment, auth);
                stored = SecretMasker.MaskComposed(
                    effectiveRequest,
                    ActiveEnvironment,
                    composed,
                    secrets
                );
                result = await _http.SendAsync(
                    composed,
                    stored.Url,
                    cancellationToken,
                    effectiveRequest.TimeoutSeconds
                );
            }
            catch (Exception exception)
            {
                Log.Error(
                    exception,
                    "套件步骤发送失败 {SuiteName} {StepName}",
                    suite.Name,
                    step.RequestName
                );
                result = HttpRequestService.Failure(exception.Message);
            }

            if (!result.HasResponse && cancellationToken.IsCancellationRequested)
            {
                RecordAudit(effectiveRequest, stored, result, source, tool, client, secrets);
                return new SuiteStepRunOutcome(
                    new TestStepResult
                    {
                        StepId = step.Id,
                        RequestId = request.Id,
                        Name = request.Name,
                        Method = request.Method,
                        Url = request.Url,
                        StatusText = "已取消",
                        Error = "套件已取消",
                        Passed = false,
                    },
                    null
                );
            }

            var checkedResult =
                step.Assertions.Count > 0 || !string.IsNullOrWhiteSpace(step.BodyContains)
                    ? SecretMasker.MaskResult(result, secrets)
                    : result;
            var statusMatches = step.ExpectedStatus is null
                ? result.StatusCode is >= 200 and < 400
                : result.StatusCode == step.ExpectedStatus;
            var bodyMatches =
                string.IsNullOrWhiteSpace(step.BodyContains)
                || checkedResult.Body.Contains(
                    step.BodyContains,
                    StringComparison.OrdinalIgnoreCase
                );
            assertionResults = SuiteAssertionEvaluator
                .Evaluate(step.Assertions, checkedResult)
                .ToList();
            passed =
                result.HasResponse
                && statusMatches
                && bodyMatches
                && assertionResults.All(assertion => assertion.Passed);
            error = result.Error;
            if (!passed && error is null)
            {
                error =
                    !statusMatches
                        ? $"期望状态 {step.ExpectedStatus?.ToString() ?? "2xx/3xx"}，实际 {result.StatusCode}"
                    : !bodyMatches ? $"响应不包含 \"{step.BodyContains}\""
                    : assertionResults.FirstOrDefault(assertion => !assertion.Passed)?.Detail;
            }

            if (!SuiteRetryPolicy.ShouldRetry(passed, attempts, step.RetryCount))
            {
                break;
            }

            try
            {
                await Task.Delay(SuiteRetryPolicy.Delay(step.RetryDelayMs), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (!result.HasResponse && cancellationToken.IsCancellationRequested)
        {
            RecordAudit(effectiveRequest, stored, result, source, tool, client, secrets);
            return new SuiteStepRunOutcome(
                new TestStepResult
                {
                    StepId = step.Id,
                    RequestId = request.Id,
                    Name = request.Name,
                    Method = request.Method,
                    Url = request.Url,
                    StatusText = "已取消",
                    Error = "套件已取消",
                    Passed = false,
                },
                null
            );
        }

        AddHistory(effectiveRequest, result);
        var audited = RecordAudit(effectiveRequest, stored, result, source, tool, client, secrets);
        var stepResult = new TestStepResult
        {
            StepId = step.Id,
            RequestId = request.Id,
            Name = request.Name,
            Method = request.Method,
            Url = audited.Url,
            StatusCode = result.StatusCode,
            StatusText = SecretMasker.MaskSecrets(result.ReasonPhrase, secrets),
            ElapsedMs = result.ElapsedMs,
            Attempts = attempts,
            Passed = passed,
            Error = error is null ? null : SecretMasker.MaskSecrets(error, secrets),
            ResponsePreview = SecretMasker.MaskPreview(result.Body, 500, secrets),
        };
        stepResult.Assertions.AddRange(assertionResults);
        return new SuiteStepRunOutcome(stepResult, result.HasResponse ? result : null);
    }

    private static bool ShouldRunStep(
        TestStep step,
        TestStepResult? previous,
        IReadOnlyDictionary<Guid, HttpResult> stepResults,
        EnvironmentModel? environment
    )
    {
        var matches = step.Condition switch
        {
            "OnPreviousSuccess" => previous is null || (previous.Passed && !previous.Skipped),
            "OnPreviousFailure" => previous is { } result && !result.Passed && !result.Skipped,
            _ => true,
        };
        if (!matches || step.Condition != "Expression")
        {
            return matches;
        }

        var response =
            previous is not null && stepResults.TryGetValue(previous.StepId, out var value)
                ? value
                : null;
        return SuiteConditionEvaluator.Evaluate(
            step.ConditionRules,
            response,
            previous,
            environment
        );
    }

    private static TestStepResult CreateSkippedStepResult(TestStep step)
    {
        return new TestStepResult
        {
            StepId = step.Id,
            RequestId = step.RequestId,
            Name = step.RequestName,
            Method = step.Method,
            Url = step.Url,
            Passed = true,
            Skipped = true,
            StatusText = "已跳过",
            Error = $"条件 {step.Condition} 不满足",
        };
    }

    private static string? ApplyInputMappings(
        RequestItem request,
        TestStep step,
        IReadOnlyDictionary<Guid, HttpResult> stepResults
    )
    {
        foreach (var mapping in step.InputMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.TargetPath))
            {
                return "入参映射的目标字段为空";
            }

            if (
                !stepResults.TryGetValue(mapping.SourceStepId, out var source)
                || !source.HasResponse
            )
            {
                return "入参映射的来源步骤不存在或尚未执行";
            }

            try
            {
                var sourceValue = SelectJsonPath(source.Body, mapping.SourcePath);
                if (sourceValue is null)
                {
                    return $"来源字段不存在：{mapping.SourcePath}";
                }

                var text = JsonNodeToText(sourceValue);
                var template = string.IsNullOrWhiteSpace(mapping.Template)
                    ? "{value}"
                    : mapping.Template;
                var rendered = template.Replace("{value}", text, StringComparison.Ordinal);

                var error = ApplyMappingTarget(request, mapping, sourceValue, rendered);
                if (error is not null)
                {
                    return error;
                }
            }
            catch (JsonException exception)
            {
                Log.Warning(
                    exception,
                    "入参映射来源不是有效 JSON {SourcePath}",
                    mapping.SourcePath
                );
                return $"来源响应不是有效 JSON：{mapping.SourcePath}";
            }
            catch (Exception exception)
            {
                Log.Error(exception, "入参映射失败 {SourcePath}", mapping.SourcePath);
                return $"入参映射失败：{exception.Message}";
            }
        }

        return null;
    }

    private static string? ApplyMappingTarget(
        RequestItem request,
        InputMapping mapping,
        JsonNode sourceValue,
        string rendered
    )
    {
        switch (mapping.TargetKind.Trim().ToLowerInvariant())
        {
            case "header":
                SetInputRow(request.Headers, mapping.TargetPath.Trim(), rendered);
                return null;

            case "query":
                SetInputRow(request.Params, mapping.TargetPath.Trim(), rendered);
                return null;

            case "path":
            {
                var name = mapping.TargetPath.Trim().Trim('{', '}');
                if (name.StartsWith(':'))
                {
                    name = name[1..];
                }

                var encoded = Uri.EscapeDataString(rendered);
                var before = request.Url;
                request.Url = request
                    .Url.Replace($"{{{name}}}", encoded, StringComparison.Ordinal)
                    .Replace($":{name}", encoded, StringComparison.Ordinal);

                return string.Equals(before, request.Url, StringComparison.Ordinal)
                    ? $"URL 中未找到占位符：{mapping.TargetPath}"
                    : null;
            }

            case "body":
            {
                var template = string.IsNullOrWhiteSpace(mapping.Template)
                    ? "{value}"
                    : mapping.Template;
                JsonNode valueNode;
                if (string.Equals(template, "{value}", StringComparison.Ordinal))
                {
                    valueNode = sourceValue.DeepClone();
                }
                else
                {
                    try
                    {
                        valueNode = JsonNode.Parse(rendered) ?? JsonValue.Create(rendered)!;
                    }
                    catch (JsonException exception)
                    {
                        Log.Debug(
                            exception,
                            "映射模板不是 JSON，按字符串写入 {TargetPath}",
                            mapping.TargetPath
                        );
                        valueNode = JsonValue.Create(rendered)!;
                    }
                }

                var body = string.IsNullOrWhiteSpace(request.Body)
                    ? new JsonObject()
                    : JsonNode.Parse(request.Body);
                if (body is null)
                {
                    return "请求体不是有效 JSON";
                }

                var path = mapping.TargetPath.Trim();
                if (path is "" or "$")
                {
                    request.Body = valueNode.ToJsonString(MappingJsonOptions);
                }
                else
                {
                    if (body is not JsonObject && body is not JsonArray)
                    {
                        return "目标请求体不是 JSON 对象/数组";
                    }

                    var error = SetJsonPath(body, path, valueNode);
                    if (error is not null)
                    {
                        return error;
                    }

                    request.Body = body.ToJsonString(MappingJsonOptions);
                }

                if (string.Equals(request.BodyMode, "None", StringComparison.OrdinalIgnoreCase))
                {
                    request.BodyMode = "Json";
                }

                return null;
            }

            default:
                return $"不支持的映射目标类型：{mapping.TargetKind}";
        }
    }

    private static void SetInputRow(EditableRowCollection rows, string key, string value)
    {
        var row = rows.FirstOrDefault(candidate =>
            candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
        );
        if (row is not null)
        {
            row.Value = value;
            return;
        }

        rows.AddRow(key, value);
    }

    private static JsonNode? SelectJsonPath(string body, string path)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var root = JsonNode.Parse(body);
        if (root is null)
        {
            return null;
        }

        var segments = ParseJsonPath(path);
        if (segments.Count == 0)
        {
            return root.DeepClone();
        }

        JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (segment.Property is { } property)
            {
                if (
                    current is not JsonObject obj
                    || !obj.TryGetPropertyValue(property, out var child)
                )
                {
                    return null;
                }

                current = child;
            }
            else if (segment.Index is { } index)
            {
                if (current is not JsonArray array || index < 0 || index >= array.Count)
                {
                    return null;
                }

                current = array[index];
            }

            if (current is null)
            {
                return null;
            }
        }

        return current?.DeepClone();
    }

    private static string? SetJsonPath(JsonNode root, string path, JsonNode value)
    {
        var segments = ParseJsonPath(path);
        if (segments.Count == 0)
        {
            return "目标路径为空";
        }

        JsonNode? current = root;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var last = index == segments.Count - 1;

            if (segment.Property is { } property)
            {
                if (current is not JsonObject obj)
                {
                    return $"目标路径不是对象：{property}";
                }

                if (last)
                {
                    obj[property] = value;
                }
                else
                {
                    if (obj[property] is not { } child)
                    {
                        child = segments[index + 1].Property is not null
                            ? new JsonObject()
                            : new JsonArray();
                        obj[property] = child;
                    }

                    current = obj[property];
                }
            }
            else if (segment.Index is { } arrayIndex)
            {
                if (current is not JsonArray array || arrayIndex < 0 || arrayIndex >= array.Count)
                {
                    return $"目标数组索引不可用：{arrayIndex}";
                }

                if (last)
                {
                    array[arrayIndex] = value;
                }
                else
                {
                    current = array[arrayIndex];
                }
            }
        }

        return null;
    }

    private static List<(string? Property, int? Index)> ParseJsonPath(string path)
    {
        var result = new List<(string? Property, int? Index)>();
        var text = path.Trim();
        if (text.StartsWith('$'))
        {
            text = text[1..];
        }

        text = text.TrimStart('.');
        var token = new System.Text.StringBuilder();

        void Flush()
        {
            if (token.Length == 0)
            {
                return;
            }

            result.Add((token.ToString(), null));
            token.Clear();
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '.')
            {
                Flush();
                continue;
            }

            if (character == '[')
            {
                Flush();
                var end = text.IndexOf(']', index);
                if (end < 0 || !int.TryParse(text[(index + 1)..end], out var arrayIndex))
                {
                    throw new JsonException($"无效的数组索引：{text}");
                }

                result.Add((null, arrayIndex));
                index = end;
                continue;
            }

            token.Append(character);
        }

        Flush();
        return result;
    }

    private static string JsonNodeToText(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text ?? string.Empty;
        }

        return node.ToJsonString(MappingJsonOptions);
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
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }
                );
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
            Log.Warning(exception, "请求体格式化失败 {RequestName}", Editing.Name);
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
                new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
            );
            ShowToast("JSON 已压缩为单行");
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "请求体不是 JSON，无法压缩 {RequestName}", Editing.Name);
            ShowToast("只有 JSON 请求体可以压缩为单行");
        }

        RefreshDerivedState();
    }

    [RelayCommand]
    private void FormatResponse()
    {
        TransformResponse(indented: true);
    }

    [RelayCommand]
    private void MinifyResponse()
    {
        TransformResponse(indented: false);
    }

    [RelayCommand]
    private void FormatSse()
    {
        TransformSse(compact: false);
    }

    [RelayCommand]
    private void MinifySse()
    {
        TransformSse(compact: true);
    }

    private void TransformSse(bool compact)
    {
        if (!CanTransformSse)
        {
            return;
        }

        var isContent = SseViewMode == "内容";
        var isThinking = SseViewMode == "思考过程";
        var source =
            isContent ? SseContent
            : isThinking ? SseThinking
            : SseRaw;
        var result = SseTextFormatter.Apply(source, compact);
        if (string.Equals(result.Text, source, StringComparison.Ordinal))
        {
            ShowToast(compact ? "当前内容没有可压缩的空白" : "当前内容已经是整理后的格式");
            return;
        }

        if (isContent)
        {
            SseContent = result.Text;
        }
        else if (isThinking)
        {
            SseThinking = result.Text;
        }
        else
        {
            SseRaw = result.Text;
        }

        var action = compact ? "压缩" : "格式化";
        ShowToast($"{result.Kind} 已{action}");
    }

    private void TransformResponse(bool indented)
    {
        if (!HasResponse || string.IsNullOrWhiteSpace(FullResponseBody))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(FullResponseBody);
            var text = JsonSerializer.Serialize(
                document.RootElement,
                indented ? MappingJsonOptions : CompactJsonOptions
            );
            ResponseBody = text;
            FullResponseBody = text;
            ResponseClipboardText = text;
            IsJsonResponse = true;
            IsLargeResponse = false;
            IsResponseJsonView = indented;
            ShowToast(indented ? "响应 JSON 已格式化" : "响应 JSON 已压缩");
        }
        catch (JsonException exception)
        {
            Log.Debug(exception, "响应不是 JSON，无法格式化");
            ShowToast("当前响应不是合法 JSON");
        }
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
        BindAuthTracking();
        _trackedParams.CollectionChanged += OnTrackedRowsCollectionChanged;
        _trackedHeaders.CollectionChanged += OnTrackedRowsCollectionChanged;

        foreach (var row in _trackedParams.Concat(_trackedHeaders))
        {
            row.PropertyChanged += OnTrackedRowPropertyChanged;
            _trackedRows.Add(row);
        }
    }

    /// <summary>跟踪当前作用域那份授权，集合与请求切换时换订阅。</summary>
    private void BindAuthTracking()
    {
        if (_trackedAuth is not null)
        {
            _trackedAuth.PropertyChanged -= OnTrackedAuthPropertyChanged;
        }

        _trackedAuth = EditingAuth;
        _trackedAuth.PropertyChanged += OnTrackedAuthPropertyChanged;
    }

    private RequestItem? _trackedEditing;
    private RequestAuth? _trackedAuth;
    private EditableRowCollection? _trackedParams;
    private EditableRowCollection? _trackedHeaders;
    private readonly List<KeyValueRow> _trackedRows = new();

    private void OnTrackedAuthPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    ) => RefreshAuthState();

    private void OnTrackedEditingPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (e.PropertyName == nameof(RequestItem.Name))
        {
            RefreshAuthState();
            return;
        }

        if (
            e.PropertyName
            is nameof(RequestItem.Url)
                or nameof(RequestItem.Method)
                or nameof(RequestItem.Body)
                or nameof(RequestItem.BodyMode)
                or nameof(RequestItem.UploadFilePath)
                or nameof(RequestItem.UploadFieldName)
        )
        {
            RefreshDerivedState();
        }
    }

    private void OnTrackedRowsCollectionChanged(
        object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e
    )
    {
        BindEditingTracking();
        RefreshDerivedState();
    }

    private void OnTrackedRowPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
        if (
            e.PropertyName
            is nameof(KeyValueRow.Key)
                or nameof(KeyValueRow.Value)
                or nameof(KeyValueRow.IsEnabled)
        )
        {
            RefreshDerivedState();
        }
    }

    private void RefreshOpenApiDocState()
    {
        var doc = Editing.OpenApiDoc;
        HasOpenApiDoc = doc is not null;
        OpenApiDocMarkdown = doc is null ? string.Empty : OpenApiMarkdownRenderer.Render(doc);
        var source = RootCollectionOf(Editing.Owner)?.OpenApiSource;
        HasOpenApiSource = source is { CanSync: true };
        if (doc is null)
        {
            OpenApiDocSourceText = string.Empty;
            return;
        }

        var parts = new List<string> { $"{doc.Method} {doc.Path}" };
        if (source is { CanSync: true } syncSource)
        {
            parts.Add($"来源：{syncSource.Url}");
            parts.Add(
                syncSource.LastSyncedAt == DateTimeOffset.MinValue
                    ? "尚未同步"
                    : $"上次同步：{syncSource.LastSyncedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
            );
            parts.Add(syncSource.AutoSync ? "自动同步：开" : "自动同步：关");
        }
        else
        {
            parts.Add("来源：本地 OpenAPI 文件");
        }

        OpenApiDocSourceText = string.Join(" · ", parts);
    }

    private void RefreshCodePreview()
    {
        CodePreview = CodeSampleService.Generate(
            SelectedCodeLanguage.Id,
            Editing,
            ActiveEnvironment
        );
    }

    private static RequestCollection? RootCollectionOf(RequestCollection? collection)
    {
        var current = collection;
        while (current?.Parent is not null)
        {
            current = current.Parent;
        }

        return current;
    }

    private void RefreshDerivedState()
    {
        var body = Editing.Body ?? string.Empty;
        BodyPreview = string.IsNullOrWhiteSpace(body)
            ? "请求体为空"
            : Truncate(
                string.Join(
                    " ",
                    body.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                ),
                260
            );

        OnPropertyChanged(nameof(IsMultipartBody));
        OnPropertyChanged(nameof(IsJsonBody));
        OnPropertyChanged(nameof(IsTextBody));
        OnPropertyChanged(nameof(ShowRequestBodyJsonViewer));
        OnPropertyChanged(nameof(ShowRequestBodyEditor));
        OnPropertyChanged(nameof(HasUploadFile));

        var duplicates = Editing
            .Headers.ActiveRows.Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .GroupBy(row => row.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        HasHeaderWarning = duplicates.Count > 0;
        HeaderWarningText = HasHeaderWarning
            ? $"重复请求头：{string.Join("、", duplicates)}；发送时会按行依次附加。"
            : string.Empty;

        CurlPreview = RequestComposer.BuildCurl(Editing, ActiveEnvironment, ResolveAuth(Editing));
        RefreshCodePreview();
    }

    private static string Truncate(string text, int limit)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= limit)
        {
            return text ?? string.Empty;
        }

        return text[..limit] + "\n…（已截断）";
    }

    public void Shutdown()
    {
        _suiteScheduleTimer.Stop();
        _suiteScheduleTimer.Tick -= OnSuiteScheduleTick;
        UpdateService.PendingChanged -= OnPendingUpdateChanged;
        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _openApiSyncCts?.Cancel();
        _openApiSyncCts?.Dispose();
        _openApiSyncRequestCts?.Cancel();
        _openApiSyncRequestCts?.Dispose();
        Persist();
        _mockServer.Dispose();
    }

    private void OnPendingUpdateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            HasPendingUpdate = UpdateService.PendingVersion is not null;
        });
    }

    private async Task RunUpdateLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await UpdateService
                .RunBackgroundLoopAsync(
                    Settings,
                    result =>
                        Dispatcher.UIThread.Post(() =>
                        {
                            HasPendingUpdate = UpdateService.PendingVersion is not null;
                            ShowToast(result.Message);
                        }),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Application shutdown.
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "后台更新检查意外终止");
        }
    }

    private async void OnSuiteScheduleTick(object? sender, EventArgs e)
    {
        if (IsRunningSuite)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        foreach (var suite in Suites.Where(candidate => candidate.ScheduleMinutes > 0).ToList())
        {
            var last = _lastScheduledRuns.TryGetValue(suite.Id, out var value)
                ? value
                : DateTimeOffset.MinValue;
            if (last == DateTimeOffset.MinValue)
            {
                _lastScheduledRuns[suite.Id] = now;
                continue;
            }

            if (now - last < TimeSpan.FromMinutes(Math.Clamp(suite.ScheduleMinutes, 1, 1440)))
            {
                continue;
            }

            _lastScheduledRuns[suite.Id] = now;
            var validation = ValidateSuite(suite);
            if (validation.HasErrors)
            {
                Log.Warning(
                    "定时套件预检未通过，已跳过本次运行 {SuiteName} {Summary}",
                    suite.Name,
                    validation.Summary
                );
                continue;
            }

            try
            {
                IsRunningSuite = true;
                await RunSuiteCoreAsync(suite, "定时", "schedule", null, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Log.Error(exception, "定时运行套件失败 {SuiteName}", suite.Name);
            }
            finally
            {
                IsRunningSuite = false;
            }
        }
    }

    private void ApplyUploadProgress(UploadProgress progress)
    {
        IsUploading = progress.SentBytes < progress.TotalBytes;
        UploadProgressValue =
            progress.TotalBytes <= 0
                ? 0
                : Math.Clamp(progress.SentBytes * 100d / progress.TotalBytes, 0, 100);
        UploadProgressText =
            progress.TotalBytes <= 0
                ? $"已上传 {FormatSize(progress.SentBytes)}"
                : $"上传 {FormatSize(progress.SentBytes)} / {FormatSize(progress.TotalBytes)}";
    }

    private void ApplySseProgress(SseProgress progress)
    {
        if (!IsSseResponse)
        {
            HasResponse = true;
            HasResponseError = false;
            ResponseError = null;
            ResponseBody = string.Empty;
            FullResponseBody = string.Empty;
            ResponseClipboardText = string.Empty;
            LargeResponseNotice = string.Empty;
            IsJsonResponse = false;
            IsLargeResponse = false;
            IsResponseJsonView = true;
            IsSseResponse = true;
            SseViewMode = "内容";
            ResponseTabIndex = 2;
            ResponseStatusText = "流式接收中…";
            ResponseTimeText = string.Empty;
            ResponseSizeText = string.Empty;
            ResponseContentType = "text/event-stream";
            ResponseTone = BadgeTone.Info;
        }

        SseThinking = _sseResume is { } resumeThinking
            ? resumeThinking.Thinking + progress.Thinking
            : progress.Thinking;
        SseContent = _sseResume is { } resumeContent
            ? resumeContent.Content + progress.Content
            : progress.Content;
        SseRaw = _sseResume is { } resumeRaw ? resumeRaw.Raw + progress.Raw : progress.Raw;
        SseEventCount = (_sseResume?.Count ?? 0) + progress.EventCount;
        if (!string.IsNullOrWhiteSpace(progress.LastEventId))
        {
            SseLastEventId = progress.LastEventId;
        }
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
            IsResponseJsonView = true;
            IsSseResponse = false;
            SseThinking = string.Empty;
            SseContent = string.Empty;
            SseRaw = string.Empty;
            SseEventCount = 0;
            SseLastEventId = string.Empty;
            SseCanResume = false;
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
        IsResponseJsonView = true;
        IsSseResponse = result.IsSse;
        SseThinking = result.SseThinking;
        SseContent = result.SseContent;
        SseRaw = result.SseRaw;
        SseEventCount = result.SseEventCount;
        SseLastEventId = result.SseLastEventId;
        SseCanResume =
            result.IsSse
            && result.SseCancelled
            && !string.IsNullOrWhiteSpace(result.SseLastEventId);
        SseViewMode = "内容";
        ResponseTabIndex = result.IsSse ? 2 : 0;
        LargeResponseNotice =
            result.IsTruncated
                ? "响应超过 8 MB，已截断并仅保留前 8 MB；请缩小响应内容后重试，避免一次性占用过多内存。"
            : isLarge
                ? $"响应体 {FormatSize(result.SizeBytes)}，已折叠显示前 {ResponsePreviewLimit / 1024} KB。完整内容可在详情页分页查看或复制。"
            : string.Empty;
        ResponseStatusText =
            result.SseCancelled
                ? (
                    result.ReasonPhrase == "超时"
                        ? "超时 · 保留已接收内容"
                        : "已取消 · 保留已接收内容"
                )
            : string.IsNullOrWhiteSpace(result.ReasonPhrase) ? result.StatusCode.ToString()
            : $"{result.StatusCode} {result.ReasonPhrase}";
        ResponseTimeText = $"耗时 {result.ElapsedMs} ms";
        ResponseSizeText = $"大小 {FormatSize(result.SizeBytes)}";
        ResponseContentType = string.IsNullOrWhiteSpace(result.MediaType)
            ? "未知内容类型"
            : result.MediaType;
        ResponseTone = result.SseCancelled
            ? BadgeTone.Warning
            : result.StatusCode switch
            {
                >= 200 and < 300 => BadgeTone.Success,
                >= 300 and < 400 => BadgeTone.Info,
                >= 400 and < 500 => BadgeTone.Warning,
                _ => BadgeTone.Danger,
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
        IsResponseJsonView = true;
        IsSseResponse = false;
        SseThinking = string.Empty;
        SseContent = string.Empty;
        SseRaw = string.Empty;
        SseEventCount = 0;
        SseLastEventId = string.Empty;
        SseCanResume = false;
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
        // Suite input mappings can write a resolved secret directly into the URL or body. Mask the stored snapshot
        // before it reaches workspace.json or list_history, including short secrets that server-text masking skips.
        var secrets = SecretMasker.CollectAllSecrets(Environments);
        var entry = new HistoryEntry
        {
            Name = request.Name,
            Method = request.Method,
            Url = SecretMasker.MaskSecrets(request.Url, secrets),
            TimeoutSeconds = request.TimeoutSeconds,
            BodyMode = request.BodyMode,
            Body = HistoryEntry.LimitBody(SecretMasker.MaskSecrets(request.Body, secrets)),
            StatusCode = result.StatusCode,
            DurationMs = result.ElapsedMs,
            Params = request
                .Params.ActiveRows.Select(row => new RowSnapshot(
                    row.Key,
                    SecretMasker.MaskSecrets(row.Value, secrets),
                    row.IsEnabled
                ))
                .ToList(),
            Headers = request
                .Headers.ActiveRows.Select(row => new RowSnapshot(
                    row.Key,
                    SecretMasker.MaskHeaderValue(row.Key, row.Value, secrets),
                    row.IsEnabled
                ))
                .ToList(),
        };

        WireHistoryEntry(entry);
        History.Insert(0, entry);
        while (History.Count > 100)
        {
            History.RemoveAt(History.Count - 1);
        }

        SelectHistoryEntry(entry);
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
        UpdateSelectionFlags();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsRequestSelected));
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(EditorHint));
        OnPropertyChanged(nameof(CanExportCurl));
        RefreshOpenApiSelectionState();
    }

    private void UpdateSelectionFlags()
    {
        foreach (var collection in Collections)
        {
            collection.IsSelected =
                SelectedRequest is null && ReferenceEquals(SelectedCollection, collection);

            foreach (var request in collection.Requests)
            {
                request.IsSelected = ReferenceEquals(SelectedRequest, request);
            }
        }
    }

    private void NotifyTreeChanged() => RefreshTree();

    private void RefreshTree()
    {
        var filter = FilterText.Trim();
        var hasFilter = filter.Length > 0;
        var favoritesOnly = TreeFilterMode == "收藏";
        var nodes = new List<TreeDisplayNode>();
        var reusable = new Dictionary<object, TreeDisplayNode>(TreeNodes.Count);
        foreach (var node in TreeNodes)
        {
            if (node.Collection is { } collection)
            {
                reusable[collection] = node;
            }
            else if (node.Request is { } request)
            {
                reusable[request] = node;
            }
        }

        foreach (var collection in Collections.Where(collection => collection.Parent is null))
        {
            AddCollectionRows(
                collection,
                0,
                filter,
                hasFilter,
                favoritesOnly,
                false,
                nodes,
                reusable
            );
        }

        ApplyTreeNodes(nodes);
        TreeIsEmpty = TreeNodes.Count == 0;
    }

    /// <summary>
    /// Reconciles <see cref="TreeNodes"/> with the freshly built row list in place: unchanged rows keep their
    /// existing node and container, and only inserted / removed / reordered rows touch the collection.
    /// </summary>
    private void ApplyTreeNodes(IReadOnlyList<TreeDisplayNode> nodes)
    {
        var keep = new HashSet<TreeDisplayNode>(nodes);
        for (var index = TreeNodes.Count - 1; index >= 0; index--)
        {
            if (!keep.Contains(TreeNodes[index]))
            {
                TreeNodes.RemoveAt(index);
            }
        }

        for (var index = 0; index < nodes.Count; index++)
        {
            if (index < TreeNodes.Count && ReferenceEquals(TreeNodes[index], nodes[index]))
            {
                continue;
            }

            var existingIndex = IndexOfNode(nodes[index], index + 1);
            if (existingIndex >= 0)
            {
                TreeNodes.Move(existingIndex, index);
            }
            else
            {
                TreeNodes.Insert(index, nodes[index]);
            }
        }
    }

    private int IndexOfNode(TreeDisplayNode node, int startIndex)
    {
        for (var index = startIndex; index < TreeNodes.Count; index++)
        {
            if (ReferenceEquals(TreeNodes[index], node))
            {
                return index;
            }
        }

        return -1;
    }

    private void AddCollectionRows(
        RequestCollection collection,
        int depth,
        string filter,
        bool hasFilter,
        bool favoritesOnly,
        bool forceInclude,
        List<TreeDisplayNode> nodes,
        Dictionary<object, TreeDisplayNode> reusable
    )
    {
        if (!hasFilter && !favoritesOnly)
        {
            nodes.Add(GetCollectionNode(collection, depth, reusable));
            if (!collection.IsExpanded)
            {
                return;
            }

            foreach (var child in collection.Children)
            {
                AddCollectionRows(child, depth + 1, filter, false, false, false, nodes, reusable);
            }

            foreach (var request in collection.Requests)
            {
                nodes.Add(GetRequestNode(request, depth, reusable));
            }

            return;
        }

        var collectionMatches = !hasFilter || Contains(collection.Name, filter);
        var includeAll =
            forceInclude
            || (!favoritesOnly && collectionMatches)
            || (favoritesOnly && collection.IsFavorite);

        var requests = collection
            .Requests.Where(request =>
                (!favoritesOnly || includeAll || request.IsFavorite)
                && (!hasFilter || collectionMatches || Matches(request, filter))
            )
            .ToList();

        var childNodes = new List<TreeDisplayNode>();
        foreach (var child in collection.Children)
        {
            AddCollectionRows(
                child,
                depth + 1,
                filter,
                hasFilter,
                favoritesOnly,
                includeAll,
                childNodes,
                reusable
            );
        }

        var selfVisible = favoritesOnly
            ? collection.IsFavorite || requests.Count > 0 || childNodes.Count > 0
            : !hasFilter || collectionMatches || requests.Count > 0 || childNodes.Count > 0;

        if (!selfVisible)
        {
            return;
        }

        // Search results must stay expanded so the matching descendants are visible.
        // Favorite mode honors the user's collapse state instead of forcing every folder open.
        if (!favoritesOnly || hasFilter)
        {
            collection.IsExpanded = true;
        }

        nodes.Add(GetCollectionNode(collection, depth, reusable));
        if (!collection.IsExpanded)
        {
            return;
        }

        nodes.AddRange(childNodes);
        foreach (var request in requests)
        {
            nodes.Add(GetRequestNode(request, depth, reusable));
        }
    }

    private static TreeDisplayNode GetCollectionNode(
        RequestCollection collection,
        int depth,
        Dictionary<object, TreeDisplayNode> reusable
    )
    {
        if (reusable.TryGetValue(collection, out var node) && node.Depth == depth)
        {
            return node;
        }

        return new TreeCollectionNode(collection, depth);
    }

    private static TreeDisplayNode GetRequestNode(
        RequestItem request,
        int depth,
        Dictionary<object, TreeDisplayNode> reusable
    )
    {
        if (reusable.TryGetValue(request, out var node) && node.Depth == depth)
        {
            return node;
        }

        return new TreeRequestNode(request, depth);
    }

    private static bool Matches(RequestItem request, string filter) =>
        Contains(request.Name, filter)
        || Contains(request.Url, filter)
        || Contains(request.Method, filter);

    private static bool Contains(string? value, string filter) =>
        filter.Length == 0
        || (value?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    public void ShowToast(string title)
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

        var saved = _store.Save(
            WorkspaceStore.Capture(
                Collections,
                Environments,
                History,
                RecycledHistory,
                Suites,
                TreeTrash.Items,
                ActiveEnvironment?.Id,
                includeSuites: false
            )
        );
        var suitesSaved = _suiteStore.Save(Suites);
        if (saved == SaveResult.Failed || suitesSaved == SaveResult.Failed)
        {
            DataNotice.Warn(
                "工作区保存失败：无法写入本地 SQLite 数据库，最近的修改还没有保存到磁盘。请检查数据目录是否可写、数据库是否被其他程序占用；下次保存时会自动重试。"
            );
        }
    }

    private void LoadWorkspace(WorkspaceDto workspace)
    {
        foreach (var collectionDto in workspace.Collections)
        {
            Collections.Add(WorkspaceStore.ToCollectionModel(collectionDto));
        }

        foreach (var collectionDto in workspace.Collections)
        {
            if (collectionDto.ParentId is not { } parentId)
            {
                continue;
            }

            var parent = Collections.FirstOrDefault(collection => collection.Id == parentId);
            var child = Collections.FirstOrDefault(collection => collection.Id == collectionDto.Id);
            if (parent is not null && child is not null && !ReferenceEquals(parent, child))
            {
                child.Parent = parent;
                parent.Children.Add(child);
            }
        }

        foreach (var environmentDto in workspace.Environments)
        {
            var environment = new EnvironmentModel
            {
                Id = environmentDto.Id == Guid.Empty ? Guid.NewGuid() : environmentDto.Id,
                Name = environmentDto.Name,
            };

            foreach (var row in environmentDto.Variables)
            {
                environment.Variables.AddRow(row.Key, row.Value, row.IsEnabled, row.IsSecret);
            }

            environment.Variables.EnsureTrailingRow();
            Environments.Add(environment);
        }

        foreach (var historyDto in workspace.History.Take(100))
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
            var suite = WorkspaceStore.ToSuiteModel(suiteDto);
            WireSuiteRunCommands(suite);
            Suites.Add(suite);
        }

        foreach (var trashedDto in workspace.TrashedNodes)
        {
            if (WorkspaceStore.ToTrashedNode(trashedDto) is { } trashed)
            {
                TreeTrash.Load(trashed);
            }
        }

        ActiveEnvironment =
            Environments.FirstOrDefault(environment =>
                environment.Id == workspace.ActiveEnvironmentId
            ) ?? Environments.FirstOrDefault();
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
            Owner = collection,
        };

        var users = new RequestItem
        {
            Name = "用户列表",
            Method = "GET",
            Url = "{{baseUrl}}/api/users",
            Owner = collection,
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
            Owner = collection,
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
            Owner = collection,
        };

        var slow = new RequestItem
        {
            Name = "慢响应",
            Method = "GET",
            Url = "{{baseUrl}}/api/slow?ms=1200",
            Owner = collection,
        };

        var error = new RequestItem
        {
            Name = "模拟错误",
            Method = "GET",
            Url = "{{baseUrl}}/api/error",
            Owner = collection,
        };

        var large = new RequestItem
        {
            Name = "大响应 (200 KB)",
            Method = "GET",
            Url = "{{baseUrl}}/api/large?kb=200",
            Owner = collection,
        };

        var sse = new RequestItem
        {
            Name = "AI 流式输出 (SSE)",
            Method = "GET",
            Url = "{{baseUrl}}/api/ai/stream",
            Owner = collection,
        };
        sse.Headers.AddRow("Accept", "text/event-stream");

        var upload = new RequestItem
        {
            Name = "上传文件",
            Method = "POST",
            Url = "{{baseUrl}}/api/upload",
            BodyMode = "Multipart",
            UploadFieldName = "file",
            Owner = collection,
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
        collection.IsExpanded = true;

        var auth = new RequestCollection { Name = "示例：带鉴权头" };
        var me = new RequestItem
        {
            Name = "读取用户详情",
            Method = "GET",
            Url = "{{baseUrl}}/api/users/1",
            Owner = auth,
        };
        me.Headers.AddRow("Authorization", "Bearer {{token}}");
        auth.Requests.Add(me);
        Collections.Add(auth);

        var smokeSuite = new TestSuite { Name = "内置 Mock 冒烟测试", StopOnError = true };

        smokeSuite.Steps.Add(
            new TestStep
            {
                RequestId = health.Id,
                RequestName = health.Name,
                Method = health.Method,
                Url = health.Url,
                ExpectedStatus = 200,
            }
        );
        smokeSuite.Steps.Add(
            new TestStep
            {
                RequestId = users.Id,
                RequestName = users.Name,
                Method = users.Method,
                Url = users.Url,
                ExpectedStatus = 200,
            }
        );
        smokeSuite.Steps.Add(
            new TestStep
            {
                RequestId = echo.Id,
                RequestName = echo.Name,
                Method = echo.Method,
                Url = echo.Url,
                ExpectedStatus = 200,
            }
        );
        smokeSuite.Steps.Add(
            new TestStep
            {
                RequestId = orders.Id,
                RequestName = orders.Name,
                Method = orders.Method,
                Url = orders.Url,
                ExpectedStatus = 201,
            }
        );

        Suites.Add(smokeSuite);
        SelectedSuite = smokeSuite;
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
            _ => $"{bytes / (1024d * 1024d):0.##} MB",
        };
    }
}

public sealed partial class RequestTabOption : ObservableObject
{
    public RequestTabOption(int index, string header)
    {
        Index = index;
        Header = header;
    }

    public int Index { get; }

    public string Header { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string Variant => IsSelected ? "Secondary" : "Ghost";

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(Variant));

    public override string ToString() => Header;
}
