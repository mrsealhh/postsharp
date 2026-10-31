using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace PostSharp.Services;

/// <summary>One menu row for a tray icon; a null entry is a separator.</summary>
internal sealed record TrayMenuEntry(string Header, Action Action);

/// <summary>
/// The tray surface used by <see cref="App"/>: a native Windows menu on Windows, Avalonia's
/// TrayIcon elsewhere or as a fallback.
/// </summary>
internal interface ITrayIconHost : IDisposable
{
    bool IsVisible { get; set; }
}

/// <summary>Avalonia's managed tray icon, used on macOS/Linux and when the native host fails.</summary>
internal sealed class AvaloniaTrayIconHost : ITrayIconHost
{
    private readonly TrayIcon _icon;

    public AvaloniaTrayIconHost(
        Application application,
        string toolTipText,
        IReadOnlyList<TrayMenuEntry?> menuEntries
    )
    {
        var menu = new NativeMenu();
        foreach (var entry in menuEntries)
        {
            if (entry is null)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                continue;
            }

            var item = new NativeMenuItem(entry.Header);
            item.Click += (_, _) => entry.Action();
            menu.Items.Add(item);
        }

        _icon = new TrayIcon
        {
            Icon = LoadTrayIcon(),
            ToolTipText = toolTipText,
            Menu = menu,
            IsVisible = false,
        };
        _icon.Clicked += (_, _) => menuEntries.FirstOrDefault()?.Action();
        TrayIcon.SetIcons(application, new TrayIcons { _icon });
    }

    public bool IsVisible
    {
        get => _icon.IsVisible;
        set => _icon.IsVisible = value;
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }

    private static WindowIcon LoadTrayIcon() =>
        new(
            AssetLoader.Open(
                new Uri(
                    OperatingSystem.IsWindows()
                        ? "avares://PostSharp/Assets/logo.ico"
                        : "avares://PostSharp/Assets/logo-icon.png"
                )
            )
        );
}
