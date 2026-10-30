namespace PostSharp.Services;

/// <summary>Single source of truth for the local data directory used by every store.</summary>
public static class AppPaths
{
    private static readonly string LegacyRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PostSharp"
    );

    private static readonly string[] LegacyFiles =
    [
        "settings.json",
        "settings.json.bak",
        "workspace.json",
        "workspace.json.bak",
        "audit.json",
        "audit.json.bak",
        "audit-trash.json",
        "audit-trash.json.bak",
        "postsharp.db",
        "postsharp.db-wal",
        "postsharp.db-shm",
        "storage.json",
    ];

    private static bool _legacyMigrationDone;

    /// <summary>
    /// Project root when the app is launched from a source tree; the current directory for an installed
    /// copy. Data lives under <c>.postsharp</c> so it travels with the workspace.
    /// </summary>
    public static string ProjectRoot { get; } = FindProjectRoot();

    public static string Root { get; } = ResolveDataRoot();

    public static string DefaultLogDirectory => Path.Combine(Root, "logs");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string WorkspaceFile => Path.Combine(Root, "workspace.json");

    public static string AuditFile => Path.Combine(Root, "audit.json");

    public static string AuditTrashFile => Path.Combine(Root, "audit-trash.json");

    /// <summary>SQLite database for audit records, storage documents and their recycle bin.</summary>
    public static string DatabaseFile => Path.Combine(Root, "postsharp.db");

    public static string StorageConfigFile => Path.Combine(Root, "storage.json");

    public static string BackupDirectory => Path.Combine(Root, "backups");

    /// <summary>Fixed directory where MCP and CLI reports are written.</summary>
    public static string ReportDirectory => Path.Combine(Root, "reports");

    /// <summary>Fixed directory where users drop downloaded remote-storage plugins.</summary>
    public static string StoragePluginDirectory => Path.Combine(Root, "plugins");

    /// <summary>Portable installs may also ship plugins next to the executable.</summary>
    public static string BundledPluginDirectory =>
        Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Downloaded update packages and the pending-update marker.</summary>
    public static string UpdateDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PostSharp",
            "updates"
        );

    public static string EnsureRoot()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(StoragePluginDirectory);
            MigrateLegacyData();
        }
        catch (Exception exception)
        {
            // 目录不可写时仍允许启动，后续保存会各自记录失败，而不是在启动阶段崩溃。
            Log.Warning(exception, "无法创建数据目录 {Directory}", Root);
        }

        return Root;
    }

    public static string EnsureStoragePluginDirectory()
    {
        try
        {
            Directory.CreateDirectory(StoragePluginDirectory);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "无法创建存储插件目录 {Directory}", StoragePluginDirectory);
        }

        return StoragePluginDirectory;
    }

    public static string CreateBackupPath(string label)
    {
        Directory.CreateDirectory(BackupDirectory);
        var safeLabel = string.Concat(
            label.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '-' : character
            )
        );
        return Path.Combine(BackupDirectory, $"{safeLabel}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
    }

    private static string ResolveDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable("POSTSHARP_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                return Path.GetFullPath(configured);
            }
            catch (Exception exception)
            {
                Log.Warning(
                    exception,
                    "POSTSHARP_DATA_DIR 无效，改用项目数据目录 {Directory}",
                    configured
                );
            }
        }

        var projectData = Path.Combine(ProjectRoot, ".postsharp");
        if (TryEnsureWritable(projectData))
        {
            return projectData;
        }

        return LegacyRoot;
    }

    private static bool TryEnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "项目数据目录不可写，改用用户数据目录 {Directory}", directory);
            return false;
        }
    }

    private static string FindProjectRoot()
    {
        var candidates = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };

        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(candidate);
            for (var depth = 0; depth < 10 && directory is not null; depth++)
            {
                try
                {
                    if (
                        Directory.Exists(Path.Combine(directory.FullName, ".git"))
                        || Directory.EnumerateFiles(directory.FullName, "*.slnx").Any()
                    )
                    {
                        return directory.FullName;
                    }
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(exception, "无法检查项目根目录 {Directory}", directory.FullName);
                }

                directory = directory.Parent;
            }
        }

        return Environment.CurrentDirectory;
    }

    private static void MigrateLegacyData()
    {
        if (_legacyMigrationDone || PathsEqual(Root, LegacyRoot))
        {
            return;
        }

        _legacyMigrationDone = true;
        foreach (var name in LegacyFiles)
        {
            var source = Path.Combine(LegacyRoot, name);
            var target = Path.Combine(Root, name);
            if (!File.Exists(source) || File.Exists(target))
            {
                continue;
            }

            try
            {
                File.Copy(source, target);
                Log.Information(
                    "已将旧数据复制到项目数据目录 {SourceFile} {TargetFile}",
                    source,
                    target
                );
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "复制旧数据失败 {SourceFile} {TargetFile}", source, target);
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase
        );
}
