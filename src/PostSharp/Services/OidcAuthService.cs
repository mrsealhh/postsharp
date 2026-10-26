using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PostSharp.Services;

/// <summary>交给界面直接展示的授权错误，消息本身就是最终文案。</summary>
public sealed class OidcException : Exception
{
    public OidcException(string message)
        : base(message) { }

    public OidcException(string message, Exception innerException)
        : base(message, innerException) { }
}

public sealed record OidcEndpoints(
    Uri Issuer,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    IReadOnlyList<string> ScopesSupported,
    IReadOnlyList<string> CodeChallengeMethods
);

public sealed record OidcTokenSet(
    string AccessToken,
    string RefreshToken,
    string IdToken,
    string TokenType,
    DateTimeOffset ExpiresAt
);

public sealed record OidcCallback(
    string? Code,
    string? Error,
    string? ErrorDescription,
    string State
);

public sealed record PkcePair(string Verifier, string Challenge);

/// <summary>OIDC 授权码 + PKCE 流程中除浏览器以外的部分：发现、拼授权地址、换令牌、刷新令牌。</summary>
public sealed class OidcAuthService
{
    private const int DiscoveryTimeoutSeconds = 20;
    private const int TokenTimeoutSeconds = 30;
    private const int ExpirySkewSeconds = 30;

    private static readonly HttpClient Client = CreateClient();

    public async Task<OidcEndpoints> DiscoverAsync(
        string discoveryUrl,
        CancellationToken cancellationToken = default
    )
    {
        if (
            !Uri.TryCreate(discoveryUrl.Trim(), UriKind.Absolute, out var uri)
            || !IsAcceptableAddress(uri)
        )
        {
            throw new OidcException("发现地址无效，请填写 https 开头的完整地址。");
        }

        var json = await GetStringAsync(uri, cancellationToken).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new OidcException("发现文档不是有效的 JSON 对象，请确认地址指向授权服务器。");
            }

            var issuer = ReadUri(root, "issuer");
            var authorizationEndpoint = ReadUri(root, "authorization_endpoint");
            var tokenEndpoint = ReadUri(root, "token_endpoint");

            if (issuer is null || authorizationEndpoint is null || tokenEndpoint is null)
            {
                throw new OidcException(
                    "发现文档缺少 issuer、authorization_endpoint 或 token_endpoint，请确认该地址是 OpenID 配置文档。"
                );
            }

            if (!IsAcceptableAddress(authorizationEndpoint) || !IsAcceptableAddress(tokenEndpoint))
            {
                throw new OidcException("授权服务器声明的端点不是 https 地址，已拒绝继续。");
            }

