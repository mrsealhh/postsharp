namespace PostSharp.Services;

/// <summary>Single source of truth for the local data directory used by every store.</summary>
public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PostSharp"
        );

    public static string DefaultLogDirectory => Path.Combine(Root, "logs");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        return Root;
    }
}
