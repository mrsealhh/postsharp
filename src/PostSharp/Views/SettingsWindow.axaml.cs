using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using PostSharp.Controls;
using PostSharp.Models;
using PostSharp.Services;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        UiScaleService.Attach(this);
        DataContextChanged += (_, _) => ApplyCompactLayout();
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png")));
        KeyDown += OnWindowKeyDown;
        PropertyChanged += OnWindowPropertyChanged;
        Opened += (_, _) =>
        {
            this.EnablePopupOverlayLayer();
            ApplyTitleBarTheme();
        };
        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();
        Closed += (_, _) =>
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.DiscardFontPreview();
                viewModel.Dispose();
            }

            HeapTrim.Request();
        };
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            UpdateWindowStateVisuals();
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || IsInteractiveTitleBarSource(e.Source))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private bool IsInteractiveTitleBarSource(object? source)
    {
        for (
            var current = source as Visual;
            current is not null;
            current = current.GetVisualParent()
        )
        {
            if (ReferenceEquals(current, TitleBar))
            {
                return false;
            }

            if (
                current is Button or TextBox or ComboBox or CheckBox or Slider
                || current is Control { Focusable: true }
            )
            {
                return true;
            }
        }

        return false;
    }

    private void OnMinimizeWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnToggleMaximizeWindowClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    ) => ToggleMaximize();

    private void OnCloseWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close();

    private void ToggleMaximize() =>
        WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateWindowStateVisuals()
    {
        var maximized = WindowState == WindowState.Maximized;
        WindowMaximizeGlyph.IsVisible = !maximized;
        WindowRestoreGlyph.IsVisible = maximized;
    }

    private void ApplyCompactLayout()
    {
        Classes.Set(
            "compact",
            DataContext is SettingsViewModel viewModel
                && viewModel.Draft.UiLayout == UiLayouts.Compact
        );
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

    private void OnOpenStoragePluginFolderClick(
        object? sender,
        Avalonia.Interactivity.RoutedEventArgs e
    )
    {
        try
        {
            var directory = AppPaths.EnsureStoragePluginDirectory();
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                viewModel.ReportStoragePluginFolderFailure(
                    $"无法打开插件目录：{exception.Message}。请手动打开 {AppPaths.StoragePluginDirectory}。"
                );
            }
        }
    }

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
