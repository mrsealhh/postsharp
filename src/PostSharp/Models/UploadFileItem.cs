using CommunityToolkit.Mvvm.ComponentModel;

namespace PostSharp.Models;

/// <summary>One multipart file field.</summary>
public partial class UploadFileItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _filePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _fieldName = "file";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private long _sizeBytes;

    public string FileName => Path.GetFileName(FilePath);

    public string SizeText =>
        SizeBytes switch
        {
            <= 0 => "大小未知",
            < 1024 => $"{SizeBytes} B",
            < 1024 * 1024 => $"{SizeBytes / 1024d:0.#} KB",
            _ => $"{SizeBytes / (1024d * 1024d):0.##} MB",
        };

    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(FileName) ? "未选择文件" : $"{FileName} · {SizeText}";
}
