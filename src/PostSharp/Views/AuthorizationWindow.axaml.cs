using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Styling;
using PostSharp.Controls;
using PostSharp.Services;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class AuthorizationWindow : Window
{
    private CancellationTokenSource? _signInCts;

    public AuthorizationWindow()
    {
        InitializeComponent();
        UiScaleService.Attach(this);
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PostSharp/Assets/logo-icon.png")));
        KeyDown += OnWindowKeyDown;
        Opened += (_, _) =>
        {
            this.EnablePopupOverlayLayer();
            ApplyTitleBarTheme();
            DiscoveryInput.Focus();
        };
        ActualThemeVariantChanged += (_, _) => ApplyTitleBarTheme();
        Closed += OnClosed;
    }

    private AuthorizationViewModel? ViewModel => DataContext as AuthorizationViewModel;

    private async void OnSignInClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        _signInCts?.Cancel();
        _signInCts?.Dispose();
        _signInCts = new CancellationTokenSource();
        var cancellationToken = _signInCts.Token;

        var authorizationUri = await viewModel.BeginSignInAsync(cancellationToken);
        if (authorizationUri is null)
        {
            return;
        }

        await RunSystemBrowserSignInAsync(viewModel, authorizationUri, cancellationToken);
    }

    private async Task RunSystemBrowserSignInAsync(
        AuthorizationViewModel viewModel,
        Uri authorizationUri,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (!LoopbackRedirectListener.IsLoopbackRedirect(viewModel.Profile.RedirectUri))
            {
                SystemBrowserLauncher.Open(authorizationUri);
                return;
            }

            using var receiver = LoopbackRedirectListener.Start(viewModel.Profile.RedirectUri);
            SystemBrowserLauncher.Open(authorizationUri);
            var callback = await receiver.WaitForCallbackAsync(cancellationToken);
            await viewModel.CompleteSignInAsync(callback, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            viewModel.CancelSignIn();
        }
        catch (OidcException exception)
        {
            viewModel.ReportBrowserFailure(exception.Message);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "系统浏览器授权流程失败");
            viewModel.ReportBrowserFailure(
                "系统浏览器授权失败，请检查网络、回调地址和授权服务后重试。"
            );
        }
    }

    private void OnCancelSignInClick(object? sender, RoutedEventArgs e)
    {
        _signInCts?.Cancel();
        ViewModel?.CancelSignIn();
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await viewModel.RefreshAsync(cancellation.Token);
    }

    private void OnSignOutClick(object? sender, RoutedEventArgs e) => ViewModel?.SignOut();

    private async void OnManualCallbackClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (!Uri.TryCreate(viewModel.ManualCallbackText.Trim(), UriKind.Absolute, out var callback))
        {
            viewModel.ReportBrowserFailure("回调地址无效，请复制浏览器地址栏里的完整地址后重试。");
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await viewModel.CompleteSignInAsync(callback, cancellation.Token);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (ViewModel is { IsSigningIn: true } viewModel)
        {
            _signInCts?.Cancel();
            viewModel.CancelSignIn();
            e.Handled = true;
            return;
        }

        Close();
        e.Handled = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _signInCts?.Cancel();
        _signInCts?.Dispose();
        _signInCts = null;
        if (ViewModel is { } viewModel)
        {
            viewModel.Disconnect();
        }

        HeapTrim.Request();
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
