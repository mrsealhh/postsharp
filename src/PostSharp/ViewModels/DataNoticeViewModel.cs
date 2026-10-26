using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PostSharp.Services;
using TKCode.Arc;

namespace PostSharp.ViewModels;

/// <summary>
/// The persistent banner shown when a data file could not be read at startup: what happened, where the original
/// went, and what still works. It stays until the person dismisses it.
/// </summary>
public partial class DataNoticeViewModel : ObservableObject
{
    private readonly List<string> _messages = new();

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private AlertTone _tone = AlertTone.Warning;

    /// <summary>
    /// Adds a line for a data file that was not read normally; normal loads are ignored. <paramref name="setAsideHint"/>
    /// is appended when the original was moved aside, for files whose copy needs a warning of its own.
    /// </summary>
    public void Report<T>(
        DataFileLoad<T> load,
        string fileLabel,
        string fallback,
        string setAsideHint = ""
    )
        where T : class
    {
        var text = load.State switch
        {
            DataFileState.RestoredFromBackup when load.SetAsidePath is not null =>
                $"{fileLabel}无法读取，已改用上次保存的备份恢复，最近一次改动可能缺失。原文件已移到 {load.SetAsidePath}，没有被覆盖。{setAsideHint}",
            DataFileState.RestoredFromBackup =>
                $"{fileLabel}不存在，已改用上次保存的备份恢复，最近一次改动可能缺失。",
            DataFileState.Unreadable when load.SetAsidePath is null =>
                $"{fileLabel}无法读取，{fallback}。原文件仍在原位置，没有被覆盖，请检查文件是否被其他程序占用。{setAsideHint}",
            DataFileState.Unreadable =>
                $"{fileLabel}无法读取，也没有可用的备份，{fallback}。原文件已移到 {load.SetAsidePath}，没有被覆盖，可以用文本编辑器打开找回内容。{setAsideHint}",
            DataFileState.Inaccessible when load.Value is not null =>
                $"{fileLabel}无法读取，可能正被其他程序占用，已改用上次保存的备份打开，最近一次改动可能缺失。为避免覆盖原文件，本次运行不会保存对它的修改；请关闭占用该文件的程序后重启 PostSharp。",
            DataFileState.Inaccessible =>
                $"{fileLabel}无法读取，可能正被其他程序占用，{fallback}。为避免覆盖原文件，本次运行不会保存对它的修改；请关闭占用该文件的程序后重启 PostSharp。",
            _ => null,
        };

        if (text is null)
        {
            return;
        }

        _messages.Add(text);
        if (load.State is DataFileState.Unreadable or DataFileState.Inaccessible)
        {
            Tone = AlertTone.Danger;
        }

        Title = Tone == AlertTone.Danger ? "数据文件无法读取" : "数据文件已从备份恢复";
        Message = string.Join("\n", _messages);
        IsOpen = true;
    }

    /// <summary>Adds a warning that is not about reading a file, keeping a more serious tone if one is shown.</summary>
    public void Warn(string text)
    {
        // A repeat is not listed twice, but the banner reopens so the failure is seen again.
        if (_messages.Contains(text))
        {
            IsOpen = true;
            return;
        }

        _messages.Add(text);
        if (Tone != AlertTone.Danger)
        {
            Title = "需要注意的数据文件";
        }

        Message = string.Join("\n", _messages);
        IsOpen = true;
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(
                new ProcessStartInfo { FileName = AppPaths.Root, UseShellExecute = true }
            );
            Log.Information("已打开数据目录 {DataDirectory}", AppPaths.Root);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "打开数据目录失败 {DataDirectory}", AppPaths.Root);
            Warn("无法打开数据目录，请在资源管理器中手动打开 %APPDATA%\\PostSharp。");
        }
    }
}
