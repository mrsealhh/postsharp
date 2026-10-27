using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using PostSharp.Storage.Abstractions;

namespace PostSharp.Services;

/// <summary>Raised when a profile points at a provider whose plugin is not installed.</summary>
public sealed class StoragePluginMissingException(string providerId)
    : InvalidOperationException(
        $"未安装存储插件「{providerId}」。请把插件文件夹放到 {AppPaths.StoragePluginDirectory}，"
            + "然后在设置中点击「重新扫描」。"
    )
{
    public string ProviderId { get; } = providerId;
}

/// <summary>
/// Loads remote storage plugins from <c>plugins/&lt;name&gt;/plugin.json</c> under the PostSharp data
/// directory (and next to the executable for portable installs). Each plugin gets its own
/// <see cref="AssemblyLoadContext"/> with a dependency resolver, while the contract assembly is shared
/// with the host. A broken plugin is reported and skipped; local SQLite always keeps working.
/// </summary>
public sealed class StoragePluginRegistry
{
    private const string ManifestName = "plugin.json";
    private const string ContractAssembly = "PostSharp.Storage.Abstractions";
    private readonly object _gate = new();
    private readonly List<IStorageProviderPlugin> _plugins = new();
    private readonly List<string> _issues = new();
    private bool _loaded;

    public static StoragePluginRegistry Current { get; } = new();

    public IReadOnlyList<IStorageProviderPlugin> Plugins
    {
        get
        {
            EnsureLoaded();
            lock (_gate)
            {
                return _plugins.ToList();
            }
        }
    }

    public IReadOnlyList<string> Issues
    {
        get
        {
            EnsureLoaded();
            lock (_gate)
            {
                return _issues.ToList();
            }
        }
    }

    public IReadOnlyList<string> ProviderIds =>
        Plugins.Select(plugin => plugin.Info.Id).Distinct(StringComparer.Ordinal).ToList();

    public IReadOnlyList<string> Directories => GetScanDirectories();

