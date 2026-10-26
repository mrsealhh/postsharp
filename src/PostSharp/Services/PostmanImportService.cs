using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Imports Postman Collection v2.0 / v2.1 documents without executing Postman scripts.
/// The parser keeps unsupported constructs as visible warnings instead of silently dropping them.
/// </summary>
public static class PostmanImportService
{
    public const int MaxCollectionDepth = 5;

    private const string CollectionSchemaV2 = "collection/v2.";

    public static ImportResult Parse(string content, string source)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException(
                "Postman Collection 内容为空，请选择有效的 JSON 文件。"
            );
        }

        JsonObject root;
        try
        {
            root =
                JsonNode.Parse(content) as JsonObject
                ?? throw new InvalidOperationException(
                    "Postman Collection 的根节点必须是 JSON 对象。"
                );
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "无法解析 Postman Collection，请确认文件是合法的 JSON。",
                exception
            );
        }

        var info = root["info"] as JsonObject;
        if (info is null)
        {
            throw new InvalidOperationException(
                "这不是 Postman Collection v2.x：缺少 info 字段。请在 Postman 中选择 Collection v2.1 格式导出。"
            );
        }

        var schema = GetText(info["schema"]);
        var rootName = GetText(info["name"]).Trim();
        if (string.IsNullOrEmpty(rootName))
        {
            rootName = "Postman 集合";
        }

        var context = new ImportContext();
        if (
            !string.IsNullOrEmpty(schema)
            && !schema.Contains(CollectionSchemaV2, StringComparison.OrdinalIgnoreCase)
        )
        {
            context.Warnings.Add(
                $"文档声明的格式是「{schema}」，将按 Postman Collection v2.x 尝试导入。"
            );
        }

        context.ScriptCount += CountEvents(root);
        var rootAuth = ParseAuth(root["auth"] as JsonObject, context, "集合");
        var importedRoot = new ImportedCollection { Name = rootName };
        ParseItems(root["item"] as JsonArray, importedRoot, 1, rootAuth, context, string.Empty);

        var environment = ParseEnvironment(root, rootName);
        if (environment is not null)
        {
            context.Warnings.Add(
                $"已把 {environment.Variables.Count} 个集合变量导入为环境「{environment.Name}」。"
            );
        }

        if (context.ScriptCount > 0)
        {
            context.Warnings.Add(
                $"检测到 {context.ScriptCount} 段 Postman 脚本或测试；PostSharp 不会执行脚本，相关变量改写和断言需要手工迁移。"
            );
        }

        if (context.RequestCount == 0 && context.FolderCount == 0)
        {
            context.Warnings.Add("集合中没有请求或子分组，导入后只会创建一个空集合。");
        }

        return new ImportResult
        {
            Format = string.IsNullOrEmpty(schema)
                ? "Postman Collection"
                : $"Postman Collection {schema[(schema.LastIndexOf('/') + 1)..]}",
            Source = source,
            Root = importedRoot,
            Environment = environment,
            Warnings = context.Warnings.ToList(),
            RequestCount = context.RequestCount,
            FolderCount = context.FolderCount,
        };
    }

    private static void ParseItems(
        JsonArray? items,
        ImportedCollection parent,
        int depth,
        PostmanAuth? inheritedAuth,
        ImportContext context,
        string flattenPrefix
    )
    {
        if (items is null)
        {
            return;
        }

        foreach (var node in items)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            context.ScriptCount += CountEvents(item);
            var itemName = GetText(item["name"]).Trim();
            if (string.IsNullOrEmpty(itemName))
            {
                itemName = "未命名项目";
            }

            var displayName = flattenPrefix + itemName;
            var itemAuth = item["auth"] is JsonObject auth
                ? ParseAuth(auth, context, displayName)
                : inheritedAuth;

            if (item["item"] is JsonArray childItems)
            {
                if (depth >= MaxCollectionDepth)
                {
                    context.Warnings.Add(
                        $"集合层级超过 {MaxCollectionDepth} 级，已把「{displayName}」内的请求平铺到上一级。"
                    );
                    ParseItems(childItems, parent, depth, itemAuth, context, displayName + " / ");
                    continue;
                }

                var folder = new ImportedCollection { Name = displayName };
                parent.Children.Add(folder);
                context.FolderCount++;
                ParseItems(childItems, folder, depth + 1, itemAuth, context, string.Empty);
                continue;
            }

            if (item["request"] is JsonObject request)
            {
                var imported = ParseRequest(request, displayName, itemAuth, context);
                parent.Requests.Add(imported);
                context.RequestCount++;
            }
        }
    }

    private static ImportedRequest ParseRequest(
        JsonObject request,
        string name,
        PostmanAuth? inheritedAuth,
        ImportContext context
    )
    {
        var parsedUrl = ParseUrl(request["url"], context);
        var headers = ParseFields(request["header"]);
        var body = ParseBody(request["body"], headers, context);
        var requestAuth = request["auth"] is JsonObject auth
            ? ParseAuth(auth, context, name)
            : inheritedAuth;

        var imported = new ImportedRequest
        {
            Name = name,
            Method = NormalizeMethod(GetText(request["method"])),
            Url = parsedUrl.Url,
            BodyMode = body.Mode,
            Body = body.Body,
            UploadFilePath = body.UploadFilePath,
            UploadFieldName = body.UploadFieldName,
        };

        foreach (var field in parsedUrl.Query)
        {
            imported.Params.Add(field);
        }

        foreach (var field in headers)
        {
            imported.Headers.Add(field);
        }

        foreach (var field in body.FormFields)
        {
            imported.FormFields.Add(field);
        }

        ApplyAuth(imported, requestAuth, context, name);
        return imported;
    }

    private static ParsedUrl ParseUrl(JsonNode? node, ImportContext context)
    {
        var query = new List<ImportedField>();
        var raw = string.Empty;

        if (node is JsonValue)
        {
            raw = GetText(node).Trim();
            if (string.IsNullOrEmpty(raw))
            {
                context.Warnings.Add("有请求没有填写 URL，导入后需要手工补全。");
            }

            return new ParsedUrl(raw, query);
        }

        if (node is not JsonObject url)
        {
            context.Warnings.Add("有请求的 URL 结构无法识别，导入后需要手工补全。");
            return new ParsedUrl(raw, query);
        }

        raw = GetText(url["raw"]).Trim();
        if (string.IsNullOrEmpty(raw))
        {
            raw = BuildRawUrl(url);
        }

        if (string.IsNullOrEmpty(raw))
        {
            context.Warnings.Add("有请求没有填写 URL，导入后需要手工补全。");
            return new ParsedUrl(raw, query);
        }

        var queryItems = url["query"] as JsonArray;
        if (queryItems is { Count: > 0 })
        {
            query.AddRange(ParseFields(queryItems));
            raw = RemoveQuery(raw);
        }

        return new ParsedUrl(raw, query);
    }

    private static string BuildRawUrl(JsonObject url)
    {
        var protocol = GetText(url["protocol"]).Trim();
        var host = url["host"] switch
        {
            JsonArray hostItems => string.Join(
                ".",
                hostItems.Select(item => GetText(item).Trim()).Where(item => item.Length > 0)
            ),
            _ => GetText(url["host"]).Trim(),
        };
        var port = GetText(url["port"]).Trim();
        var path = url["path"] switch
        {
            JsonArray pathItems => string.Join(
                "/",
                pathItems.Select(item => GetText(item).Trim()).Where(item => item.Length > 0)
            ),
            _ => GetText(url["path"]).Trim().TrimStart('/'),
        };

        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(protocol))
        {
            builder.Append(protocol).Append("://");
        }

        builder.Append(host);
        if (!string.IsNullOrEmpty(port))
        {
            builder.Append(':').Append(port);
        }

        if (!string.IsNullOrEmpty(path))
        {
            builder.Append('/').Append(path);
        }

        return builder.ToString();
    }

    private static BodyImport ParseBody(
        JsonNode? node,
        List<ImportedField> headers,
        ImportContext context
    )
    {
        if (node is not JsonObject body)
        {
            return BodyImport.Empty;
        }

        var mode = GetText(body["mode"]).Trim().ToLowerInvariant();
        return mode switch
        {
            "raw" => ParseRawBody(body),
            "urlencoded" => ParseUrlEncodedBody(body, headers, context),
            "formdata" => ParseFormDataBody(body, context),
            "file" => ParseFileBody(body, context),
            "graphql" => ParseGraphqlBody(body, context),
            "" or "none" => BodyImport.Empty,
            _ => ParseUnknownBody(mode, context),
        };
    }

    private static BodyImport ParseRawBody(JsonObject body)
    {
        var raw = GetText(body["raw"]);
        if (string.IsNullOrEmpty(raw))
        {
            return BodyImport.Empty;
        }

        var options = body["options"] as JsonObject;
        var rawOptions = options?["raw"] as JsonObject;
        var language = GetText(rawOptions?["language"]).Trim().ToLowerInvariant();
        var isJson =
            language == "json"
            || (
                language.Length == 0
                && (raw.TrimStart().StartsWith('{') || raw.TrimStart().StartsWith('['))
            );

        return new BodyImport(isJson ? "Json" : "Text", raw, string.Empty, "file", []);
    }

    private static BodyImport ParseUrlEncodedBody(
        JsonObject body,
        List<ImportedField> headers,
        ImportContext context
    )
    {
        var rows = ParseFields(body["urlencoded"] as JsonArray);
        var enabled = rows.Where(row => row.IsEnabled).ToList();
        var disabledCount = rows.Count - enabled.Count;
        if (disabledCount > 0)
        {
            context.Warnings.Add($"有 {disabledCount} 个禁用的 urlencoded 字段已忽略。");
        }

        if (enabled.Count == 0)
        {
            return BodyImport.Empty;
        }

        var value = string.Join(
            "&",
            enabled.Select(row =>
                $"{EncodeFormPart(row.Key, preserveVariables: false)}={EncodeFormPart(row.Value, preserveVariables: true)}"
            )
        );
        AddContentTypeHeader(headers, "application/x-www-form-urlencoded");
        context.Warnings.Add(
            "urlencoded 请求体已转换为文本请求体，并写入 Content-Type；需要继续按表单行编辑时可手工改成 multipart。"
        );
        return new BodyImport("Text", value, string.Empty, "file", []);
    }

    private static BodyImport ParseFormDataBody(JsonObject body, ImportContext context)
    {
        var fields = new List<ImportedField>();
        var uploadFilePath = string.Empty;
        var uploadFieldName = "file";
        var fileCount = 0;

        foreach (var node in body["formdata"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject field)
            {
                continue;
            }

            var key = GetText(field["key"]).Trim();
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var isEnabled = !GetBool(field["disabled"]);
            var type = GetText(field["type"]).Trim().ToLowerInvariant();
            if (type == "file")
            {
                if (!isEnabled)
                {
                    continue;
                }

                fileCount++;
                var source = ReadFileSource(field["src"]);
                if (string.IsNullOrEmpty(source))
                {
                    context.Warnings.Add($"multipart 文件字段「{key}」没有可用的本地路径。");
                    continue;
                }

                if (fileCount == 1)
                {
                    uploadFilePath = NormalizeFilePath(source);
                    uploadFieldName = key;
                }
                else
                {
                    context.Warnings.Add(
                        $"multipart 请求包含多个文件，MVP 只保留第一个文件字段「{uploadFieldName}」，其余文件需要手工补充。"
                    );
                }

                continue;
            }

            fields.Add(new ImportedField(key, GetText(field["value"]), isEnabled));
        }

        if (fileCount == 0 && fields.Count == 0)
        {
            return BodyImport.Empty;
        }

        return new BodyImport("Multipart", string.Empty, uploadFilePath, uploadFieldName, fields);
    }

    private static BodyImport ParseFileBody(JsonObject body, ImportContext context)
    {
        var file = body["file"] as JsonObject;
        var source = ReadFileSource(file?["src"]);
        if (string.IsNullOrEmpty(source))
        {
            context.Warnings.Add("二进制文件请求没有本地文件路径，导入后需要重新选择文件。");
            return BodyImport.Empty;
        }

        context.Warnings.Add("二进制文件请求已按 multipart 单文件上传导入。");
        return new BodyImport("Multipart", string.Empty, NormalizeFilePath(source), "file", []);
    }

    private static BodyImport ParseGraphqlBody(JsonObject body, ImportContext context)
    {
        var graphql = body["graphql"] as JsonObject;
        var query = GetText(graphql?["query"]);
        if (string.IsNullOrEmpty(query))
        {
            context.Warnings.Add("GraphQL 请求没有 query，导入后需要手工补全。");
            return BodyImport.Empty;
        }

        context.Warnings.Add("GraphQL 请求体已转换为文本，Variables 未自动转换。");
        return new BodyImport("Text", query, string.Empty, "file", []);
    }

    private static BodyImport ParseUnknownBody(string mode, ImportContext context)
    {
        context.Warnings.Add($"请求体类型「{mode}」暂不支持，已跳过请求体。");
        return BodyImport.Empty;
    }

    private static void AddContentTypeHeader(List<ImportedField> headers, string value)
    {
        if (
            headers.Any(header =>
                header.IsEnabled
                && header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return;
        }

        headers.Add(new ImportedField("Content-Type", value));
    }

    private static List<ImportedField> ParseFields(JsonNode? node)
    {
        var fields = new List<ImportedField>();
        if (node is not JsonArray items)
        {
            return fields;
        }

        foreach (var item in items)
        {
            if (item is not JsonObject field)
            {
                continue;
            }

            var key = GetText(field["key"]).Trim();
            if (string.IsNullOrEmpty(key))
            {
                key = GetText(field["name"]).Trim();
            }

            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            fields.Add(
                new ImportedField(key, GetText(field["value"]), !GetBool(field["disabled"]))
            );
        }

        return fields;
    }

    private static ImportedEnvironment? ParseEnvironment(JsonObject root, string rootName)
    {
        var variables = ParseFields(root["variable"]);
        if (variables.Count == 0)
        {
            return null;
        }

        var environment = new ImportedEnvironment { Name = $"{rootName} 变量" };
        foreach (var variable in variables)
        {
            environment.Variables.Add(variable);
        }

        return environment;
    }

    private static PostmanAuth? ParseAuth(JsonObject? auth, ImportContext context, string scope)
    {
        if (auth is null)
        {
            return null;
        }

        var type = GetText(auth["type"]).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(type))
        {
            return null;
        }

        if (type == "noauth")
        {
            return new PostmanAuth(type, new Dictionary<string, string>());
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (auth[type] is JsonArray entries)
        {
            foreach (var item in entries)
            {
                if (item is not JsonObject entry)
                {
                    continue;
                }

                var key = GetText(entry["key"]).Trim();
                if (!string.IsNullOrEmpty(key))
                {
                    values[key] = GetText(entry["value"]);
                }
            }
        }
        else if (auth[type] is JsonObject single)
        {
            foreach (var pair in single)
            {
                values[pair.Key] = GetText(pair.Value);
            }
        }

        if (type is not ("bearer" or "basic" or "apikey" or "noauth"))
        {
            context.Warnings.Add(
                $"「{scope}」使用了 {type} 认证，PostSharp 未自动转换；请在请求头或环境变量中配置。"
            );
        }

        return new PostmanAuth(type, values);
    }

    private static void ApplyAuth(
        ImportedRequest request,
        PostmanAuth? auth,
        ImportContext context,
        string scope
    )
    {
        if (auth is null || auth.Type == "noauth")
        {
            return;
        }

        switch (auth.Type)
        {
            case "bearer":
                ApplyBearerAuth(request, auth, context, scope);
                break;
            case "basic":
                ApplyBasicAuth(request, auth, context, scope);
                break;
            case "apikey":
                ApplyApiKeyAuth(request, auth, context, scope);
                break;
            default:
                break;
        }
    }

    private static void ApplyBearerAuth(
        ImportedRequest request,
        PostmanAuth auth,
        ImportContext context,
        string scope
    )
    {
        var token = auth.Values.TryGetValue("token", out var value) ? value : string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            context.Warnings.Add($"「{scope}」的 Bearer 认证没有 token，已跳过 Authorization 头。");
            return;
        }

        AddHeaderIfMissing(request, "Authorization", $"Bearer {token}", context, scope);
    }

    private static void ApplyBasicAuth(
        ImportedRequest request,
        PostmanAuth auth,
        ImportContext context,
        string scope
    )
    {
        var username = auth.Values.TryGetValue("username", out var user) ? user : string.Empty;
        var password = auth.Values.TryGetValue("password", out var pass) ? pass : string.Empty;
        if (
            username.Contains("{{", StringComparison.Ordinal)
            || password.Contains("{{", StringComparison.Ordinal)
        )
        {
            context.Warnings.Add(
                $"「{scope}」的 Basic 认证包含变量，未自动转换；请在请求头中配置 Authorization。"
            );
            return;
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        AddHeaderIfMissing(request, "Authorization", $"Basic {encoded}", context, scope);
    }

    private static void ApplyApiKeyAuth(
        ImportedRequest request,
        PostmanAuth auth,
        ImportContext context,
        string scope
    )
    {
        var key = auth.Values.TryGetValue("key", out var keyValue) ? keyValue.Trim() : string.Empty;
        var value = auth.Values.TryGetValue("value", out var authValue) ? authValue : string.Empty;
        var location = auth.Values.TryGetValue("in", out var locationValue)
            ? locationValue.Trim().ToLowerInvariant()
            : "header";

        if (string.IsNullOrEmpty(key))
        {
            context.Warnings.Add($"「{scope}」的 API Key 认证缺少 key，已跳过。");
            return;
        }

        if (location == "query")
        {
            if (
                request.Params.Any(field =>
                    field.IsEnabled && field.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return;
            }

            request.Params.Add(new ImportedField(key, value));
            return;
        }

        AddHeaderIfMissing(request, key, value, context, scope);
    }

    private static void AddHeaderIfMissing(
        ImportedRequest request,
        string key,
        string value,
        ImportContext context,
        string scope
    )
    {
        if (
            request.Headers.Any(field =>
                field.IsEnabled && field.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            context.Warnings.Add($"「{scope}」已有 {key} 请求头，认证配置未覆盖原值。");
            return;
        }

        request.Headers.Add(new ImportedField(key, value));
    }

    private static int CountEvents(JsonObject item)
    {
        return item["event"] is JsonArray events ? events.Count : 0;
    }

    private static string EncodeFormPart(string value, bool preserveVariables)
    {
        if (!preserveVariables)
        {
            return Uri.EscapeDataString(value);
        }

        var builder = new StringBuilder();
        var index = 0;
        while (index < value.Length)
        {
            var start = value.IndexOf("{{", index, StringComparison.Ordinal);
            if (start < 0)
            {
                builder.Append(Uri.EscapeDataString(value[index..]));
                break;
            }

            builder.Append(Uri.EscapeDataString(value[index..start]));
            var end = value.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                builder.Append(value[start..]);
                break;
            }

            builder.Append(value[start..(end + 2)]);
            index = end + 2;
        }

        return builder.ToString();
    }

    private static string ReadFileSource(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            return GetText(value).Trim();
        }

        if (node is JsonArray sources)
        {
            return sources
                    .Select(GetText)
                    .Select(path => path.Trim())
                    .FirstOrDefault(path => path.Length > 0)
                ?? string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeFilePath(string path)
    {
        var normalized = path.Trim();
        if (
            normalized.Length >= 3
            && normalized[0] == '/'
            && char.IsLetter(normalized[1])
            && normalized[2] == ':'
        )
        {
            normalized = normalized[1..];
        }

        return normalized;
    }

    private static string NormalizeMethod(string method)
    {
        var normalized = method.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(normalized) ? "GET" : normalized;
    }

    private static string RemoveQuery(string url)
    {
        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        var withoutFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        var queryIndex = withoutFragment.IndexOf('?');
        var path = queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment;
        return path + fragment;
    }

    private static bool GetBool(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    }

    private static string GetText(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        if (node is not JsonValue value)
        {
            return string.Empty;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (value.TryGetValue<bool>(out var boolean))
        {
            return boolean ? "true" : "false";
        }

        if (value.TryGetValue<long>(out var integer))
        {
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<double>(out var number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        return value.ToJsonString();
    }

    private sealed record ParsedUrl(string Url, List<ImportedField> Query);

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

    private sealed record PostmanAuth(string Type, Dictionary<string, string> Values);

    private sealed class ImportContext
    {
        public WarningCollector Warnings { get; } = new();

        public int RequestCount { get; set; }

        public int FolderCount { get; set; }

        public int ScriptCount { get; set; }
    }

    private sealed class WarningCollector
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
