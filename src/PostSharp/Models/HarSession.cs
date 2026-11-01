using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PostSharp.Models;

/// <summary>One HAR entry shown in the recording session preview.</summary>
public sealed class HarSessionEntry
{
    public required int Index { get; init; }

    public required string Method { get; init; }

    public required string Url { get; init; }

    public int Status { get; init; }

    public double ElapsedMs { get; init; }

    public string MimeType { get; init; } = string.Empty;

    public string ResourceType { get; init; } = string.Empty;

    public DateTimeOffset? StartedAt { get; init; }

    public bool IsStatic { get; init; }

    public required JsonObject Raw { get; init; }

    public string DisplayUrl => MaskSensitiveQuery(Url);

    public string DisplayName
    {
        get
        {
            var displayUrl = DisplayUrl;
            if (!Uri.TryCreate(displayUrl, UriKind.Absolute, out var uri))
            {
                return displayUrl;
            }

            var path = uri.AbsolutePath;
            return string.IsNullOrEmpty(path) || path == "/" ? uri.Host : $"{uri.Host}{path}";
        }
    }

    private static readonly Regex SensitiveQuery = new(
        @"([?&](?:token|access_token|refresh_token|id_token|api_key|apikey|key|secret|password|pwd|signature|sig|code|auth)=)[^&]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    private static string MaskSensitiveQuery(string url) =>
        string.IsNullOrWhiteSpace(url) ? url : SensitiveQuery.Replace(url, "$1***");
}

/// <summary>Parses HAR entries and rebuilds a HAR document for selective import.</summary>
public static class HarSessionService
{
    public const int MaxEntries = 500;

    public static IReadOnlyList<HarSessionEntry> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("HAR 内容为空。");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "无法解析 HAR，请确认文件是合法的 JSON。",
                exception
            );
        }

        using (document)
        {
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("log", out var log)
                || log.ValueKind != JsonValueKind.Object
                || !log.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array
            )
            {
                throw new InvalidOperationException("HAR 缺少 log.entries 数组。");
            }

            var result = new List<HarSessionEntry>();
            var index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                index++;
                if (result.Count >= MaxEntries)
                {
                    break;
                }

                if (
                    entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("request", out var request)
                    || request.ValueKind != JsonValueKind.Object
                )
                {
                    continue;
                }

                var url = ReadString(request, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var method = ReadString(request, "method");
                if (string.IsNullOrWhiteSpace(method))
                {
                    method = "GET";
                }

                var status = 0;
                var mimeType = string.Empty;
                if (
                    entry.TryGetProperty("response", out var response)
                    && response.ValueKind == JsonValueKind.Object
                )
                {
                    status = ReadInt(response, "status");
                    if (
                        response.TryGetProperty("content", out var responseContent)
                        && responseContent.ValueKind == JsonValueKind.Object
                    )
                    {
                        mimeType = ReadString(responseContent, "mimeType");
                    }
                }

                var resourceType = ReadString(entry, "_resourceType");
                var raw = JsonNode.Parse(entry.GetRawText()) as JsonObject;
                if (raw is null)
                {
                    continue;
                }

                result.Add(
                    new HarSessionEntry
                    {
                        Index = index,
                        Method = method.ToUpperInvariant(),
                        Url = url.Trim(),
                        Status = status,
                        ElapsedMs = ReadDouble(entry, "time"),
                        MimeType = mimeType,
                        ResourceType = resourceType,
                        StartedAt = ReadDate(entry, "startedDateTime"),
                        IsStatic = IsStaticResource(resourceType, mimeType, url),
                        Raw = raw,
                    }
                );
            }

            return result;
        }
    }

    public static string BuildHar(IEnumerable<HarSessionEntry> entries) =>
        new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["version"] = "1.2",
                ["creator"] = new JsonObject
                {
                    ["name"] = "PostSharp recording session",
                    ["version"] = "1.0",
                },
                ["entries"] = new JsonArray(
                    entries.Select(entry => (JsonNode?)entry.Raw.DeepClone()).ToArray()
                ),
            },
        }.ToJsonString();

    public static bool IsStaticResource(string resourceType, string mimeType, string url)
    {
        var type = resourceType.Trim().ToLowerInvariant();
        if (
            type
            is "stylesheet"
                or "script"
                or "image"
                or "font"
                or "media"
                or "manifest"
                or "document"
        )
        {
            return true;
        }

        var mime = mimeType.Trim().ToLowerInvariant();
        if (
            mime.StartsWith("text/css", StringComparison.Ordinal)
            || mime.StartsWith("image/", StringComparison.Ordinal)
            || mime.StartsWith("font/", StringComparison.Ordinal)
            || mime.StartsWith("audio/", StringComparison.Ordinal)
            || mime.StartsWith("video/", StringComparison.Ordinal)
            || mime.Contains("javascript", StringComparison.Ordinal)
        )
        {
            return true;
        }

        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant()
            is ".css"
                or ".js"
                or ".mjs"
                or ".png"
                or ".jpg"
                or ".jpeg"
                or ".gif"
                or ".svg"
                or ".webp"
                or ".ico"
                or ".woff"
                or ".woff2"
                or ".ttf"
                or ".map";
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static double ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : 0;

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var date
        )
            ? date
            : null;
}
