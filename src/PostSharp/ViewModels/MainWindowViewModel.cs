using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
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

public partial class MainWindowViewModel : ObservableObject
{
    private readonly WorkspaceStore _store = new();
    private readonly AuditStore _auditStore = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly HttpRequestService _http = new();
    private readonly MockApiServer _mockServer = new();
    private readonly Dictionary<Guid, TestRunResult> _suiteResults = new();

    private const int AuditPreviewLimit = 2000;
    private const int ResponsePreviewLimit = 8 * 1024;
    private const int DetailPageSize = 16 * 1024;
    private const int MaxCollectionDepth = 5;

    private static readonly JsonSerializerOptions MappingJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private int _auditLimit = 500;
    private int _largeResponseThreshold = 64 * 1024;

    private object? _pendingDelete;
    private HistoryEntry? _pendingHistoryTrash;
    private HistoryEntry? _pendingHistoryPurge;
    private bool _trashAllPending;
    private bool _purgeAllPending;
    private AuditEntry? _pendingAuditPurge;
    private bool _auditPurgeAllPending;
    private CancellationTokenSource? _sendCts;
    private CancellationTokenSource? _importCts;
    private bool _initializing = true;
    private IReadOnlyList<SuiteStepOption> _mappingSourceOptions = Array.Empty<SuiteStepOption>();

