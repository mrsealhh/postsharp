using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PostSharp.Models;
using PostSharp.Services;
using TKCode.Arc;

namespace PostSharp.ViewModels;

/// <summary>授权窗口的状态机：配置校验、发起授权、处理回调、刷新与注销。</summary>
public sealed partial class AuthorizationViewModel : ObservableObject
{
    private readonly AuthorizationService _authorization = new();
    private readonly Func<string> _targetDetailFactory;

    private OidcEndpoints? _endpoints;
    private string _codeVerifier = string.Empty;
    private string _expectedState = string.Empty;

    public AuthorizationViewModel(
        RequestAuth auth,
        string targetLabel,
        Func<string> targetDetailFactory
    )
    {
        Auth = auth;
        TargetLabel = targetLabel;
        _targetDetailFactory = targetDetailFactory;
        Profile = auth.OAuth2 ?? new AuthorizationProfile();
        if (auth.IsOAuth2 && !ReferenceEquals(auth.OAuth2, Profile))
        {
            auth.OAuth2 = Profile;
        }

        Profile.PropertyChanged += OnProfileChanged;
        Auth.PropertyChanged += OnAuthChanged;
        RefreshStatus();
    }

    /// <summary>正在编辑的授权配置，窗口里的类型、附加位置和凭据直接写回它。</summary>
    public RequestAuth Auth { get; }

    public AuthorizationProfile Profile { get; }

    public string TargetLabel { get; }

    public string TargetDetail => _targetDetailFactory();

    public string WindowTitle => $"授权配置 · {TargetLabel} · PostSharp";

    public string AttachDescription =>
        Auth.Target == AuthKinds.InQuery
            ? "登录后把令牌作为查询参数 access_token 附加到使用这份配置的请求。"
            : "登录后把令牌附加到使用这份配置的请求的请求头 Authorization。";

    [ObservableProperty]
    private string _statusTitle = "尚未登录";

    [ObservableProperty]
    private string _statusDetail =
        "点击「登录」，在右侧浏览器完成授权；令牌会附加到使用这份配置的请求。";

    [ObservableProperty]
    private BadgeTone _statusTone = BadgeTone.Neutral;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isSigningIn;

    [ObservableProperty]
    private bool _isSignedIn;

    [ObservableProperty]
    private string _expiresAtText = "—";

    [ObservableProperty]
    private string _browserPlaceholderTitle = "浏览器区域";

    [ObservableProperty]
    private string _browserPlaceholderDetail =
        "点击「登录」后，这里会打开授权服务器的登录页；登录成功后自动回到本窗口。";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorText = string.Empty;

