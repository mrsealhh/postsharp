using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PostSharp.Services;

namespace PostSharp.Models;

/// <summary>授权类型与附加位置的固定取值，与界面下拉的 Value 一一对应。</summary>
public static class AuthKinds
{
    public const string Inherit = "Inherit";
    public const string None = "None";
    public const string Bearer = "Bearer";
    public const string Basic = "Basic";
    public const string ApiKey = "ApiKey";
    public const string OAuth2 = "OAuth2";
    public const string InHeader = "Header";
    public const string InQuery = "Query";

    public static bool IsKnownType(string value) =>
        value is Inherit or None or Bearer or Basic or ApiKey or OAuth2;

    public static bool IsKnownTarget(string value) => value is InHeader or InQuery;
}

/// <summary>
/// 一个请求或集合上的授权配置。Inherit 表示继续向上级集合取；根级集合仍是 Inherit 时，
/// 这次请求就不附加任何认证，而不是回落到某个全局令牌。
/// </summary>
public sealed partial class RequestAuth : ObservableObject
{
    public const string DefaultApiKeyName = "X-Api-Key";

    private string _token = string.Empty;
    private string _tokenCipher = string.Empty;
    private string _password = string.Empty;
    private string _passwordCipher = string.Empty;
    private string _apiKeyValue = string.Empty;
    private string _apiKeyValueCipher = string.Empty;

    [ObservableProperty]
    private string _authType = AuthKinds.Inherit;

    /// <summary>附加位置：请求头或查询参数；Basic 只附加到请求头。</summary>
    [ObservableProperty]
    private string _target = AuthKinds.InHeader;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _apiKeyName = DefaultApiKeyName;

    [ObservableProperty]
    private string _apiKeyLocation = AuthKinds.InHeader;

    private AuthorizationProfile? _oauth2;

    /// <summary>OAuth 2.0 的服务器配置与令牌；只有类型为 OAuth2 时才使用。</summary>
    public AuthorizationProfile? OAuth2
    {
        get => _oauth2;
        set
        {
            if (ReferenceEquals(_oauth2, value))
            {
                return;
            }

            if (_oauth2 is not null)
            {
                _oauth2.PropertyChanged -= OnOAuthProfileChanged;
            }

            _oauth2 = value;
            if (_oauth2 is not null)
            {
                _oauth2.PropertyChanged += OnOAuthProfileChanged;
            }

            OnPropertyChanged(nameof(OAuth2));
            NotifyOAuthState();
        }
    }