    public MainWindowViewModel()
    {
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
        _http.TimeoutSeconds = Settings.RequestTimeoutSeconds;
        _http.AutoFormatResponse = Settings.AutoFormatResponse;
        _auditLimit = Math.Max(20, Settings.AuditLimit);
        _largeResponseThreshold = Math.Max(8, Settings.LargeResponseThresholdKb) * 1024;

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

        var firstRequest = Collections
            .SelectMany(collection => collection.Requests)
            .FirstOrDefault();
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
        ImportWarnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasImportWarnings));
        AuditTrash.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAuditTrash));
            OnPropertyChanged(nameof(AuditTrashLabel));
        };

        foreach (var entry in _auditStore.Load().Take(_auditLimit))
        {
            Audit.Add(entry);
        }

        foreach (var entry in _auditStore.LoadTrash())
        {
            WireAuditTrashEntry(entry);
            AuditTrash.Add(entry);
        }

        RefreshAuditView();
        BindEditingTracking();
        RefreshDerivedState();
        RefreshTree();
        UpdateSelectionFlags();

        Persist();
    }

    public ObservableCollection<RequestCollection> Collections { get; } = new();

    public ObservableCollection<TreeDisplayNode> TreeNodes { get; } = new();

    public ObservableCollection<EnvironmentModel> Environments { get; } = new();

    public ObservableCollection<HistoryEntry> History { get; } = new();

    public ObservableCollection<HistoryEntry> RecycledHistory { get; } = new();

    public ObservableCollection<AuditEntry> Audit { get; } = new();

    public ObservableCollection<AuditEntry> AuditTrash { get; } = new();

    public ObservableCollection<AuditEntry> VisibleAudit { get; } = new();

    public ObservableCollection<TestSuite> Suites { get; } = new();

    public ObservableCollection<SuiteSourceCollection> SuiteSources { get; } = new();

    public ObservableCollection<SuiteBuilderStep> SuiteBuilderSteps { get; } = new();

    public ObservableCollection<InputMappingEditor> MappingEditors { get; } = new();

    public ObservableCollection<ResponseHeaderView> ResponseHeaders { get; } = new();

    public IReadOnlyList<string> Methods { get; } =
        new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

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
    private bool _isSuiteBuilderOpen;

    [ObservableProperty]
    private string _suiteBuilderName = string.Empty;

    [ObservableProperty]
    private bool _isMappingEditorOpen;

    [ObservableProperty]
    private SuiteBuilderStep? _mappingStep;

    [ObservableProperty]
    private string _mappingTitle = string.Empty;

    [ObservableProperty]
    private bool _isBodySingleLine;

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

    [ObservableProperty]
    private bool _isImportDialogOpen;

    [ObservableProperty]
    private string _importTab = "Postman";

    [ObservableProperty]
    private string _importUrl = string.Empty;

    [ObservableProperty]
    private string _postmanJson = string.Empty;

    [ObservableProperty]
    private string _curlCommand = string.Empty;

    [ObservableProperty]
    private string _importStatus =
        "支持 OpenAPI 3.x / Swagger 2.0，JSON 或 YAML；也可以填写可直接访问的文档 URL。";

    [ObservableProperty]
    private bool _hasImportError;

    [ObservableProperty]
    private bool _isImporting;

    public ObservableCollection<string> ImportWarnings { get; } = new();

    public bool HasImportWarnings => ImportWarnings.Count > 0;

    public bool IsOpenApiImport => ImportTab == "OpenAPI";

    public bool IsPostmanImport => ImportTab == "Postman";

    public bool IsCurlImport => ImportTab == "cURL";

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

    [RelayCommand]
    private void OpenImportDialog()
    {
        ImportTab = "Postman";
        ImportUrl = string.Empty;
        PostmanJson = string.Empty;
        CurlCommand = string.Empty;
        ImportWarnings.Clear();
        ImportStatus = ImportStatusForTab(ImportTab);
        HasImportError = false;
        IsImportDialogOpen = true;
    }

    partial void OnImportTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOpenApiImport));
        OnPropertyChanged(nameof(IsPostmanImport));
        OnPropertyChanged(nameof(IsCurlImport));
        ImportWarnings.Clear();
        HasImportError = false;
        ImportStatus = ImportStatusForTab(value);
    }

    private static string ImportStatusForTab(string tab)
    {
        return tab switch
        {
            "Postman" =>
                "选择 Postman Collection v2.1 JSON 文件，或把集合导出内容粘贴到下方。支持嵌套文件夹、认证、查询参数和常见请求体。",
            "cURL" =>
                "粘贴以 curl 开头的 bash 命令。支持 -X、-H、-d、--data-raw、-F、-u、--max-time 等常用参数。",
            _ => "支持 OpenAPI 3.x / Swagger 2.0，JSON 或 YAML；也可以填写可直接访问的文档 URL。",
        };
    }

    [RelayCommand]
    private void CloseImportDialog()
    {
        if (IsImporting)
        {
            _importCts?.Cancel();
        }

        IsImportDialogOpen = false;
    }

    [RelayCommand]
    private async Task ImportFromUrlAsync()
    {
        if (IsImporting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ImportUrl))
        {
            HasImportError = true;
            ImportStatus = "请填写 OpenAPI 文档地址，或改为从本地文件导入。";
            return;
        }

        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        IsImporting = true;
        HasImportError = false;
        ImportStatus = "正在下载并解析 OpenAPI 文档…";

        try
        {
            var document = await OpenApiImportService
                .LoadFromUrlAsync(ImportUrl, _importCts.Token)
                .ConfigureAwait(true);
            ApplyOpenApiImport(document);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "从 URL 导入 OpenAPI 已取消 {Url}", ImportUrl);
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "从 URL 导入 OpenAPI 失败 {Url}", ImportUrl);
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    public async Task ImportFromFileAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        if (IsImporting)
        {
            return;
        }

        _importCts?.Dispose();
        _importCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsImporting = true;
        HasImportError = false;
        ImportStatus = "正在读取并解析 OpenAPI 文件…";

        try
        {
            var content = await File.ReadAllTextAsync(path, _importCts.Token).ConfigureAwait(true);
            var document = await Task.Run(
                    () => OpenApiImportService.Parse(content, Path.GetFileName(path)),
                    _importCts.Token
                )
                .ConfigureAwait(true);
            ApplyOpenApiImport(document);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "从文件导入 OpenAPI 已取消 {FilePath}", path);
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "从文件导入 OpenAPI 失败 {FilePath}", path);
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    public async Task ImportPostmanFileAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        if (IsImporting)
        {
            return;
        }

        if (!File.Exists(path))
        {
            HasImportError = true;
            ImportStatus = "Postman Collection 文件不存在，请重新选择。";
            return;
        }

        _importCts?.Dispose();
        _importCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsImporting = true;
        HasImportError = false;
        ImportWarnings.Clear();
        ImportStatus = "正在读取并解析 Postman Collection 文件…";

        try
        {
            var content = await File.ReadAllTextAsync(path, _importCts.Token).ConfigureAwait(true);
            var result = await Task.Run(
                    () => PostmanImportService.Parse(content, Path.GetFileName(path)),
                    _importCts.Token
                )
                .ConfigureAwait(true);
            ApplyPostmanImport(result);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "从文件导入 Postman 已取消 {FilePath}", path);
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "从文件导入 Postman 失败 {FilePath}", path);
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    [RelayCommand]
    private async Task ImportPostmanTextAsync()
    {
        if (IsImporting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(PostmanJson))
        {
            HasImportError = true;
            ImportStatus = "请粘贴 Postman Collection JSON，或改为选择文件。";
            return;
        }

        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        IsImporting = true;
        HasImportError = false;
        ImportWarnings.Clear();
        ImportStatus = "正在解析粘贴的 Postman Collection…";

        try
        {
            var result = await Task.Run(
                    () => PostmanImportService.Parse(PostmanJson, "粘贴的 Postman Collection"),
                    _importCts.Token
                )
                .ConfigureAwait(true);
            ApplyPostmanImport(result);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "导入粘贴的 Postman 已取消");
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "导入粘贴的 Postman 失败");
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    [RelayCommand]
    private void ImportCurl()
    {
        if (string.IsNullOrWhiteSpace(CurlCommand))
        {
            HasImportError = true;
            ImportStatus = "请粘贴以 curl 开头的命令。";
            return;
        }

        try
        {
            var result = CurlImportService.Parse(CurlCommand);
            ApplyCurlImport(result);
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "导入 cURL 失败");
        }
    }

    private void ApplyPostmanImport(ImportResult result)
    {
        var root = new RequestCollection
        {
            Name = string.IsNullOrWhiteSpace(result.Root.Name) ? "Postman 集合" : result.Root.Name,
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
        SetImportWarnings(result.Warnings);

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

        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        RefreshDerivedState();

        var environmentText = environmentName is null
            ? string.Empty
            : $"，并激活环境「{environmentName}」";
        var warningText =
            result.Warnings.Count > 0
                ? $"；{result.Warnings.Count} 条兼容性提示见下方"
                : string.Empty;
        ImportStatus =
            $"导入完成：{result.Root.Name}，{requestCount} 个请求，{folderCount} 个分组{environmentText}{warningText}。";
        HasImportError = false;

        if (result.Warnings.Count == 0)
        {
            IsImportDialogOpen = false;
            ShowToast($"已导入「{result.Root.Name}」：{requestCount} 个请求");
        }

        Log.Information(
            "Postman 导入完成 {Source}：{Title}，{RequestCount} 个请求，{FolderCount} 个分组，{WarningCount} 条提示",
            result.Source,
            result.Root.Name,
            requestCount,
            folderCount,
            result.Warnings.Count
        );
    }

    private void ApplyCurlImport(CurlImportResult result)
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
        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        RefreshDerivedState();

        foreach (var warning in result.Warnings)
        {
            Log.Warning("cURL 导入提示：{Warning}", warning);
        }

        ImportWarnings.Clear();
        IsImportDialogOpen = false;
        var warningText =
            result.Warnings.Count > 0
                ? $"；{result.Warnings.Count} 条提示已写入日志"
                : string.Empty;
        ShowToast($"已导入 cURL「{request.Name}」{warningText}");
        Log.Information(
            "cURL 导入完成 {Method} {Url}，{WarningCount} 条提示",
            request.Method,
            request.Url,
            result.Warnings.Count
        );
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
            if (depth >= MaxCollectionDepth)
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

    private string? ApplyImportedEnvironment(ImportedEnvironment? imported)
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

    private void SetImportWarnings(IReadOnlyList<string> warnings)
    {
        ImportWarnings.Clear();
        foreach (var warning in warnings)
        {
            ImportWarnings.Add(warning);
        }
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

    private void ApplyOpenApiImport(OpenApiImportDocument document)
    {
        var root = new RequestCollection { Name = document.Title, IsExpanded = true };
        Collections.Insert(0, root);

        var groupCount = 0;
        var requestCount = 0;
        RequestCollection? firstGroup = null;
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

            firstGroup ??= target;

            foreach (var imported in group.Requests)
            {
                var request = new RequestItem
                {
                    Name = imported.Name,
                    Method = imported.Method,
                    Url = imported.Url,
                    BodyMode = imported.BodyMode,
                    Body = imported.Body,
                    Owner = target,
                };

                foreach (var field in imported.Query)
                {
                    request.Params.AddRow(field.Name, field.Value, true);
                }

                foreach (var header in imported.Headers)
                {
                    request.Headers.AddRow(header.Name, header.Value, true);
                }

                request.Params.EnsureTrailingRow();
                request.Headers.EnsureTrailingRow();
                request.FormFields.EnsureTrailingRow();
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

        IsImportDialogOpen = false;
        Persist();
        NotifySelectionChanged();
        NotifyTreeChanged();
        RefreshDerivedState();
        ShowToast($"已导入「{document.Title}」：{requestCount} 个请求，{groupCount} 个分组");
        Log.Information(
            "OpenAPI 导入完成 {Source}：{Title} {Version}，{RequestCount} 个请求，{GroupCount} 个分组",
            document.Source,
            document.Title,
            document.Version,
            requestCount,
            groupCount
        );
    }

    private static string DescribeImportFailure(Exception exception)
    {
        return exception switch
        {
            HttpRequestException => "无法连接文档地址，请检查网络和 URL 是否可直接访问。",
            TaskCanceledException => "下载超时，请检查网络后重试。",
            UnauthorizedAccessException => "没有读取该文件的权限，请换一个位置。",
            FileNotFoundException => "文件不存在，请重新选择。",
            InvalidOperationException => exception.Message,
            _ => "内容格式无法识别，请确认文件或命令格式。",
        };
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

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        _http.TimeoutSeconds = Math.Clamp(Settings.RequestTimeoutSeconds, 5, 600);
        _http.AutoFormatResponse = Settings.AutoFormatResponse;
        _largeResponseThreshold = Math.Max(8, Settings.LargeResponseThresholdKb) * 1024;
        _auditLimit = Math.Max(20, Settings.AuditLimit);

        while (Audit.Count > _auditLimit)
        {
            var oldest = Audit[^1];
            Audit.RemoveAt(Audit.Count - 1);
            MoveAuditToTrash(oldest);
        }

        PersistAudit();

        if (e.PropertyName == nameof(AppSettings.Theme))
        {
            _ = ThemeService.ApplyAsync(Settings.Theme);
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

        _settingsStore.Save(Settings);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HasSelection => SelectedRequest is not null || SelectedCollection is not null;

    public bool IsRequestSelected => SelectedRequest is not null;

    public bool HasOpenDialog =>
        IsEnvironmentDialogOpen
        || IsMcpDialogOpen
        || IsSuiteBuilderOpen
        || IsMappingEditorOpen
        || IsRenameDialogOpen
        || IsDeleteDialogOpen
        || IsHistoryTrashConfirmOpen
        || IsHistoryTrashOpen
        || IsHistoryPurgeConfirmOpen
        || IsAuditTrashConfirmOpen
        || IsAuditTrashOpen
        || IsAuditPurgeConfirmOpen
        || IsImportDialogOpen;

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

        return false;
    }

    /// <summary>Esc closes the top-most overlay and applies its cancel semantics.</summary>
    public bool TryCancelActiveDialog()
    {
        if (IsImportDialogOpen)
        {
            CloseImportDialogCommand.Execute(null);
            return true;
        }

        if (IsMappingEditorOpen)
        {
            CancelMappingEditorCommand.Execute(null);
            return true;
        }

        if (IsSuiteBuilderOpen)
        {
            CancelSuiteBuilderCommand.Execute(null);
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

        return false;
    }

    public bool HasHistory => History.Count > 0;

    public string TreeEmptyText =>
        TreeFilterMode == "收藏" ? "还没有收藏的集合或请求" : "没有匹配的请求";

    public bool HasRecycledHistory => RecycledHistory.Count > 0;

    public string RecycleBinLabel =>
        RecycledHistory.Count > 0 ? $"回收站 ({RecycledHistory.Count})" : "回收站";

    public bool HasSelectedAuditEntry => SelectedAuditEntry is not null;

    public bool HasSelectedSuite => SelectedSuite is not null;

    public bool HasSelectedSuiteResult => SelectedSuiteResult is not null;

    public int AuditCount => Audit.Count;

    public bool HasAuditTrash => AuditTrash.Count > 0;

    public string AuditTrashLabel =>
        AuditTrash.Count > 0 ? $"审计回收站 ({AuditTrash.Count})" : "审计回收站";

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
        + "get_request、create_request、update_request、move_request、delete_request；"
        + "套件：list_suites、get_suite、create_suite、create_suite_from_collections、update_suite、run_suite、delete_suite；"
        + "运行与审计：send_request、run_sequence、get_curl、list_audit、get_audit、list_history、list_environments、set_variable。";

    public string EditorHint => IsDraft ? "草稿 · 保存后加入集合" : "Ctrl+S 保存 · Ctrl+Enter 发送";

    partial void OnEditingChanged(RequestItem value)
    {
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(EditorHint));
        BindEditingTracking();
        RefreshDerivedState();
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

    partial void OnSelectedAuditEntryChanged(AuditEntry? value) =>
        OnPropertyChanged(nameof(HasSelectedAuditEntry));

    partial void OnSelectedSuiteChanged(TestSuite? value)
    {
        OnPropertyChanged(nameof(HasSelectedSuite));
        SelectedSuiteResult =
            value is not null && _suiteResults.TryGetValue(value.Id, out var result)
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
                result = await _http.SendAsync(composed, _sendCts.Token, Editing.TimeoutSeconds);
            }
            catch (Exception exception)
            {
                Log.Error(exception, "UI 发送请求失败 {RequestName}", Editing.Name);
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
        if (parent.Depth >= MaxCollectionDepth)
        {
            ShowToast($"集合最多支持 {MaxCollectionDepth} 级");
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

        if (parent is not null && parent.Depth >= MaxCollectionDepth)
        {
            throw new InvalidOperationException($"集合最多支持 {MaxCollectionDepth} 级。");
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
                && target.Depth + GetCollectionSubtreeHeight(collection) > MaxCollectionDepth
            )
            {
                throw new InvalidOperationException($"集合最多支持 {MaxCollectionDepth} 级。");
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
        return collection;
    }

    public bool DeleteCollection(Guid id)
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
        RemoveCollectionRecursive(collection);

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
                DeleteDialogDescription = $"“{request.Name}”将从集合中删除。此操作无法撤销。";
                break;
            case RequestCollection collection:
                _pendingDelete = collection;
                DeleteDialogTitle = "删除集合？";
                DeleteDialogDescription =
                    $"“{collection.Name}”及其子集合、共 {CountRequests(collection)} 个请求将被删除。此操作无法撤销。";
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
                && target.Depth + GetCollectionSubtreeHeight(collection) <= MaxCollectionDepth;
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
                && parent.Depth + GetCollectionSubtreeHeight(collection) <= MaxCollectionDepth
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

    private void SyncFlatCollectionOrder(IReadOnlyList<RequestCollection> roots)
    {
        var ordered = new List<RequestCollection>();
        foreach (var root in roots)
        {
            AddCollectionInTreeOrder(root, ordered);
        }

        if (ordered.Count != Collections.Count)
        {
            return;
        }

        Collections.Clear();
        foreach (var collection in ordered)
        {
            Collections.Add(collection);
        }
    }

    private static void AddCollectionInTreeOrder(
        RequestCollection collection,
        ICollection<RequestCollection> target
    )
    {
        target.Add(collection);
        foreach (var child in collection.Children)
        {
            AddCollectionInTreeOrder(child, target);
        }
    }

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
        if (_pendingDelete is RequestItem request)
        {
            var wasSelected = ReferenceEquals(SelectedRequest, request);
            request.Owner?.Requests.Remove(request);
            if (wasSelected)
            {
                SelectFirstRequestOrBlank();
            }
        }
        else if (_pendingDelete is RequestCollection collection)
        {
            var editingRemoved =
                Editing.Owner is not null && IsSelfOrDescendant(Editing.Owner, collection);
            var selectedRemoved =
                SelectedCollection is not null
                && IsSelfOrDescendant(SelectedCollection, collection);

            RemoveCollectionRecursive(collection);

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
        }

        _pendingDelete = null;
        IsDeleteDialogOpen = false;
        NotifySelectionChanged();
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

    private void RemoveCollectionRecursive(RequestCollection collection)
    {
        foreach (var child in collection.Children.ToList())
        {
            RemoveCollectionRecursive(child);
        }

        collection.Parent?.Children.Remove(collection);
        Collections.Remove(collection);
    }

    private static int CountRequests(RequestCollection collection)
    {
        return collection.Requests.Count + collection.Children.Sum(child => CountRequests(child));
    }

    private static int GetCollectionSubtreeHeight(RequestCollection collection)
    {
        return 1
            + (
                collection.Children.Count == 0
                    ? 0
                    : collection.Children.Max(GetCollectionSubtreeHeight)
            );
    }

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

    public void RecordExternalSend(
        RequestItem request,
        ComposedRequest? composed,
        HttpResult result,
        string source,
        string? tool,
        string? client
    )
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

        var lines = request.FormFields.ActiveRows.Select(row => $"{row.Key}: {row.Value}").ToList();
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
        string? client
    )
    {
        var requestHeaders = composed is not null
            ? string.Join(
                "\n",
                composed
                    .Headers.Select(pair => $"{pair.Key}: {pair.Value}")
                    .Concat(
                        composed.ContentType is null
                            ? Array.Empty<string>()
                            : new[] { $"Content-Type: {composed.ContentType}" }
                    )
            )
            : string.Join(
                "\n",
                request.Headers.ActiveRows.Select(row => $"{row.Key}: {row.Value}")
            );

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
                AuditPreviewLimit
            ),
            ResponseHeaders = Truncate(
                string.Join("\n", result.Headers.Select(header => $"{header.Key}: {header.Value}")),
                AuditPreviewLimit
            ),
            ResponseBodyPreview = Truncate(result.Body, AuditPreviewLimit),
            ResponseContentType = result.MediaType,
            IsSse = result.IsSse,
            SseEventCount = result.SseEventCount,
            AiThinking = result.SseThinking,
            AiContent = result.SseContent,
        };

        Audit.Insert(0, entry);
        while (Audit.Count > _auditLimit)
        {
            var oldest = Audit[^1];
            Audit.RemoveAt(Audit.Count - 1);
            MoveAuditToTrash(oldest);
        }

        PersistAudit();
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

    private void MoveAuditToTrash(AuditEntry entry)
    {
        WireAuditTrashEntry(entry);
        AuditTrash.Insert(0, entry);
        while (AuditTrash.Count > 1000)
        {
            AuditTrash.RemoveAt(AuditTrash.Count - 1);
        }
    }

    private void WireAuditTrashEntry(AuditEntry entry)
    {
        entry.RestoreCommand = new RelayCommand(() => RestoreAudit(entry));
        entry.PurgeCommand = new RelayCommand(() => RequestPurgeAudit(entry));
    }

    private void RestoreAudit(AuditEntry entry)
    {
        if (!AuditTrash.Remove(entry))
        {
            return;
        }

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

    private void PersistAudit()
    {
        _auditStore.Save(Audit);
        _auditStore.SaveTrash(AuditTrash);
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

    [RelayCommand]
    private void OpenSuiteBuilder()
    {
        SuiteSources.Clear();
        SuiteBuilderSteps.Clear();
        MappingEditors.Clear();
        var defaultCollection = SelectedCollection ?? Editing.Owner;
        foreach (var collection in Collections.Where(collection => collection.Parent is null))
        {
            AddSuiteSourceRows(collection, 0, defaultCollection);
        }

        foreach (var source in SuiteSources)
        {
            source.PropertyChanged += OnSuiteSourceChanged;
        }

        RebuildSuiteBuilderSteps();
        SuiteBuilderName = $"组装套件 {Suites.Count + 1}";
        IsSuiteBuilderOpen = true;
    }

    private void AddSuiteSourceRows(
        RequestCollection collection,
        int depth,
        RequestCollection? defaultCollection
    )
    {
        var isSelected =
            defaultCollection is null || ReferenceEquals(collection, defaultCollection);
        SuiteSources.Add(
            new SuiteSourceCollection
            {
                Collection = collection,
                Depth = depth,
                IsSelected = isSelected,
            }
        );

        foreach (var child in collection.Children)
        {
            AddSuiteSourceRows(child, depth + 1, defaultCollection);
        }
    }

    private void OnSuiteSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SuiteSourceCollection.IsSelected) && IsSuiteBuilderOpen)
        {
            RebuildSuiteBuilderSteps();
        }
    }

    private void RebuildSuiteBuilderSteps()
    {
        var selectedRequests = SuiteSources
            .Where(source => source.IsSelected)
            .SelectMany(source => source.Collection.Requests)
            .ToList();
        var existing = SuiteBuilderSteps.ToDictionary(wrapper => wrapper.Step.RequestId);
        var next = new List<SuiteBuilderStep>();

        foreach (var request in selectedRequests)
        {
            if (!existing.TryGetValue(request.Id, out var wrapper))
            {
                wrapper = new SuiteBuilderStep
                {
                    Step = new TestStep
                    {
                        RequestId = request.Id,
                        RequestName = request.Name,
                        Method = request.Method,
                        Url = request.Url,
                        DelayMs = 200,
                    },
                    DelayText = "200",
                };
            }

            wrapper.EditMappingsCommand = new RelayCommand(() => OpenMappingEditor(wrapper));
            next.Add(wrapper);
        }

        SuiteBuilderSteps.Clear();
        foreach (var wrapper in next)
        {
            SuiteBuilderSteps.Add(wrapper);
        }
    }

    [RelayCommand]
    private void CancelSuiteBuilder()
    {
        IsSuiteBuilderOpen = false;
        IsMappingEditorOpen = false;
    }

    [RelayCommand]
    private void AddMappingRule()
    {
        var editor = CreateMappingEditor(new InputMapping());
        MappingEditors.Add(editor);
    }

    private InputMappingEditor CreateMappingEditor(InputMapping source)
    {
        var editor = new InputMappingEditor
        {
            SourceOptions = _mappingSourceOptions,
            SelectedSource = _mappingSourceOptions.FirstOrDefault(option =>
                option.Id == source.SourceStepId
            ),
            SourcePath = source.SourcePath,
            TargetKind = string.IsNullOrWhiteSpace(source.TargetKind) ? "Body" : source.TargetKind,
            TargetPath = source.TargetPath,
            Template = string.IsNullOrWhiteSpace(source.Template) ? "{value}" : source.Template,
        };
        editor.RemoveCommand = new RelayCommand(() => MappingEditors.Remove(editor));
        return editor;
    }

    private void OpenMappingEditor(SuiteBuilderStep step)
    {
        var index = SuiteBuilderSteps.IndexOf(step);
        _mappingSourceOptions = SuiteBuilderSteps
            .Take(Math.Max(0, index))
            .Select(
                (source, sourceIndex) =>
                    new SuiteStepOption(source.Step.Id, $"{sourceIndex + 1}. {source.RequestName}")
            )
            .ToList();

        MappingEditors.Clear();
        foreach (var mapping in step.Step.InputMappings)
        {
            MappingEditors.Add(CreateMappingEditor(mapping));
        }

        if (MappingEditors.Count == 0)
        {
            MappingEditors.Add(CreateMappingEditor(new InputMapping()));
        }

        MappingStep = step;
        MappingTitle = $"入参映射 · {step.RequestName}";
        IsMappingEditorOpen = true;
    }

    [RelayCommand]
    private void CancelMappingEditor() => IsMappingEditorOpen = false;

    [RelayCommand]
    private void SaveMappingEditor()
    {
        if (MappingStep is not { } step)
        {
            return;
        }

        foreach (var editor in MappingEditors)
        {
            if (editor.SelectedSource is null)
            {
                editor.Error = "请选择来源步骤";
                return;
            }

            if (string.IsNullOrWhiteSpace(editor.TargetPath))
            {
                editor.Error = "请填写目标字段";
                return;
            }
        }

        step.Step.InputMappings.Clear();
        foreach (var editor in MappingEditors)
        {
            step.Step.InputMappings.Add(
                new InputMapping
                {
                    SourceStepId = editor.SelectedSource?.Id ?? Guid.Empty,
                    SourcePath = editor.SourcePath.Trim(),
                    TargetKind = editor.TargetKind,
                    TargetPath = editor.TargetPath.Trim(),
                    Template = string.IsNullOrWhiteSpace(editor.Template)
                        ? "{value}"
                        : editor.Template,
                }
            );
        }

        step.RefreshMappingLabel();
        IsMappingEditorOpen = false;
    }

    [RelayCommand]
    private void CreateSuiteFromSources()
    {
        var steps = new List<TestStep>();
        var previousStepIds = new HashSet<Guid>();
        foreach (var wrapper in SuiteBuilderSteps)
        {
            if (!int.TryParse(wrapper.DelayText, out var delay))
            {
                delay = 200;
            }

            foreach (var mapping in wrapper.Step.InputMappings)
            {
                if (!previousStepIds.Contains(mapping.SourceStepId))
                {
                    ShowToast($"步骤「{wrapper.RequestName}」的映射来源不在前序步骤中");
                    return;
                }
            }

            wrapper.Step.DelayMs = Math.Clamp(delay, 0, 60_000);
            previousStepIds.Add(wrapper.Step.Id);
            steps.Add(wrapper.Step);
        }

        if (steps.Count == 0)
        {
            ShowToast("请选择至少一个包含请求的集合");
            return;
        }

        var name = SuiteBuilderName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"组装套件 {Suites.Count + 1}";
        }

        var suite = new TestSuite { Name = name, StopOnError = true };

        foreach (var step in steps)
        {
            suite.Steps.Add(step);
        }

        Suites.Add(suite);
        SelectedSuite = suite;
        SelectedSuiteResult = null;
        IsSuiteBuilderOpen = false;
        Persist();
        ShowToast($"已创建套件（{steps.Count} 个步骤）");
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

        var suite = new TestSuite { Name = $"{collection.Name} · 回归测试", StopOnError = true };

        foreach (var request in collection.Requests)
        {
            suite.Steps.Add(
                new TestStep
                {
                    RequestId = request.Id,
                    RequestName = request.Name,
                    Method = request.Method,
                    Url = request.Url,
                }
            );
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
                CancellationToken.None
            );
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
        CancellationToken cancellationToken
    )
    {
        return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var suite =
                Suites.FirstOrDefault(candidate => candidate.Id == suiteId)
                ?? throw new KeyNotFoundException($"没有找到 id 为 {suiteId} 的套件。");
            return await RunSuiteCoreAsync(suite, "MCP", "run_suite", client, cancellationToken);
        });
    }

    private async Task<TestRunResult> RunSuiteCoreAsync(
        TestSuite suite,
        string source,
        string tool,
        string? client,
        CancellationToken cancellationToken
    )
    {
        var steps = new List<TestStepResult>();
        var stopped = false;
        var stepResults = new Dictionary<Guid, HttpResult>();

        for (var index = 0; index < suite.Steps.Count; index++)
        {
            var step = suite.Steps[index];

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
                    break;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                break;
            }

            var request = Collections
                .SelectMany(collection => collection.Requests)
                .FirstOrDefault(candidate => candidate.Id == step.RequestId);

            if (request is null)
            {
                steps.Add(
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
                    }
                );

                if (suite.StopOnError)
                {
                    stopped = true;
                    break;
                }

                continue;
            }

            var effectiveRequest = step.InputMappings.Count > 0 ? request.Clone() : request;
            var mappingError =
                step.InputMappings.Count > 0
                    ? ApplyInputMappings(effectiveRequest, step, stepResults)
                    : null;

            if (mappingError is not null)
            {
                var failure = HttpRequestService.Failure(mappingError);
                AddHistory(effectiveRequest, failure);
                RecordAudit(effectiveRequest, null, failure, source, tool, client);
                stepResults[step.Id] = failure;
                steps.Add(
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
                    }
                );

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
                composed = RequestComposer.Compose(effectiveRequest, ActiveEnvironment);
                result = await _http.SendAsync(
                    composed,
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

            stepResults[step.Id] = result;
            AddHistory(effectiveRequest, result);
            RecordAudit(effectiveRequest, composed, result, source, tool, client);

            var statusMatches = step.ExpectedStatus is null
                ? result.StatusCode is >= 200 and < 400
                : result.StatusCode == step.ExpectedStatus;
            var bodyMatches =
                string.IsNullOrWhiteSpace(step.BodyContains)
                || result.Body.Contains(step.BodyContains, StringComparison.OrdinalIgnoreCase);
            var passed = result.HasResponse && statusMatches && bodyMatches;
            var error = result.Error;
            if (passed is false && error is null)
            {
                error = !statusMatches
                    ? $"期望状态 {step.ExpectedStatus?.ToString() ?? "2xx/3xx"}，实际 {result.StatusCode}"
                    : $"响应不包含 \"{step.BodyContains}\"";
            }

            steps.Add(
                new TestStepResult
                {
                    StepId = step.Id,
                    RequestId = request.Id,
                    Name = request.Name,
                    Method = request.Method,
                    Url = composed?.Url ?? request.Url,
                    StatusCode = result.StatusCode,
                    StatusText = result.ReasonPhrase,
                    ElapsedMs = result.ElapsedMs,
                    Passed = passed,
                    Error = error,
                    ResponsePreview = Truncate(result.Body, 500),
                }
            );

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
            Steps = steps,
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

    private void OnTrackedEditingPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e
    )
    {
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
        OnPropertyChanged(nameof(IsTextBody));
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
            TimeoutSeconds = request.TimeoutSeconds,
            BodyMode = request.BodyMode,
            Body = request.Body,
            StatusCode = result.StatusCode,
            DurationMs = result.ElapsedMs,
            Params = request
                .Params.ActiveRows.Select(row => new RowSnapshot(row.Key, row.Value, row.IsEnabled))
                .ToList(),
            Headers = request
                .Headers.ActiveRows.Select(row => new RowSnapshot(
                    row.Key,
                    row.Value,
                    row.IsEnabled
                ))
                .ToList(),
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
        UpdateSelectionFlags();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsRequestSelected));
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(EditorHint));
        OnPropertyChanged(nameof(CanExportCurl));
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

        collection.IsExpanded = true;
        nodes.Add(GetCollectionNode(collection, depth, reusable));
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

        _store.Save(
            WorkspaceStore.Capture(
                Collections,
                Environments,
                History,
                RecycledHistory,
                Suites,
                ActiveEnvironment?.Id
            )
        );
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
                Name = collectionDto.Name,
                IsFavorite = collectionDto.IsFavorite,
            };

            foreach (var requestDto in collectionDto.Requests)
            {
                var request = WorkspaceStore.ToModel(requestDto);
                request.Owner = collection;
                collection.Requests.Add(request);
            }

            Collections.Add(collection);
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
                LastSummary = string.IsNullOrWhiteSpace(suiteDto.LastSummary)
                    ? "尚未运行"
                    : suiteDto.LastSummary,
            };

            foreach (var stepDto in suiteDto.Steps)
            {
                var step = new TestStep
                {
                    Id = stepDto.Id == Guid.Empty ? Guid.NewGuid() : stepDto.Id,
                    RequestId = stepDto.RequestId,
                    RequestName = stepDto.RequestName,
                    Method = stepDto.Method,
                    Url = stepDto.Url,
                    DelayMs = stepDto.DelayMs,
                    ExpectedStatus = stepDto.ExpectedStatus,
                    BodyContains = stepDto.BodyContains,
                };

                foreach (var mappingDto in stepDto.InputMappings)
                {
                    step.InputMappings.Add(
                        new InputMapping
                        {
                            SourceStepId = mappingDto.SourceStepId,
                            SourcePath = mappingDto.SourcePath,
                            TargetKind = mappingDto.TargetKind,
                            TargetPath = mappingDto.TargetPath,
                            Template = mappingDto.Template,
                        }
                    );
                }

                suite.Steps.Add(step);
            }

            Suites.Add(suite);
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
