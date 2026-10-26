using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// 解析「这次请求到底该用哪份授权」：请求自己的配置优先，否则沿集合层级向上找，
/// 一路都是 Inherit 就表示这次请求不附加认证。
/// </summary>
public static class AuthResolver
{
    public static RequestAuth? Resolve(RequestItem request)
    {
        if (request.Auth.AuthType != AuthKinds.Inherit)
        {
            return request.Auth;
        }

        return request.Owner is { } owner ? Resolve(owner) : null;
    }

    /// <summary>集合自身及其父级中最近一份生效的授权；一路继承时返回 null。</summary>
    public static RequestAuth? Resolve(RequestCollection collection)
    {
        for (var current = collection; current is not null; current = current.Parent)
        {
            if (current.Auth.AuthType != AuthKinds.Inherit)
            {
                return current.Auth;
            }
        }

        return null;
    }

    /// <summary>请求通过继承拿到的配置来自哪个集合；请求自己配置了授权时返回 null。</summary>
    public static RequestCollection? FindInheritSource(RequestItem request)
    {
        if (request.Auth.AuthType != AuthKinds.Inherit)
        {
            return null;
        }

        for (var collection = request.Owner; collection is not null; collection = collection.Parent)
        {
            if (collection.Auth.AuthType != AuthKinds.Inherit)
            {
                return collection;
            }
        }

        return null;
    }

    /// <summary>集合通过继承拿到的配置来自哪个上级集合；集合自己配置了授权时返回 null。</summary>
    public static RequestCollection? FindInheritSource(RequestCollection collection)
    {
        if (collection.Auth.AuthType != AuthKinds.Inherit)
        {
            return null;
        }

        for (var parent = collection.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Auth.AuthType != AuthKinds.Inherit)
            {
                return parent;
            }
        }

        return null;
    }

    /// <summary>给界面用的一句话摘要；<paramref name="auth" /> 为 null 表示不附加认证。</summary>
    public static string Describe(RequestAuth? auth)
    {
        if (auth is null)
        {
            return "不附加认证";
        }

        return auth.AuthType switch
        {
            AuthKinds.None => "无认证",
            AuthKinds.Bearer => $"Bearer 令牌 · {DescribeTarget(auth.Target)}",
            AuthKinds.Basic => "Basic 认证 · 请求头",
            AuthKinds.ApiKey =>
                $"API Key「{auth.ApiKeyName}」· {DescribeTarget(auth.ApiKeyLocation)}",
            AuthKinds.OAuth2 => $"OAuth 2.0 · {auth.OAuthStatusText}",
            _ => "不附加认证",
        };
    }

    private static string DescribeTarget(string target) =>
        target == AuthKinds.InQuery ? "查询参数" : "请求头";
}
