using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using Avalonia.Media;
using TKCode.Arc.Primitives;

namespace PostSharp.Services;

/// <summary>
/// Applies the user's whole-window UI zoom by wrapping each window's content in a
/// <see cref="LayoutTransformControl"/>. The layout size changes with the zoom (unlike a render
/// transform), so 90% really fits more rows instead of only shrinking the pixels.
/// </summary>
public static class UiScaleService
{
    public const double MinimumScale = 0.6;
    public const double MaximumScale = 1.5;

    private static readonly Dictionary<Window, LayoutTransformControl> Hosts = new();

    public static double CurrentScale { get; private set; } = 0.8;

    public static void SetScale(double scale) => CurrentScale = Normalize(scale);

    public static void ApplyToAll(double scale)
    {
        CurrentScale = Normalize(scale);
        if (
            Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop
        )
        {
            return;
        }

        foreach (var window in desktop.Windows.ToList())
        {
            Apply(window, CurrentScale);
        }
    }

    /// <summary>Wraps a window created after the initial scale was chosen.</summary>
    public static void Attach(Window window) => Apply(window, CurrentScale);

    private static void Apply(Window window, double scale)
    {
        if (window.Content is not Control content)
        {
            return;
        }

        if (!Hosts.TryGetValue(window, out var host))
        {
            host = window
                .GetLogicalDescendants()
                .OfType<LayoutTransformControl>()
                .FirstOrDefault(control => control.Name == "ScaleHost");

            if (host is null)
            {
                host = new LayoutTransformControl
                {
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                };

                window.Content = null;
                host.Child = content;
                window.Content = host;
            }

            Hosts[window] = host;
            window.Closed += (_, _) => Hosts.Remove(window);
        }

        host.LayoutTransform = new ScaleTransform(scale, scale);

        // Arc Select / Tooltip surfaces are hosted in the overlay layer, outside the scaled content.
        // ArcZoom carries the same factor to those surfaces so their anchors and sizes stay aligned.
        ArcZoom.SetScale(window, scale);
    }

    private static double Normalize(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, MinimumScale, MaximumScale) : 0.8;
}