            return new OidcEndpoints(
                issuer,
                authorizationEndpoint,
                tokenEndpoint,
                ReadStringArray(root, "scopes_supported"),
                ReadStringArray(root, "code_challenge_methods_supported")
            );
        }
        catch (JsonException exception)
        {
            Log.Warning(exception, "发现文档不是合法 JSON {DiscoveryUrl}", discoveryUrl);
            throw new OidcException("发现文档不是合法的 JSON，请确认地址拼写。", exception);
        }
    }

    public static PkcePair CreatePkce()
    {
        var verifier = CreateRandomToken(32);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new PkcePair(verifier, challenge);
    }

    public static string CreateState() => CreateRandomToken(24);

    public static Uri BuildAuthorizationUri(
        Uri authorizationEndpoint,
        string clientId,
        string redirectUri,
        string scopes,
        string state,
        string codeChallenge,
        string? nonce
    )
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("redirect_uri", redirectUri),
            new("response_type", "code"),
            new("scope", scopes),
            new("state", state),
            new("code_challenge", codeChallenge),
            new("code_challenge_method", "S256"),
        };

        if (!string.IsNullOrWhiteSpace(nonce))
        {
            parameters.Add(new KeyValuePair<string, string>("nonce", nonce));
        }

        var query = string.Join(
            "&",
            parameters.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"
            )
        );

        var builder = new UriBuilder(authorizationEndpoint)
        {
            Query = string.IsNullOrEmpty(authorizationEndpoint.Query)
                ? query
                : authorizationEndpoint.Query.TrimStart('?') + "&" + query,
        };

        return builder.Uri;
    }

    public async Task<OidcTokenSet> ExchangeCodeAsync(
        Uri tokenEndpoint,
        string clientId,
        string clientSecret,
        string redirectUri,
        string code,
        string codeVerifier,
        CancellationToken cancellationToken = default
    )
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = codeVerifier,
        };

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            form["client_secret"] = clientSecret;
        }

        return await PostTokenAsync(
                tokenEndpoint,
                form,
                string.Empty,
                string.Empty,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task<OidcTokenSet> RefreshAsync(
        Uri tokenEndpoint,
        string clientId,
        string clientSecret,
        string refreshToken,
        string currentIdToken,
        CancellationToken cancellationToken = default
    )
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        };

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            form["client_secret"] = clientSecret;
        }

        return await PostTokenAsync(
                tokenEndpoint,
                form,
                refreshToken,
                currentIdToken,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>判断地址是否是本次授权的回调，并取出 code / error / state。</summary>
    public static bool TryMatchCallback(Uri callback, string redirectUri, out OidcCallback result)
    {
        result = new OidcCallback(null, null, null, string.Empty);

        if (!Uri.TryCreate(redirectUri.Trim(), UriKind.Absolute, out var expected))
        {
            return false;
        }

        if (
            !string.Equals(callback.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(callback.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            || callback.Port != expected.Port
            || !string.Equals(
                callback.AbsolutePath.TrimEnd('/'),
                expected.AbsolutePath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }

        var query = ParseQuery(callback.Query);
        query.TryGetValue("code", out var code);
        query.TryGetValue("error", out var error);
        query.TryGetValue("error_description", out var errorDescription);
        query.TryGetValue("state", out var state);

        result = new OidcCallback(code, error, errorDescription, state ?? string.Empty);
        return code is not null || error is not null;
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var text = query.TrimStart('?');
        if (text.Length == 0)
        {
            return values;
        }

        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            var decodedKey = Uri.UnescapeDataString(key.Replace('+', ' '));
            if (values.ContainsKey(decodedKey))
            {
                continue;
            }

            values[decodedKey] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return values;
    }

    private static async Task<OidcTokenSet> PostTokenAsync(
        Uri tokenEndpoint,
        Dictionary<string, string> form,
        string previousRefreshToken,
        string currentIdToken,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TokenTimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
            {
                Content = content,
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            response = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OidcException("换取令牌超时，请检查授权服务器是否可用后重试。");
        }
        catch (HttpRequestException exception)
        {
            Log.Warning(exception, "令牌端点不可达 {TokenEndpoint}", tokenEndpoint);
            throw new OidcException(
                "无法连接令牌端点，请确认服务已启动、地址和证书都正确。",
                exception
            );
        }

        using (response)
        {
            var body = await response
                .Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateTokenException(body, response.StatusCode);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var accessToken = ReadString(root, "access_token");
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    throw new OidcException("授权服务器没有返回访问令牌，请重新登录。");
                }

                var expiresIn = ReadInt(root, "expires_in");
                var refreshToken = ReadString(root, "refresh_token") ?? previousRefreshToken;
                var idToken = ReadString(root, "id_token") ?? currentIdToken;
                var tokenType = ReadString(root, "token_type") ?? "Bearer";

                return new OidcTokenSet(
                    accessToken,
                    refreshToken ?? string.Empty,
                    idToken ?? string.Empty,
                    tokenType,
                    expiresIn > 0
                        ? DateTimeOffset.Now.AddSeconds(expiresIn - ExpirySkewSeconds)
                        : DateTimeOffset.MaxValue
                );
            }
            catch (JsonException exception)
            {
                Log.Warning(exception, "令牌响应不是合法 JSON {TokenEndpoint}", tokenEndpoint);
                throw new OidcException("授权服务器返回的数据无法解析，请稍后重试。", exception);
            }
        }
    }

    private static OidcException CreateTokenException(string body, HttpStatusCode statusCode)
    {
        var error = string.Empty;
        var description = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                error = ReadString(document.RootElement, "error") ?? string.Empty;
                description = ReadString(document.RootElement, "error_description") ?? string.Empty;
            }
        }
        catch (JsonException exception)
        {
            Log.Debug(exception, "令牌错误响应不是 JSON {StatusCode}", (int)statusCode);
        }

        var message = error switch
        {
            "invalid_grant" => "授权码已失效或已被使用，请重新登录。",
            "invalid_client" => "授权服务器未通过客户端校验，请检查客户端 ID 与客户端密钥。",
            "invalid_request" => "授权服务器拒绝了令牌请求，请检查重定向地址与授权范围是否已注册。",
            "unauthorized_client" => "该客户端不允许使用授权码方式登录，请联系授权服务器管理员。",
            "unsupported_grant_type" => "授权服务器不支持该授权方式。",
            _ => description.Length > 0
                ? $"授权服务器拒绝了令牌请求：{description}"
                : "授权服务器拒绝了令牌请求，请重新登录。",
        };

        Log.Warning(
            "令牌请求失败 {StatusCode} {Error} {Description}",
            (int)statusCode,
            error,
            description
        );
        return new OidcException(message);
    }

    private static async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(DiscoveryTimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await Client
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new OidcException(
                    $"读取 OpenID 配置失败（HTTP {(int)response.StatusCode}），请确认地址可用。"
                );
            }

            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OidcException("读取 OpenID 配置超时，请检查服务状态后重试。");
        }
        catch (HttpRequestException exception)
        {
            Log.Warning(exception, "发现文档不可达 {DiscoveryUrl}", uri);
            throw new OidcException(
                "无法连接授权服务器，请确认服务已启动、地址和证书都正确。",
                exception
            );
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) =>
            errors == SslPolicyErrors.None
            || (message?.RequestUri is { } requestUri && requestUri.IsLoopback);

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static bool IsAcceptableAddress(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    private static string CreateRandomToken(int bytes) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Uri? ReadUri(JsonElement root, string property)
    {
        return
            ReadString(root, property) is { } value
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    private static string? ReadString(JsonElement root, string property)
    {
        return
            root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int ReadInt(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (
            value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var parsed)
        )
        {
            return parsed;
        }

        return 0;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property)
    {
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array
        )
        {
            return Array.Empty<string>();
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
            {
                items.Add(text);
            }
        }

        return items;
    }
}
