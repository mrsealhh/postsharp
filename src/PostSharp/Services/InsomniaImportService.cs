using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Imports legacy Insomnia v4 JSON exports (flat resources array).</summary>
public static class InsomniaImportService
{
    public static ImportResult Parse(string content, string source)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Insomnia 导出内容为空，请选择有效的 JSON 文件。");
        }

        JsonObject root;
        try
        {
            root =
                JsonNode.Parse(content) as JsonObject
                ?? throw new InvalidOperationException(
                    "Insomnia v4 导出文件的根节点必须是 JSON 对象。"
                );
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "无法解析 Insomnia 导出文件，请确认文件是合法的 JSON。",
                exception
            );
        }

        if (root["resources"] is not JsonArray resourceNodes)
        {
            throw new InvalidOperationException(
                "这不是 Insomnia v4 导出文件：缺少 resources 数组。Insomnia v5 的 YAML 暂不支持，请从 Insomnia 导出 v4 JSON。"
            );
        }

        var resources = resourceNodes.OfType<JsonObject>().ToList();
        var warnings = new WarningBag();
        var workspace = resources.FirstOrDefault(resource =>
            GetText(resource["_type"]) == "workspace"
        );
        if (resources.Count(resource => GetText(resource["_type"]) == "workspace") > 1)
        {
            warnings.Add("Insomnia 导出包含多个 workspace，MVP 只导入第一个 workspace。");
        }

        var rootName = GetText(workspace?["name"]).Trim();
        if (rootName.Length == 0)
        {
            rootName = Path.GetFileNameWithoutExtension(source).Trim();
        }

        if (rootName.Length == 0)
        {
            rootName = "Insomnia 导入";
        }

        var importedRoot = new ImportedCollection { Name = rootName };
        var groups = new Dictionary<string, ImportedCollection>(StringComparer.Ordinal);
        var groupResources = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var folderCount = 0;
        foreach (var resource in resources)
        {
            if (GetText(resource["_type"]) != "request_group")
            {
                continue;
            }

            var id = GetText(resource["_id"]);
            if (id.Length == 0)
            {
                continue;
            }

            var name = GetText(resource["name"]).Trim();
            var group = new ImportedCollection { Name = name.Length == 0 ? "未命名分组" : name };
            groups[id] = group;
            groupResources[id] = resource;
            folderCount++;
        }

        foreach (var (id, group) in groups)
        {
            var parentId = GetText(groupResources[id]["parentId"]);
            if (parentId.Length > 0 && groups.TryGetValue(parentId, out var parent))
            {
                parent.Children.Add(group);
            }
            else
            {
                importedRoot.Children.Add(group);
            }
        }

        var requestCount = 0;
        var scriptCount = 0;
        foreach (var resource in resources)
        {
            if (GetText(resource["_type"]) != "request")
            {
                continue;
            }

            var imported = ParseRequest(resource, warnings, ref scriptCount);
            var parentId = GetText(resource["parentId"]);
            var target =
                parentId.Length > 0 && groups.TryGetValue(parentId, out var group)
                    ? group
                    : importedRoot;
            target.Requests.Add(imported);
            requestCount++;
        }

        var environment = ParseEnvironment(resources, rootName, warnings);
        if (scriptCount > 0)
        {
            warnings.Add(
                $"检测到 {scriptCount} 段 Insomnia 脚本或测试，PostSharp 不会执行，请手工迁移。"
            );
        }

        if (requestCount == 0)
        {
            warnings.Add("Insomnia 导出中没有可导入的请求。");
        }

        if (environment is not null)
        {
            warnings.Add(
                $"已把 {environment.Variables.Count} 个 Insomnia 环境变量导入为独立环境。"
            );
        }

        return new ImportResult
        {
            Format = "Insomnia v4",
            Source = source,
            Root = importedRoot,
            Environment = environment,
            Warnings = warnings.ToList(),
            RequestCount = requestCount,
            FolderCount = folderCount,
        };
    }

    private static ImportedRequest ParseRequest(
        JsonObject resource,
        WarningBag warnings,
        ref int scriptCount
    )
    {
        var headers = ParsePairs(resource["headers"] as JsonArray);
        var parameters = ParsePairs(resource["parameters"] as JsonArray);
        var url = GetText(resource["url"]).Trim();
        if (parameters.Count > 0)
        {
            url = RemoveQuery(url);
        }

        var body = ParseBody(resource["body"] as JsonObject, headers, warnings);
        var name = GetText(resource["name"]).Trim();
        if (name.Length == 0)
        {
            name = "Insomnia 请求";
        }

        if (
            GetText(resource["preRequestScript"]).Length > 0
            || GetText(resource["testScript"]).Length > 0
        )
        {
            scriptCount++;
        }

        var imported = new ImportedRequest
        {
            Name = name,
            Method = NormalizeMethod(GetText(resource["method"])),
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

        ApplyAuth(imported, resource["authentication"] as JsonObject, warnings, name);
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

        var mimeType = GetText(body["mimeType"]).Trim().ToLowerInvariant();
        var text = GetText(body["text"]);
        var parameters = ParsePairs(body["params"] as JsonArray);
        if (text.Length == 0 && parameters.Count == 0)
        {
            return BodyImport.Empty;
        }

        if (mimeType.Contains("json", StringComparison.Ordinal))
        {
            return text.Length > 0
                ? new BodyImport("Json", text, string.Empty, "file", [])
                : BodyImport.Empty;
        }

        if (mimeType.Contains("x-www-form-urlencoded", StringComparison.Ordinal))
        {
            var value =
                text.Length > 0
                    ? text
                    : string.Join(
                        "&",
                        parameters.Select(item =>
                            $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"
                        )
                    );
            AddContentTypeIfMissing(headers, "application/x-www-form-urlencoded");
            return value.Length > 0
                ? new BodyImport("Text", value, string.Empty, "file", [])
                : BodyImport.Empty;
        }

        if (mimeType.StartsWith("multipart/form-data", StringComparison.Ordinal))
        {
            if (parameters.Any(item => item.Key.Contains('.', StringComparison.Ordinal)))
            {
                warnings.Add("Insomnia multipart 文件名可能包含扩展名，请导入后复核文件字段。");
            }

            return new BodyImport("Multipart", string.Empty, string.Empty, "file", parameters);
        }

        if (text.Length > 0)
        {
            return new BodyImport("Text", text, string.Empty, "file", []);
        }

        return BodyImport.Empty;
    }

    private static void ApplyAuth(
        ImportedRequest request,
        JsonObject? authentication,
        WarningBag warnings,
        string scope
    )
    {
        if (authentication is null)
        {
            return;
        }

        var type = GetText(authentication["type"]).Trim().ToLowerInvariant();
        if (type is "" or "none")
        {
            return;
        }

        switch (type)
        {
            case "bearer":
                var prefix = GetText(authentication["prefix"]).Trim();
                if (prefix.Length == 0)
                {
                    prefix = "Bearer";
                }

                var token = GetText(authentication["token"]);
                if (token.Length > 0)
                {
                    AddHeaderIfMissing(
                        request,
                        "Authorization",
                        $"{prefix} {token}",
                        warnings,
                        scope
                    );
                }

                break;
            case "basic":
                var username = GetText(authentication["username"]);
                var password = GetText(authentication["password"]);
                if (
                    username.Contains("{{", StringComparison.Ordinal)
                    || password.Contains("{{", StringComparison.Ordinal)
                )
                {
                    warnings.Add($"「{scope}」的 Basic 认证包含变量，未自动转换。");
                    break;
                }

                AddHeaderIfMissing(
                    request,
                    "Authorization",
                    $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}",
                    warnings,
                    scope
                );
                break;
            case "apikey":
                var key = GetText(authentication["key"]).Trim();
                var value = GetText(authentication["value"]);
                if (key.Length == 0)
                {
                    warnings.Add($"「{scope}」的 API Key 认证缺少 key。");
                    break;
                }

                var addTo = GetText(authentication["addTo"]).Trim().ToLowerInvariant();
                if (addTo == "query")
                {
                    request.Params.Add(new ImportedField(key, value));
                }
                else
                {
                    AddHeaderIfMissing(request, key, value, warnings, scope);
                }

                break;
            default:
                warnings.Add(
                    $"「{scope}」使用了 Insomnia {type} 认证，PostSharp 未自动转换；请在请求头中配置。"
                );
                break;
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

    private static ImportedEnvironment? ParseEnvironment(
        IReadOnlyList<JsonObject> resources,
        string rootName,
        WarningBag warnings
    )
    {
        var environments = resources
            .Where(resource => GetText(resource["_type"]) == "environment")
            .ToList();
        if (environments.Count == 0)
        {
            return null;
        }

        if (environments.Count > 1)
        {
            warnings.Add("Insomnia 导出包含多个环境，MVP 只导入第一个有数据的环境。");
        }

        foreach (var environment in environments)
        {
            if (environment["data"] is not JsonObject data)
            {
                continue;
            }

            var imported = new ImportedEnvironment
            {
                Name = GetText(environment["name"]).Trim() is { Length: > 0 } name
                    ? name
                    : $"{rootName} 环境",
            };
            foreach (var entry in data)
            {
                imported.Variables.Add(new ImportedField(entry.Key, NodeToText(entry.Value)));
            }

            if (imported.Variables.Count > 0)
            {
                return imported;
            }
        }

        return null;
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

            var key = GetText(item["name"]).Trim();
            if (key.Length == 0)
            {
                key = GetText(item["key"]).Trim();
            }

            if (key.Length == 0)
            {
                continue;
            }

            fields.Add(
                new ImportedField(
                    key,
                    GetText(item["value"]),
                    !GetBool(item["disabled"]) && GetBool(item["enabled"], true)
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

    private static string NodeToText(JsonNode? node)
    {
        return node is null ? string.Empty : GetText(node);
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
