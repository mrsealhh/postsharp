using CommunityToolkit.Mvvm.ComponentModel;
using PostSharp.Services;

namespace PostSharp.Models;

/// <summary>User-level preferences, stored next to the workspace and applied immediately.</summary>
public static class RenderingModes
{
    public const string Software = "Software";
    public const string Auto = "Auto";

    public static IReadOnlyList<string> All { get; } = [Software, Auto];

    public static string Label(string mode) => mode == Auto ? "自动（GPU）" : "软件（低内存）";
}

public static class UiLayouts
{
    public const string Modern = "Modern";
    public const string Compact = "Compact";

    public static string Label(string layout) => layout == Compact ? "经典紧凑" : "现代分隔";
}

public static class TabOverflows
{
    public const string Scroll = "Scroll";
    public const string Wrap = "Wrap";

    public static string Label(string overflow) => overflow == Wrap ? "多行平铺" : "单行滚动";
}

public partial class AppSettings : ObservableObject
{
    [ObservableProperty]
    private string _theme = "跟随系统";

    /// <summary>Whole-window zoom; 0.8 is the default for denser information on small screens.</summary>
    [ObservableProperty]
    private double _uiScale = 0.8;

    /// <summary>Modern uses the roomy Arc panel rhythm; Compact tightens headers, margins and tabs.</summary>
    [ObservableProperty]
    private string _uiLayout = UiLayouts.Modern;

    /// <summary>Scroll keeps one tab row with arrows; Wrap lays every visible tab out in multiple rows.</summary>
    [ObservableProperty]
    private string _tabOverflow = TabOverflows.Scroll;

    /// <summary>
    /// Software rendering keeps the native GPU/ANGLE memory low; Auto restores the platform default.
    /// The value is read before the Avalonia app builder is created, so a change needs a restart.
    /// </summary>
    [ObservableProperty]
    private string _renderingMode = RenderingModes.Software;

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

    /// <summary>Maximum retained audit entries; 0 means unlimited.</summary>
    [ObservableProperty]
    private int _auditLimit;

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

    /// <summary>User-selected request/response layout; defaults to side-by-side.</summary>
    [ObservableProperty]
    private string? _workspaceLayout = "SideBySide";

    [ObservableProperty]
    private bool _historyCollapsed;

    /// <summary>Checks GitHub Releases in the background and stages newer builds for the next launch.</summary>
    [ObservableProperty]
    private bool _autoCheckUpdates = true;
}
