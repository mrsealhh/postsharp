using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Keeps credentials out of what PostSharp stores and hands to AI clients. A request PostSharp builds itself is
/// rebuilt with every secret variable resolved to the mask, so the secret never enters the stored text whatever its
/// length or quoting. Text that comes back from a server is searched for the secret values instead, and credential
/// headers are masked by name.
/// </summary>
public static partial class SecretMasker
{
    public const string Mask = "******";

    /// <summary>
    /// Only for searching server text: shorter values ("true", "admin", "1234") would also match ordinary text and
    /// rewrite unrelated content. Requests PostSharp builds are masked at the variable, whatever the length.
    /// </summary>
    public const int MinimumSecretLength = 8;

    private static readonly string EscapedMask = Uri.EscapeDataString(Mask);

    private static readonly JavaScriptEncoder JsonText =
        JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

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

    /// <summary>
    /// Every secret value across the environments in the forms it can take in text (raw, URL-encoded, JSON-escaped,
    /// shell-quoted), longest first so overlapping values mask fully.
    /// </summary>
    public static IReadOnlyList<string> CollectSecrets(
        IEnumerable<EnvironmentModel> environments
    ) =>
        Variants(
            environments
                .SelectMany(environment => environment.Variables)
                .Where(row => row.IsSecret && row.Value.Length >= MinimumSecretLength)
        );

    /// <summary>A copy of the environment in which every secret variable resolves to the mask.</summary>
    public static EnvironmentModel? MaskEnvironment(EnvironmentModel? environment)
    {
        if (environment is null)
        {
            return null;
        }

        var masked = new EnvironmentModel { Id = environment.Id, Name = environment.Name };
        foreach (var row in environment.Variables)
        {
            masked.Variables.AddRow(
                row.Key,
                row.IsSecret ? Mask : row.Value,
                row.IsEnabled,
                row.IsSecret
            );
        }

        return masked;
    }

    /// <summary>
    /// The request as it may be stored or shown to an AI client: rebuilt with secret variables resolved to the mask,
    /// then credential headers masked. Falls back to masking the sent request when the masked one cannot be built,
    /// for example when a secret is part of the host name.
    /// </summary>
    public static ComposedRequest MaskComposed(
        RequestItem request,
        EnvironmentModel? environment,
        ComposedRequest sent,
        IReadOnlyList<string> secrets
    )
    {
        ComposedRequest masked;
        try
        {
            masked = RequestComposer.Compose(request, MaskEnvironment(environment));
            masked = masked with
            {
                Url = masked.Url.Replace(EscapedMask, Mask, StringComparison.Ordinal),
            };
        }
        catch (InvalidOperationException exception)
        {
            Log.Debug(
                exception,
                "按打码环境重建请求失败，改为在已发送请求上打码 {RequestName}",
                request.Name
            );

            // Only the sent request is left to mask, so every secret of its environment is searched, however short.
            var ownSecrets = Variants(
                environment?.Variables.Where(row => row.IsSecret && row.Value.Length > 0) ?? []
            );
            return MaskRequest(
                sent,
                ownSecrets.Concat(secrets).OrderByDescending(value => value.Length).ToList()
            );
        }

        return MaskRequest(masked, secrets);
    }

    /// <summary>
    /// Masks only the start of a possibly large text: what a preview keeps, plus enough to finish a secret that
    /// crosses the cut. Callers still truncate the result, and judge whether anything was cut from the original
    /// length, since masking can shorten the text.
    /// </summary>
    public static string MaskHead(string text, int length, IReadOnlyList<string> secrets)
    {
        var margin = secrets.Count == 0 ? 0 : secrets[0].Length;
        var take = length + margin + 1;
        while (take < text.Length)
        {
            // A secret cut at the end of the head leaves fewer than `margin` characters, which must all lie past
            // `length`. Masked copies earlier in the head shorten it, so keep taking more until that holds.
            var masked = MaskSecrets(text[..take], secrets);
            if (masked.Length > length + margin)
            {
                return masked;
            }

            take += length + margin + 1 - masked.Length;
        }

        return MaskSecrets(text, secrets);
    }

    /// <summary>A masked preview of at most <paramref name="length"/> characters, marked when the text was cut.</summary>
    public static string MaskPreview(string text, int length, IReadOnlyList<string> secrets)
    {
        // MaskHead returns more than `length` characters whenever it cut the text, so this also marks every cut.
        var masked = MaskHead(text, length, secrets);
        return masked.Length <= length ? masked : masked[..length] + "\n…（已截断）";
    }

    public static bool IsCredentialHeader(string name) => CredentialHeaders.Contains(name.Trim());

    /// <summary>The forms a value takes in text (raw, URL-encoded, JSON-escaped, shell-quoted), longest first.</summary>
    private static IReadOnlyList<string> Variants(IEnumerable<KeyValueRow> rows) =>
        rows.Where(row => !string.IsNullOrWhiteSpace(row.Key))
            .SelectMany(row =>
                new[]
                {
                    row.Value,
                    Uri.EscapeDataString(row.Value),
                    JsonEncodedText.Encode(row.Value, JsonText).ToString(),
                    row.Value.Replace("'", "'\\''", StringComparison.Ordinal),
                }
            )
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToList();

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
            UploadFilePath = request.UploadFilePath is null
                ? null
                : MaskSecrets(request.UploadFilePath, secrets),
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
