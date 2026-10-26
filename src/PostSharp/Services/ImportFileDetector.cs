using System.Text.Json;
using System.Text.Json.Nodes;

namespace PostSharp.Services;

public enum ImportFileKind
{
    Unknown,
    OpenApi,
    PostmanCollection,
    PostmanEnvironment,
    Har,
    Insomnia,
    Hoppscotch,
    Curl,
}

/// <summary>Detects an import source from the file extension and content without applying it.</summary>
public static class ImportFileDetector
{
    public static ImportFileKind Detect(string path, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return ImportFileKind.Unknown;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".har")
        {
            return ImportFileKind.Har;
        }

        if (extension is ".yaml" or ".yml")
        {
            return ImportFileKind.OpenApi;
        }

        if (content.TrimStart().StartsWith("curl ", StringComparison.OrdinalIgnoreCase))
        {
            return ImportFileKind.Curl;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            return ImportFileKind.Unknown;
        }

        if (root is null)
        {
            return ImportFileKind.Unknown;
        }

        if (GetText(root["openapi"]).Length > 0 || GetText(root["swagger"]).Length > 0)
        {
            return ImportFileKind.OpenApi;
        }

        if (root["log"] is JsonObject log && log["entries"] is JsonArray)
        {
            return ImportFileKind.Har;
        }

        if (GetText(root["_type"]) == "export" || root["resources"] is JsonArray)
        {
            return ImportFileKind.Insomnia;
        }

        if (root["values"] is JsonArray)
        {
            return ImportFileKind.PostmanEnvironment;
        }

        var schema = GetText((root["info"] as JsonObject)?["schema"]);
        if (
            schema.Contains("collection/v2", StringComparison.OrdinalIgnoreCase)
            || (
                root["info"] is JsonObject
                && root["item"] is JsonArray
                && root["requests"] is not JsonArray
            )
        )
        {
            return ImportFileKind.PostmanCollection;
        }

        if (root["requests"] is JsonArray || root["folders"] is JsonArray)
        {
            return ImportFileKind.Hoppscotch;
        }

        if (root["variable"] is JsonArray)
        {
            return ImportFileKind.PostmanEnvironment;
        }

        return ImportFileKind.Unknown;
    }

    private static string GetText(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : string.Empty;
    }
}
