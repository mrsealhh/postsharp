using System.Text;
using System.Text.Json;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>把 OIDC 流程的结果写回授权配置，并为请求提供 Authorization 头。</summary>
public sealed class AuthorizationService
{
    private readonly OidcAuthService _oidc = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public async Task<OidcEndpoints> DiscoverAsync(
        AuthorizationProfile profile,
        CancellationToken cancellationToken = default
    )
    {
        var endpoints = await _oidc
            .DiscoverAsync(profile.DiscoveryUrl, cancellationToken)
            .ConfigureAwait(false);

        if (
            endpoints.CodeChallengeMethods.Count > 0
            && !endpoints.CodeChallengeMethods.Contains("S256", StringComparer.Ordinal)
        )
        {
            throw new OidcException("授权服务器不支持 S256 的 PKCE，无法安全完成登录。");
        }

        if (endpoints.ScopesSupported.Count > 0)
        {
            var unsupported = profile
                .Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(scope => !endpoints.ScopesSupported.Contains(scope, StringComparer.Ordinal))
                .ToList();

            if (unsupported.Count > 0)
            {
                throw new OidcException(
                    $"授权服务器不支持这些授权范围：{string.Join('、', unsupported)}。请在「授权范围」里删除后重试。"
                );
            }
        }

        return endpoints;
    }

    public static Uri BuildAuthorizationUri(
        AuthorizationProfile profile,
        OidcEndpoints endpoints,
        string state,
        string codeChallenge
    )
    {
        return OidcAuthService.BuildAuthorizationUri(
            endpoints.AuthorizationEndpoint,
            profile.ClientId.Trim(),
            profile.RedirectUri.Trim(),
            profile.Scopes.Trim(),
            state,
            codeChallenge,
            nonce: null
        );
    }

    public async Task SignInAsync(
        AuthorizationProfile profile,
        OidcEndpoints endpoints,
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default
    )
    {
        var tokens = await _oidc
            .ExchangeCodeAsync(
                endpoints.TokenEndpoint,
                profile.ClientId.Trim(),
                profile.ClientSecret,
                profile.RedirectUri.Trim(),
                code,
                codeVerifier,
                cancellationToken
            )
            .ConfigureAwait(false);

        Apply(profile, tokens);
    }

    /// <summary>
    /// 发送前调用：令牌过期且有 refresh_token 时先刷新。返回 false 表示这次请求没有可用的访问令牌。
    /// </summary>
    public async Task<bool> EnsureFreshAsync(
        AuthorizationProfile profile,
        CancellationToken cancellationToken = default
    )
    {
        if (!profile.HasAccessToken)
        {
            return false;
        }

        if (!profile.IsTokenExpired)
        {
            return true;
        }

        if (!profile.CanRefresh)
        {
            return false;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!profile.IsTokenExpired)
            {
                return true;
            }

            var endpoints = await _oidc
                .DiscoverAsync(profile.DiscoveryUrl, cancellationToken)
                .ConfigureAwait(false);
            var tokens = await _oidc
                .RefreshAsync(
                    endpoints.TokenEndpoint,
                    profile.ClientId.Trim(),
                    profile.ClientSecret,
                    profile.RefreshToken,
                    profile.IdToken,
                    cancellationToken
                )
                .ConfigureAwait(false);

            Apply(profile, tokens);
            Log.Information("授权令牌已刷新 {ExpiresAt}", profile.ExpiresAt);
            return true;
        }
        catch (OidcException exception)
        {
            if (exception.ErrorCode is "invalid_grant" or "invalid_client")
            {
                Log.Warning(exception, "刷新令牌已失效，需要重新登录");
                profile.ClearTokens();
            }
            else
            {
                // Network, timeout and server errors are not proof that the refresh token is dead.
                Log.Warning(exception, "刷新授权令牌暂时失败，保留 refresh_token 等待重试");
            }

            return false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public static AuthInjection? BuildInjection(AuthorizationProfile profile, string target)
    {
        if (string.IsNullOrWhiteSpace(profile.AccessToken))
        {
            return null;
        }

        var scheme = string.IsNullOrWhiteSpace(profile.TokenType)
            ? "Bearer"
            : profile.TokenType.Trim();

        return target == AuthKinds.InQuery
            ? new AuthInjection("access_token", profile.AccessToken, true)
            : new AuthInjection("Authorization", $"{scheme} {profile.AccessToken}", false);
    }

    /// <summary>把一处授权配置解析成可附加的材料；缺少凭据时返回 null，表示这次不附加。</summary>
    public static AuthInjection? BuildInjection(RequestAuth auth)
    {
        return auth.AuthType switch
        {
            AuthKinds.Bearer => BuildBearerInjection(auth.Token, auth.Target),
            AuthKinds.Basic => BuildBasicInjection(auth.Username, auth.Password),
            AuthKinds.ApiKey => BuildApiKeyInjection(auth),
            AuthKinds.OAuth2 when auth.OAuth2 is { } profile => BuildInjection(
                profile,
                auth.Target
            ),
            _ => null,
        };
    }

    private static AuthInjection? BuildBearerInjection(string token, string target)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var value = token.Trim();
        return target == AuthKinds.InQuery
            ? new AuthInjection("access_token", value, true)
            : new AuthInjection("Authorization", $"Bearer {value}", false);
    }

    private static AuthInjection? BuildBasicInjection(string username, string password)
    {
        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
        {
            return null;
        }

        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return new AuthInjection("Authorization", $"Basic {raw}", false);
    }

    private static AuthInjection? BuildApiKeyInjection(RequestAuth auth)
    {
        if (string.IsNullOrWhiteSpace(auth.ApiKeyName) || string.IsNullOrEmpty(auth.ApiKeyValue))
        {
            return null;
        }

        return new AuthInjection(
            auth.ApiKeyName.Trim(),
            auth.ApiKeyValue,
            auth.ApiKeyLocation == AuthKinds.InQuery
        );
    }

    private static void Apply(AuthorizationProfile profile, OidcTokenSet tokens)
    {
        profile.AccessToken = tokens.AccessToken;
        profile.RefreshToken =
            tokens.RefreshToken.Length > 0 ? tokens.RefreshToken : profile.RefreshToken;
        profile.IdToken = tokens.IdToken.Length > 0 ? tokens.IdToken : profile.IdToken;
        profile.TokenType = string.IsNullOrWhiteSpace(tokens.TokenType)
            ? "Bearer"
            : tokens.TokenType;
        profile.ExpiresAt = tokens.ExpiresAt == DateTimeOffset.MaxValue ? null : tokens.ExpiresAt;
        profile.SignedInAs = ReadDisplayName(profile.IdToken);
    }

    /// <summary>从 id_token 的负载里读取展示用的用户名；不校验签名，只用于界面显示。</summary>
    private static string ReadDisplayName(string idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return string.Empty;
        }

        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var property in new[] { "name", "preferred_username", "email", "sub" })
            {
                if (
                    document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(property, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } text
                )
                {
                    return text;
                }
            }
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "id_token 负载不是可读的 JWT，忽略展示用户名");
        }

        return string.Empty;
    }
}
