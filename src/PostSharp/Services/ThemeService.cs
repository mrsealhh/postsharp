using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.Themes.Corners;

namespace PostSharp.Services;

/// <summary>
/// Applies the saved appearance preference through ArcTheme (the theme engine Arc controls actually read).
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

    public static Task ApplyAsync(
        string? preference,
        bool isStartup = false,
        CancellationToken cancellationToken = default,
        double cornerScale = 1
    )
    {
        _preference = preference is Light or Dark ? preference : FollowSystem;

        if (Application.Current is not { } application)
        {
            return Task.CompletedTask;
        }

        var variant = ResolveVariant(_preference);
        application.RequestedThemeVariant = variant;
        WatchSystemTheme(application);

        try
        {
            ArcTheme.Apply(theme =>
            {
                theme.Appearance(
                    _preference switch
                    {
                        Light => ArcAppearance.Light,
                        Dark => ArcAppearance.Dark,
                        _ => ArcAppearance.System,
                    }
                );

                if (double.IsFinite(cornerScale) && Math.Abs(cornerScale - 1) > 0.0001)
                {
                    theme.Corners(ArcCornerStyle.Scale(Math.Clamp(cornerScale, 0, 2)));
                }
            });

            Log.Information(
                "主题已应用 {Preference} / {Appearance}",
                _preference,
                ArcTheme.State.Appearance
            );
        }
        catch (Exception exception)
        {
            Log.Error(exception, "应用主题失败 {Preference}", _preference);
        }

        return Task.CompletedTask;
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
}
