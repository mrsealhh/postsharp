using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png")));
        KeyDown += OnWindowKeyDown;
        Opened += (_, _) => ApplyTitleBarTheme();
        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();
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
