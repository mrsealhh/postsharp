using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using PostSharp.Models;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png")));
        KeyDown += OnWindowKeyDown;
        Opened += (_, _) =>
        {
            EnablePopupOverlayLayer();
            ApplyTitleBarTheme();
        };
        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();
        Closed += (_, _) =>
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.DiscardFontPreview();
            }
        };
    }

    /// <summary>
    /// Arc Select renders its popup in the window overlay layer. A plain Avalonia window must opt into that
    /// layer explicitly, otherwise opening the popup throws "no overlay layer is found for the target control".
    /// </summary>
    private void EnablePopupOverlayLayer()
    {
        var manager = this.GetVisualDescendants()
            .OfType<Avalonia.Controls.Primitives.VisualLayerManager>()
            .FirstOrDefault();

        manager
            ?.GetType()
            .GetProperty(
                "EnablePopupOverlayLayer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            )
            ?.SetValue(manager, true);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private async void OnBrowseLogDirectoryClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "选择日志目录", AllowMultiple = false }
        );

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.SetLogDirectory(path);
        }
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private async void OnImportFontsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "导入字体文件",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("字体文件")
                    {
                        Patterns = new[] { "*.ttf", "*.otf", "*.ttc" },
                        MimeTypes = new[] { "font/ttf", "font/otf", "font/collection" },
                    },
                },
            }
        );

        var paths = new List<string>();
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                paths.Add(path);
            }
        }

        if (paths.Count > 0)
        {
            await viewModel.ImportFontsAsync(paths);
        }
    }

    private async void OnAddFontDirectoryClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (DataContext is not SettingsViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "添加字体目录", AllowMultiple = false }
        );

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await viewModel.AddFontDirectoryAsync(path);
        }
    }

    private void OnRemoveFontDirectoryClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        if (
            sender is Control { DataContext: FontDirectoryItem item }
            && DataContext is SettingsViewModel viewModel
        )
        {
            viewModel.RemoveFontDirectory(item);
        }
    }

    private void OnUseFontForAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (
            sender is Control { DataContext: string family }
            && DataContext is SettingsViewModel viewModel
        )
        {
            viewModel.ApplyFontToAll(family);
        }
    }

    private void ApplyTitleBarTheme()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { } handle)
        {
            return;
        }

        var useDark = ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
        DwmSetWindowAttribute(handle.Handle, 20, ref useDark, sizeof(int));
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size
    );
}
