using AtomUI;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using TKCode.Arc.Themes.Corners;

namespace PostSharp.Services;

/// <summary>
/// Applies the saved appearance preference through AtomUI (the theme engine Arc controls actually read).
/// "跟随系统" resolves the current Windows theme on startup and keeps following it while the app runs.
/// </summary>
public static class ThemeService
{
    private const string FollowSystem = "跟随系统";
    private const string Light = "浅色";
    private const string Dark = "深色";

    public static IReadOnlyList<string> Options { get; } = new[] { FollowSystem, Light, Dark };

    private static string _preference = FollowSystem;
    private static bool _watching;

    public static async Task ApplyAsync(
        string? preference,
        bool isStartup = false,
        CancellationToken cancellationToken = default,
        double cornerScale = 1
    )
    {
        _preference = preference is Light or Dark ? preference : FollowSystem;

        if (Application.Current is not { } application)
        {
            return;
        }

        var variant = ResolveVariant(_preference);
        application.RequestedThemeVariant = variant;
        WatchSystemTheme(application);

        try
        {
            var manager = application.GetThemeManager();
            if (manager is null)
            {
                Log.Warning("AtomUI 主题管理器不可用，保留当前外观");
                return;
            }

            var current = manager.CurrentTheme;
            if (current is null)
            {
                Log.Warning("AtomUI 当前主题状态不可用，保留当前外观");
                return;
            }

            var algorithms = BuildAlgorithms(current.Algorithms, variant == ThemeVariant.Dark);
            var builder = new ThemeConfigBuilder().WithInherit(true);
            if (double.IsFinite(cornerScale) && Math.Abs(cornerScale - 1) > 0.0001)
            {
                builder = builder.WithArcCorners(
                    ArcCornerStyle.Scale(Math.Clamp(cornerScale, 0, 2)),
                    ArcCornerScope.Controls
                );
            }

            var config = builder.WithAlgorithms(algorithms).Build();
            var request = new ThemeRequest(
                current.ThemeId,
                config,
                isStartup ? ThemeTransitionReason.Startup : ThemeTransitionReason.UserRequest
            );
            var result = await manager.ApplyThemeAsync(request, cancellationToken);
            if (result.Status == ThemeTransitionStatus.Failed)
            {
                Log.Warning(
                    "应用主题失败 {Preference}：{Error}",
                    _preference,
                    result.Exception?.Message ?? "主题引擎未返回原因"
                );
                return;
            }

            Log.Information(
                "主题已应用 {Preference} / {Appearance} / {Status}",
                _preference,
                result.State?.Appearance ?? ThemeAppearance.Light,
                result.Status
            );
        }
        catch (Exception exception)
        {
            Log.Error(exception, "应用主题失败 {Preference}", _preference);
        }
    }

    private static void WatchSystemTheme(Application application)
    {
        if (_watching || application.PlatformSettings is not { } settings)
        {
            return;
        }

        _watching = true;
        settings.ColorValuesChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_preference != FollowSystem)
                {
                    return;
                }

                var variant = SystemVariant();
                if (application.RequestedThemeVariant != variant)
                {
                    _ = ApplyAsync(FollowSystem);
                }
            });
        };
    }

    private static ThemeVariant ResolveVariant(string preference)
    {
        return preference switch
        {
            Light => ThemeVariant.Light,
            Dark => ThemeVariant.Dark,
            _ => SystemVariant(),
        };
    }

    private static ThemeVariant SystemVariant()
    {
        var variant = Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant;
        return variant == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private static ThemeAlgorithm[] BuildAlgorithms(
        IReadOnlyList<ThemeAlgorithm> current,
        bool dark
    )
    {
        var algorithms = new List<ThemeAlgorithm>();
        foreach (var algorithm in current)
        {
            if (algorithm is not ThemeAlgorithm.Dark && algorithm is not ThemeAlgorithm.Default)
            {
                algorithms.Add(algorithm);
            }
        }

        algorithms.Add(dark ? ThemeAlgorithm.Dark : ThemeAlgorithm.Default);
        return algorithms.ToArray();
    }
}
