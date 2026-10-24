namespace PostSharp.Models;

/// <summary>A font directory shown in the settings window; the managed app directory cannot be removed.</summary>
public sealed record FontDirectoryItem(string Path, bool IsManaged)
{
    public string Label => IsManaged ? "应用字体库" : "外部目录";

    public string Description =>
        IsManaged
            ? "导入的字体文件会复制到这里，随应用数据保留。"
            : "直接扫描该目录下的 TTF / OTF / TTC 文件。";
}
