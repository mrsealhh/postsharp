using System.Text.RegularExpressions;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Keeps credentials out of what PostSharp stores and hands to AI clients: values of environment variables marked
/// as secret are replaced wherever they appear, and credential headers are masked in audit records.
/// </summary>
public static partial class SecretMasker
{
    public const string Mask = "******";

    /// <summary>
    /// Shorter values ("true", "admin", "1234") would also match ordinary text and permanently rewrite unrelated
    /// audit content, so they are not searched for; credential headers are still masked by name.
    /// </summary>
    public const int MinimumSecretLength = 8;

    private static readonly HashSet<string> CredentialHeaders = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "Api-Key",
        "X-Auth-Token",
        "X-Access-Token",
    };

    private static readonly string[] AuthSchemes = ["Bearer", "Basic", "Digest", "Token"];

    [GeneratedRegex(@"^(\s*-H ')([^:']+)(:\s*)(.*?)('(?: \\)?)$", RegexOptions.Multiline)]
    private static partial Regex CurlHeaderLine();

    /// <summary>Every secret value across the environments, longest first so overlapping values mask fully.</summary>
    public static IReadOnlyList<string> CollectSecrets(IEnumerable<EnvironmentModel> environments)
    {
        return environments
            .SelectMany(environment => environment.Variables)
            .Where(row =>
                row.IsSecret
                && !string.IsNullOrWhiteSpace(row.Key)
                && row.Value.Length >= MinimumSecretLength
            )
            .SelectMany(row => new[] { row.Value, Uri.EscapeDataString(row.Value) })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToList();
    }

    public static bool IsCredentialHeader(string name) => CredentialHeaders.Contains(name.Trim());

    public static string MaskSecrets(string text, IReadOnlyList<string> secrets)
    {
        if (string.IsNullOrEmpty(text) || secrets.Count == 0)
        {
            return text;
        }

        foreach (var secret in secrets)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Masks a credential header but keeps its scheme, so "Bearer ******" still tells what was sent.</summary>
    public static string MaskHeaderValue(string name, string value, IReadOnlyList<string> secrets)
    {
        if (!IsCredentialHeader(name))
        {
            return MaskSecrets(value, secrets);
        }

        var trimmed = value.Trim();
        foreach (var scheme in AuthSchemes)
        {
            if (
                trimmed.Length > scheme.Length
                && trimmed.StartsWith(scheme + " ", StringComparison.OrdinalIgnoreCase)
            )
            {
                return trimmed[..scheme.Length] + " " + Mask;
            }
        }

        return Mask;
    }

    /// <summary>Masks "Name: value" lines, the format audit records keep request and response headers in.</summary>
    public static string MaskHeaderLines(string text, IReadOnlyList<string> secrets)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var separator = lines[index].IndexOf(':');
            if (separator <= 0)
            {
                lines[index] = MaskSecrets(lines[index], secrets);
                continue;
            }

            var name = lines[index][..separator];
            var value = lines[index][(separator + 1)..].TrimStart();
            lines[index] = $"{name}: {MaskHeaderValue(name, value, secrets)}";
        }

        return string.Join("\n", lines);
    }

    /// <summary>Masks a cURL command produced by <see cref="RequestComposer.BuildCurl(ComposedRequest)"/>.</summary>
    public static string MaskCurl(string curl, IReadOnlyList<string> secrets)
    {
        if (string.IsNullOrEmpty(curl))
        {
            return curl;
        }

        var masked = CurlHeaderLine()
            .Replace(
                curl,
                match =>
                    IsCredentialHeader(match.Groups[2].Value)
                        ? match.Groups[1].Value
                            + match.Groups[2].Value
                            + match.Groups[3].Value
                            + MaskHeaderValue(match.Groups[2].Value, match.Groups[4].Value, secrets)
                            + match.Groups[5].Value
                        : match.Value
            );
        return MaskSecrets(masked, secrets);
    }

    /// <summary>A copy of the request that is safe to store: secrets and credential headers are masked.</summary>
    public static ComposedRequest MaskRequest(
        ComposedRequest request,
        IReadOnlyList<string> secrets
    )
    {
        return request with
        {
            Url = MaskSecrets(request.Url, secrets),
            Headers = request
                .Headers.Select(pair => new KeyValuePair<string, string>(
                    pair.Key,
                    MaskHeaderValue(pair.Key, pair.Value, secrets)
                ))
                .ToList(),
            Body = request.Body is null ? null : MaskSecrets(request.Body, secrets),
            FormFields = request
                .FormFields?.Select(pair => new KeyValuePair<string, string>(
                    pair.Key,
                    MaskSecrets(pair.Value, secrets)
                ))
                .ToList(),
        };
    }

    /// <summary>Scrubs an audit record in place; returns whether anything changed.</summary>
    public static bool MaskAuditEntry(AuditEntry entry, IReadOnlyList<string> secrets)
    {
        var changed = false;

        string Apply(string value, Func<string, string> mask)
        {
            var result = mask(value);
            changed |= !string.Equals(result, value, StringComparison.Ordinal);
            return result;
        }

        entry.Url = Apply(entry.Url, value => MaskSecrets(value, secrets));
        entry.RequestHeaders = Apply(
            entry.RequestHeaders,
            value => MaskHeaderLines(value, secrets)
        );
        entry.RequestBodyPreview = Apply(
            entry.RequestBodyPreview,
            value => MaskSecrets(value, secrets)
        );
        entry.ResponseHeaders = Apply(
            entry.ResponseHeaders,
            value => MaskHeaderLines(value, secrets)
        );
        entry.ResponseBodyPreview = Apply(
            entry.ResponseBodyPreview,
            value => MaskSecrets(value, secrets)
        );
        entry.AiThinking = Apply(entry.AiThinking, value => MaskSecrets(value, secrets));
        entry.AiContent = Apply(entry.AiContent, value => MaskSecrets(value, secrets));
        if (entry.Curl is { } curl)
        {
            entry.Curl = Apply(curl, value => MaskCurl(value, secrets));
        }

        if (entry.Error is { } error)
        {
            entry.Error = Apply(error, value => MaskSecrets(value, secrets));
        }

        return changed;
    }
}
