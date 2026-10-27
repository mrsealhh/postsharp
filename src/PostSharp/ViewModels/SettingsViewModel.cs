using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.ViewModels;

/// <summary>
/// Edits a draft copy of <see cref="AppSettings"/>; saving validates the log directory, writes the JSON file,
/// then pushes the values into the shared settings instance used by the running application.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _current;
    private readonly SettingsStore _store;
    private readonly StorageCoordinator _storage = StorageCoordinator.Current;
    private bool _fontDirectoriesPreviewActive;

    public SettingsViewModel(AppSettings current, SettingsStore store)
    {
        _current = current;
        _store = store;
        _draft = Clone(current);
        SelectedUiScale =
            UiScaleOptions.FirstOrDefault(option =>
                Math.Abs(option.Value - current.UiScale) < 0.001
            ) ?? UiScaleOptions[^1];
        foreach (var profile in _storage.Config.Profiles)
        {
            StorageProfiles.Add(profile.Clone());
        }

        RefreshStorageProviderOptions();
        _storage.StatusChanged += OnStorageStatusChanged;
        SettingsStorageProfile = FindProfile(
            _storage.Config.ProfileFor(StorageCategories.Settings)
        );
        WorkspaceStorageProfile = FindProfile(
            _storage.Config.ProfileFor(StorageCategories.Workspace)
        );
        SuitesStorageProfile = FindProfile(_storage.Config.ProfileFor(StorageCategories.Suites));
        SelectedStorageProfile = StorageProfiles.FirstOrDefault();
        _updateStatusMessage = InitialUpdateStatus();
        RefreshFontState();
    }

    private void RefreshStorageProviderOptions()
    {
        var registry = StoragePluginRegistry.Current;
        registry.EnsureLoaded();
        var selectedProvider = SelectedStorageProfile?.Provider ?? SelectedStorageProvider?.Value;
        StorageProviderOptions.Clear();
        StorageProviderOptions.Add(
            new StorageProviderOption(
                StorageProviders.LocalSqlite,
                "本地 SQLite",
                "内置存储，离线可用。",
                "数据库文件固定在 PostSharp 数据目录，不能修改连接串。"
            )
        );

        var pluginOptions = new List<StorageProviderOption>();
        var pluginLabels = new List<string>();
        foreach (var plugin in registry.Plugins)
        {
            try
            {
                var info = plugin.Info;
                var label = string.IsNullOrWhiteSpace(info.DisplayName)
                    ? info.Id
                    : info.DisplayName;
                pluginOptions.Add(
                    new StorageProviderOption(
                        info.Id,
                        label,
                        info.Description,
                        info.ConnectionStringPlaceholder
                    )
                );
                pluginLabels.Add(label);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "读取存储插件信息失败 {Plugin}", plugin.GetType().Name);
            }
        }

        foreach (
            var option in pluginOptions.OrderBy(option => option.Label, StringComparer.Ordinal)
        )
        {
            StorageProviderOptions.Add(option);
        }

        HasUsableStoragePlugins = pluginOptions.Count > 0;

        foreach (
            var provider in StorageProfiles
                .Select(profile => profile.Provider)
                .Where(provider =>
                    !string.Equals(provider, StorageProviders.LocalSqlite, StringComparison.Ordinal)
                    && !registry.IsAvailable(provider)
                )
                .Distinct(StringComparer.Ordinal)
        )
        {
            StorageProviderOptions.Add(
                new StorageProviderOption(
                    provider,
                    $"{provider}（插件未安装）",
                    "下载插件并重新扫描后即可连接。",
                    string.Empty
                )
            );
        }

        SelectedStorageProvider = StorageProviderOptions.FirstOrDefault(option =>
            string.Equals(option.Value, selectedProvider, StringComparison.Ordinal)
        );
        StoragePluginSummary =
            pluginLabels.Count == 0
                ? "尚未安装远程存储插件；本地 SQLite 始终可用。"
                : $"已安装 {pluginLabels.Count} 个远程存储插件：" + string.Join("、", pluginLabels);
        var issues = registry.Issues;
        HasStoragePluginIssue = issues.Count > 0;
        StoragePluginIssues = string.Join(Environment.NewLine, issues);
        OnPropertyChanged(nameof(CanAddStorageProfile));
        OnPropertyChanged(nameof(StoragePluginDirectory));
        OnPropertyChanged(nameof(StorageConnectionPlaceholder));
    }

    [RelayCommand]
    private void RescanStoragePlugins()
    {
        var issues = StoragePluginRegistry.Current.Refresh();
        _storage.InvalidateBackends();
        RefreshStorageProviderOptions();
        HasStorageError = issues.Count > 0;
        StorageStatusMessage =
            issues.Count == 0
                ? "存储插件已重新扫描。"
                : $"存储插件扫描完成，有 {issues.Count} 个问题；详见插件状态。";
    }

    [ObservableProperty]
    private AppSettings _draft;

    [ObservableProperty]
    private string _statusMessage = "修改后点击「保存设置」生效。";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _fontStatusMessage = "可导入本地字体文件，或添加包含字体的目录。";

    [ObservableProperty]
    private bool _hasFontError;

    [ObservableProperty]
    private bool _isFontsBusy;

    [ObservableProperty]
    private StorageProfile? _selectedStorageProfile;

    [ObservableProperty]
    private StorageProviderOption? _selectedStorageProvider;

    [ObservableProperty]
    private StorageProfile? _settingsStorageProfile;

    [ObservableProperty]
    private StorageProfile? _workspaceStorageProfile;

    [ObservableProperty]
    private StorageProfile? _suitesStorageProfile;

    [ObservableProperty]
    private bool _isStorageBusy;

    [ObservableProperty]
    private string _storageStatusMessage =
        "选择本地 SQLite 或远程数据库；保存后新数据会写入所选存储。";

    [ObservableProperty]
    private bool _hasStorageError;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string _updateStatusMessage = string.Empty;

    public ObservableCollection<StorageProfile> StorageProfiles { get; } = new();

    public IReadOnlyList<UiScaleOption> UiScaleOptions { get; } =
    [
        new(0.6, "60%"),
        new(0.8, "80%"),
        new(0.9, "90%"),
        new(1.0, "100%"),
        new(1.2, "120%"),
        new(1.5, "150%"),
    ];

    [ObservableProperty]
    private UiScaleOption? _selectedUiScale;

    public ObservableCollection<StorageProviderOption> StorageProviderOptions { get; } = new();

    public bool IsStorageProfileEditable => SelectedStorageProfile?.IsRemote == true;

    public bool CanAddStorageProfile => !IsStorageBusy && HasUsableStoragePlugins;

    [ObservableProperty]
    private bool _hasUsableStoragePlugins;

    public string StoragePluginDirectory => AppPaths.StoragePluginDirectory;

    public string StorageConnectionPlaceholder =>
        SelectedStorageProvider is { Placeholder.Length: > 0 } option
            ? option.Placeholder
            : "选择存储类型后填写连接串";

    [ObservableProperty]
    private string _storagePluginSummary = string.Empty;

    [ObservableProperty]
    private bool _hasStoragePluginIssue;

    [ObservableProperty]
    private string _storagePluginIssues = string.Empty;

    public string LocalDatabasePath => AppPaths.DatabaseFile;

    public string CurrentVersionText => $"当前版本 {UpdateService.CurrentVersionText}";

    public string CheckUpdateButtonText => IsCheckingUpdates ? "检查中…" : "检查更新";

    public IReadOnlyList<string> ThemeOptions => ThemeService.Options;

    public ObservableCollection<string> FontOptions { get; } = new();

    public ObservableCollection<string> CustomFonts { get; } = new();

    public ObservableCollection<FontDirectoryItem> FontDirectories { get; } = new();

    public IReadOnlyList<string> CodeFontSizeOptions { get; } = new[] { "12", "13", "14", "16" };

    public IReadOnlyList<int> TimeoutOptions { get; } = new[] { 10, 30, 60, 120, 300 };

    public IReadOnlyList<int> LargeResponseThresholdOptions { get; } = new[] { 32, 64, 128, 256 };

    public string DefaultLogDirectory => AppPaths.DefaultLogDirectory;

    public string SettingsFilePath => AppPaths.DatabaseFile;

    public string ManagedFontDirectory => FontService.ManagedFontDirectory;

    public bool HasCustomFonts => CustomFonts.Count > 0;

    public void SetLogDirectory(string directory)
    {
        Draft.LogDirectory = directory;
        StatusMessage = "日志目录已选择，点击「保存设置」后生效。";
        HasError = false;
    }

    public void ReportStoragePluginFolderFailure(string message)
    {
        HasStorageError = true;
        StorageStatusMessage = message;
    }

    partial void OnIsCheckingUpdatesChanged(bool value) =>
        OnPropertyChanged(nameof(CheckUpdateButtonText));

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdates)
        {
            return;
        }

        IsCheckingUpdates = true;
        UpdateStatusMessage = "正在检查 GitHub 更新…";
        try
        {
            var result = await UpdateService.CheckAndStageAsync(
                _current,
                force: true,
                CancellationToken.None
            );
            UpdateStatusMessage = result.Message;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "手动检查更新失败");
            UpdateStatusMessage = "检查更新失败，请确认网络可以访问 GitHub 后重试。";
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    private static string InitialUpdateStatus()
    {
        if (UpdateService.PendingVersion is { } pending)
        {
            return $"新版本 {pending} 已下载，点击顶栏「安装更新」应用。";
        }

        return UpdateService.IsEnabledLayout
            ? "启动后会自动检查 GitHub 更新；下载完成后由你决定何时安装。"
            : "当前为开发运行目录，自动更新仅在已发布安装包中启用。";
    }

    public async Task ImportFontsAsync(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken = default
    )
    {
        if (IsFontsBusy)
        {
            return;
        }

        IsFontsBusy = true;
        HasFontError = false;
        FontStatusMessage = "正在导入字体文件…";

        try
        {
            var result = await FontService.ImportFilesAsync(sourcePaths, cancellationToken);
            RefreshFontState();
            HasFontError = false;
            FontStatusMessage =
                $"已导入 {result.FileCount} 个字体文件，当前可用自定义字体 {result.Families.Count} 个。"
                + "点击字体名可同时设为界面和代码字体，保存后生效。";
            Log.Information(
                "设置窗口导入字体完成：{FileCount} 个文件，{FamilyCount} 个字体族",
                result.FileCount,
                result.Families.Count
            );
        }
        catch (Exception exception)
        {
            HasFontError = true;
            FontStatusMessage =
                "导入失败：未能读取所选字体文件。请确认文件是完整的 TTF、OTF 或 TTC，然后重试。";
            Log.Error(exception, "导入字体文件失败");
        }
        finally
        {
            IsFontsBusy = false;
        }
    }

    public async Task AddFontDirectoryAsync(
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        if (IsFontsBusy)
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(directory.Trim());
        }
        catch (Exception exception)
        {
            HasFontError = true;
            FontStatusMessage = "字体目录无效，请选择一个本地文件夹。";
            Log.Error(exception, "字体目录路径无效 {FontDirectory}", directory);
            return;
        }

        if (
            Draft.FontDirectories.Contains(full, StringComparer.OrdinalIgnoreCase)
            || string.Equals(
                full,
                FontService.ManagedFontDirectory,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            HasFontError = false;
            FontStatusMessage = "该目录已经在字体目录列表中。";
            return;
        }

        if (!FontService.HasFontFiles(full))
        {
            HasFontError = true;
            FontStatusMessage = "该目录下没有 TTF、OTF 或 TTC 字体文件，请选择字体所在目录。";
            return;
        }

        IsFontsBusy = true;
        HasFontError = false;
        FontStatusMessage = "正在扫描字体目录…";
        await Task.Yield();

        try
        {
            Draft.FontDirectories = new List<string>(Draft.FontDirectories) { full };
            _fontDirectoriesPreviewActive = true;
            var families = FontService.Reload(Draft.FontDirectories);
            RefreshFontState();
            FontStatusMessage =
                $"已添加字体目录，共扫描到 {families.Count} 个自定义字体族。保存设置后下次启动仍会加载。";
            Log.Information("设置窗口添加字体目录 {FontDirectory}", full);
        }
        catch (Exception exception)
        {
            HasFontError = true;
            FontStatusMessage = "扫描字体目录失败，请确认目录可访问且字体文件完整。";
            Log.Error(exception, "扫描字体目录失败 {FontDirectory}", full);
        }
        finally
        {
            IsFontsBusy = false;
        }
    }

    public void RemoveFontDirectory(FontDirectoryItem item)
    {
        if (item.IsManaged)
        {
            return;
        }

        Draft.FontDirectories = Draft
            .FontDirectories.Where(directory =>
                !string.Equals(directory, item.Path, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();

        _fontDirectoriesPreviewActive = true;

        try
        {
            var families = FontService.Reload(Draft.FontDirectories);
            RefreshFontState();
            HasFontError = false;
            FontStatusMessage = $"已移除目录，当前自定义字体 {families.Count} 个。保存设置后生效。";
            Log.Information("设置窗口移除字体目录 {FontDirectory}", item.Path);
        }
        catch (Exception exception)
        {
            HasFontError = true;
            FontStatusMessage =
                "移除字体目录后重新加载失败，请点击「重新扫描」或重新打开设置窗口。";
            Log.Error(exception, "移除字体目录失败 {FontDirectory}", item.Path);
        }
    }

    public void ApplyFontToAll(string family)
    {
        Draft.UiFont = family;
        Draft.CodeFont = family;
        HasFontError = false;
        FontStatusMessage = $"已选择「{family}」作为界面和代码字体，点击「保存设置」后生效。";
    }

    /// <summary>Reloads the font collection from the saved settings; called when the window closes unsaved.</summary>
    public void DiscardFontPreview()
    {
        if (IsFontsBusy)
        {
            return;
        }

        if (!_fontDirectoriesPreviewActive)
        {
            return;
        }

        _fontDirectoriesPreviewActive = false;
        FontService.Reload(_current.FontDirectories);
    }

    [RelayCommand]
    private async Task RefreshFontsAsync()
    {
        if (IsFontsBusy)
        {
            return;
        }

        IsFontsBusy = true;
        HasFontError = false;
        FontStatusMessage = "正在重新扫描字体…";
        await Task.Yield();

        try
        {
            var families = FontService.Reload(Draft.FontDirectories);
            RefreshFontState();
            FontStatusMessage = $"已重新扫描，当前自定义字体 {families.Count} 个。";
        }
        catch (Exception exception)
        {
            HasFontError = true;
            FontStatusMessage = "重新扫描字体失败，请检查字体目录是否可访问。";
            Log.Error(exception, "重新扫描字体失败");
        }
        finally
        {
            IsFontsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFontDirectory()
    {
        try
        {
            Directory.CreateDirectory(FontService.ManagedFontDirectory);
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = FontService.ManagedFontDirectory,
                    UseShellExecute = true,
                }
            );
            Log.Information("已打开字体目录 {FontDirectory}", FontService.ManagedFontDirectory);
        }
        catch (Exception exception)
        {
            HasFontError = true;
            FontStatusMessage = "无法打开字体目录，请确认目录存在且可访问。";
            Log.Error(
                exception,
                "打开字体目录失败 {FontDirectory}",
                FontService.ManagedFontDirectory
            );
        }
    }

    [RelayCommand]
    private void Save()
    {
        var requested = Draft.LogDirectory.Trim();
        var directory = string.IsNullOrEmpty(requested) ? AppPaths.DefaultLogDirectory : requested;

        try
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            VerifyWriteAccess(directory);
        }
        catch (Exception exception)
        {
            HasError = true;
            StatusMessage = "日志目录无法写入，请选择有权限的本地目录。";
            Log.Error(exception, "日志目录不可写 {LogDirectory}", directory);
            return;
        }

        Draft.LogDirectory = directory;
        CopyToCurrent();

        var storageSaved = SaveStorageConfig();
        var saved = storageSaved ? _store.Save(_current) : SaveResult.Failed;
        if (saved == SaveResult.Saved && storageSaved)
        {
            _fontDirectoriesPreviewActive = false;
            HasError = false;
            StatusMessage =
                $"已保存到 {_store.StorageDescription}，日志级别 {Draft.LogLevel}，新日志将写入 {directory}。";
            Log.Information(
                "设置已保存 {StorageFile}，日志目录 {LogDirectory}，日志级别 {LogLevel}",
                AppPaths.DatabaseFile,
                directory,
                Draft.LogLevel
            );
            return;
        }

        HasError = true;
        StatusMessage = !storageSaved
            ? "设置已应用到当前会话，但存储配置写入失败，请检查数据目录是否可写。"
            : "设置已应用到当前会话，但写入本地数据库失败，请检查数据目录是否可写、数据库是否被其他程序占用。";
        Log.Error(
            "设置或存储配置写入失败 {StorageFile} {SettingsResult} {StorageResult}",
            AppPaths.DatabaseFile,
            saved,
            storageSaved
        );
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        var defaults = new AppSettings();
        Draft.Theme = defaults.Theme;
        Draft.RenderingMode = defaults.RenderingMode;
        Draft.CornerScale = defaults.CornerScale;
        Draft.UiScale = defaults.UiScale;
        SelectedUiScale =
            UiScaleOptions.FirstOrDefault(option => Math.Abs(option.Value - Draft.UiScale) < 0.001)
            ?? UiScaleOptions[^1];
        Draft.UiLayout = defaults.UiLayout;
        Draft.TabOverflow = defaults.TabOverflow;
        Draft.UiFont = defaults.UiFont;
        Draft.CodeFont = defaults.CodeFont;
        Draft.CodeFontSize = defaults.CodeFontSize;
        Draft.RequestTimeoutSeconds = defaults.RequestTimeoutSeconds;
        Draft.DefaultBodyMode = defaults.DefaultBodyMode;
        Draft.SseThinkingPath = defaults.SseThinkingPath;
        Draft.SseContentPath = defaults.SseContentPath;
        Draft.AutoFormatResponse = defaults.AutoFormatResponse;
        Draft.LargeResponseThresholdKb = defaults.LargeResponseThresholdKb;
        Draft.AuditLimit = defaults.AuditLimit;
        Draft.LogDirectory = defaults.LogDirectory;
        Draft.LogLevel = defaults.LogLevel;
        Draft.FontDirectories = new List<string>();
        Draft.TreePaneWidth = defaults.TreePaneWidth;
        Draft.HistoryCollapsed = defaults.HistoryCollapsed;
        Draft.AutoCheckUpdates = defaults.AutoCheckUpdates;
        _fontDirectoriesPreviewActive = true;
        FontService.Reload(Draft.FontDirectories);
        RefreshFontState();
        HasError = false;
        HasFontError = false;
        StatusMessage = "已恢复默认值，点击「保存设置」后生效。";
        FontStatusMessage = "已恢复默认字体，应用字体库中的文件仍保留。";
    }

    [RelayCommand]
    private void OpenLogDirectory()
    {
        var directory = Draft.LogDirectory.Trim();
        if (string.IsNullOrEmpty(directory))
        {
            directory = AppPaths.DefaultLogDirectory;
        }

        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            Log.Information("已打开日志目录 {LogDirectory}", directory);
        }
        catch (Exception exception)
        {
            HasError = true;
            StatusMessage = "无法打开日志目录，请确认目录存在且可访问。";
            Log.Error(exception, "打开日志目录失败 {LogDirectory}", directory);
        }
    }

    public void Dispose() => _storage.StatusChanged -= OnStorageStatusChanged;

    private void OnStorageStatusChanged(object? sender, string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StorageStatusMessage = message;
            HasStorageError =
                message.Contains("失败", StringComparison.Ordinal)
                || message.Contains("错误", StringComparison.Ordinal);
        });
    }

    partial void OnIsStorageBusyChanged(bool value) =>
        OnPropertyChanged(nameof(CanAddStorageProfile));

    partial void OnSelectedUiScaleChanged(UiScaleOption? value)
    {
        if (value is not null)
        {
            Draft.UiScale = value.Value;
        }
    }

    partial void OnSelectedStorageProfileChanged(StorageProfile? value)
    {
        if (value is not null)
        {
            var option = StorageProviderOptions.FirstOrDefault(candidate =>
                string.Equals(candidate.Value, value.Provider, StringComparison.Ordinal)
            );
            if (option is not null && !ReferenceEquals(SelectedStorageProvider, option))
            {
                SelectedStorageProvider = option;
            }
        }

        OnPropertyChanged(nameof(IsStorageProfileEditable));
        OnPropertyChanged(nameof(StorageConnectionPlaceholder));
    }

    partial void OnSelectedStorageProviderChanged(StorageProviderOption? value)
    {
        if (value is null || SelectedStorageProfile is not { IsRemote: true } profile)
        {
            return;
        }

        if (value.Value == StorageProviders.LocalSqlite)
        {
            SelectedStorageProvider = StorageProviderOptions.FirstOrDefault(option =>
                string.Equals(option.Value, profile.Provider, StringComparison.Ordinal)
            );
            return;
        }

        if (!string.Equals(profile.Provider, value.Value, StringComparison.Ordinal))
        {
            profile.Provider = value.Value;
            OnPropertyChanged(nameof(StorageConnectionPlaceholder));
        }
    }

    [RelayCommand]
    private void AddStorageProfile()
    {
        var provider = StorageProviderOptions.FirstOrDefault(option =>
            !string.Equals(option.Value, StorageProviders.LocalSqlite, StringComparison.Ordinal)
            && !option.Label.Contains("插件未安装", StringComparison.Ordinal)
        );
        if (provider is null)
        {
            HasStorageError = true;
            StorageStatusMessage =
                $"尚未安装远程存储插件。请把插件文件夹放到 {AppPaths.StoragePluginDirectory}，"
                + "再点击「重新扫描」。";
            return;
        }

        var profile = new StorageProfile
        {
            Name = $"远程库 {StorageProfiles.Count}",
            Provider = provider.Value,
            ConnectionString = string.Empty,
        };
        StorageProfiles.Add(profile);
        SelectedStorageProfile = profile;
        StorageStatusMessage = "填写连接串后点击「测试连接」，保存设置后生效。";
        HasStorageError = false;
    }

    [RelayCommand]
    private void RemoveStorageProfile()
    {
        if (SelectedStorageProfile is not { IsRemote: true } profile)
        {
            return;
        }

        StorageProfiles.Remove(profile);
        foreach (
            var binding in new[]
            {
                SettingsStorageProfile,
                WorkspaceStorageProfile,
                SuitesStorageProfile,
            }
        )
        {
            if (binding?.Id == profile.Id)
            {
                var local = FindProfile(StorageConfig.LocalProfileId);
                if (ReferenceEquals(binding, SettingsStorageProfile))
                {
                    SettingsStorageProfile = local;
                }
                else if (ReferenceEquals(binding, WorkspaceStorageProfile))
                {
                    WorkspaceStorageProfile = local;
                }
                else
                {
                    SuitesStorageProfile = local;
                }
            }
        }

        SelectedStorageProfile = StorageProfiles.FirstOrDefault();
        StorageStatusMessage = "已移除远程库，保存存储配置后生效。";
        HasStorageError = false;
    }

    [RelayCommand]
    private async Task TestStorageAsync()
    {
        if (IsStorageBusy || SelectedStorageProfile is not { } profile)
        {
            return;
        }

        IsStorageBusy = true;
        StorageStatusMessage = "正在测试连接…";
        try
        {
            var result = await _storage.TestAsync(profile);
            HasStorageError = !result.Success;
            StorageStatusMessage = $"{result.Message}（{result.ElapsedMs} ms）";
        }
        catch (Exception exception)
        {
            HasStorageError = true;
            StorageStatusMessage = $"测试失败：{exception.Message}";
            Log.Error(exception, "测试存储配置失败 {Profile}", profile.Name);
        }
        finally
        {
            IsStorageBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncStorageAsync()
    {
        if (IsStorageBusy || SelectedStorageProfile is not { } target)
        {
            return;
        }

        var source = target.IsLocal
            ? StorageProfiles.FirstOrDefault(profile => profile.IsRemote)
            : FindProfile(StorageConfig.LocalProfileId);
        if (source is null || source.Id == target.Id)
        {
            HasStorageError = true;
            StorageStatusMessage = "请先添加并选择一个远程库，再执行同步。";
            return;
        }

        IsStorageBusy = true;
        StorageStatusMessage = "正在同步数据…";
        try
        {
            if (
                _storage.RegisterProfile(source) != SaveResult.Saved
                || _storage.RegisterProfile(target) != SaveResult.Saved
            )
            {
                HasStorageError = true;
                StorageStatusMessage = "同步失败：无法保存存储连接配置。";
                return;
            }

            var result = await _storage.SyncAsync(source.Id, target.Id, StorageCategories.All);
            HasStorageError = !result.Success;
            StorageStatusMessage = result.Message;
        }
        catch (Exception exception)
        {
            HasStorageError = true;
            StorageStatusMessage = $"同步失败：{exception.Message}";
            Log.Error(exception, "同步存储失败 {Target}", target.Name);
        }
        finally
        {
            IsStorageBusy = false;
        }
    }

    [RelayCommand]
    private async Task SwitchStorageAsync()
    {
        if (IsStorageBusy || SelectedStorageProfile is not { } target)
        {
            return;
        }

        IsStorageBusy = true;
        StorageStatusMessage = "正在切换存储…";
        try
        {
            if (_storage.RegisterProfile(target) != SaveResult.Saved)
            {
                HasStorageError = true;
                StorageStatusMessage = "切换失败：无法保存存储连接配置。";
                return;
            }

            var result = await _storage.SwitchAsync(target.Id, StorageCategories.All);
            HasStorageError = !result.Success;
            StorageStatusMessage =
                result.Message
                + (result.BackupPath is null ? string.Empty : $" 切换前备份：{result.BackupPath}");
        }
        catch (Exception exception)
        {
            HasStorageError = true;
            StorageStatusMessage = $"切换失败：{exception.Message}";
            Log.Error(exception, "切换存储失败 {Target}", target.Name);
        }
        finally
        {
            IsStorageBusy = false;
        }
    }

    private StorageProfile FindProfile(StorageProfile configured) =>
        StorageProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, configured.Id, StringComparison.Ordinal)
        ) ?? StorageProfiles.First();

    private StorageProfile FindProfile(string profileId) =>
        StorageProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.Ordinal)
        ) ?? StorageProfiles.First();

    private bool SaveStorageConfig()
    {
        var incomplete = StorageProfiles.FirstOrDefault(profile =>
            profile.IsRemote && string.IsNullOrWhiteSpace(profile.ConnectionString)
        );
        if (incomplete is not null)
        {
            HasStorageError = true;
            StorageStatusMessage =
                $"存储配置「{incomplete.DisplayName}」缺少连接串，请填写或移除后再保存。";
            return false;
        }

        var config = new StorageConfig
        {
            Profiles = StorageProfiles.Select(profile => profile.Clone()).ToList(),
            Bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [StorageCategories.Settings] =
                    SettingsStorageProfile?.Id ?? StorageConfig.LocalProfileId,
                [StorageCategories.Workspace] =
                    WorkspaceStorageProfile?.Id ?? StorageConfig.LocalProfileId,
                [StorageCategories.Suites] =
                    SuitesStorageProfile?.Id ?? StorageConfig.LocalProfileId,
            },
        };

        return _storage.SaveConfig(config) == SaveResult.Saved;
    }

    private void RefreshFontState()
    {
        var choices = FontService.GetChoices().ToList();
        AddChoice(choices, Draft.UiFont);
        AddChoice(choices, Draft.CodeFont);

        for (var index = FontOptions.Count - 1; index >= 0; index--)
        {
            if (!choices.Contains(FontOptions[index], StringComparer.OrdinalIgnoreCase))
            {
                FontOptions.RemoveAt(index);
            }
        }

        foreach (var choice in choices)
        {
            if (!FontOptions.Contains(choice, StringComparer.OrdinalIgnoreCase))
            {
                FontOptions.Add(choice);
            }
        }

        CustomFonts.Clear();
        foreach (var family in FontService.CustomFamilies)
        {
            CustomFonts.Add(family);
        }

        FontDirectories.Clear();
        FontDirectories.Add(new FontDirectoryItem(FontService.ManagedFontDirectory, true));
        foreach (var directory in Draft.FontDirectories)
        {
            if (
                !string.Equals(
                    directory,
                    FontService.ManagedFontDirectory,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                FontDirectories.Add(new FontDirectoryItem(directory, false));
            }
        }

        OnPropertyChanged(nameof(HasCustomFonts));
    }

    private static void AddChoice(List<string> choices, string? choice)
    {
        if (
            !string.IsNullOrWhiteSpace(choice)
            && !choices.Contains(choice, StringComparer.OrdinalIgnoreCase)
        )
        {
            choices.Add(choice);
        }
    }

    private void CopyToCurrent()
    {
        _current.Theme = Draft.Theme;
        _current.RenderingMode = Draft.RenderingMode;
        _current.CornerScale = Math.Clamp(Draft.CornerScale, 0, 2);
        _current.UiScale = Math.Clamp(
            Draft.UiScale,
            UiScaleService.MinimumScale,
            UiScaleService.MaximumScale
        );
        _current.UiLayout = Draft.UiLayout;
        _current.TabOverflow = Draft.TabOverflow;
        _current.UiFont = Draft.UiFont;
        _current.CodeFont = Draft.CodeFont;
        _current.CodeFontSize = Draft.CodeFontSize;
        _current.RequestTimeoutSeconds = Draft.RequestTimeoutSeconds;
        _current.DefaultBodyMode = Draft.DefaultBodyMode;
        _current.SseThinkingPath = Draft.SseThinkingPath;
        _current.SseContentPath = Draft.SseContentPath;
        _current.AutoFormatResponse = Draft.AutoFormatResponse;
        _current.LargeResponseThresholdKb = Draft.LargeResponseThresholdKb;
        _current.AuditLimit = Draft.AuditLimit;
        _current.LogDirectory = Draft.LogDirectory;
        _current.LogLevel = Draft.LogLevel;
        if (
            !_current.FontDirectories.SequenceEqual(
                Draft.FontDirectories,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            _current.FontDirectories = new List<string>(Draft.FontDirectories);
        }
        _current.TreePaneWidth = Draft.TreePaneWidth;
        _current.HistoryCollapsed = Draft.HistoryCollapsed;
        _current.AutoCheckUpdates = Draft.AutoCheckUpdates;
    }

    private static AppSettings Clone(AppSettings source)
    {
        return new AppSettings
        {
            Theme = source.Theme,
            RenderingMode = source.RenderingMode,
            CornerScale = source.CornerScale,
            UiScale = source.UiScale,
            UiLayout = source.UiLayout,
            TabOverflow = source.TabOverflow,
            UiFont = source.UiFont,
            CodeFont = source.CodeFont,
            CodeFontSize = source.CodeFontSize,
            RequestTimeoutSeconds = source.RequestTimeoutSeconds,
            DefaultBodyMode = source.DefaultBodyMode,
            SseThinkingPath = source.SseThinkingPath,
            SseContentPath = source.SseContentPath,
            AutoFormatResponse = source.AutoFormatResponse,
            LargeResponseThresholdKb = source.LargeResponseThresholdKb,
            AuditLimit = source.AuditLimit,
            LogDirectory = source.LogDirectory,
            LogLevel = source.LogLevel,
            FontDirectories = new List<string>(source.FontDirectories),
            TreePaneWidth = source.TreePaneWidth,
            RequestPaneRatio = source.RequestPaneRatio,
            HistoryCollapsed = source.HistoryCollapsed,
            AutoCheckUpdates = source.AutoCheckUpdates,
        };
    }

    private static void VerifyWriteAccess(string directory)
    {
        var probe = Path.Combine(directory, $".postsharp-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "probe");
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }
}

public sealed record StorageProviderOption(
    string Value,
    string Label,
    string Description,
    string Placeholder
)
{
    public override string ToString() => Label;
}

public sealed record UiScaleOption(double Value, string Label)
{
    public override string ToString() => Label;
}
