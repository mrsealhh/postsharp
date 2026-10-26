using CommunityToolkit.Mvvm.ComponentModel;
using PostSharp.Services;

namespace PostSharp.Models;

/// <summary>User-level preferences, stored next to the workspace and applied immediately.</summary>
public partial class AppSettings : ObservableObject
{
    [ObservableProperty]
    private string _theme = "跟随系统";

    /// <summary>Global corner multiplier: 0 is square, 1 keeps Arc defaults, 2 doubles the radii.</summary>
    [ObservableProperty]
    private double _cornerScale = 1;

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
    private string _sseThinkingPath = string.Empty;

    [ObservableProperty]
    private string _sseContentPath = string.Empty;

    [ObservableProperty]
    private bool _autoFormatResponse = true;

    [ObservableProperty]
    private int _largeResponseThresholdKb = 64;

    [ObservableProperty]
    private int _auditLimit = 500;

    [ObservableProperty]
    private string _logDirectory = AppPaths.DefaultLogDirectory;

    [ObservableProperty]
    private string _logLevel = LogLevels.Default;

    [ObservableProperty]
    private List<string> _fontDirectories = new();

    [ObservableProperty]
    private double _treePaneWidth = 320;

    /// <summary>Share of the editor area used by the request pane; the rest goes to the response pane.</summary>
    [ObservableProperty]
    private double _requestPaneRatio = 0.5;

    /// <summary>User-selected request/response layout; null keeps the automatic side-by-side switch.</summary>
    [ObservableProperty]
    private string? _workspaceLayout;

    [ObservableProperty]
    private bool _historyCollapsed;
}
