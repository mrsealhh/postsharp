using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PostSharp.Services;

/// <summary>
/// Minimal OpenAPI 3.x / Swagger 2.0 reader. It keeps the parts that matter for manual debugging — method,
/// path, server, query/header parameters and a JSON body sample — and groups operations by their first tag.
/// </summary>
public static class OpenApiImportService
{
    private const int MaxRequests = 500;
    private const int MaxSchemaDepth = 8;
    private const int MaxExternalReferences = 20;
    private const int MaxExternalReferenceBytes = 2 * 1024 * 1024;

    private static readonly string[] Methods =
    {
        "get",
        "post",
        "put",
        "patch",
        "delete",
        "head",
        "options",
    };

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonNodeOptions NodeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<OpenApiImportDocument> LoadFromUrlAsync(
        string url,
        CancellationToken cancellationToken = default,
        HttpClient? client = null
    )
    {
        if (
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw new InvalidOperationException(
                "请输入以 http:// 或 https:// 开头的 OpenAPI 文档地址。"
            );
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/yaml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/yaml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));

        using var response = await (client ?? Client)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"下载 OpenAPI 文档失败：HTTP {(int)response.StatusCode}。请确认地址可直接访问。"
            );
        }

        var content = await response
            .Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("下载到的 OpenAPI 文档为空。");
        }

        Log.Information("已下载 OpenAPI 文档 {Url}，大小 {Length} 字符", uri, content.Length);
        return await ParseWithExternalReferencesAsync(
                content,
                uri.ToString(),
                uri,
                cancellationToken,
                client
            )
            .ConfigureAwait(false);
    }

    public static async Task<OpenApiImportDocument> ParseWithExternalReferencesAsync(
        string content,
        string source,
        Uri? baseUri,
        CancellationToken cancellationToken = default,
        HttpClient? client = null
    )
    {
        var root =
            ParseDocument(content) as JsonObject
            ?? throw new InvalidOperationException("OpenAPI 文档的根节点必须是对象。");
        if (CountExternalReferences(root) == 0)
        {
            return Parse(content, source);
        }

        var warnings = new List<string>();
        var state = new ExternalReferenceState(client ?? Client, baseUri, warnings);
        await ResolveExternalReferencesAsync(root, state, baseUri, cancellationToken)
            .ConfigureAwait(false);
        if (state.BundleRoot["x-postsharp-external"] is JsonObject bundle)
        {
            root["x-postsharp-external"] = bundle.DeepClone();
        }

        return Parse(root.ToJsonString(), source, warnings);
    }

    public static OpenApiImportDocument Parse(
        string content,
        string source,
        IReadOnlyList<string>? extraWarnings = null
    )
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("OpenAPI 文档为空。");
        }

        var root = ParseDocument(content);
        if (root is not JsonObject document)
        {
            throw new InvalidOperationException("OpenAPI 文档的根节点必须是对象。");
        }

        var openApi = GetText(document["openapi"]);
        var swagger = GetText(document["swagger"]);
        if (string.IsNullOrWhiteSpace(openApi) && string.IsNullOrWhiteSpace(swagger))
        {
            throw new InvalidOperationException(
                "不是有效的 OpenAPI 文档：缺少 openapi（3.x）或 swagger（2.0）字段。"
            );
        }

        var isVersion3 = !string.IsNullOrWhiteSpace(openApi);
        var info = document["info"] as JsonObject;
        var title = GetText(info?["title"]);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "导入的接口";
        }

        var version = GetText(info?["version"]) ?? (isVersion3 ? openApi : swagger) ?? string.Empty;
        var servers = ResolveServers(document, isVersion3);
        var baseUrl = servers.Count > 0 ? servers[0].Url : "{{baseUrl}}";
        var warnings = new WarningBag();
        if (servers.Count > 1)
        {
            warnings.Add($"文档包含 {servers.Count} 个服务器，当前使用第一个服务器地址。");
        }

        var externalRefCount = CountExternalReferences(document);
        if (externalRefCount > 0)
        {
            warnings.Add(
                $"文档包含 {externalRefCount} 个外部 $ref，当前只解析本地 #/ 引用；相关参数或请求体可能不完整。"
            );
        }

        if (document["paths"] is not JsonObject paths)
        {
            throw new InvalidOperationException("OpenAPI 文档没有 paths，无法创建接口分组。");
        }

        var groups = new List<GroupBuilder>();
        var groupIndex = new Dictionary<string, GroupBuilder>(StringComparer.Ordinal);
        var total = 0;
        var multiTagCount = 0;
        var securityCount = 0;

        foreach (var pathEntry in paths)
        {
            if (pathEntry.Value is not JsonObject pathItem)
            {
                continue;
            }

            var resolvedPathItem = ResolveObject(pathItem, document);
            if (resolvedPathItem is null)
            {
                continue;
            }

            pathItem = resolvedPathItem;
            var pathParameters = pathItem["parameters"] as JsonArray;

            foreach (var method in Methods)
            {
                if (pathItem[method] is not JsonObject operation)
                {
                    continue;
                }

                if (total >= MaxRequests)
                {
                    Log.Warning(
                        "OpenAPI 导入达到上限 {MaxRequests} 个请求，后续操作已忽略",
                        MaxRequests
                    );
                    break;
                }

                var groupName = GetGroupName(operation);
                if (groupName is null)
                {
                    groupName = "未分组";
                }

                if (HasMultipleTags(operation))
                {
                    multiTagCount++;
                }

                if (!groupIndex.TryGetValue(groupName, out var group))
                {
                    group = new GroupBuilder(groupName);
                    groupIndex[groupName] = group;
                    groups.Add(group);
                }

                group.Requests.Add(
                    BuildRequest(
                        document,
                        pathEntry.Key,
                        method,
                        operation,
                        pathParameters,
                        baseUrl,
                        out var usedSecurity
                    )
                );
                if (usedSecurity)
                {
                    securityCount++;
                }

                total++;
            }
        }

        if (total == 0)
        {
            throw new InvalidOperationException("OpenAPI 文档没有可导入的操作（GET、POST 等）。");
        }

        if (multiTagCount > 0)
        {
            warnings.Add(
                $"{multiTagCount} 个操作包含多个 tag，当前按 x-apifox-folder 或第一个 tag 分组。"
            );
        }

        if (securityCount > 0)
        {
            warnings.Add(
                $"{securityCount} 个操作使用了安全方案，已添加占位认证头；请在请求中填写实际令牌。"
            );
        }

        Log.Information(
            "OpenAPI 文档解析完成 {Source}：{Title}，{RequestCount} 个请求，{GroupCount} 个分组",
            source,
            title,
            total,
            groups.Count
        );

        return new OpenApiImportDocument
        {
            Title = title.Trim(),
            Version = version,
            Source = source,
            Servers = servers,
            BaseUrl = baseUrl,
            Groups = groups
                .Select(group => new OpenApiImportGroup
                {
                    Name = group.Name,
                    Requests = group.Requests,
                })
                .ToList(),
            Warnings = warnings.ToList().Concat(extraWarnings ?? Array.Empty<string>()).ToList(),
        };
    }

    private static OpenApiImportRequest BuildRequest(
        JsonObject document,
        string path,
        string method,
        JsonObject operation,
        JsonArray? pathParameters,
        string baseUrl,
        out bool usedSecurity
    )
    {
        var query = new List<OpenApiImportField>();
        var headers = new List<OpenApiImportField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddParameters(document, pathParameters, query, headers, seen);
        AddParameters(document, operation["parameters"] as JsonArray, query, headers, seen);
        usedSecurity = AddSecurity(document, operation, query, headers, seen);

        var name = GetText(operation["summary"]) ?? GetText(operation["operationId"]);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"{method.ToUpperInvariant()} {path}";
        }

        var url = CombineUrl(baseUrl, path);
        var (bodyMode, body) = ResolveBody(document, operation);

        return new OpenApiImportRequest
        {
            Name = name.Trim(),
            Method = method.ToUpperInvariant(),
            Url = url,
            BodyMode = bodyMode,
            Body = body,
            Query = query,
            Headers = headers,
        };
    }

    private static void AddParameters(
        JsonObject document,
        JsonArray? parameters,
        List<OpenApiImportField> query,
        List<OpenApiImportField> headers,
        HashSet<string> seen
    )
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var node in parameters)
        {
            if (node is not JsonObject parameter)
            {
                continue;
            }

            var resolvedParameter = ResolveObject(parameter, document);
            if (resolvedParameter is null)
            {
                continue;
            }

            parameter = resolvedParameter;
            var location = GetText(parameter["in"]);
            var name = GetText(parameter["name"]);
            if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (location is not ("query" or "header"))
            {
                continue;
            }

            if (!seen.Add($"{location}:{name}"))
            {
                continue;
            }

            var value = ExtractParameterValue(document, parameter);
            if (location == "query")
            {
                query.Add(new OpenApiImportField(name, value));
            }
            else
            {
                headers.Add(new OpenApiImportField(name, value));
            }
        }
    }

    private static string ExtractParameterValue(JsonObject document, JsonObject parameter)
    {
        if (parameter["example"] is { } directExample)
        {
            return NodeToText(directExample);
        }

        if (parameter["examples"] is JsonObject examples)
        {
            foreach (var entry in examples)
            {
                if (entry.Value is JsonObject exampleObject && exampleObject["value"] is { } value)
                {
                    return NodeToText(value);
                }

                if (entry.Value is not null)
                {
                    return NodeToText(entry.Value);
                }
            }
        }

        var schema = ResolveObject(parameter["schema"], document);
        if (schema is not null)
        {
            var sample = BuildSample(schema, document, 0);
            if (sample is not null)
            {
                return NodeToText(sample);
            }
        }

        return string.Empty;
    }

    private static (string BodyMode, string Body) ResolveBody(
        JsonObject document,
        JsonObject operation
    )
    {
        var requestBody = ResolveObject(operation["requestBody"], document);
        if (requestBody?["content"] is JsonObject content)
        {
            var media = SelectMediaType(content);
            if (media is not null)
            {
                var mediaType = media.Value.Key;
                var mediaObject = media.Value.Value;
                var sample = ExtractMediaExample(mediaObject, document);
                if (sample is not null)
                {
                    var isJson =
                        mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                        || sample is JsonObject
                        || sample is JsonArray;
                    return isJson
                        ? ("Json", sample.ToJsonString(PrettyJson))
                        : ("Text", NodeToText(sample));
                }

                var schema = ResolveObject(mediaObject["schema"], document);
                var generated = schema is null ? null : BuildSample(schema, document, 0);
                if (generated is not null)
                {
                    return mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                        ? ("Json", generated.ToJsonString(PrettyJson))
                        : ("Text", NodeToText(generated));
                }
            }
        }

        var bodyParameters = operation["parameters"] as JsonArray;
        if (bodyParameters is not null)
        {
            foreach (var node in bodyParameters)
            {
                if (node is not JsonObject parameter)
                {
                    continue;
                }

                var resolvedParameter = ResolveObject(parameter, document);
                if (resolvedParameter is null)
                {
                    continue;
                }

                parameter = resolvedParameter;
                var location = GetText(parameter["in"]);
                var schema = ResolveObject(parameter["schema"], document);
                if (schema is null)
                {
                    continue;
                }

                var generated = BuildSample(schema, document, 0);
                if (generated is null)
                {
                    continue;
                }

                if (string.Equals(location, "body", StringComparison.OrdinalIgnoreCase))
                {
                    return ("Json", generated.ToJsonString(PrettyJson));
                }

                if (string.Equals(location, "formData", StringComparison.OrdinalIgnoreCase))
                {
                    return ("Text", NodeToText(generated));
                }
            }
        }

        return ("None", string.Empty);
    }

    private static KeyValuePair<string, JsonObject>? SelectMediaType(JsonObject content)
    {
        string[] preferred =
        {
            "application/json",
            "application/*+json",
            "text/json",
            "application/x-www-form-urlencoded",
            "text/plain",
            "multipart/form-data",
        };

        foreach (var mediaType in preferred)
        {
            foreach (var entry in content)
            {
                if (entry.Value is JsonObject mediaObject && MatchesMediaType(entry.Key, mediaType))
                {
                    return new KeyValuePair<string, JsonObject>(entry.Key, mediaObject);
                }
            }
        }

        foreach (var entry in content)
        {
            if (entry.Value is JsonObject mediaObject)
            {
                return new KeyValuePair<string, JsonObject>(entry.Key, mediaObject);
            }
        }

        return null;
    }

    private static bool MatchesMediaType(string actual, string preferred)
    {
        if (string.Equals(actual, preferred, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return preferred.EndsWith("+json", StringComparison.Ordinal)
            && actual.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? ExtractMediaExample(JsonObject media, JsonObject document)
    {
        if (media["example"] is { } example)
        {
            return example.DeepClone();
        }

        if (media["examples"] is JsonObject examples)
        {
            foreach (var entry in examples)
            {
                if (entry.Value is JsonObject exampleObject && exampleObject["value"] is { } value)
                {
                    return value.DeepClone();
                }

                if (entry.Value is not null && entry.Value is not JsonObject)
                {
                    return entry.Value.DeepClone();
                }
            }
        }

        var schema = ResolveObject(media["schema"], document);
        if (schema?["example"] is { } schemaExample)
        {
            return schemaExample.DeepClone();
        }

        return null;
    }

    private static JsonNode? BuildSample(JsonNode? node, JsonObject document, int depth)
    {
        if (node is null || depth > MaxSchemaDepth)
        {
            return null;
        }

        var schema = ResolveObject(node, document);
        if (schema is null)
        {
            return null;
        }

        if (schema["example"] is { } example)
        {
            return example.DeepClone();
        }

        if (schema["default"] is { } defaultValue)
        {
            return defaultValue.DeepClone();
        }

        if (schema["enum"] is JsonArray enumValues && enumValues.Count > 0)
        {
            return enumValues[0]?.DeepClone();
        }

        foreach (var combinator in new[] { "oneOf", "anyOf", "allOf" })
        {
            if (schema[combinator] is JsonArray candidates && candidates.Count > 0)
            {
                if (combinator == "allOf")
                {
                    var merged = new JsonObject();
                    foreach (var candidate in candidates)
                    {
                        if (BuildSample(candidate, document, depth + 1) is JsonObject part)
                        {
                            foreach (var property in part)
                            {
                                merged[property.Key] = property.Value?.DeepClone();
                            }
                        }
                    }

                    if (merged.Count > 0)
                    {
                        return merged;
                    }
                }
                else
                {
                    return BuildSample(candidates[0], document, depth + 1);
                }
            }
        }

        var type = GetText(schema["type"]);
        switch (type)
        {
            case "object":
                return BuildObjectSample(schema, document, depth);

            case "array":
                var item = BuildSample(schema["items"], document, depth + 1);
                return new JsonArray(item?.DeepClone());

            case "integer":
                return JsonValue.Create(0);

            case "number":
                return JsonValue.Create(0.0);

            case "boolean":
                return JsonValue.Create(false);

            case "string":
                return JsonValue.Create(SampleString(GetText(schema["format"])));

            default:
                if (schema["properties"] is JsonObject)
                {
                    return BuildObjectSample(schema, document, depth);
                }

                return null;
        }
    }

    private static JsonObject BuildObjectSample(JsonObject schema, JsonObject document, int depth)
    {
        var result = new JsonObject();
        if (schema["properties"] is not JsonObject properties)
        {
            return result;
        }

        var count = 0;
        foreach (var property in properties)
        {
            if (count++ >= 30)
            {
                break;
            }

            var sample = BuildSample(property.Value, document, depth + 1);
            if (sample is not null)
            {
                result[property.Key] = sample;
            }
        }

        return result;
    }

    private static string SampleString(string? format)
    {
        return format switch
        {
            "date-time" => "2026-01-01T00:00:00Z",
            "date" => "2026-01-01",
            "email" => "user@example.com",
            "uri" or "url" => "https://example.com",
            "uuid" => "00000000-0000-0000-0000-000000000000",
            "password" => "password",
            _ => "string",
        };
    }

    private static IReadOnlyList<OpenApiImportServer> ResolveServers(
        JsonObject document,
        bool isVersion3
    )
    {
        var servers = new List<OpenApiImportServer>();
        if (isVersion3 && document["servers"] is JsonArray items)
        {
            foreach (var item in items)
            {
                if (item is not JsonObject server)
                {
                    continue;
                }

                var url = ResolveServerUrl(server);
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                servers.Add(
                    new OpenApiImportServer(
                        servers.Count,
                        url,
                        (GetText(server["description"]) ?? string.Empty).Trim()
                    )
                );
            }
        }

        if (!isVersion3)
        {
            var host = GetText(document["host"]);
            if (!string.IsNullOrWhiteSpace(host))
            {
                var schemes = document["schemes"] as JsonArray;
                var scheme = schemes is { Count: > 0 } ? GetText(schemes[0]) : null;
                var basePath = GetText(document["basePath"]) ?? string.Empty;
                var prefix = string.IsNullOrWhiteSpace(scheme) ? "https" : scheme;
                servers.Add(
                    new OpenApiImportServer(
                        0,
                        NormalizeBaseUrl(
                            $"{prefix}://{host.TrimEnd('/')}/{basePath.TrimStart('/')}"
                        ),
                        "Swagger host"
                    )
                );
            }
        }

        return servers;
    }

    private static string ResolveServerUrl(JsonObject server)
    {
        var url = GetText(server["url"]);
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (server["variables"] is JsonObject variables)
        {
            foreach (var variable in variables)
            {
                if (variable.Value is JsonObject definition)
                {
                    var value = GetText(definition["default"]);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        url = url.Replace($"{{{variable.Key}}}", value, StringComparison.Ordinal);
                    }
                }
            }
        }

        return NormalizeBaseUrl(url);
    }

    private static string NormalizeBaseUrl(string url)
    {
        var trimmed = url.Trim();
        if (
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
        {
            return trimmed.TrimEnd('/');
        }

        if (trimmed.StartsWith('/'))
        {
            return "{{baseUrl}}" + trimmed.TrimEnd('/');
        }

        return "{{baseUrl}}/" + trimmed.TrimEnd('/');
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        var suffix = path.StartsWith('/') ? path : "/" + path;
        return baseUrl.TrimEnd('/') + suffix;
    }

    private static bool AddSecurity(
        JsonObject document,
        JsonObject operation,
        List<OpenApiImportField> query,
        List<OpenApiImportField> headers,
        HashSet<string> seen
    )
    {
        var security = operation["security"] as JsonArray ?? document["security"] as JsonArray;
        if (security is null || security.Count == 0)
        {
            return false;
        }

        if (security[0] is not JsonObject requirement)
        {
            return false;
        }

        var components = document["components"] as JsonObject;
        var schemes =
            components?["securitySchemes"] as JsonObject
            ?? document["securityDefinitions"] as JsonObject;
        if (schemes is null)
        {
            return false;
        }

        var applied = false;
        foreach (var entry in requirement)
        {
            var scheme = ResolveObject(schemes[entry.Key], document);
            if (scheme is null)
            {
                continue;
            }

            var type = (GetText(scheme["type"]) ?? string.Empty).Trim().ToLowerInvariant();
            if (type == "apikey")
            {
                var name = (GetText(scheme["name"]) ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                var location = (GetText(scheme["in"]) ?? string.Empty).Trim().ToLowerInvariant();
                var value = "{{" + entry.Key + "}}";
                if (location == "query")
                {
                    if (seen.Add($"query:{name}"))
                    {
                        query.Add(new OpenApiImportField(name, value));
                        applied = true;
                    }
                }
                else if (seen.Add($"header:{name}"))
                {
                    headers.Add(new OpenApiImportField(name, value));
                    applied = true;
                }

                continue;
            }

            if (type is "http" or "oauth2" or "openidconnect")
            {
                if (!seen.Add("header:Authorization"))
                {
                    continue;
                }

                var httpScheme = (GetText(scheme["scheme"]) ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                var value = type switch
                {
                    "http" when httpScheme == "basic" => "Basic {{basicAuth}}",
                    "http" when httpScheme is "" or "bearer" => "Bearer {{accessToken}}",
                    "http" => "{{accessToken}}",
                    _ => "Bearer {{accessToken}}",
                };
                headers.Add(new OpenApiImportField("Authorization", value));
                applied = true;
            }
        }

        return applied;
    }

    private static string? GetGroupName(JsonObject operation)
    {
        var apifoxFolder = (GetText(operation["x-apifox-folder"]) ?? string.Empty).Trim();
        if (apifoxFolder.Length > 0)
        {
            return apifoxFolder;
        }

        var tag = FirstTag(operation);
        return string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
    }

    private static bool HasMultipleTags(JsonObject operation)
    {
        if (operation["tags"] is not JsonArray tags)
        {
            return false;
        }

        return tags.Count(tag => (GetText(tag) ?? string.Empty).Trim().Length > 0) > 1;
    }

    private static async Task ResolveExternalReferencesAsync(
        JsonNode? node,
        ExternalReferenceState state,
        Uri? baseUri,
        CancellationToken cancellationToken
    )
    {
        if (node is JsonObject obj)
        {
            if (
                obj["$ref"] is JsonValue referenceValue
                && referenceValue.TryGetValue<string>(out var reference)
            )
            {
                var resolved = await ResolveExternalReferenceAsync(
                        reference,
                        state,
                        baseUri,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (resolved is not null)
                {
                    obj["$ref"] = resolved;
                }
            }

            foreach (var entry in obj.ToList())
            {
                if (entry.Key == "$ref")
                {
                    continue;
                }

                await ResolveExternalReferencesAsync(entry.Value, state, baseUri, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                await ResolveExternalReferencesAsync(item, state, baseUri, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<string?> ResolveExternalReferenceAsync(
        string reference,
        ExternalReferenceState state,
        Uri? baseUri,
        CancellationToken cancellationToken
    )
    {
        if (reference.StartsWith('#') || reference.Trim().Length == 0)
        {
            return null;
        }

        var hashIndex = reference.IndexOf('#');
        var location = hashIndex < 0 ? reference : reference[..hashIndex];
        var fragment = hashIndex < 0 ? string.Empty : reference[hashIndex..];
        if (location.Length == 0)
        {
            return null;
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri))
        {
            if (baseUri is null || !Uri.TryCreate(baseUri, location, out uri))
            {
                state.Warnings.Add($"已跳过无法解析的外部引用：{reference}");
                return null;
            }
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            state.Warnings.Add($"已跳过非 HTTP 外部引用：{uri}");
            return null;
        }

        var uriKey = uri.ToString();
        if (state.Keys.TryGetValue(uriKey, out var existingKey))
        {
            return BuildExternalReference(existingKey, fragment);
        }

        if (state.ReferenceCount >= MaxExternalReferences)
        {
            if (!state.LimitWarningAdded)
            {
                state.Warnings.Add($"外部 $ref 超过 {MaxExternalReferences} 个，剩余引用未下载。");
                state.LimitWarningAdded = true;
            }

            return null;
        }

        if (!state.Visited.Add(uriKey))
        {
            state.Warnings.Add($"检测到循环外部引用，已跳过：{uri}");
            return null;
        }

        state.ReferenceCount++;
        var key = $"external{state.ReferenceCount}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("application/yaml");
            request.Headers.Accept.ParseAdd("text/yaml");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await state
                .Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            }

            var bytes = await response
                .Content.ReadAsByteArrayAsync(timeout.Token)
                .ConfigureAwait(false);
            if (bytes.Length > MaxExternalReferenceBytes)
            {
                state.Warnings.Add($"外部引用超过 2 MB，已跳过：{uri}");
                return null;
            }

            var externalRoot =
                ParseDocument(Encoding.UTF8.GetString(bytes)) as JsonObject
                ?? throw new InvalidOperationException("外部引用不是对象。");
            await ResolveExternalReferencesAsync(externalRoot, state, uri, cancellationToken)
                .ConfigureAwait(false);
            var bundle = (JsonObject)state.BundleRoot["x-postsharp-external"]!;
            bundle[key] = externalRoot;
            state.Keys[uriKey] = key;
            Log.Information("已下载 OpenAPI 外部引用 {Url}", uri);
            return BuildExternalReference(key, fragment);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            state.Warnings.Add($"外部引用下载超时，已跳过：{uri}");
            Log.Warning(exception, "下载 OpenAPI 外部引用超时 {Url}", uri);
            return null;
        }
        catch (Exception exception)
        {
            state.Warnings.Add($"外部引用下载失败，已跳过：{uri}");
            Log.Warning(exception, "下载 OpenAPI 外部引用失败 {Url}", uri);
            return null;
        }
    }

    private static string BuildExternalReference(string key, string fragment)
    {
        var suffix = fragment.StartsWith('#') ? fragment[1..] : fragment;
        return $"#/x-postsharp-external/{key}{suffix}";
    }

    private sealed class ExternalReferenceState(
        HttpClient client,
        Uri? baseUri,
        List<string> warnings
    )
    {
        public HttpClient Client { get; } = client;

        public Uri? BaseUri { get; } = baseUri;

        public List<string> Warnings { get; } = warnings;

        public JsonObject BundleRoot { get; } =
            new() { ["x-postsharp-external"] = new JsonObject() };

        public Dictionary<string, string> Keys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);

        public int ReferenceCount { get; set; }

        public bool LimitWarningAdded { get; set; }
    }

    private static int CountExternalReferences(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var count = 0;
                var reference = (GetText(obj["$ref"]) ?? string.Empty).Trim();
                if (reference.Length > 0 && !reference.StartsWith("#/", StringComparison.Ordinal))
                {
                    count++;
                }

                foreach (var entry in obj)
                {
                    count += CountExternalReferences(entry.Value);
                }

                return count;
            }
            case JsonArray array:
                return array.Sum(CountExternalReferences);
            default:
                return 0;
        }
    }

    private static string? FirstTag(JsonObject operation)
    {
        if (operation["tags"] is JsonArray tags)
        {
            foreach (var tag in tags)
            {
                var value = GetText(tag);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static JsonObject? ResolveObject(JsonNode? node, JsonObject document)
    {
        if (node is not JsonObject current)
        {
            return null;
        }

        return ResolveReference(current, document, 0);
    }

    private static JsonObject ResolveReference(JsonObject node, JsonObject document, int depth)
    {
        var reference = GetText(node["$ref"]);
        if (string.IsNullOrWhiteSpace(reference) || depth > 12)
        {
            return node;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return node;
        }

        JsonNode? target = document;
        foreach (var segment in reference[2..].Split('/'))
        {
            var key = segment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            target = target?[key];
        }

        return target is JsonObject targetObject
            ? ResolveReference(targetObject, document, depth + 1)
            : node;
    }

    private static JsonNode ParseDocument(string content)
    {
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return JsonNode.Parse(
                        content,
                        NodeOptions,
                        new JsonDocumentOptions
                        {
                            AllowTrailingCommas = true,
                            CommentHandling = JsonCommentHandling.Skip,
                        }
                    ) ?? throw new InvalidOperationException("OpenAPI JSON 文档为空。");
            }
            catch (JsonException exception)
            {
                Log.Debug(exception, "OpenAPI 内容不是合法 JSON，尝试按 YAML 解析");
            }
        }

        return ParseYaml(content);
    }

    private static JsonNode ParseYaml(string content)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(content);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
            {
                throw new InvalidOperationException("OpenAPI YAML 文档为空。");
            }

            return ConvertYamlNode(stream.Documents[0].RootNode)
                ?? throw new InvalidOperationException("OpenAPI YAML 文档没有根节点。");
        }
        catch (YamlException exception)
        {
            Log.Error(exception, "OpenAPI YAML 解析失败");
            throw new InvalidOperationException(
                "无法解析 OpenAPI 文档，请确认文件是合法的 JSON 或 YAML。"
            );
        }
    }

    private static JsonNode? ConvertYamlNode(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                var result = new JsonObject();
                foreach (var entry in mapping.Children)
                {
                    var key = entry.Key.ToString();
                    result[key] = ConvertYamlNode(entry.Value);
                }

                return result;

            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (var child in sequence.Children)
                {
                    array.Add(ConvertYamlNode(child));
                }

                return array;

            case YamlScalarNode scalar:
                return ConvertScalar(scalar);

            default:
                return null;
        }
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        if (scalar.Value is null)
        {
            return null;
        }

        if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain)
        {
            if (bool.TryParse(scalar.Value, out var boolValue))
            {
                return JsonValue.Create(boolValue);
            }

            if (long.TryParse(scalar.Value, out var longValue))
            {
                return JsonValue.Create(longValue);
            }

            if (
                double.TryParse(
                    scalar.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var doubleValue
                )
            )
            {
                return JsonValue.Create(doubleValue);
            }
        }

        return JsonValue.Create(scalar.Value);
    }

    private static string? GetText(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }

    private static string NodeToText(JsonNode node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node.ToJsonString();
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

    private sealed class GroupBuilder
    {
        public GroupBuilder(string name) => Name = name;

        public string Name { get; }

        public List<OpenApiImportRequest> Requests { get; } = new();
    }
}
