using Avalonia.Input;

namespace PostSharp.Models;

/// <summary>Payload dragged from the suite collection browser onto the workflow canvas.</summary>
public sealed record SuiteSourceDragData(RequestCollection? Collection, RequestItem? Request);

public static class SuiteSourceDragFormats
{
    public static readonly DataFormat<SuiteSourceDragData> Source =
        DataFormat.CreateInProcessFormat<SuiteSourceDragData>("PostSharp.SuiteSourceDrag");
}