    public IStorageProviderPlugin? Find(string providerId)
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _plugins.FirstOrDefault(plugin =>
                string.Equals(plugin.Info.Id, providerId, StringComparison.Ordinal)
            );
        }
    }

    public bool IsAvailable(string providerId) => Find(providerId) is not null;

    public void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        Refresh();
    }

    /// <summary>Rescans the plugin directories. Safe to call from the settings window.</summary>
    public IReadOnlyList<string> Refresh()
    {
        lock (_gate)
        {
            _plugins.Clear();
            _issues.Clear();
            IReadOnlyList<string> scanDirectories;
            try
            {
                scanDirectories = GetScanDirectories();
            }
            catch (Exception exception)
            {
                _issues.Add($"无法确定插件目录：{exception.Message}");
                Log.Warning(exception, "无法确定存储插件目录");
                _loaded = true;
                return _issues.ToList();
            }

            foreach (var directory in scanDirectories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                try
                {
                    foreach (var pluginDirectory in Directory.EnumerateDirectories(directory))
                    {
                        ScanPluginDirectory(pluginDirectory);
                    }

                    foreach (
                        var dll in Directory
                            .EnumerateFiles(directory, "*.dll")
                            .Where(file =>
                                !string.Equals(
                                    Path.GetFileNameWithoutExtension(file),
                                    ContractAssembly,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                    )
                    {
                        LoadEntryAssembly(dll, "插件文件", reportNoPlugin: false);
                    }
                }
                catch (Exception exception)
                {
                    _issues.Add($"无法读取插件目录 {directory}：{exception.Message}");
                    Log.Warning(exception, "无法读取存储插件目录 {Directory}", directory);
                }
            }

            _loaded = true;
            Log.Information(
                "存储插件扫描完成：{PluginCount} 个插件，{IssueCount} 个问题，目录 {Directories}",
                _plugins.Count,
                _issues.Count,
                string.Join("；", scanDirectories)
            );
            return _issues.ToList();
        }
    }

    private void ScanPluginDirectory(string directory)
    {
        var manifest = Path.Combine(directory, ManifestName);
        if (File.Exists(manifest))
        {
            var entry = ResolveManifestEntry(directory, manifest);
            if (entry is not null)
            {
                LoadEntryAssembly(entry, Path.GetFileName(directory), reportNoPlugin: true);
            }

            return;
        }

        var candidates = Directory
            .EnumerateFiles(directory, "*.dll")
            .Where(file =>
                !string.Equals(
                    Path.GetFileNameWithoutExtension(file),
                    ContractAssembly,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();
        if (candidates.Count == 1)
        {
            LoadEntryAssembly(candidates[0], Path.GetFileName(directory), reportNoPlugin: true);
            return;
        }

        if (candidates.Count > 1)
        {
            _issues.Add(
                $"{Path.GetFileName(directory)}：缺少 {ManifestName}，无法确定入口程序集（目录中有 {candidates.Count} 个 DLL）。"
            );
        }
    }

    private string? ResolveManifestEntry(string directory, string manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("assembly", out var assembly)
                || assembly.ValueKind != JsonValueKind.String
            )
            {
                _issues.Add($"{Path.GetFileName(directory)}：{ManifestName} 缺少 assembly 字段。");
                return null;
            }

            var name = assembly.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                _issues.Add($"{Path.GetFileName(directory)}：{ManifestName} 的 assembly 为空。");
                return null;
            }

            var root =
                Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var entry = Path.GetFullPath(Path.Combine(directory, name));
            if (!entry.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                _issues.Add($"{Path.GetFileName(directory)}：入口程序集不能位于插件目录之外。");
                return null;
            }

            if (!File.Exists(entry))
            {
                _issues.Add($"{Path.GetFileName(directory)}：找不到入口程序集 {name}。");
                return null;
            }

            return entry;
        }
        catch (Exception exception)
        {
            _issues.Add(
                $"{Path.GetFileName(directory)}：无法读取 {ManifestName}（{exception.Message}）。"
            );
            Log.Warning(exception, "读取存储插件清单失败 {Manifest}", manifest);
            return null;
        }
    }

    private void LoadEntryAssembly(string entry, string source, bool reportNoPlugin)
    {
        try
        {
            var resolver = new AssemblyDependencyResolver(entry);
            var context = new AssemblyLoadContext(
                $"PostSharp.Storage.{Path.GetFileNameWithoutExtension(entry)}.{Guid.NewGuid():N}",
                isCollectible: false
            );
            context.Resolving += (loadContext, name) =>
            {
                if (
                    string.Equals(name.Name, ContractAssembly, StringComparison.Ordinal)
                    || string.Equals(name.Name, "PostSharp", StringComparison.Ordinal)
                )
                {
                    return null;
                }

                var path = resolver.ResolveAssemblyToPath(name);
                if (path is null)
                {
                    var probe = Path.Combine(
                        Path.GetDirectoryName(entry) ?? string.Empty,
                        (name.Name ?? string.Empty) + ".dll"
                    );
                    path = File.Exists(probe) ? probe : null;
                }

                return path is null ? null : loadContext.LoadFromAssemblyPath(path);
            };
            context.ResolvingUnmanagedDll += (_, name) =>
            {
                var path = resolver.ResolveUnmanagedDllToPath(name);
                if (path is null)
                {
                    var probe = Path.Combine(
                        Path.GetDirectoryName(entry) ?? string.Empty,
                        name + ".dll"
                    );
                    path = File.Exists(probe) ? probe : null;
                }

                return path is null ? IntPtr.Zero : NativeLibrary.Load(path);
            };

            var assembly = context.LoadFromAssemblyPath(entry);
            var found = 0;
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (
                    type.IsAbstract
                    || !type.IsClass
                    || !typeof(IStorageProviderPlugin).IsAssignableFrom(type)
                )
                {
                    continue;
                }

                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    _issues.Add($"{source}：{type.Name} 缺少公共无参构造函数。");
                    continue;
                }

                if (Activator.CreateInstance(type) is not IStorageProviderPlugin plugin)
                {
                    _issues.Add($"{source}：无法创建 {type.Name}。");
                    continue;
                }

                if (
                    string.IsNullOrWhiteSpace(plugin.Info.Id)
                    || string.IsNullOrWhiteSpace(plugin.Info.DisplayName)
                )
                {
                    _issues.Add($"{source}：{type.Name} 的插件 Id 或显示名为空。");
                    continue;
                }

                if (
                    string.Equals(
                        plugin.Info.Id,
                        PostSharp.Models.StorageProviders.LocalSqlite,
                        StringComparison.Ordinal
                    )
                )
                {
                    _issues.Add($"{source}：插件不能使用保留 Id「{plugin.Info.Id}」。");
                    continue;
                }

                var existing = _plugins.FindIndex(candidate =>
                    string.Equals(candidate.Info.Id, plugin.Info.Id, StringComparison.Ordinal)
                );
                if (existing >= 0)
                {
                    _plugins[existing] = plugin;
                }
                else
                {
                    _plugins.Add(plugin);
                }

                found++;
                Log.Information(
                    "已加载存储插件 {PluginId} {DisplayName} {Version}",
                    plugin.Info.Id,
                    plugin.Info.DisplayName,
                    plugin.Info.Version
                );
            }

            if (found == 0 && reportNoPlugin)
            {
                _issues.Add($"{source}：没有找到 IStorageProviderPlugin 实现。");
            }
        }
        catch (Exception exception)
        {
            _issues.Add($"{source}：插件加载失败（{exception.Message}）。");
            Log.Warning(exception, "加载存储插件失败 {Entry}", entry);
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Select(type => type!);
        }
    }

    private static IReadOnlyList<string> GetScanDirectories()
    {
        var directories = new List<string>();
        AddDirectory(directories, AppPaths.BundledPluginDirectory);
        AddDirectory(directories, AppPaths.StoragePluginDirectory);
        return directories;
    }

    private static void AddDirectory(List<string> directories, string directory)
    {
        var fullPath = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (
            !directories.Contains(fullPath, StringComparer.OrdinalIgnoreCase)
            && !string.Equals(
                fullPath,
                Path.GetFullPath(AppContext.BaseDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            directories.Add(fullPath);
        }
    }
}
