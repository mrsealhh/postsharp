using Avalonia.Media;
using Avalonia.Media.Fonts;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Result of copying font files into the managed font directory.</summary>
public sealed record FontImportResult(int FileCount, IReadOnlyList<string> Families);

/// <summary>
/// Loads user font files into a private Avalonia font collection. Imported files are copied into
/// <c>%APPDATA%\PostSharp\fonts</c>; additional folders are scanned in place. Both are remembered in
/// <see cref="AppSettings.FontDirectories"/> so the same families are available on the next start.
/// </summary>
public static class FontService
{
    private const string CollectionKeyValue = "fonts:PostSharp";
    private const string SystemDefaultFont = "系统默认";
    private const string MicrosoftYaHeiFont = "微软雅黑";

    private static readonly Uri CollectionKey = new(CollectionKeyValue, UriKind.Absolute);
    private static readonly object Gate = new();
    private static readonly string[] FontExtensions = new[] { ".ttf", ".otf", ".ttc" };
    private static readonly string[] BuiltInChoices = new[]
    {
        "Inter",
        "系统默认",
        "微软雅黑",
        "Consolas",
        "Cascadia Mono",
        "Geist Mono",
    };

    private static readonly Dictionary<string, FontFamily> CustomFamiliesMap = new(
        StringComparer.OrdinalIgnoreCase
    );

    private static bool _initialized;
    private static IReadOnlyList<string> _configuredDirectories = Array.Empty<string>();

    public static string ManagedFontDirectory => Path.Combine(AppPaths.Root, "fonts");

    public static IReadOnlyList<string> CustomFamilies =>
        CustomFamiliesMap.Keys.OrderBy(name => name, StringComparer.CurrentCulture).ToArray();

    public static IReadOnlyList<string> GetChoices()
    {
        var choices = new List<string>(BuiltInChoices);

        foreach (var family in CustomFamilies)
        {
            if (!choices.Contains(family, StringComparer.OrdinalIgnoreCase))
            {
                choices.Add(family);
            }
        }

        return choices;
    }

    public static void Initialize(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ManagedFontDirectory);

            if (_initialized)
            {
                Reload(settings.FontDirectories);
                return;
            }

            _initialized = true;
            Reload(settings.FontDirectories);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "初始化字体服务失败，继续使用系统字体");
        }
    }

    /// <summary>Rebuilds the private collection from the managed directory plus the supplied folders.</summary>
    public static IReadOnlyList<string> Reload(IReadOnlyList<string> fontDirectories)
    {
        if (!_initialized)
        {
            return Array.Empty<string>();
        }

        lock (Gate)
        {
            var directories = new List<string> { ManagedFontDirectory };
            foreach (var directory in fontDirectories)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    var full = Path.GetFullPath(directory.Trim());
                    if (!directories.Contains(full, StringComparer.OrdinalIgnoreCase))
                    {
                        directories.Add(full);
                    }
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "忽略无效字体目录 {FontDirectory}", directory);
                }
            }

            _configuredDirectories = directories
                .Where(directory =>
                    !string.Equals(
                        directory,
                        ManagedFontDirectory,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .ToArray();

            if (FontManager.Current is not { } manager)
            {
                Log.Warning("字体管理器不可用，自定义字体未加载");
                return Array.Empty<string>();
            }

            manager.RemoveFontCollection(CollectionKey);

            var collection = new DiskFontCollection(CollectionKey);
            var loaded = false;

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    Log.Warning("字体目录不存在，已跳过 {FontDirectory}", directory);
                    continue;
                }

                try
                {
                    if (collection.TryAddFontSource(new Uri(directory)))
                    {
                        loaded = true;
                    }
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "加载字体目录失败 {FontDirectory}", directory);
                }
            }

            CustomFamiliesMap.Clear();

            if (collection.Count == 0)
            {
                if (loaded)
                {
                    Log.Warning("字体目录中没有可读取的字体文件");
                }

                (collection as IDisposable)?.Dispose();
                Log.Information("未加载自定义字体，继续使用系统字体");
                return Array.Empty<string>();
            }

            manager.AddFontCollection(collection);

            for (var index = 0; index < collection.Count; index++)
            {
                var family = collection[index];
                var displayName = family.FamilyNames.PrimaryFamilyName;

                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    CustomFamiliesMap[displayName] = family;
                }
            }

            var families = CustomFamilies;
            Log.Information(
                "已加载 {Count} 个自定义字体族：{Families}",
                families.Count,
                string.Join("、", families)
            );
            return families;
        }
    }

    public static FontFamily Resolve(string? familyName)
    {
        var requested = familyName?.Trim();
        if (string.IsNullOrEmpty(requested) || requested == SystemDefaultFont)
        {
            return FontFamily.Default;
        }

        if (requested == MicrosoftYaHeiFont)
        {
            return new FontFamily("Microsoft YaHei UI");
        }

        return CustomFamiliesMap.TryGetValue(requested, out var family)
            ? family
            : new FontFamily(requested);
    }

    public static bool HasFontFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                && Directory
                    .EnumerateFiles(directory)
                    .Any(file =>
                        FontExtensions.Contains(
                            Path.GetExtension(file),
                            StringComparer.OrdinalIgnoreCase
                        )
                    );
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "检查字体目录失败 {FontDirectory}", directory);
            return false;
        }
    }

    public static async Task<FontImportResult> ImportFilesAsync(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken = default
    )
    {
        Directory.CreateDirectory(ManagedFontDirectory);

        var copied = new List<string>();
        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (
                !FontExtensions.Contains(
                    Path.GetExtension(sourcePath),
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                Log.Warning("跳过非字体文件 {FontFile}", sourcePath);
                continue;
            }

            if (!File.Exists(sourcePath))
            {
                Log.Warning("字体文件不存在，已跳过 {FontFile}", sourcePath);
                continue;
            }

            var fileName = Path.GetFileName(sourcePath);
            var destination = Path.Combine(ManagedFontDirectory, fileName);
            var temporary = destination + ".tmp";

            try
            {
                await using (var source = File.OpenRead(sourcePath))
                await using (var target = File.Create(temporary))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }

                File.Move(temporary, destination, overwrite: true);
                copied.Add(destination);
                Log.Information(
                    "字体文件已复制 {SourceFile} -> {DestinationFile}",
                    sourcePath,
                    destination
                );
            }
            catch (Exception exception)
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }

                Log.Error(exception, "复制字体文件失败 {SourceFile}", sourcePath);
            }
        }

        if (copied.Count == 0)
        {
            throw new InvalidOperationException(
                "没有可导入的字体文件，请选择 TTF、OTF 或 TTC 文件。"
            );
        }

        var families = Reload(_configuredDirectories);
        return new FontImportResult(copied.Count, families);
    }

    /// <summary>Font collection whose key uses the <c>fonts:</c> scheme required by Avalonia.</summary>
    private sealed class DiskFontCollection(Uri key) : FontCollectionBase
    {
        public override Uri Key { get; } = key;
    }
}