    /// <summary>Bearer 令牌；落盘走 <see cref="TokenCipher" />，界面只读明文属性。</summary>
    public string Token
    {
        get => _token;
        set
        {
            if (SetProperty(ref _token, value))
            {
                _tokenCipher = AuthSecretProtector.Protect(value);
            }
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                _passwordCipher = AuthSecretProtector.Protect(value);
            }
        }
    }

    public string ApiKeyValue
    {
        get => _apiKeyValue;
        set
        {
            if (SetProperty(ref _apiKeyValue, value))
            {
                _apiKeyValueCipher = AuthSecretProtector.Protect(value);
            }
        }
    }

    /// <summary>落盘字段：DPAPI 加密后的值，只在加载时解密回明文属性。</summary>
    public string TokenCipher
    {
        get => _tokenCipher;
        set
        {
            _tokenCipher = value;
            ApplyCipher(ref _token, value, nameof(Token));
        }
    }

    public string PasswordCipher
    {
        get => _passwordCipher;
        set
        {
            _passwordCipher = value;
            ApplyCipher(ref _password, value, nameof(Password));
        }
    }

    public string ApiKeyValueCipher
    {
        get => _apiKeyValueCipher;
        set
        {
            _apiKeyValueCipher = value;
            ApplyCipher(ref _apiKeyValue, value, nameof(ApiKeyValue));
        }
    }

    public bool IsInherit => AuthType == AuthKinds.Inherit;

    public bool IsNone => AuthType == AuthKinds.None;

    public bool IsBearer => AuthType == AuthKinds.Bearer;

    public bool IsBasic => AuthType == AuthKinds.Basic;

    public bool IsApiKey => AuthType == AuthKinds.ApiKey;

    public bool IsOAuth2 => AuthType == AuthKinds.OAuth2;

    public string OAuthStatusText =>
        OAuth2 is not { } profile ? "未配置 OAuth 2.0"
        : profile.IsUsable
            ? profile.IsClientCredentials ? "已获取客户端凭据令牌"
                : string.IsNullOrWhiteSpace(profile.SignedInAs) ? "已登录"
                : $"已登录：{profile.SignedInAs}"
        : profile.HasAccessToken ? "令牌已过期"
        : profile.IsClientCredentials ? "未获取令牌"
        : "未登录";

    public string OAuthExpiresText =>
        OAuth2 is { ExpiresAt: { } expiresAt }
            ? expiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "未提供";

    /// <summary>没有任何配置时不必写入工作区，省得每个请求都带一段空授权。</summary>
    public bool IsDefault =>
        AuthType == AuthKinds.Inherit
        && Target == AuthKinds.InHeader
        && string.IsNullOrEmpty(_token)
        && string.IsNullOrEmpty(Username)
        && string.IsNullOrEmpty(_password)
        && string.IsNullOrEmpty(_apiKeyValue)
        && ApiKeyName == DefaultApiKeyName
        && ApiKeyLocation == AuthKinds.InHeader
        && OAuth2 is null;

    public AuthorizationProfile EnsureOAuth2() => OAuth2 ??= new AuthorizationProfile();

    public RequestAuth Clone()
    {
        var clone = new RequestAuth();
        clone.CopyFrom(this);
        return clone;
    }

    /// <summary>
    /// 把另一份配置复制进来；OAuth 2.0 配置复制为独立对象，
    /// 避免两个请求或集合在刷新令牌后互相影响。
    /// </summary>
    public void CopyFrom(RequestAuth source)
    {
        AuthType = source.AuthType;
        Target = source.Target;
        Username = source.Username;
        ApiKeyName = source.ApiKeyName;
        ApiKeyLocation = source.ApiKeyLocation;
        Token = source.Token;
        Password = source.Password;
        ApiKeyValue = source.ApiKeyValue;
        OAuth2 = source.OAuth2?.Clone();
    }

    partial void OnAuthTypeChanged(string value)
    {
        if (value == AuthKinds.OAuth2)
        {
            EnsureOAuth2();
        }

        OnPropertyChanged(nameof(IsInherit));
        OnPropertyChanged(nameof(IsNone));
        OnPropertyChanged(nameof(IsBearer));
        OnPropertyChanged(nameof(IsBasic));
        OnPropertyChanged(nameof(IsApiKey));
        OnPropertyChanged(nameof(IsOAuth2));
    }

    private void OnOAuthProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is not (
                nameof(AuthorizationProfile.AccessToken)
                or nameof(AuthorizationProfile.RefreshToken)
                or nameof(AuthorizationProfile.ExpiresAt)
                or nameof(AuthorizationProfile.SignedInAs)
                or nameof(AuthorizationProfile.HasAccessToken)
                or nameof(AuthorizationProfile.CanRefresh)
                or nameof(AuthorizationProfile.IsUsable)
            )
        )
        {
            return;
        }

        // 令牌刷新可能发生在后台（发送/MCP），这里统一切回 UI 线程再通知绑定。
        if (Dispatcher.UIThread.CheckAccess())
        {
            NotifyOAuthState();
            return;
        }

        Dispatcher.UIThread.Post(NotifyOAuthState);
    }

    private void NotifyOAuthState()
    {
        OnPropertyChanged(nameof(OAuthStatusText));
        OnPropertyChanged(nameof(OAuthExpiresText));
    }

    private void ApplyCipher(ref string field, string cipher, string propertyName)
    {
        var plain = AuthSecretProtector.Unprotect(cipher);
        if (field == plain)
        {
            return;
        }

        field = plain;
        OnPropertyChanged(propertyName);
    }
}
