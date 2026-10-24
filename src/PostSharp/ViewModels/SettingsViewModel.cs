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

    public SettingsViewModel(AppSettings current, SettingsStore store)
    {
        _current = current;
        _store = store;
        _draft = Clone(current);
    }

    [ObservableProperty]
    private AppSettings _draft;

    [ObservableProperty]
    private string _statusMessage = "修改后点击「保存设置」生效。";

    [ObservableProperty]
    private bool _hasError;

    public IReadOnlyList<string> ThemeOptions => ThemeService.Options;

    public IReadOnlyList<string> UiFontOptions { get; } = new[] { "Inter", "系统默认", "微软雅黑" };

    public IReadOnlyList<string> CodeFontOptions { get; } =
        new[] { "Geist Mono", "Consolas", "Cascadia Mono" };

    public IReadOnlyList<string> CodeFontSizeOptions { get; } = new[] { "12", "13", "14", "16" };

    public IReadOnlyList<int> TimeoutOptions { get; } = new[] { 10, 30, 60, 120, 300 };

    public IReadOnlyList<int> LargeResponseThresholdOptions { get; } = new[] { 32, 64, 128, 256 };

    public IReadOnlyList<int> AuditLimitOptions { get; } = new[] { 100, 500, 1000, 2000 };

    public string DefaultLogDirectory => AppPaths.DefaultLogDirectory;

    public string SettingsFilePath => _store.FilePath;

    public void SetLogDirectory(string directory)
    {
        Draft.LogDirectory = directory;
        StatusMessage = "日志目录已选择，点击「保存设置」后生效。";
        HasError = false;
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
            HasError = false;
            StatusMessage = $"已保存到 {_store.FilePath}，新日志将写入 {directory}。";
            Log.Information(
                "设置已保存 {SettingsFile}，日志目录 {LogDirectory}",
                _store.FilePath,
                directory
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
        Draft.UiFont = defaults.UiFont;
        Draft.CodeFont = defaults.CodeFont;
        Draft.CodeFontSize = defaults.CodeFontSize;
        Draft.RequestTimeoutSeconds = defaults.RequestTimeoutSeconds;
        Draft.DefaultBodyMode = defaults.DefaultBodyMode;
        Draft.AutoFormatResponse = defaults.AutoFormatResponse;
        Draft.LargeResponseThresholdKb = defaults.LargeResponseThresholdKb;
        Draft.AuditLimit = defaults.AuditLimit;
        Draft.LogDirectory = defaults.LogDirectory;
        HasError = false;
        StatusMessage = "已恢复默认值，点击「保存设置」后生效。";
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

    private void CopyToCurrent()
    {
        _current.Theme = Draft.Theme;
        _current.UiFont = Draft.UiFont;
        _current.CodeFont = Draft.CodeFont;
        _current.CodeFontSize = Draft.CodeFontSize;
        _current.RequestTimeoutSeconds = Draft.RequestTimeoutSeconds;
        _current.DefaultBodyMode = Draft.DefaultBodyMode;
        _current.AutoFormatResponse = Draft.AutoFormatResponse;
        _current.LargeResponseThresholdKb = Draft.LargeResponseThresholdKb;
        _current.AuditLimit = Draft.AuditLimit;
        _current.LogDirectory = Draft.LogDirectory;
    }

    private static AppSettings Clone(AppSettings source)
    {
        return new AppSettings
        {
            Theme = source.Theme,
            UiFont = source.UiFont,
            CodeFont = source.CodeFont,
            CodeFontSize = source.CodeFontSize,
            RequestTimeoutSeconds = source.RequestTimeoutSeconds,
            DefaultBodyMode = source.DefaultBodyMode,
            AutoFormatResponse = source.AutoFormatResponse,
            LargeResponseThresholdKb = source.LargeResponseThresholdKb,
            AuditLimit = source.AuditLimit,
            LogDirectory = source.LogDirectory,
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
