using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using PostSharp.Controls;
using PostSharp.Services;
using PostSharp.ViewModels;

namespace PostSharp.Views;

public partial class AuthorizationWindow : Window
{
    private CancellationTokenSource? _signInCts;
    private NativeWebView? _browser;
    private bool _suppressCompletionFailure;

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

        if (viewModel.IsSystemBrowser)
        {
            await RunSystemBrowserSignInAsync(viewModel, authorizationUri, cancellationToken);
            return;
        }

        try
        {
            EnsureEmbeddedBrowser();
            _browser!.Navigate(authorizationUri);
        }
        catch (OidcException exception)
        {
            viewModel.ReportBrowserFailure(exception.Message);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "打开应用内授权浏览器失败");
            viewModel.ReportBrowserFailure();
        }
    }

    private async Task RunSystemBrowserSignInAsync(
        AuthorizationViewModel viewModel,
        Uri authorizationUri,
        CancellationToken cancellationToken
    )
    {
        try
        {
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

    private void EnsureEmbeddedBrowser()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new OidcException("当前系统不支持应用内浏览器，请把登录方式改为系统默认浏览器。");
        }

        if (_browser is not null)
        {
            return;
        }

        var browser = new NativeWebView();
        browser.EnvironmentRequested += OnEnvironmentRequested;
        browser.NavigationStarted += OnNavigationStarted;
        browser.NavigationCompleted += OnNavigationCompleted;
        browser.NewWindowRequested += OnNewWindowRequested;
        BrowserHost.Content = browser;
        _browser = browser;
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

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is not WindowsWebView2EnvironmentRequestedEventArgs windows)
        {
            return;
        }

        try
        {
            if (AppPaths.TryPrepareWebViewDataDirectory(out var dataDirectory))
            {
                windows.UserDataFolder = dataDirectory;
            }

            var arguments = new List<string> { "--deny-permission-prompts" };
            if (IsLoopbackAuthorizationServer())
            {
                // 本地开发授权服务通常使用自签名证书，内嵌浏览器不忽略的话会停在证书错误页。
                arguments.Add("--ignore-certificate-errors");
            }

            windows.AdditionalBrowserArguments = string.Join(' ', arguments);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "配置内嵌浏览器环境失败，改用平台默认设置");
        }
    }

    private async void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        _suppressCompletionFailure = false;
        if (ViewModel is not { } viewModel || !viewModel.IsSigningIn)
        {
            return;
        }

        var request = e.Request;
        if (request is null)
        {
            return;
        }

        if (!OidcAuthService.TryMatchCallback(request, viewModel.Profile.RedirectUri, out _))
        {
            return;
        }

        e.Cancel = true;
        _suppressCompletionFailure = true;
        _signInCts ??= new CancellationTokenSource();

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                viewModel.CompleteSignInAsync(request, _signInCts.Token)
            );
        }
        catch (OperationCanceledException)
        {
            viewModel.CancelSignIn();
        }
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (_suppressCompletionFailure)
        {
            // 回调导航是被主动取消的，换取令牌的结果由 OnNavigationStarted 负责。
            _suppressCompletionFailure = false;
            return;
        }

        if (e.IsSuccess || ViewModel is not { } viewModel || !viewModel.IsSigningIn)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => viewModel.ReportBrowserFailure());
    }

    private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs e) =>
        e.Handled = true;

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
    }

    private bool IsLoopbackAuthorizationServer()
    {
        var discoveryUrl = ViewModel?.Profile.DiscoveryUrl;
        return Uri.TryCreate(discoveryUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
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
