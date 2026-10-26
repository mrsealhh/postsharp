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
    private bool _fontDirectoriesPreviewActive;

    public SettingsViewModel(AppSettings current, SettingsStore store)
    {
        _current = current;
        _store = store;
        _draft = Clone(current);
        RefreshFontState();
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

    public IReadOnlyList<string> ThemeOptions => ThemeService.Options;

    public ObservableCollection<string> FontOptions { get; } = new();

    public ObservableCollection<string> CustomFonts { get; } = new();

    public ObservableCollection<FontDirectoryItem> FontDirectories { get; } = new();

    public IReadOnlyList<string> CodeFontSizeOptions { get; } = new[] { "12", "13", "14", "16" };

    public IReadOnlyList<int> TimeoutOptions { get; } = new[] { 10, 30, 60, 120, 300 };

    public IReadOnlyList<int> LargeResponseThresholdOptions { get; } = new[] { 32, 64, 128, 256 };

    public IReadOnlyList<int> AuditLimitOptions { get; } = new[] { 100, 500, 1000, 2000 };

    public string DefaultLogDirectory => AppPaths.DefaultLogDirectory;

    public string SettingsFilePath => _store.FilePath;

    public string ManagedFontDirectory => FontService.ManagedFontDirectory;

    public bool HasCustomFonts => CustomFonts.Count > 0;

    public void SetLogDirectory(string directory)
    {
        Draft.LogDirectory = directory;
        StatusMessage = "日志目录已选择，点击「保存设置」后生效。";
        HasError = false;
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

        if (_store.Save(_current))
        {
            _fontDirectoriesPreviewActive = false;
            HasError = false;
            StatusMessage =
                $"已保存到 {_store.FilePath}，日志级别 {Draft.LogLevel}，新日志将写入 {directory}。";
            Log.Information(
                "设置已保存 {SettingsFile}，日志目录 {LogDirectory}，日志级别 {LogLevel}",
                _store.FilePath,
                directory,
                Draft.LogLevel
            );
            return;
        }

        HasError = true;
        StatusMessage = "设置已应用到当前会话，但写入配置文件失败，请检查目录权限。";
        Log.Error("设置文件写入失败 {SettingsFile}", _store.FilePath);
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        var defaults = new AppSettings();
        Draft.Theme = defaults.Theme;
        Draft.CornerScale = defaults.CornerScale;
        Draft.UiFont = defaults.UiFont;
        Draft.CodeFont = defaults.CodeFont;
        Draft.CodeFontSize = defaults.CodeFontSize;
        Draft.RequestTimeoutSeconds = defaults.RequestTimeoutSeconds;
        Draft.DefaultBodyMode = defaults.DefaultBodyMode;
        Draft.AutoFormatResponse = defaults.AutoFormatResponse;
        Draft.LargeResponseThresholdKb = defaults.LargeResponseThresholdKb;
        Draft.AuditLimit = defaults.AuditLimit;
        Draft.LogDirectory = defaults.LogDirectory;
        Draft.LogLevel = defaults.LogLevel;
        Draft.FontDirectories = new List<string>();
        Draft.TreePaneWidth = defaults.TreePaneWidth;
        Draft.HistoryCollapsed = defaults.HistoryCollapsed;
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
        _current.CornerScale = Math.Clamp(Draft.CornerScale, 0, 2);
        _current.UiFont = Draft.UiFont;
        _current.CodeFont = Draft.CodeFont;
        _current.CodeFontSize = Draft.CodeFontSize;
        _current.RequestTimeoutSeconds = Draft.RequestTimeoutSeconds;
        _current.DefaultBodyMode = Draft.DefaultBodyMode;
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
    }

    private static AppSettings Clone(AppSettings source)
    {
        return new AppSettings
        {
            Theme = source.Theme,
            CornerScale = source.CornerScale,
            UiFont = source.UiFont,
            CodeFont = source.CodeFont,
            CodeFontSize = source.CodeFontSize,
            RequestTimeoutSeconds = source.RequestTimeoutSeconds,
            DefaultBodyMode = source.DefaultBodyMode,
            AutoFormatResponse = source.AutoFormatResponse,
            LargeResponseThresholdKb = source.LargeResponseThresholdKb,
            AuditLimit = source.AuditLimit,
            LogDirectory = source.LogDirectory,
            LogLevel = source.LogLevel,
            FontDirectories = new List<string>(source.FontDirectories),
            TreePaneWidth = source.TreePaneWidth,
            HistoryCollapsed = source.HistoryCollapsed,
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
