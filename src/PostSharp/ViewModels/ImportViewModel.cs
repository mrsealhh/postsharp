using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;
using PostSharp.Services;
using TKCode.Arc;

namespace PostSharp.ViewModels;

/// <summary>Owns the import dialog state and turns parsed documents into workspace changes.</summary>
public partial class ImportViewModel : ObservableObject
{
    private readonly IImportHost _host;
    private readonly ImportProviderRegistry _providers;

    private CancellationTokenSource? _importCts;
    private OpenApiImportDocument? _pendingOpenApiDocument;

    public ImportViewModel(IImportHost host, ImportProviderRegistry? providers = null)
    {
        _host = host;
        _providers = providers ?? ImportProviderRegistry.CreateDefault();
        ImportWarnings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasImportWarnings));
        ImportReport.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasImportReport));
    }

    [ObservableProperty]
    private bool _isImportDialogOpen;

    [ObservableProperty]
    private string _importSource = "PostmanCollection";

    [ObservableProperty]
    private string _importUrl = string.Empty;

    [ObservableProperty]
    private string _postmanJson = string.Empty;

    [ObservableProperty]
    private string _curlCommand = string.Empty;

    [ObservableProperty]
    private string _harJson = string.Empty;

    [ObservableProperty]
    private bool _harIncludeStaticResources;

    [ObservableProperty]
    private string _insomniaJson = string.Empty;

    [ObservableProperty]
    private string _hoppscotchJson = string.Empty;

    [ObservableProperty]
    private string _importStatus =
        "支持 OpenAPI 3.x / Swagger 2.0，JSON 或 YAML；也可以填写可直接访问的文档 URL。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoImportError))]
    private bool _hasImportError;

    public bool HasNoImportError => !HasImportError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotImporting))]
    private bool _isImporting;

    public ObservableCollection<string> ImportWarnings { get; } = new();

    public ObservableCollection<string> ImportReport { get; } = new();

    public bool HasImportWarnings => ImportWarnings.Count > 0;

    public bool HasImportReport => ImportReport.Count > 0;

    public bool IsNotImporting => !IsImporting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportSourceToggleLabel))]
    private bool _isImportSourceExpanded = true;

    public string ImportSourceToggleLabel => IsImportSourceExpanded ? "收起来源" : "展开来源";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportReportToggleLabel))]
    private bool _isImportReportExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportWarningsToggleLabel))]
    private bool _isImportWarningsExpanded;

    public string ImportReportToggleLabel => IsImportReportExpanded ? "收起" : "展开";

    public string ImportWarningsToggleLabel => IsImportWarningsExpanded ? "收起" : "展开";

    public bool IsOpenApiImport => ImportSource == "OpenApi";

    public bool IsPostmanCollectionImport => ImportSource == "PostmanCollection";

    public bool IsPostmanEnvironmentImport => ImportSource == "PostmanEnvironment";

    public bool IsCurlImport => ImportSource == "Curl";

    public bool IsHarImport => ImportSource == "Har";

    public bool IsInsomniaImport => ImportSource == "Insomnia";

    public bool IsHoppscotchImport => ImportSource == "Hoppscotch";

    public bool IsOtherToolsImport => ImportSource == "OtherTools";

    public bool IsApifoxImport => ImportSource == "Apifox";

    public ObservableCollection<OpenApiImportServer> OpenApiServers { get; } = new();

    [ObservableProperty]
    private OpenApiImportServer? _selectedOpenApiServer;

    [ObservableProperty]
    private bool _hasPendingOpenApiImport;

    [RelayCommand]
    private void OpenImportDialog()
    {
        ClearPendingOpenApiImport();
        ImportSource = "PostmanCollection";
        ImportUrl = string.Empty;
        PostmanJson = string.Empty;
        CurlCommand = string.Empty;
        HarJson = string.Empty;
        HarIncludeStaticResources = false;
        InsomniaJson = string.Empty;
        HoppscotchJson = string.Empty;
        ImportWarnings.Clear();
        ImportReport.Clear();
        IsImportSourceExpanded = true;
        IsImportReportExpanded = false;
        IsImportWarningsExpanded = false;
        ImportStatus = ImportStatusForSource(ImportSource);
        HasImportError = false;
        IsImportDialogOpen = true;
    }

    [RelayCommand]
    private void SelectImportSource(string source)
    {
        if (
            source
            is "OpenApi"
                or "PostmanCollection"
                or "PostmanEnvironment"
                or "Curl"
                or "Har"
                or "Insomnia"
                or "Hoppscotch"
                or "OtherTools"
                or "Apifox"
        )
        {
            ImportSource = source;
        }
    }

    [RelayCommand]
    private void ToggleImportSource() => IsImportSourceExpanded = !IsImportSourceExpanded;

    [RelayCommand]
    private void ToggleImportReport() => IsImportReportExpanded = !IsImportReportExpanded;

    [RelayCommand]
    private void ToggleImportWarnings() => IsImportWarningsExpanded = !IsImportWarningsExpanded;

    partial void OnImportSourceChanged(string value)
    {
        ClearPendingOpenApiImport();
        OnPropertyChanged(nameof(IsOpenApiImport));
        OnPropertyChanged(nameof(IsPostmanCollectionImport));
        OnPropertyChanged(nameof(IsPostmanEnvironmentImport));
        OnPropertyChanged(nameof(IsCurlImport));
        OnPropertyChanged(nameof(IsHarImport));
        OnPropertyChanged(nameof(IsInsomniaImport));
        OnPropertyChanged(nameof(IsHoppscotchImport));
        OnPropertyChanged(nameof(IsOtherToolsImport));
        OnPropertyChanged(nameof(IsApifoxImport));
        ImportWarnings.Clear();
        ImportReport.Clear();
        IsImportReportExpanded = false;
        IsImportWarningsExpanded = false;
        HasImportError = false;
        ImportStatus = ImportStatusForSource(value);
    }

    private static string ImportStatusForSource(string source)
    {
        return source switch
        {
            "PostmanCollection" =>
                "选择 Postman Collection v2.1 JSON 文件，或把集合导出内容粘贴到下方。支持嵌套文件夹、认证、查询参数和常见请求体。",
            "PostmanEnvironment" => "选择 Postman Environment JSON；变量会创建或合并到同名环境。",
            "Curl" =>
                "粘贴以 curl 开头的 bash 命令，或以 Invoke-RestMethod / Invoke-WebRequest 开头的 PowerShell 命令。支持常用参数。",
            "Har" =>
                "选择浏览器、Postman 或 Apifox 导出的 .har 文件，或粘贴 HAR JSON。默认只导入 XHR / Fetch，响应内容会被忽略。",
            "Insomnia" =>
                "选择 Insomnia v4 JSON 导出文件，或粘贴 JSON。支持请求组、环境变量和常见认证；脚本不会执行。",
            "Hoppscotch" =>
                "选择 Hoppscotch Collection JSON 导出文件，或粘贴 JSON。支持文件夹、params/headers/body 和基础认证。",
            "OtherTools" =>
                "YApi / ApiPost / Eolink 建议先导出 OpenAPI 或 Postman Collection，再使用对应导入器。",
            "Apifox" =>
                "Apifox 建议使用「导出数据 → Postman / OpenAPI」，或导出 HAR；原生格式暂未直接解析。",
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

        BeginImport("正在下载并解析 OpenAPI 文档…", default);

        try
        {
            var document = await OpenApiImportService
                .LoadFromUrlAsync(ImportUrl, _importCts!.Token)
                .ConfigureAwait(true);
            ApplyOpenApiDocument(document);
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
            EndImport();
        }
    }

    [RelayCommand]
    private void FillSpringDocUrl()
    {
        var current = ImportUrl.Trim();
        if (current.Length == 0)
        {
            ImportUrl = "http://localhost:8080/v3/api-docs";
            ImportStatus = "已填入 springdoc 默认地址；请把主机和端口改成你的服务地址。";
            HasImportError = false;
            return;
        }

        if (current.Contains("/v3/api-docs", StringComparison.OrdinalIgnoreCase))
        {
            ImportStatus = "当前地址已经是 springdoc / Knife4j 的 /v3/api-docs 地址。";
            HasImportError = false;
            return;
        }

        ImportUrl = current.TrimEnd('/') + "/v3/api-docs";
        ImportStatus = "已补全 springdoc / Knife4j 地址，点击「下载并导入」继续。";
        HasImportError = false;
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

        BeginImport("正在读取并解析 OpenAPI 文件…", cancellationToken);

        try
        {
            var content = await File.ReadAllTextAsync(path, _importCts!.Token).ConfigureAwait(true);
            var document = await OpenApiImportService
                .ParseWithExternalReferencesAsync(
                    content,
                    Path.GetFileName(path),
                    new Uri(Path.GetFullPath(path)),
                    _importCts.Token
                )
                .ConfigureAwait(true);
            ApplyOpenApiDocument(document);
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
            EndImport();
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

        BeginImport("正在读取并解析 Postman Collection 文件…", cancellationToken);

        try
        {
            var content = await File.ReadAllTextAsync(path, _importCts!.Token).ConfigureAwait(true);
            var result = await Task.Run(
                    () => PostmanImportService.Parse(content, Path.GetFileName(path)),
                    _importCts.Token
                )
                .ConfigureAwait(true);
            ApplyCollectionResult(result);
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
            EndImport();
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

        BeginImport("正在解析粘贴的 Postman Collection…", default);

        try
        {
            var result = await Task.Run(
                    () => PostmanImportService.Parse(PostmanJson, "粘贴的 Postman Collection"),
                    _importCts!.Token
                )
                .ConfigureAwait(true);
            ApplyCollectionResult(result);
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
            EndImport();
        }
    }

    public async Task ImportPostmanEnvironmentFileAsync(
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
            ImportStatus = "Postman 环境文件不存在，请重新选择。";
            return;
        }

        BeginImport("正在解析 Postman 环境文件…", cancellationToken);

        try
        {
            var content = await File.ReadAllTextAsync(path, _importCts!.Token).ConfigureAwait(true);
            var result = await Task.Run(
                    () => PostmanEnvironmentImportService.Parse(content, Path.GetFileName(path)),
                    _importCts.Token
                )
                .ConfigureAwait(true);
            ApplyEnvironmentResult(result);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "导入 Postman 环境已取消 {FilePath}", path);
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "导入 Postman 环境失败 {FilePath}", path);
        }
        finally
        {
            EndImport();
        }
    }

    public async Task ImportInsomniaFileAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        if (IsImporting)
        {
            return;
        }

        BeginImport("正在解析 Insomnia v4 导出文件…", cancellationToken);

        try
        {
            var content = await File.ReadAllTextAsync(path, _importCts!.Token).ConfigureAwait(true);
            var result = await Task.Run(
                    () => InsomniaImportService.Parse(content, Path.GetFileName(path)),
                    _importCts.Token
                )
                .ConfigureAwait(true);
            ApplyCollectionResult(result);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "从文件导入 Insomnia 已取消 {FilePath}", path);
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "从文件导入 Insomnia 失败 {FilePath}", path);
        }
        finally
        {
            EndImport();
        }
    }

    [RelayCommand]
    private async Task ImportInsomniaTextAsync()
    {
        if (IsImporting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(InsomniaJson))
        {
            HasImportError = true;
            ImportStatus = "请粘贴 Insomnia v4 JSON，或改为选择文件。";
            return;
        }

        BeginImport("正在解析粘贴的 Insomnia 导出…", default);

        try
        {
            var result = await Task.Run(
                    () => InsomniaImportService.Parse(InsomniaJson, "粘贴的 Insomnia 导出"),
                    _importCts!.Token
                )
                .ConfigureAwait(true);
            ApplyCollectionResult(result);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "导入粘贴的 Insomnia 已取消");
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "导入粘贴的 Insomnia 失败");
        }
        finally
        {
            EndImport();
        }
    }

    public async Task ImportHoppscotchFileAsync(
        string path,
        CancellationToken cancellationToken = default
    )
    {
        if (IsImporting)
        {
            return;
        }

        BeginImport("正在解析 Hoppscotch Collection…", cancellationToken);

        try
        {
            var content = await File.ReadAllTextAsync(path, _importCts!.Token).ConfigureAwait(true);
            var result = await Task.Run(
                    () => HoppscotchImportService.Parse(content, Path.GetFileName(path)),
                    _importCts.Token
                )
                .ConfigureAwait(true);
            ApplyCollectionResult(result);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "从文件导入 Hoppscotch 已取消 {FilePath}", path);
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "从文件导入 Hoppscotch 失败 {FilePath}", path);
        }
        finally
        {
            EndImport();
        }
    }

    [RelayCommand]
    private async Task ImportHoppscotchTextAsync()
    {
        if (IsImporting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(HoppscotchJson))
        {
            HasImportError = true;
            ImportStatus = "请粘贴 Hoppscotch Collection JSON，或改为选择文件。";
            return;
        }

        BeginImport("正在解析粘贴的 Hoppscotch Collection…", default);

        try
        {
            var result = await Task.Run(
                    () => HoppscotchImportService.Parse(HoppscotchJson, "粘贴的 Hoppscotch"),
                    _importCts!.Token
                )
                .ConfigureAwait(true);
            ApplyCollectionResult(result);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "导入粘贴的 Hoppscotch 已取消");
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "导入粘贴的 Hoppscotch 失败");
        }
        finally
        {
            EndImport();
        }
    }

    public async Task ImportBatchFilesAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default
    )
    {
        if (IsImporting || paths.Count == 0)
        {
            return;
        }

        BeginImport($"正在批量导入 {paths.Count} 个文件…", cancellationToken);
        var warnings = new List<string>();
        var requests = 0;
        var folders = 0;
        var environments = 0;
        var succeeded = 0;
        var failed = 0;

        try
        {
            foreach (var path in paths)
            {
                _importCts!.Token.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(path);
                try
                {
                    var content = await File.ReadAllTextAsync(path, _importCts.Token)
                        .ConfigureAwait(true);
                    var kind = ImportFileDetector.Detect(path, content);
                    var provider =
                        _providers.Find(kind)
                        ?? throw new InvalidOperationException(
                            "无法识别导入格式，请使用 Postman / OpenAPI / HAR / Insomnia / Hoppscotch 文件。"
                        );
                    var payload = await Task.Run(
                            () =>
                                provider.Parse(
                                    fileName,
                                    content,
                                    new ImportProviderContext(HarIncludeStaticResources)
                                ),
                            _importCts.Token
                        )
                        .ConfigureAwait(true);
                    switch (payload)
                    {
                        case OpenApiImportPayload openApi:
                        {
                            var outcome = _host.ApplyOpenApiImport(openApi.Document);
                            requests += outcome.RequestCount;
                            folders += outcome.FolderCount;
                            AddImportReport(
                                $"成功：{fileName} · {provider.FormatName} · {outcome.RequestCount} 个请求，{outcome.FolderCount} 个分组"
                            );
                            warnings.AddRange(
                                openApi.Document.Warnings.Select(warning =>
                                    $"{fileName}：{warning}"
                                )
                            );
                            succeeded++;
                            break;
                        }
                        case CollectionImportPayload collection:
                        {
                            var outcome = _host.ApplyCollectionImport(collection.Result);
                            requests += outcome.RequestCount;
                            folders += outcome.FolderCount;
                            if (outcome.EnvironmentName is not null)
                            {
                                environments++;
                            }

                            AddImportReport(
                                $"成功：{fileName} · {provider.FormatName} · {outcome.RequestCount} 个请求，{outcome.FolderCount} 个分组"
                            );
                            warnings.AddRange(
                                collection.Result.Warnings.Select(warning =>
                                    $"{fileName}：{warning}"
                                )
                            );
                            succeeded++;
                            break;
                        }
                        case EnvironmentImportPayload environment:
                        {
                            var environmentName =
                                _host.ApplyImportedEnvironment(environment.Result.Environment)
                                ?? environment.Result.Environment.Name;
                            environments++;
                            AddImportReport(
                                $"成功：{fileName} · {provider.FormatName} · 环境「{environmentName}」"
                            );
                            warnings.AddRange(
                                environment.Result.Warnings.Select(warning =>
                                    $"{fileName}：{warning}"
                                )
                            );
                            succeeded++;
                            break;
                        }
                        case CurlImportPayload curl:
                        {
                            var request = _host.ApplyCurlImport(curl.Result);
                            requests++;
                            AddImportReport(
                                $"成功：{fileName} · {provider.FormatName} · {request.Method} {request.Url}"
                            );
                            warnings.AddRange(
                                curl.Result.Warnings.Select(warning => $"{fileName}：{warning}")
                            );
                            succeeded++;
                            break;
                        }
                        default:
                            throw new InvalidOperationException(
                                "无法识别导入格式，请使用 Postman / OpenAPI / HAR / Insomnia / Hoppscotch 文件。"
                            );
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failed++;
                    var message = DescribeImportFailure(exception);
                    AddImportReport($"失败：{fileName} · {message}");
                    warnings.Add($"{fileName}：{message}");
                    Log.Warning(exception, "批量导入文件失败 {FilePath}", path);
                }
            }

            SetImportWarnings(warnings);
            _host.CompleteImport();

            IsImportDialogOpen = true;
            HasImportError = succeeded == 0;
            ImportStatus =
                $"批量导入完成：{succeeded} 个文件，{requests} 个请求，{folders} 个分组，{environments} 个环境"
                + (failed > 0 ? $"，{failed} 个失败。" : "。");
            if (succeeded > 0)
            {
                _host.ShowToast(
                    failed > 0
                        ? $"批量导入完成：{succeeded} 个成功，{failed} 个失败"
                        : $"批量导入完成：{succeeded} 个文件，{requests} 个请求"
                );
            }

            Log.Information(
                "批量导入完成：成功 {Succeeded} 个，失败 {Failed} 个，请求 {RequestCount}，分组 {FolderCount}，环境 {EnvironmentCount}",
                succeeded,
                failed,
                requests,
                folders,
                environments
            );
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "批量导入已取消");
            ImportStatus = "批量导入已取消。";
            HasImportError = true;
        }
        finally
        {
            EndImport();
        }
    }

    public async Task ImportHarFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (IsImporting)
        {
            return;
        }

        if (!File.Exists(path))
        {
            HasImportError = true;
            ImportStatus = "HAR 文件不存在，请重新选择。";
            return;
        }

        BeginImport("正在读取并解析 HAR 文件…", cancellationToken);

        try
        {
            var content = await File.ReadAllTextAsync(path, _importCts!.Token).ConfigureAwait(true);
            var result = await Task.Run(
                    () =>
                        HarImportService.Parse(
                            content,
                            Path.GetFileName(path),
                            HarIncludeStaticResources
                        ),
                    _importCts.Token
                )
                .ConfigureAwait(true);
            ApplyCollectionResult(result);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "从文件导入 HAR 已取消 {FilePath}", path);
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "从文件导入 HAR 失败 {FilePath}", path);
        }
        finally
        {
            EndImport();
        }
    }

    [RelayCommand]
    private async Task ImportHarTextAsync()
    {
        if (IsImporting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(HarJson))
        {
            HasImportError = true;
            ImportStatus = "请粘贴 HAR JSON，或改为选择 .har 文件。";
            return;
        }

        BeginImport("正在解析粘贴的 HAR…", default);

        try
        {
            var result = await Task.Run(
                    () => HarImportService.Parse(HarJson, "粘贴的 HAR", HarIncludeStaticResources),
                    _importCts!.Token
                )
                .ConfigureAwait(true);
            ApplyCollectionResult(result);
        }
        catch (OperationCanceledException exception)
        {
            Log.Debug(exception, "导入粘贴的 HAR 已取消");
            ImportStatus = "导入已取消。";
            HasImportError = true;
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "导入粘贴的 HAR 失败");
        }
        finally
        {
            EndImport();
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
            ImportWarnings.Clear();
            ImportReport.Clear();
            var result = CurlCommand
                .TrimStart()
                .StartsWith("Invoke-", StringComparison.OrdinalIgnoreCase)
                ? PowerShellImportService.Parse(CurlCommand)
                : CurlImportService.Parse(CurlCommand);
            ApplyCurlResult(result);
        }
        catch (Exception exception)
        {
            HasImportError = true;
            ImportStatus = $"导入失败：{DescribeImportFailure(exception)}";
            Log.Error(exception, "导入 cURL 失败");
        }
    }

    private void ApplyOpenApiDocument(OpenApiImportDocument document)
    {
        if (document.Servers.Count > 1)
        {
            _pendingOpenApiDocument = document;
            OpenApiServers.Clear();
            foreach (var server in document.Servers)
            {
                OpenApiServers.Add(server);
            }

            SelectedOpenApiServer = OpenApiServers[0];
            HasPendingOpenApiImport = true;
            HasImportError = false;
            ImportStatus = $"检测到 {document.Servers.Count} 个服务器，请选择后导入。";
            IsImportDialogOpen = true;
            return;
        }

        ApplyOpenApiDocumentCore(document);
    }

    [RelayCommand]
    private void ConfirmOpenApiImport()
    {
        if (_pendingOpenApiDocument is not { } pending)
        {
            return;
        }

        var index = SelectedOpenApiServer?.Index ?? 0;
        var document = pending.WithServer(index);
        ClearPendingOpenApiImport();
        ApplyOpenApiDocumentCore(document);
    }

    private void ApplyOpenApiDocumentCore(OpenApiImportDocument document)
    {
        var outcome = _host.ApplyOpenApiImport(document);
        SetImportWarnings(document.Warnings);
        AddImportReport(
            $"已导入 OpenAPI「{outcome.RootName}」：{outcome.RequestCount} 个请求，{outcome.FolderCount} 个分组"
        );
        _host.CompleteImport();

        if (document.Warnings.Count == 0)
        {
            IsImportDialogOpen = false;
            _host.ShowToast(
                $"已导入「{outcome.RootName}」：{outcome.RequestCount} 个请求，{outcome.FolderCount} 个分组"
            );
        }
        else
        {
            ImportStatus =
                $"导入完成：{outcome.RootName}，{outcome.RequestCount} 个请求，{outcome.FolderCount} 个分组；{document.Warnings.Count} 条提示见下方。";
            HasImportError = false;
        }

        Log.Information(
            "OpenAPI 导入完成 {Source}：{Title} {Version}，{RequestCount} 个请求，{GroupCount} 个分组",
            document.Source,
            document.Title,
            document.Version,
            outcome.RequestCount,
            outcome.FolderCount
        );
    }

    private void ClearPendingOpenApiImport()
    {
        _pendingOpenApiDocument = null;
        OpenApiServers.Clear();
        SelectedOpenApiServer = null;
        HasPendingOpenApiImport = false;
    }

    private void ApplyCollectionResult(ImportResult result)
    {
        var outcome = _host.ApplyCollectionImport(result);
        SetImportWarnings(result.Warnings);
        AddImportReport(
            $"已导入 {result.Format}「{outcome.RootName}」：{outcome.RequestCount} 个请求，{outcome.FolderCount} 个分组"
        );
        _host.CompleteImport();

        var environmentText = outcome.EnvironmentName is null
            ? string.Empty
            : $"，并激活环境「{outcome.EnvironmentName}」";
        var warningText =
            result.Warnings.Count > 0
                ? $"；{result.Warnings.Count} 条兼容性提示见下方"
                : string.Empty;
        ImportStatus =
            $"导入完成：{outcome.RootName}，{outcome.RequestCount} 个请求，{outcome.FolderCount} 个分组{environmentText}{warningText}。";
        HasImportError = false;

        if (result.Warnings.Count == 0)
        {
            IsImportDialogOpen = false;
            _host.ShowToast($"已导入「{outcome.RootName}」：{outcome.RequestCount} 个请求");
        }

        Log.Information(
            "集合导入完成 {Format} {Source}：{Title}，{RequestCount} 个请求，{FolderCount} 个分组，{WarningCount} 条提示",
            result.Format,
            result.Source,
            outcome.RootName,
            outcome.RequestCount,
            outcome.FolderCount,
            result.Warnings.Count
        );
    }

    private void ApplyEnvironmentResult(EnvironmentImportResult result)
    {
        var outcome = _host.ApplyEnvironmentImport(result);
        var environmentName = outcome.EnvironmentName ?? result.Environment.Name;
        SetImportWarnings(result.Warnings);
        AddImportReport(
            $"已导入环境「{environmentName}」：{result.Environment.Variables.Count} 个变量"
        );
        _host.CompleteImport();

        ImportStatus =
            $"环境导入完成：{environmentName}，{result.Environment.Variables.Count} 个变量。";
        HasImportError = false;
        if (result.Warnings.Count == 0)
        {
            IsImportDialogOpen = false;
            _host.ShowToast($"已导入环境「{environmentName}」");
        }

        Log.Information(
            "环境导入完成 {Source}：{EnvironmentName}，{VariableCount} 个变量，{WarningCount} 条提示",
            result.Source,
            environmentName,
            result.Environment.Variables.Count,
            result.Warnings.Count
        );
    }

    private void ApplyCurlResult(CurlImportResult result)
    {
        var request = _host.ApplyCurlImport(result);
        _host.CompleteImport();

        foreach (var warning in result.Warnings)
        {
            Log.Warning("cURL 导入提示：{Warning}", warning);
        }

        SetImportWarnings(result.Warnings);
        AddImportReport($"已导入 cURL「{request.Name}」：{request.Method} {request.Url}");
        IsImportDialogOpen = false;
        HasImportError = false;
        var warningText =
            result.Warnings.Count > 0
                ? $"；{result.Warnings.Count} 条提示已写入日志"
                : string.Empty;
        _host.ShowToast($"已导入 cURL「{request.Name}」{warningText}");
        Log.Information(
            "cURL 导入完成 {Method} {Url}，{WarningCount} 条提示",
            request.Method,
            request.Url,
            result.Warnings.Count
        );
    }

    private void BeginImport(string status, CancellationToken cancellationToken)
    {
        ClearPendingOpenApiImport();
        _importCts?.Dispose();
        _importCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        IsImporting = true;
        HasImportError = false;
        ImportWarnings.Clear();
        ImportReport.Clear();
        ImportStatus = status;
    }

    private void EndImport()
    {
        IsImporting = false;
        _importCts?.Dispose();
        _importCts = null;
    }

    private void SetImportWarnings(IReadOnlyList<string> warnings)
    {
        ImportWarnings.Clear();
        IsImportWarningsExpanded = false;
        foreach (var warning in warnings)
        {
            ImportWarnings.Add(warning);
        }
    }

    private void AddImportReport(string line)
    {
        IsImportReportExpanded = false;
        ImportReport.Add(line);
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
}
