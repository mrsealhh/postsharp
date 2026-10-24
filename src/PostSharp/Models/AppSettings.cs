using CommunityToolkit.Mvvm.ComponentModel;

namespace PostSharp.Models;

/// <summary>User-level preferences, stored next to the workspace and applied immediately.</summary>
public partial class AppSettings : ObservableObject
{
    [ObservableProperty]
    private string _theme = "跟随系统";

    [ObservableProperty]
    private string _uiFont = "Inter";

    [ObservableProperty]
    private string _codeFont = "Geist Mono";

    [ObservableProperty]
    private string _codeFontSize = "12";

    [ObservableProperty]
    private int _requestTimeoutSeconds = 60;

    [ObservableProperty]
    private string _defaultBodyMode = "None";

    [ObservableProperty]
    private bool _autoFormatResponse = true;

    [ObservableProperty]
    private int _largeResponseThresholdKb = 64;

    [ObservableProperty]
    private int _auditLimit = 500;

    [ObservableProperty]
    private string _logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PostSharp",
        "logs"
    );
}
