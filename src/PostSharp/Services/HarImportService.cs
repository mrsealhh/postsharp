using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Imports HTTP Archive (HAR 1.2) entries as PostSharp requests. HAR responses are ignored;
/// scripts and binary upload contents are not executed or restored.
/// </summary>
public static class HarImportService
{
    private const int MaxEntries = 5000;

    private static readonly HashSet<string> IgnoredHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept-Encoding",
        "Connection",
        "Content-Length",
        "Host",
        "Transfer-Encoding",
        "Upgrade-Insecure-Requests",
    };

    private static readonly HashSet<string> StaticResourceTypes = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "document",
        "stylesheet",
        "script",
        "image",
        "font",
        "media",
    };

    public static ImportResult Parse(
        string content,
        string source,
        bool includeStaticResources = false
    )
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("HAR 内容为空，请选择有效的 .har 文件。");
        }

        JsonObject root;
        try
        {
            root =
                JsonNode.Parse(content) as JsonObject
                ?? throw new InvalidOperationException("HAR 文件的根节点必须是 JSON 对象。");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "无法解析 HAR，请确认文件是合法的 JSON。",
                exception
            );
        }

        var log =
            root["log"] as JsonObject
            ?? throw new InvalidOperationException("不是有效的 HAR：缺少 log 字段。");
        var entries =
            log["entries"] as JsonArray
            ?? throw new InvalidOperationException("不是有效的 HAR：缺少 log.entries。");

        var warnings = new WarningBag();
        var pages = ParsePages(log["pages"] as JsonArray);
        var rootName = BuildRootName(source);
        var importedRoot = new ImportedCollection { Name = rootName };
        var groups = new Dictionary<string, ImportedCollection>(StringComparer.OrdinalIgnoreCase);
        var groupOrder = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var context = new HarContext(warnings);
        var requestCount = 0;
        var skippedStatic = 0;
        var duplicateCount = 0;

        if (entries.Count > MaxEntries)
        {
            warnings.Add($"HAR 包含 {entries.Count} 条记录，只导入前 {MaxEntries} 条。");
        }

        foreach (var node in entries.Take(MaxEntries))
        {
            if (node is not JsonObject entry)
            {
                continue;
            }

            var request = entry["request"] as JsonObject;
            var url = GetText(request?["url"]).Trim();
            if (request is null || !IsHttpUrl(url))
            {
                context.InvalidEntryCount++;
                continue;
            }

            var mimeType = GetResponseMimeType(entry);
            if (!includeStaticResources && IsStatic(entry, url, mimeType))
            {
                skippedStatic++;
                continue;
            }

            var method = NormalizeMethod(GetText(request["method"]));
            var deduplicationKey = method + "\n" + url;
            if (!seen.Add(deduplicationKey))
            {
                duplicateCount++;
                continue;
            }

            var groupName = GetGroupName(entry, pages, url);
            if (!groups.TryGetValue(groupName, out var group))
            {
                group = new ImportedCollection { Name = groupName };
                groups[groupName] = group;
                groupOrder.Add(groupName);
            }

            var imported = ParseRequest(entry, request, url, context);
            group.Requests.Add(imported);
            requestCount++;
        }

        foreach (var groupName in groupOrder)
        {
            importedRoot.Children.Add(groups[groupName]);
        }

        if (context.InvalidEntryCount > 0)
        {
            warnings.Add($"有 {context.InvalidEntryCount} 条记录不是完整的 HTTP 请求，已跳过。");
        }

        if (skippedStatic > 0)
        {
            warnings.Add(
                $"已跳过 {skippedStatic} 个静态资源请求；如需导入，请勾选「包含静态资源」。"
            );
        }

        if (duplicateCount > 0)
        {
            warnings.Add($"已去除 {duplicateCount} 个重复的 method + URL 请求。");
        }

        if (context.CookieImported)
        {
            warnings.Add("Cookie 已作为请求头导入；发送前请确认是否保留敏感值。");
        }

        if (context.MultipartFileCount > 0)
        {
            warnings.Add(
                $"HAR 不保存 multipart 文件内容；{context.MultipartFileCount} 个文件字段需要重新选择文件。"
            );
        }

        if (requestCount == 0)
        {
            warnings.Add("HAR 中没有可导入的请求。");
        }

        return new ImportResult
        {
            Format = "HAR 1.2",
            Source = source,
            Root = importedRoot,
            Environment = null,
            Warnings = warnings.ToList(),
            RequestCount = requestCount,
            FolderCount = groups.Count,
        };
    }

    private static ImportedRequest ParseRequest(
        JsonObject entry,
        JsonObject request,
        string url,
        HarContext context
    )
    {
        var postData = request["postData"] as JsonObject;
        var mimeType = GetMediaType(GetText(postData?["mimeType"]));
        var headers = ParseHeaders(request["header"] as JsonArray, mimeType);
        var query = ParseQuery(request["queryString"] as JsonArray);
        var urlWithoutQuery = query.Count > 0 ? RemoveQuery(url) : url;
        AddCookies(request, headers, context);
        var body = ParseBody(postData, mimeType, headers, context);

        var imported = new ImportedRequest
        {
            Name = BuildRequestName(url),
            Method = NormalizeMethod(GetText(request["method"])),
            Url = urlWithoutQuery,
            BodyMode = body.Mode,
            Body = body.Body,
            UploadFilePath = body.UploadFilePath,
            UploadFieldName = body.UploadFieldName,
        };

        foreach (var field in query)
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

        return imported;
    }

    private static BodyImport ParseBody(
        JsonObject? postData,
        string mimeType,
        List<ImportedField> headers,
        HarContext context
    )
    {
        if (postData is null)
        {
            return BodyImport.Empty;
        }

        var text = GetText(postData["text"]);
        var parameters = postData["params"] as JsonArray;
        var isJsonMime = mimeType.Contains("json", StringComparison.Ordinal);
        var isFormMime = mimeType.Contains("x-www-form-urlencoded", StringComparison.Ordinal);
        var isMultipartMime = mimeType.StartsWith("multipart/form-data", StringComparison.Ordinal);

        if (isJsonMime || (LooksLikeJson(text) && !isFormMime && !isMultipartMime))
        {
            return text.Length > 0
                ? new BodyImport("Json", text, string.Empty, "file", [])
                : BodyImport.Empty;
        }

        if (isFormMime)
        {
            var rows = ParseFormRows(parameters);
            var body =
                text.Length > 0
                    ? text
                    : string.Join(
                        "&",
                        rows.Select(row =>
                            $"{Uri.EscapeDataString(row.Key)}={Uri.EscapeDataString(row.Value)}"
                        )
                    );
            if (body.Length > 0)
            {
                AddContentTypeIfMissing(headers, "application/x-www-form-urlencoded");
                return new BodyImport("Text", body, string.Empty, "file", []);
            }
        }

        if (isMultipartMime)
        {
            var fields = new List<ImportedField>();
            var uploadFilePath = string.Empty;
            var uploadFieldName = "file";
            var fileCount = 0;
            if (parameters is not null)
            {
                foreach (var node in parameters)
                {
                    if (node is not JsonObject parameter)
                    {
                        continue;
                    }

                    var key = GetText(parameter["name"]).Trim();
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(GetText(parameter["fileName"])))
                    {
                        fileCount++;
                        continue;
                    }

                    fields.Add(new ImportedField(key, GetText(parameter["value"])));
                }
            }

            if (fields.Count > 0 || fileCount > 0)
            {
                context.MultipartFileCount += fileCount;
                return new BodyImport(
                    "Multipart",
                    string.Empty,
                    uploadFilePath,
                    uploadFieldName,
                    fields
                );
            }

            if (text.Length > 0)
            {
                context.Warnings.Add(
                    "HAR 的 multipart 原始请求体无法安全还原，已跳过；请重新填写表单字段。"
                );
            }

            return BodyImport.Empty;
        }

        if (text.Length > 0)
        {
            if (
                mimeType.StartsWith("application/octet-stream", StringComparison.Ordinal)
                || mimeType.StartsWith("image/", StringComparison.Ordinal)
                || mimeType.StartsWith("audio/", StringComparison.Ordinal)
                || mimeType.StartsWith("video/", StringComparison.Ordinal)
            )
            {
                context.Warnings.Add($"请求体类型「{mimeType}」是二进制内容，已跳过。");
                return BodyImport.Empty;
            }

            return new BodyImport("Text", text, string.Empty, "file", []);
        }

        return BodyImport.Empty;
    }

    private static List<ImportedField> ParseHeaders(JsonArray? headers, string mimeType)
    {
        var fields = new List<ImportedField>();
        if (headers is null)
        {
            return fields;
        }

        foreach (var node in headers)
        {
            if (node is not JsonObject header)
            {
                continue;
            }

            var key = GetText(header["name"]).Trim();
            if (key.Length == 0)
            {
                key = GetText(header["key"]).Trim();
            }

            if (
                key.Length == 0
                || key.StartsWith(':')
                || key.StartsWith("sec-fetch-", StringComparison.OrdinalIgnoreCase)
                || IgnoredHeaders.Contains(key)
            )
            {
                continue;
            }

            var value = GetText(header["value"]);
            if (
                key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                && mimeType.StartsWith("multipart/form-data", StringComparison.Ordinal)
            )
            {
                continue;
            }

            fields.Add(new ImportedField(key, value));
        }

        return fields;
    }

    private static List<ImportedField> ParseQuery(JsonArray? queryString)
    {
        var fields = new List<ImportedField>();
        if (queryString is null)
        {
            return fields;
        }

        foreach (var node in queryString)
        {
            if (node is not JsonObject parameter)
            {
                continue;
            }

            var key = GetText(parameter["name"]).Trim();
            if (key.Length == 0)
            {
                continue;
            }

            fields.Add(new ImportedField(key, GetText(parameter["value"])));
        }

        return fields;
    }

    private static List<ImportedField> ParseFormRows(JsonArray? parameters)
    {
        var rows = new List<ImportedField>();
        if (parameters is null)
        {
            return rows;
        }

        foreach (var node in parameters)
        {
            if (node is not JsonObject parameter)
            {
                continue;
            }

            var key = GetText(parameter["name"]).Trim();
            if (key.Length > 0)
            {
                rows.Add(new ImportedField(key, GetText(parameter["value"])));
            }
        }

        return rows;
    }

    private static void AddCookies(
        JsonObject request,
        List<ImportedField> headers,
        HarContext context
    )
    {
        if (headers.Any(header => header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var cookies = request["cookies"] as JsonArray;
        if (cookies is null || cookies.Count == 0)
        {
            return;
        }

        var values = cookies
            .OfType<JsonObject>()
            .Select(cookie => $"{GetText(cookie["name"])}={GetText(cookie["value"])}")
            .Where(value => value.Length > 1)
            .ToList();
        if (values.Count == 0)
        {
            return;
        }

        headers.Add(new ImportedField("Cookie", string.Join("; ", values)));
        context.CookieImported = true;
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

    private static Dictionary<string, string> ParsePages(JsonArray? pages)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pages is null)
        {
            return result;
        }

        foreach (var node in pages)
        {
            if (node is not JsonObject page)
            {
                continue;
            }

            var id = GetText(page["id"]).Trim();
            if (id.Length == 0)
            {
                continue;
            }

            var title = GetText(page["title"]).Trim();
            result[id] = title.Length > 0 ? title : id;
        }

        return result;
    }

    private static string GetGroupName(
        JsonObject entry,
        IReadOnlyDictionary<string, string> pages,
        string url
    )
    {
        var pageRef = GetText(entry["pageref"]).Trim();
        if (pageRef.Length > 0 && pages.TryGetValue(pageRef, out var pageTitle))
        {
            return pageTitle;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host
            : "HAR 请求";
    }

    private static bool IsStatic(JsonObject entry, string url, string mimeType)
    {
        var resourceType = GetText(entry["_resourceType"]).Trim();
        if (resourceType.Length > 0)
        {
            return StaticResourceTypes.Contains(resourceType);
        }

        if (
            mimeType.StartsWith("image/", StringComparison.Ordinal)
            || mimeType.StartsWith("audio/", StringComparison.Ordinal)
            || mimeType.StartsWith("video/", StringComparison.Ordinal)
            || mimeType.StartsWith("font/", StringComparison.Ordinal)
            || mimeType.StartsWith("text/css", StringComparison.Ordinal)
            || mimeType.StartsWith("application/javascript", StringComparison.Ordinal)
        )
        {
            return true;
        }

        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.ToLowerInvariant()
            : url.ToLowerInvariant();
        return path.EndsWith(".js", StringComparison.Ordinal)
            || path.EndsWith(".css", StringComparison.Ordinal)
            || path.EndsWith(".png", StringComparison.Ordinal)
            || path.EndsWith(".jpg", StringComparison.Ordinal)
            || path.EndsWith(".jpeg", StringComparison.Ordinal)
            || path.EndsWith(".gif", StringComparison.Ordinal)
            || path.EndsWith(".svg", StringComparison.Ordinal)
            || path.EndsWith(".woff", StringComparison.Ordinal)
            || path.EndsWith(".woff2", StringComparison.Ordinal);
    }

    private static string GetResponseMimeType(JsonObject entry)
    {
        var response = entry["response"] as JsonObject;
        var content = response?["content"] as JsonObject;
        return GetMediaType(GetText(content?["mimeType"]));
    }

    private static string GetMediaType(string value)
    {
        var separator = value.IndexOf(';');
        return (separator >= 0 ? value[..separator] : value).Trim().ToLowerInvariant();
    }

    private static string BuildRequestName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "HAR 请求";
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return string.IsNullOrWhiteSpace(name) ? uri.Host : name;
    }

    private static string BuildRootName(string source)
    {
        var name = Path.GetFileNameWithoutExtension(source).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return "HAR 导入";
        }

        return name.Length > 80 ? name[..80] : name;
    }

    private static bool IsHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string NormalizeMethod(string method)
    {
        var normalized = method.Trim().ToUpperInvariant();
        return normalized.Length == 0 ? "GET" : normalized;
    }

    private static string RemoveQuery(string url)
    {
        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        var withoutFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        var queryIndex = withoutFragment.IndexOf('?');
        return (queryIndex >= 0 ? withoutFragment[..queryIndex] : withoutFragment) + fragment;
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
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

    private sealed class HarContext
    {
        public HarContext(WarningBag warnings)
        {
            Warnings = warnings;
        }

        public WarningBag Warnings { get; }

        public int InvalidEntryCount { get; set; }

        public int MultipartFileCount { get; set; }

        public bool CookieImported { get; set; }
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
