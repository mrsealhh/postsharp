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

    /// <summary>内嵌 WebView2 的配置与缓存目录，放在 LocalAppData 以免污染程序目录。</summary>
    public static string WebViewDataDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PostSharp",
            "webview"
        );

    public static string EnsureRoot()
    {
        try
        {
            Directory.CreateDirectory(Root);
        }
        catch (Exception exception)
        {
            // 目录不可写时仍允许启动，后续保存会各自记录失败，而不是在启动阶段崩溃。
            Log.Warning(exception, "无法创建数据目录 {Directory}", Root);
        }

        return Root;
    }

    /// <summary>
    /// 选出可写的 WebView2 用户数据目录：优先 LocalAppData，其次临时目录；都不可写时返回 false，
    /// 由调用方退回 WebView2 的默认位置。
    /// </summary>
    public static bool TryPrepareWebViewDataDirectory(out string directory)
    {
        var candidates = new[]
        {
            WebViewDataDirectory,
            Path.Combine(Path.GetTempPath(), "PostSharp", "webview"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                directory = candidate;
                return true;
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "WebView 数据目录不可写 {Directory}", candidate);
            }
        }

        directory = string.Empty;
        return false;
    }
}