    public bool CanSignIn => !IsBusy && !IsSigningIn;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSignIn));

    partial void OnIsSigningInChanged(bool value) => OnPropertyChanged(nameof(CanSignIn));

    /// <summary>读取授权服务器配置并生成授权地址；配置或网络有问题时返回 null，并把原因写进错误区。</summary>
    public async Task<Uri?> BeginSignInAsync(CancellationToken cancellationToken)
    {
        var problem = Profile.FindConfigurationProblem();
        if (problem is not null)
        {
            SetError(problem);
            return null;
        }

        HasError = false;
        ErrorText = string.Empty;
        IsBusy = true;
        StatusTitle = "正在准备授权";
        StatusDetail = "正在读取授权服务器的 OpenID 配置。";
        StatusTone = BadgeTone.Neutral;

        OidcEndpoints endpoints;
        try
        {
            endpoints = await _authorization.DiscoverAsync(Profile, cancellationToken);
        }
        catch (OidcException exception)
        {
            IsBusy = false;
            SetError(exception.Message);
            return null;
        }
        catch (OperationCanceledException)
        {
            IsBusy = false;
            StatusTitle = "已取消";
            StatusDetail = "本次登录已取消，可以重新点击「登录」。";
            StatusTone = BadgeTone.Neutral;
            return null;
        }
        catch (Exception exception)
        {
            IsBusy = false;
            Log.Error(exception, "准备授权失败");
            SetError("准备授权时出现意外错误，请检查配置后重试。");
            return null;
        }

        IsBusy = false;

        var pkce = OidcAuthService.CreatePkce();
        _codeVerifier = pkce.Verifier;
        _expectedState = OidcAuthService.CreateState();
        _endpoints = endpoints;

        IsSigningIn = true;
        StatusTitle = "等待授权";
        StatusDetail = "请在右侧浏览器完成登录；成功后会自动换取令牌。";
        StatusTone = BadgeTone.Warning;

        return AuthorizationService.BuildAuthorizationUri(
            Profile,
            endpoints,
            _expectedState,
            pkce.Challenge
        );
    }

    /// <summary>浏览器重定向到回调地址时调用：校验 state 后换取令牌。</summary>
    public async Task CompleteSignInAsync(Uri callback, CancellationToken cancellationToken)
    {
        if (_endpoints is not { } endpoints || _codeVerifier.Length == 0)
        {
            return;
        }

        if (!OidcAuthService.TryMatchCallback(callback, Profile.RedirectUri, out var result))
        {
            return;
        }

        if (!string.Equals(result.State, _expectedState, StringComparison.Ordinal))
        {
            ResetPending();
            IsSigningIn = false;
            SetError("授权回调的 state 与本次登录不匹配，已中止换取令牌。请重新登录。");
            return;
        }

        if (result.Error is not null)
        {
            ResetPending();
            IsSigningIn = false;
            SetError(
                result.ErrorDescription is { Length: > 0 } description
                    ? $"授权服务器拒绝了本次登录：{description}"
                    : $"授权服务器拒绝了本次登录：{result.Error}"
            );
            return;
        }

        if (result.Code is not { Length: > 0 } code)
        {
            ResetPending();
            IsSigningIn = false;
            SetError("授权回调里没有授权码，请重新登录。");
            return;
        }

        IsBusy = true;
        StatusTitle = "正在换取令牌";
        StatusDetail = "已拿到授权码，正在向令牌端点交换访问令牌。";
        StatusTone = BadgeTone.Warning;

        var succeeded = false;
        try
        {
            await _authorization.SignInAsync(
                Profile,
                endpoints,
                code,
                _codeVerifier,
                cancellationToken
            );
            succeeded = true;
        }
        catch (OidcException exception)
        {
            SetError(exception.Message);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "换取授权令牌失败");
            SetError("换取令牌时出现意外错误，请重试。");
        }
        finally
        {
            IsBusy = false;
            IsSigningIn = false;
            ResetPending();
            if (succeeded)
            {
                RefreshStatus();
            }
        }
    }

    /// <summary>用 refresh_token 立即换一枚新令牌。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!Profile.CanRefresh)
        {
            SetError("没有 refresh_token，无法刷新，请重新登录。");
            return;
        }

        IsBusy = true;
        HasError = false;
        ErrorText = string.Empty;
        StatusTitle = "正在刷新令牌";
        StatusDetail = "使用 refresh_token 换取新的访问令牌。";
        StatusTone = BadgeTone.Warning;

        var refreshed = false;
        try
        {
            if (!await _authorization.EnsureFreshAsync(Profile, cancellationToken))
            {
                SetError("刷新失败，refresh_token 可能已失效，请重新登录。");
                return;
            }

            refreshed = true;
        }
        finally
        {
            IsBusy = false;
            if (refreshed)
            {
                RefreshStatus();
            }
        }
    }

    public void CancelSignIn()
    {
        ResetPending();
        IsSigningIn = false;
        StatusTitle = "已取消登录";
        StatusDetail = "本次授权已取消，可以重新点击「登录」。";
        StatusTone = BadgeTone.Neutral;
    }

    /// <summary>内嵌浏览器没能打开授权页面（证书、连接或服务未启动）时调用。</summary>
    public void ReportBrowserFailure()
    {
        ResetPending();
        IsSigningIn = false;
        SetError(
            "内嵌浏览器无法加载授权页面，请确认授权服务已启动、证书可信，然后重新点击「登录」。"
        );
    }

    public void SignOut()
    {
        Profile.ClearTokens();
        HasError = false;
        ErrorText = string.Empty;
        RefreshStatus();
    }

    public void Disconnect()
    {
        Profile.PropertyChanged -= OnProfileChanged;
        Auth.PropertyChanged -= OnAuthChanged;
    }

    private void OnAuthChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RequestAuth.AuthType))
        {
            // 本次窗口填写的 OAuth 配置在切到 OAuth 2.0 后接管为该请求的真实配置，
            // 避免窗口里看到的一份、发请求时用的另一份。
            if (Auth.IsOAuth2 && !ReferenceEquals(Auth.OAuth2, Profile))
            {
                Auth.OAuth2 = Profile;
            }

            OnPropertyChanged(nameof(AttachDescription));
        }
        else if (e.PropertyName == nameof(RequestAuth.Target))
        {
            OnPropertyChanged(nameof(AttachDescription));
        }

        // 作用对象说明里带着当前生效的认证摘要，类型或凭据变化后要跟着刷新。
        OnPropertyChanged(nameof(TargetDetail));
    }

    private void OnProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is not (
                nameof(AuthorizationProfile.AccessToken)
                or nameof(AuthorizationProfile.ExpiresAt)
                or nameof(AuthorizationProfile.RefreshToken)
            )
        )
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            OnPropertyChanged(nameof(TargetDetail));
            RefreshStatus();
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(TargetDetail));
            RefreshStatus();
        });
    }

    private void RefreshStatus()
    {
        if (IsBusy || IsSigningIn)
        {
            return;
        }

        ExpiresAtText = Profile.ExpiresAt is { } expiresAt
            ? expiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : "未提供";

        if (Profile.IsUsable)
        {
            IsSignedIn = true;
            StatusTitle = "已登录";
            StatusDetail = string.IsNullOrWhiteSpace(Profile.SignedInAs)
                ? $"访问令牌在 {ExpiresAtText} 前有效；{AttachDescription}"
                : $"{Profile.SignedInAs} · 访问令牌在 {ExpiresAtText} 前有效。";
            StatusTone = BadgeTone.Success;
            BrowserPlaceholderTitle = "已登录";
            BrowserPlaceholderDetail =
                "授权已完成，可以关闭本窗口回到主界面；需要切换账号时重新点击「登录」。";
            return;
        }

        IsSignedIn = false;
        if (Profile.HasAccessToken)
        {
            StatusTitle = "令牌已过期";
            StatusDetail = "访问令牌已过期，点击「刷新令牌」或重新登录。";
            StatusTone = BadgeTone.Warning;
            BrowserPlaceholderTitle = "令牌已过期";
            BrowserPlaceholderDetail = "点击「刷新令牌」用 refresh_token 换新令牌，或重新登录。";
            return;
        }

        StatusTitle = "尚未登录";
        StatusDetail = $"点击「登录」，在右侧浏览器完成授权；{AttachDescription}";
        StatusTone = BadgeTone.Neutral;
        BrowserPlaceholderTitle = "浏览器区域";
        BrowserPlaceholderDetail =
            "点击「登录」后，这里会打开授权服务器的登录页；登录成功后自动回到本窗口。";
    }

    private void ResetPending()
    {
        _endpoints = null;
        _codeVerifier = string.Empty;
        _expectedState = string.Empty;
    }

    private void SetError(string message)
    {
        HasError = true;
        ErrorText = message;
        StatusTitle = "登录失败";
        StatusDetail = "上一次授权没有完成，按下方提示处理后可以重试。";
        StatusTone = BadgeTone.Danger;
        BrowserPlaceholderTitle = "登录未完成";
        BrowserPlaceholderDetail = "按左侧错误提示处理后，重新点击「登录」。";
        Log.Warning("授权流程失败：{Message}", message);
    }
}
