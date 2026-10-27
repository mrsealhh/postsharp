using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>An environment target derived from an OpenAPI document's servers or source URL.</summary>
public sealed record OpenApiEnvironmentTarget(string Name, string BaseUrl);

/// <summary>Derives importable environments from absolute OpenAPI server URLs or the source URL.</summary>
public static class OpenApiEnvironmentService
{
    public static IReadOnlyList<OpenApiEnvironmentTarget> Resolve(OpenApiImportDocument document)
    {
        var source = TryGetHttpUri(document.Source);
        var urls = new List<string>();
        foreach (var server in document.Servers)
        {
            if (TryResolveServerUrl(server.Url, source, out var url))
            {
                urls.Add(url);
            }
        }

        if (urls.Count == 0 && source is not null)
        {
            urls.Add(source.GetLeftPart(UriPartial.Authority).TrimEnd('/'));
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(url => new OpenApiEnvironmentTarget(BuildName(url), url))
            .ToList();
    }

    private static bool TryResolveServerUrl(string value, Uri? source, out string url)
    {
        var server = value.Trim();
        if (server.Length == 0 || server.Contains("{{", StringComparison.Ordinal))
        {
            url = string.Empty;
            return false;
        }

        if (Uri.TryCreate(server, UriKind.Absolute, out var absolute) && IsHttp(absolute))
        {
            url = absolute.ToString().TrimEnd('/');
            return true;
        }

        if (
            source is not null
            && Uri.TryCreate(source, server, out var combined)
            && IsHttp(combined)
        )
        {
            url = combined.ToString().TrimEnd('/');
            return true;
        }

        url = string.Empty;
        return false;
    }

    private static Uri? TryGetHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsHttp(uri) ? uri : null;

    private static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static string BuildName(string url)
    {
        var uri = new Uri(url);
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }
}
