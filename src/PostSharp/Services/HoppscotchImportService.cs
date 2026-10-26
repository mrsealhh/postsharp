using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Imports Hoppscotch collection JSON exports.</summary>
public static class HoppscotchImportService
{
    public static ImportResult Parse(string content, string source)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException(
                "Hoppscotch 导出内容为空，请选择有效的 JSON 文件。"
            );
        }

        JsonNode root;
        try
        {
            root =
                JsonNode.Parse(content)
                ?? throw new InvalidOperationException("Hoppscotch 导出内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "无法解析 Hoppscotch 导出文件，请确认文件是合法的 JSON。",
                exception
            );
        }

        var warnings = new WarningBag();
        var importedRoot = new ImportedCollection { Name = BuildRootName(source) };
        var requestCount = 0;
        var folderCount = 0;
        var scriptCount = 0;

        if (root is JsonObject collection)
        {
            if (collection["requests"] is not JsonArray && collection["folders"] is not JsonArray)
            {
                throw new InvalidOperationException(
                    "这不是 Hoppscotch Collection：缺少 requests 或 folders 数组。"
                );
            }

            importedRoot = new ImportedCollection
            {
                Name = GetText(collection["name"]).Trim() is { Length: > 0 } name
                    ? name
                    : BuildRootName(source),
            };
            ParseCollection(
                collection,
                importedRoot,
                warnings,
                ref requestCount,
                ref folderCount,
                ref scriptCount
            );
        }
        else if (root is JsonArray collections)
        {
            if (collections.Count == 0)
            {
                throw new InvalidOperationException("Hoppscotch 导出中没有集合。");
            }

            foreach (var node in collections.OfType<JsonObject>())
            {
                var folder = new ImportedCollection
                {
                    Name = GetText(node["name"]).Trim() is { Length: > 0 } name
                        ? name
                        : $"集合 {folderCount + 1}",
                };
                importedRoot.Children.Add(folder);
                folderCount++;
                ParseCollection(
                    node,
                    folder,
                    warnings,
                    ref requestCount,
                    ref folderCount,
                    ref scriptCount
                );
            }
        }
        else
        {
            throw new InvalidOperationException(
                "这不是 Hoppscotch Collection：根节点应为对象或集合数组。"
            );
        }

        if (scriptCount > 0)
        {
            warnings.Add(
                $"检测到 {scriptCount} 个 Hoppscotch 脚本或测试，PostSharp 不会执行，请手工迁移。"
            );
        }

        if (requestCount == 0)
        {
            warnings.Add("Hoppscotch 导出中没有可导入的请求。");
        }

        return new ImportResult
        {
            Format = "Hoppscotch",
            Source = source,
            Root = importedRoot,
            Environment = null,
            Warnings = warnings.ToList(),
            RequestCount = requestCount,
            FolderCount = folderCount,
        };
    }

    private static void ParseCollection(
        JsonObject collection,
        ImportedCollection target,
        WarningBag warnings,
        ref int requestCount,
        ref int folderCount,
        ref int scriptCount
    )
    {
        foreach (var child in collection["folders"] as JsonArray ?? new JsonArray())
        {
            if (child is not JsonObject folderNode)
            {
                continue;
            }

            var folder = new ImportedCollection
            {
                Name = GetText(folderNode["name"]).Trim() is { Length: > 0 } folderName
                    ? folderName
                    : "未命名分组",
            };
            target.Children.Add(folder);
            folderCount++;
            ParseCollection(
                folderNode,
                folder,
                warnings,
                ref requestCount,
                ref folderCount,
                ref scriptCount
            );
        }

        foreach (var child in collection["requests"] as JsonArray ?? new JsonArray())
        {
            if (child is not JsonObject request)
            {
                continue;
            }

            target.Requests.Add(ParseRequest(request, warnings, ref scriptCount));
            requestCount++;
        }
    }

    private static ImportedRequest ParseRequest(
        JsonObject request,
        WarningBag warnings,
        ref int scriptCount
    )
    {
        var parameters = ParsePairs(request["params"] as JsonArray);
        var headers = ParsePairs(request["headers"] as JsonArray);
        var url = GetText(request["endpoint"]).Trim();
        if (parameters.Count > 0)
        {
            url = RemoveQuery(url);
        }

        var body = ParseBody(request["body"] as JsonObject, headers, warnings);
        var name = GetText(request["name"]).Trim();
        if (name.Length == 0)
        {
            name = "Hoppscotch 请求";
        }

        if (
            GetText(request["preRequestScript"]).Length > 0
            || GetText(request["testScript"]).Length > 0
        )
        {
            scriptCount++;
        }

        var imported = new ImportedRequest
        {
            Name = name,
            Method = NormalizeMethod(GetText(request["method"])),
            Url = url,
            BodyMode = body.Mode,
            Body = body.Body,
            UploadFilePath = body.UploadFilePath,
            UploadFieldName = body.UploadFieldName,
        };

        foreach (var parameter in parameters)
        {
            imported.Params.Add(parameter);
        }

        foreach (var header in headers)
        {
            imported.Headers.Add(header);
        }

        foreach (var field in body.FormFields)
        {
            imported.FormFields.Add(field);
        }

        ApplyAuth(imported, request["auth"] as JsonObject, warnings, name);
        return imported;
    }

    private static BodyImport ParseBody(
        JsonObject? body,
        List<ImportedField> headers,
        WarningBag warnings
    )
    {
        if (body is null)
        {
            return BodyImport.Empty;
        }

        var contentType = GetText(body["contentType"]).Trim().ToLowerInvariant();
        var text = GetText(body["body"]);
        if (text.Length == 0 && contentType.Length == 0)
        {
            return BodyImport.Empty;
        }

        if (contentType.Contains("json", StringComparison.Ordinal))
        {
            return text.Length > 0
                ? new BodyImport("Json", text, string.Empty, "file", [])
                : BodyImport.Empty;
        }

        if (contentType.Contains("x-www-form-urlencoded", StringComparison.Ordinal))
        {
            AddContentTypeIfMissing(headers, "application/x-www-form-urlencoded");
            return text.Length > 0
                ? new BodyImport("Text", text, string.Empty, "file", [])
                : BodyImport.Empty;
        }

        if (contentType.StartsWith("multipart/form-data", StringComparison.Ordinal))
        {
            warnings.Add(
                "Hoppscotch multipart 导出不包含可用的本地文件路径，文件字段需要重新选择。"
            );
            return text.Length > 0
                ? new BodyImport("Multipart", string.Empty, string.Empty, "file", [])
                : BodyImport.Empty;
        }

        return text.Length > 0
            ? new BodyImport("Text", text, string.Empty, "file", [])
            : BodyImport.Empty;
    }

    private static void ApplyAuth(
        ImportedRequest request,
        JsonObject? auth,
        WarningBag warnings,
        string scope
    )
    {
        if (auth is null || !GetBool(auth["authActive"], true))
        {
            return;
        }

        var type = GetText(auth["authType"]).Trim().ToLowerInvariant();
        switch (type)
        {
            case "" or "none":
                return;
            case "bearer":
                var token = GetText(auth["token"]);
                if (token.Length == 0)
                {
                    warnings.Add($"「{scope}」的 Bearer 认证没有 token。");
                    return;
                }

                AddHeaderIfMissing(request, "Authorization", $"Bearer {token}", warnings, scope);
                return;
            case "basic":
                var username = GetText(auth["username"]);
                var password = GetText(auth["password"]);
                if (
                    username.Contains("{{", StringComparison.Ordinal)
                    || password.Contains("{{", StringComparison.Ordinal)
                )
                {
                    warnings.Add($"「{scope}」的 Basic 认证包含变量，未自动转换。");
                    return;
                }

                AddHeaderIfMissing(
                    request,
                    "Authorization",
                    $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}",
                    warnings,
                    scope
                );
                return;
            case "api-key" or "apikey":
                var key = GetText(auth["key"]).Trim();
                var value = GetText(auth["value"]);
                if (key.Length == 0)
                {
                    warnings.Add($"「{scope}」的 API Key 认证缺少 key。");
                    return;
                }

                AddHeaderIfMissing(request, key, value, warnings, scope);
                return;
            default:
                warnings.Add(
                    $"「{scope}」使用了 Hoppscotch {type} 认证，PostSharp 未自动转换；请在请求头中配置。"
                );
                return;
        }
    }

    private static void AddHeaderIfMissing(
        ImportedRequest request,
        string key,
        string value,
        WarningBag warnings,
        string scope
    )
    {
        if (
            request.Headers.Any(header =>
                header.IsEnabled && header.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            warnings.Add($"「{scope}」已有 {key} 请求头，认证配置未覆盖原值。");
            return;
        }

        request.Headers.Add(new ImportedField(key, value));
    }

    private static List<ImportedField> ParsePairs(JsonArray? nodes)
    {
        var fields = new List<ImportedField>();
        if (nodes is null)
        {
            return fields;
        }

        foreach (var node in nodes)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var key = GetText(item["key"]).Trim();
            if (key.Length == 0)
            {
                key = GetText(item["name"]).Trim();
            }

            if (key.Length == 0)
            {
                continue;
            }

            fields.Add(
                new ImportedField(
                    key,
                    GetText(item["value"]),
                    !GetBool(item["disabled"]) && GetBool(item["active"], true)
                )
            );
        }

        return fields;
    }

    private static void AddContentTypeIfMissing(List<ImportedField> headers, string contentType)
    {
        if (
            headers.Any(header =>
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return;
        }

        headers.Add(new ImportedField("Content-Type", contentType));
    }

    private static string RemoveQuery(string url)
    {
        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        var withoutFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        var queryIndex = withoutFragment.IndexOf('?');
        return (queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment) + fragment;
    }

    private static string BuildRootName(string source)
    {
        var name = Path.GetFileNameWithoutExtension(source).Trim();
        return name.Length == 0 ? "Hoppscotch 导入" : name;
    }

    private static string NormalizeMethod(string method)
    {
        var normalized = method.Trim().ToUpperInvariant();
        return normalized.Length == 0 ? "GET" : normalized;
    }

    private static bool GetBool(JsonNode? node, bool defaultValue = false)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : defaultValue;
    }

    private static string GetText(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return string.Empty;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value.TryGetValue<bool>(out var boolean)
            ? (boolean ? "true" : "false")
            : value.ToJsonString();
    }

    private sealed record BodyImport(
        string Mode,
        string Body,
        string UploadFilePath,
        string UploadFieldName,
        List<ImportedField> FormFields
    )
    {
        public static BodyImport Empty { get; } =
            new("None", string.Empty, string.Empty, "file", []);
    }

    private sealed class WarningBag
    {
        private readonly List<string> _messages = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public void Add(string message)
        {
            if (_seen.Add(message))
            {
                _messages.Add(message);
            }
        }

        public IReadOnlyList<string> ToList() => _messages;
    }
}
