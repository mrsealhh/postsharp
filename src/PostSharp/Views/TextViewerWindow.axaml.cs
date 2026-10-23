using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Styling;

namespace PostSharp.Views;

/// <summary>
/// A paged, read-only text viewer. Large responses are not rendered in the main response pane at full size;
/// this window shows one 32 KB page at a time and can copy the complete body.
/// </summary>
public partial class TextViewerWindow : Window
{
    private const int PageSize = 16 * 1024;

    private readonly string _fullText;
    private int _page;

    public TextViewerWindow()
        : this("响应详情", string.Empty, string.Empty)
    {
    }

    public TextViewerWindow(string title, string text, string info)
    {
        InitializeComponent();

        _fullText = text ?? string.Empty;
        Title = title;
        TitleText.Text = title;
        InfoText.Text = info;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png")));

        Opened += (_, _) => ApplyTitleBarTheme();
        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();
        UpdatePage();
    }

    private int PageCount => Math.Max(1, (_fullText.Length + PageSize - 1) / PageSize);

    private void UpdatePage()
    {
        var start = _page * PageSize;
        var length = Math.Min(PageSize, Math.Max(0, _fullText.Length - start));
        BodyText.Text = _fullText.Substring(start, length);
        PageText.Text = $"第 {_page + 1} / {PageCount} 页 · 每页 {PageSize / 1024} KB"
                        + (_fullText.Length == 0 ? " · 响应体为空" : string.Empty);
        PreviousButton.IsEnabled = _page > 0;
        NextButton.IsEnabled = _page < PageCount - 1;
    }

    private void OnPreviousClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_page <= 0)
        {
            return;
        }

        _page--;
        UpdatePage();
    }

    private void OnNextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_page >= PageCount - 1)
        {
            return;
        }

        _page++;
        UpdatePage();
    }

    private async void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(_fullText);
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
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
