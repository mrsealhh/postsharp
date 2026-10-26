using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Services;

public enum UpdateCheckStatus
{
    Disabled,
    UnsupportedLayout,
    NoRelease,
    NoPackage,
    UpToDate,
    Staged,
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message);

public sealed class PendingUpdate
{
    public string Version { get; init; } = string.Empty;

    public string StagingDirectory { get; init; } = string.Empty;

    public string ExecutableName { get; init; } = "PostSharp.exe";

    public string Sha256 { get; init; } = string.Empty;

    public string ReleaseUrl { get; init; } = string.Empty;
}

/// <summary>
/// Checks GitHub Releases, downloads a verified Windows package in the background and stages it for the next launch.
/// Applying the package is done by a detached PowerShell helper after this process exits, so no running executable
/// has to be overwritten.
/// </summary>
public static class UpdateService
{
    public const string Repository = "mrsealhh/postsharp";
    public const string PackageAssetName = "PostSharp-win-x64.zip";
    public const string UpdateMarkerFileName = ".postsharp-update-enabled";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string UserAgent = "PostSharp-Update";
    private const int BackgroundIntervalHours = 6;

    private static readonly HttpClient Client = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static event EventHandler? PendingChanged;

    public static Version CurrentVersion =>
        NormalizeVersion(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0));

    public static string CurrentVersionText
    {
        get
        {
            var version = CurrentVersion;
            return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        }
    }

    /// <summary>
    /// Release packages contain a marker file. Development builds and arbitrary output folders therefore never
    /// overwrite themselves while they are being debugged.
    /// </summary>
    public static bool IsEnabledLayout =>
        string.Equals(
            Environment.GetEnvironmentVariable("POSTSHARP_UPDATE_ENABLED"),
            "1",
            StringComparison.Ordinal
        ) || File.Exists(Path.Combine(AppContext.BaseDirectory, UpdateMarkerFileName));

    public static string PendingFilePath => Path.Combine(AppPaths.UpdateDirectory, "pending.json");

    public static string? PendingVersion
    {
        get
        {
            try
            {
                return LoadPending()?.Version;
            }
            catch (Exception exception)
            {
                Log.Debug(exception, "读取待安装更新信息失败");
                return null;
            }
        }
    }

    public static async Task RunBackgroundLoopAsync(
        AppSettings settings,
        Action<UpdateCheckResult> notify,
        CancellationToken cancellationToken
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (settings.AutoCheckUpdates)
            {
                var result = await CheckAndStageAsync(settings, false, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Status == UpdateCheckStatus.Staged)
                {
                    notify(result);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(BackgroundIntervalHours), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public static async Task<UpdateCheckResult> CheckAndStageAsync(
        AppSettings settings,
        bool force,
        CancellationToken cancellationToken = default
    )
    {
        if (!force && !settings.AutoCheckUpdates)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Disabled, "自动检查更新已关闭。");
        }

        if (!IsEnabledLayout)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.UnsupportedLayout,
                "当前为开发运行目录，自动更新仅在已发布安装包中启用。"
            );
        }

        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, "正在检查更新，请稍后重试。");
        }

        try
        {
            var release = await GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.NoRelease,
                    "GitHub 上还没有可用的发布版本。"
                );
            }

            if (release.Version <= CurrentVersion)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    $"已是最新版本（{CurrentVersionText}）。"
                );
            }

            var pending = LoadPending();
            if (
                pending is not null
                && pending.Version == release.Version.ToString()
                && Directory.Exists(pending.StagingDirectory)
            )
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Staged,
                    $"新版本 {release.Version} 已下载，点击顶栏「安装更新」应用。"
                );
            }

            await DownloadAndStageAsync(release, cancellationToken).ConfigureAwait(false);
            return new UpdateCheckResult(
                UpdateCheckStatus.Staged,
                $"新版本 {release.Version} 已下载，点击顶栏「安装更新」应用。"
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "检查或下载更新失败 {Version}", CurrentVersionText);
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                "检查更新失败，请确认网络可以访问 GitHub 后重试。"
            );
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Starts the detached updater when a verified package is waiting. Returns true when the current process should
    /// exit immediately; the helper waits for it, swaps the application directory and relaunches the app.
    /// </summary>
    public static bool TryApplyPendingUpdate()
    {
        if (!IsEnabledLayout)
        {
            return false;
        }

        var pending = LoadPending();
        if (pending is null || !IsValidPending(pending))
        {
            return false;
        }

        var pendingVersion = ParseReleaseVersion(pending.Version);
        if (pendingVersion is null || pendingVersion <= CurrentVersion)
        {
            TryDeletePending();
            return false;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.UpdateDirectory);
            var scriptPath = Path.Combine(AppPaths.UpdateDirectory, "apply-update.ps1");
            File.WriteAllText(scriptPath, UpdaterScript, new UTF8Encoding(false));

            var target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var executable = Path.Combine(target, pending.ExecutableName);
            var backup = Path.Combine(
                AppPaths.UpdateDirectory,
                $"backup-{pending.Version}-{DateTime.Now:yyyyMMdd-HHmmss}"
            );

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = AppPaths.UpdateDirectory,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("-Source");
            startInfo.ArgumentList.Add(pending.StagingDirectory);
            startInfo.ArgumentList.Add("-Target");
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("-Executable");
            startInfo.ArgumentList.Add(executable);
            startInfo.ArgumentList.Add("-Pending");
            startInfo.ArgumentList.Add(PendingFilePath);
            startInfo.ArgumentList.Add("-Backup");
            startInfo.ArgumentList.Add(backup);
            startInfo.ArgumentList.Add("-DataRoot");
            startInfo.ArgumentList.Add(AppPaths.Root);

            System.Diagnostics.Process.Start(startInfo);
            Log.Information(
                "已启动更新器，退出后安装 {Version} {StagingDirectory}",
                pending.Version,
                pending.StagingDirectory
            );
            return true;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "启动更新器失败 {Version}", pending.Version);
            return false;
        }
    }

    private static async Task DownloadAndStageAsync(
        UpdateRelease release,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(AppPaths.UpdateDirectory);
        var temporaryZip = Path.Combine(
            AppPaths.UpdateDirectory,
            $"{release.Version}-{Guid.NewGuid():N}.zip"
        );

        try
        {
            using var response = await Client
                .GetAsync(
                    release.AssetUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken
                )
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var file = File.Create(temporaryZip))
            {
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var checksumText = await Client
                .GetStringAsync(release.ChecksumUrl, cancellationToken)
                .ConfigureAwait(false);
            var expected = ReadChecksum(checksumText);
            if (expected is null)
            {
                throw new InvalidOperationException("发布包缺少有效 SHA256 校验值。");
            }

            using var hashStream = File.OpenRead(temporaryZip);
            var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false)
            );
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("更新包 SHA256 校验失败，已拒绝安装。");
            }

            var staging = Path.Combine(
                AppPaths.UpdateDirectory,
                "staging",
                release.Version.ToString()
            );
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            using (var archive = ZipFile.OpenRead(temporaryZip))
            {
                foreach (var entry in archive.Entries)
                {
                    var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (
                        !destination.StartsWith(
                            Path.GetFullPath(staging) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        throw new InvalidOperationException("更新包包含非法路径。");
                    }
                }
            }

            ZipFile.ExtractToDirectory(temporaryZip, staging, overwriteFiles: true);
            if (!File.Exists(Path.Combine(staging, "PostSharp.exe")))
            {
                throw new InvalidOperationException("更新包中缺少 PostSharp.exe。");
            }

            var pending = new PendingUpdate
            {
                Version = release.Version.ToString(),
                StagingDirectory = staging,
                Sha256 = actual,
                ReleaseUrl = release.ReleaseUrl,
            };
            var json = JsonSerializer.Serialize(pending, JsonOptions);
            File.WriteAllText(PendingFilePath, json, new UTF8Encoding(false));
            PendingChanged?.Invoke(null, EventArgs.Empty);
        }
        finally
        {
            TryDeleteFile(temporaryZip);
        }
    }

    private static async Task<UpdateRelease?> GetLatestReleaseAsync(
        CancellationToken cancellationToken
    )
    {
        using var response = await Client
            .GetAsync(LatestReleaseApi, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var json = await response
            .Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;
        var version = ParseReleaseVersion(tag);
        if (version is null)
        {
            return null;
        }

        string? assetUrl = null;
        string? checksumUrl = null;
        if (
            root.TryGetProperty("assets", out var assets)
            && assets.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement)
                    ? urlElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                if (string.Equals(name, PackageAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = url;
                }
                else if (name.Contains("sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksumUrl = url;
                }
            }
        }

        if (assetUrl is null || checksumUrl is null)
        {
            return null;
        }

        return new UpdateRelease(
            version,
            root.TryGetProperty("html_url", out var htmlElement)
                ? htmlElement.GetString() ?? string.Empty
                : string.Empty,
            assetUrl,
            checksumUrl
        );
    }

    /// <summary>Parses a release tag such as <c>v1.2.3</c> for version comparison.</summary>
    public static Version? ParseReleaseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var text = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(text, out var version) ? NormalizeVersion(version) : null;
    }

    private static string? ReadChecksum(string text)
    {
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 64 && token.All(Uri.IsHexDigit))
            {
                return token;
            }
        }

        return null;
    }

    private static PendingUpdate? LoadPending()
    {
        try
        {
            if (!File.Exists(PendingFilePath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<PendingUpdate>(
                File.ReadAllText(PendingFilePath),
                JsonOptions
            );
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "读取待安装更新失败 {PendingFile}", PendingFilePath);
            return null;
        }
    }

    private static bool IsValidPending(PendingUpdate pending)
    {
        try
        {
            if (
                string.IsNullOrWhiteSpace(pending.Version)
                || string.IsNullOrWhiteSpace(pending.ExecutableName)
                || string.IsNullOrWhiteSpace(pending.StagingDirectory)
            )
            {
                return false;
            }

            var executableName = pending.ExecutableName.Trim();
            if (
                executableName is "." or ".."
                || Path.IsPathRooted(executableName)
                || !string.Equals(
                    Path.GetFileName(executableName),
                    executableName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return false;
            }

            var staging = Path.GetFullPath(pending.StagingDirectory);
            var root = Path.GetFullPath(AppPaths.UpdateDirectory) + Path.DirectorySeparatorChar;
            return staging.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(staging, executableName));
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "待安装更新信息无效 {PendingFile}", PendingFilePath);
            return false;
        }
    }

    private static void TryDeletePending()
    {
        try
        {
            File.Delete(PendingFilePath);
            PendingChanged?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "删除待安装更新标记失败 {PendingFile}", PendingFilePath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "删除临时更新文件失败 {UpdateFile}", path);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private sealed record UpdateRelease(
        Version Version,
        string ReleaseUrl,
        string AssetUrl,
        string ChecksumUrl
    );

    private const string UpdaterScript = """
        param(
            [Parameter(Mandatory=$true)][int]$ProcessId,
            [Parameter(Mandatory=$true)][string]$Source,
            [Parameter(Mandatory=$true)][string]$Target,
            [Parameter(Mandatory=$true)][string]$Executable,
            [Parameter(Mandatory=$true)][string]$Pending,
            [Parameter(Mandatory=$true)][string]$Backup,
            [Parameter(Mandatory=$true)][string]$DataRoot
        )

        $ErrorActionPreference = "Stop"
        $log = Join-Path (Split-Path $Pending) "update.log"
        function Write-UpdateLog([string]$message) {
            Add-Content -Path $log -Value ("{0:o} {1}" -f (Get-Date), $message)
        }

        $backedUp = $false
        try {
            Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
            $backedUp = $false
            if (Test-Path $Backup) { Remove-Item $Backup -Recurse -Force }
            if (Test-Path $Target) {
                New-Item -ItemType Directory -Path $Backup -Force | Out-Null
                Get-ChildItem -Path $Target -Force | Copy-Item -Destination $Backup -Recurse -Force
                $backedUp = $true
                Remove-Item -Path $Target -Recurse -Force
            }
            New-Item -ItemType Directory -Path $Target -Force | Out-Null
            Get-ChildItem -Path $Source -Force | Copy-Item -Destination $Target -Recurse -Force

            # If the project data directory sits inside the application directory, the old directory
            # (including SQLite data) was moved to backup. Copy it back so an update never loses user data.
            $targetPrefix = $Target.TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
            $dataRoot = $DataRoot.TrimEnd([char[]]@('\', '/'))
            if ($dataRoot.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                $relativeData = $dataRoot.Substring($targetPrefix.Length)
                if ($relativeData) {
                    $backupData = Join-Path $Backup $relativeData
                    $targetData = Join-Path $Target $relativeData
                    if (Test-Path $backupData) {
                        if (Test-Path $targetData) {
                            Remove-Item $targetData -Recurse -Force
                        }
                        Copy-Item -Path $backupData -Destination $targetData -Recurse -Force
                    }
                }
            }

            Start-Process -FilePath $Executable -WorkingDirectory $Target
            Remove-Item -Path $Pending -Force -ErrorAction SilentlyContinue
            Write-UpdateLog "updated"
        } catch {
            Write-UpdateLog ("failed: " + $_.Exception.Message)
            if ($backedUp -and (Test-Path $Backup)) {
                if (Test-Path $Target) { Remove-Item $Target -Recurse -Force -ErrorAction SilentlyContinue }
                New-Item -ItemType Directory -Path $Target -Force | Out-Null
                Get-ChildItem -Path $Backup -Force | Copy-Item -Destination $Target -Recurse -Force
            }
            Start-Process -FilePath $Executable -WorkingDirectory $Target
        }
        """;
}
