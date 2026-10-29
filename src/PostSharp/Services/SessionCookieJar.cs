using System.Net;
using System.Net.Http;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// The in-memory cookie container shared by every request sent by the desktop app. Cookies are never
/// persisted; the cookie manager only exposes what the current process has captured.
/// </summary>
public static class SessionCookieJar
{
    private static readonly CookieContainer Container = new();

    public static CookieContainer HandlerContainer => Container;

    public static IReadOnlyList<SessionCookie> GetCookies(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? GetCookies(uri)
            : Array.Empty<SessionCookie>();
    }

    public static IReadOnlyList<SessionCookie> GetCookies(Uri uri)
    {
        return Container
            .GetCookies(uri)
            .Cast<Cookie>()
            .OrderBy(cookie => cookie.Name, StringComparer.OrdinalIgnoreCase)
            .Select(cookie => new SessionCookie(
                cookie.Name,
                cookie.Value,
                cookie.Domain,
                string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                cookie.Expires == DateTime.MinValue
                    ? "会话"
                    : cookie.Expires.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                cookie.Secure,
                cookie.HttpOnly
            ))
            .ToList();
    }

    /// <summary>Expires every cookie visible for the URI; the shared client immediately stops sending them.</summary>
    public static void Clear(Uri uri)
    {
        foreach (Cookie cookie in Container.GetCookies(uri))
        {
            try
            {
                Container.Add(
                    new Cookie(
                        cookie.Name,
                        string.Empty,
                        string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                        cookie.Domain
                    )
                    {
                        Expires = DateTime.Now.AddDays(-1),
                        Secure = cookie.Secure,
                        HttpOnly = cookie.HttpOnly,
                    }
                );
            }
            catch (CookieException exception)
            {
                Log.Warning(
                    exception,
                    "清除 Cookie 失败 {Name} {Domain}",
                    cookie.Name,
                    cookie.Domain
                );
            }
        }
    }

    /// <summary>Wires the shared cookie container into the HTTP handler.</summary>
    public static HttpClientHandler CreateHandler(bool allowAutoRedirect = true) =>
        new()
        {
            UseCookies = true,
            CookieContainer = Container,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = allowAutoRedirect,
            MaxAutomaticRedirections = 10,
        };
}
