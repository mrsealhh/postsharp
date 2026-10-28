using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using PostSharp.Services;

namespace PostSharp.Models;

/// <summary>OIDC 授权码流程的配置，以及最近一次登录换回的令牌。</summary>
public sealed partial class AuthorizationProfile : ObservableObject
{
    private string _accessToken = string.Empty;
    private string _accessTokenCipher = string.Empty;
    private string _refreshToken = string.Empty;
    private string _refreshTokenCipher = string.Empty;
    private string _idToken = string.Empty;
    private string _idTokenCipher = string.Empty;
    private string _clientSecret = string.Empty;
    private string _clientSecretCipher = string.Empty;

    [ObservableProperty]
    private string _discoveryUrl = string.Empty;

    [ObservableProperty]
    private string _clientId = string.Empty;

    [ObservableProperty]
    private string _redirectUri = "http://127.0.0.1:53682/callback";

    [ObservableProperty]
    private string _scopes = "openid profile offline_access";

    [JsonIgnore]
    public string ClientSecret
    {
        get => _clientSecret;
        set
        {
            if (SetProperty(ref _clientSecret, value))
            {
                _clientSecretCipher = AuthSecretProtector.Protect(value);
            }
        }
    }

    /// <summary>落盘字段：DPAPI 加密后的客户端密钥，只在加载时解密。</summary>
    public string ClientSecretCipher
    {
        get => _clientSecretCipher;
        set
        {
            _clientSecretCipher = value;
            SetTokenFromCipher(value, ref _clientSecret, nameof(ClientSecret));
        }
    }

    /// <summary>兼容旧工作区中仍以明文字段保存的客户端密钥；新版本只写加密字段。</summary>
    [JsonPropertyName("ClientSecret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientSecretLegacy
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                ClientSecret = value;
            }
        }
    }

    [ObservableProperty]
    private string _tokenType = "Bearer";

    [ObservableProperty]
    private string _signedInAs = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _expiresAt;

    [JsonIgnore]
    public string AccessToken
    {
        get => _accessToken;
        set
        {
            if (SetProperty(ref _accessToken, value))
            {
                _accessTokenCipher = AuthSecretProtector.Protect(value);
                OnPropertyChanged(nameof(HasAccessToken));
            }
        }
    }

    [JsonIgnore]
    public string RefreshToken
    {
        get => _refreshToken;
        set
        {
            if (SetProperty(ref _refreshToken, value))
            {
                _refreshTokenCipher = AuthSecretProtector.Protect(value);
                OnPropertyChanged(nameof(CanRefresh));
            }
        }
    }

    [JsonIgnore]
    public string IdToken
    {
        get => _idToken;
        set
        {
            if (SetProperty(ref _idToken, value))
            {
                _idTokenCipher = AuthSecretProtector.Protect(value);
            }
        }
    }

    /// <summary>落盘字段：DPAPI 加密后的访问令牌，只在加载时解密回 <see cref="AccessToken" />。</summary>
    public string AccessTokenCipher
    {
        get => _accessTokenCipher;
        set
        {
            _accessTokenCipher = value;
            SetTokenFromCipher(
                value,
                ref _accessToken,
                nameof(AccessToken),
                nameof(HasAccessToken)
            );
        }
    }

    public string RefreshTokenCipher
    {
        get => _refreshTokenCipher;
        set
        {
            _refreshTokenCipher = value;
            SetTokenFromCipher(value, ref _refreshToken, nameof(RefreshToken), nameof(CanRefresh));
        }
    }

    public string IdTokenCipher
    {
        get => _idTokenCipher;
        set
        {
            _idTokenCipher = value;
            SetTokenFromCipher(value, ref _idToken, nameof(IdToken));
        }
    }

    [JsonIgnore]
    public bool HasAccessToken => !string.IsNullOrWhiteSpace(_accessToken);

    [JsonIgnore]
    public bool CanRefresh => !string.IsNullOrWhiteSpace(_refreshToken);

    [JsonIgnore]
    public bool IsTokenExpired =>
        ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.Now.AddSeconds(10);

    [JsonIgnore]
    public bool IsUsable => HasAccessToken && !IsTokenExpired;

    public void ClearTokens()
    {
        AccessToken = string.Empty;
        RefreshToken = string.Empty;
        IdToken = string.Empty;
        TokenType = "Bearer";
        SignedInAs = string.Empty;
        ExpiresAt = null;
    }

    /// <summary>复制一份配置与令牌；复制后的登录状态独立，刷新不会影响原对象。</summary>
    public AuthorizationProfile Clone() =>
        new()
        {
            DiscoveryUrl = DiscoveryUrl,
            ClientId = ClientId,
            RedirectUri = RedirectUri,
            Scopes = Scopes,
            ClientSecret = ClientSecret,
            TokenType = TokenType,
            SignedInAs = SignedInAs,
            ExpiresAt = ExpiresAt,
            AccessToken = AccessToken,
            RefreshToken = RefreshToken,
            IdToken = IdToken,
        };

    /// <summary>返回配置缺失时的用户可读说明；配置完整时返回 null。</summary>
    public string? FindConfigurationProblem()
    {
        if (!IsAcceptableDiscoveryAddress(DiscoveryUrl))
        {
            return "发现地址无效，请填写 https 开头的完整地址。";
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            return "客户端 ID 不能为空。";
        }

        if (!IsAcceptableRedirectAddress(RedirectUri))
        {
            return "重定向地址无效，请填写 IdP 登记的 https、本机回环或自定义协议地址。";
        }

        if (string.IsNullOrWhiteSpace(Scopes))
        {
            return "授权范围不能为空，例如 openid profile offline_access。";
        }

        return null;
    }

    private static bool IsAcceptableDiscoveryAddress(string value)
    {
        var text = value.Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (
                uri.Scheme == Uri.UriSchemeHttps
                || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            );
    }

    private static bool IsAcceptableRedirectAddress(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Fragment.Length > 0)
        {
            return false;
        }

        if (uri.Scheme is "file" or "data" or "javascript" || string.IsNullOrWhiteSpace(uri.Scheme))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrWhiteSpace(uri.AbsolutePath);
    }

    private void SetTokenFromCipher(
        string cipher,
        ref string field,
        string propertyName,
        string? derivedPropertyName = null
    )
    {
        var plain = AuthSecretProtector.Unprotect(cipher);
        if (field == plain)
        {
            return;
        }

        field = plain;
        OnPropertyChanged(propertyName);
        if (derivedPropertyName is not null)
        {
            OnPropertyChanged(derivedPropertyName);
        }
    }
}
