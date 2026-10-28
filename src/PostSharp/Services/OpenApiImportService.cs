using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PostSharp.Services;

/// <summary>Result of downloading an OpenAPI document, including the cache validators used for re-sync.</summary>
public sealed record OpenApiFetchResult(
    OpenApiImportDocument? Document,
    bool NotModified,
    string ETag,
    string LastModified,
    string ContentHash,
    long ContentBytes
);

/// <summary>Progress reported while downloading and parsing a large OpenAPI document.</summary>
public sealed record OpenApiFetchProgress(string Stage, double? Percent);

/// <summary>
/// Minimal OpenAPI 3.x / Swagger 2.0 reader. It keeps the parts that matter for manual debugging — method,
/// path, server, query/header parameters and a JSON body sample — and groups operations by their first tag.
/// </summary>
public static class OpenApiImportService
{
    // Keep a recursion guard for cyclic/self-referencing schemas, but do not cap
    // the number of operations, external references, fields or sample size.
    private const int MaxSchemaDepth = 4096;

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
        MaxDepth = 4096,
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 4096,
    };

    private static readonly JsonNodeOptions NodeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<OpenApiImportDocument> LoadFromUrlAsync(
        string url,
        CancellationToken cancellationToken = default,
        HttpClient? client = null,
        IProgress<OpenApiFetchProgress>? progress = null
    ) =>
        (
            await FetchAsync(url, null, null, cancellationToken, client, progress)
                .ConfigureAwait(false)
        ).Document
        ?? throw new InvalidOperationException("OpenAPI 文档没有返回内容。");

    public static async Task<OpenApiFetchResult> FetchAsync(
        string url,
        string? etag,
        string? lastModified,
        CancellationToken cancellationToken = default,
        HttpClient? client = null,
        IProgress<OpenApiFetchProgress>? progress = null,
        string? knownContentHash = null
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
        if (
            !string.IsNullOrWhiteSpace(etag)
            && EntityTagHeaderValue.TryParse(etag, out var cachedTag)
        )
        {
            request.Headers.IfNoneMatch.Add(cachedTag);
        }

        if (
            !string.IsNullOrWhiteSpace(lastModified)
            && DateTimeOffset.TryParse(lastModified, out var cachedModified)
        )
        {
            request.Headers.IfModifiedSince = cachedModified;
        }

        using var response = await (client ?? Client)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        var responseTag = response.Headers.ETag?.ToString() ?? etag ?? string.Empty;
        var responseModified =
            response.Content.Headers.LastModified?.ToString("R")
            ?? (!string.IsNullOrWhiteSpace(lastModified) ? lastModified : string.Empty);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            Log.Information("OpenAPI 文档未更新 {Url}", uri);
            return new OpenApiFetchResult(
                null,
                true,
                responseTag,
                responseModified,
                string.Empty,
                0
            );
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"下载 OpenAPI 文档失败：HTTP {(int)response.StatusCode}。请确认地址可直接访问。"
            );
        }

        var totalBytes = response.Content.Headers.ContentLength;
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"postsharp-openapi-{Guid.NewGuid():N}.json"
        );
        try
        {
            long received = 0;
            var lastReportedPercent = -1d;
            progress?.Report(new OpenApiFetchProgress("正在下载 OpenAPI 文档…", null));
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (
                var input = await response
                    .Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            await using (var output = File.Create(temporaryPath))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await input
                        .ReadAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await output
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read);
                    received += read;
                    var percent = totalBytes is > 0
                        ? Math.Clamp(received * 100d / totalBytes.Value, 0, 100)
                        : (double?)null;
                    if (
                        percent is { } value
                        && (Math.Abs(value - lastReportedPercent) >= 1 || value >= 100)
                    )
                    {
                        lastReportedPercent = value;
                        progress?.Report(
                            new OpenApiFetchProgress(
                                $"正在下载 OpenAPI 文档… {FormatBytes(received)}",
                                value
                            )
                        );
                    }
                    else if (percent is null && received % (1024 * 1024) < read)
                    {
                        progress?.Report(
                            new OpenApiFetchProgress(
                                $"正在下载 OpenAPI 文档… {FormatBytes(received)}",
                                null
                            )
                        );
                    }
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var hash = Convert.ToHexString(hasher.GetHashAndReset(), 0, 16);
            if (received == 0)
            {
                throw new InvalidOperationException("下载到的 OpenAPI 文档为空。");
            }

            if (
                !string.IsNullOrWhiteSpace(knownContentHash)
                && string.Equals(knownContentHash, hash, StringComparison.Ordinal)
            )
            {
                // 内容没变就别解析：2MB 的文档会展开成上千个请求对象与 schema 文档，纯属浪费。
                Log.Information("OpenAPI 文档内容未变化，跳过解析 {Url} {Hash}", uri, hash);
                return new OpenApiFetchResult(
                    null,
                    true,
                    responseTag,
                    responseModified,
                    hash,
                    received
                );
            }

            var isLarge = received >= BackgroundImportThresholdBytes;
            Log.Information(
                "已下载 OpenAPI 文档 {Url}，大小 {Bytes} 字节，指纹 {Hash}",
                uri,
                received,
                hash
            );
            progress?.Report(
                new OpenApiFetchProgress(
                    isLarge
                        ? $"内容过多，已启用后台导入… {FormatBytes(received)}"
                        : "正在解析 OpenAPI 文档…",
                    null
                )
            );

            JsonObject root;
            if (await IsJsonFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false))
            {
                await using var input = File.OpenRead(temporaryPath);
                root =
                    JsonNode.Parse(
                        input,
                        NodeOptions,
                        new JsonDocumentOptions
                        {
                            AllowTrailingCommas = true,
                            CommentHandling = JsonCommentHandling.Skip,
                            MaxDepth = 4096,
                        }
                    ) as JsonObject
                    ?? throw new InvalidOperationException("OpenAPI 文档的根节点必须是对象。");
            }
            else
            {
                var content = await File.ReadAllTextAsync(temporaryPath, cancellationToken)
                    .ConfigureAwait(false);
                root =
                    ParseDocument(content) as JsonObject
                    ?? throw new InvalidOperationException("OpenAPI 文档的根节点必须是对象。");
            }

            var document = await ParseWithExternalReferencesAsync(
                    root,
                    uri.ToString(),
                    uri,
                    cancellationToken,
                    client,
                    responseTag,
                    responseModified,
                    hash
                )
                .ConfigureAwait(false);
            return new OpenApiFetchResult(
                document,
                false,
                responseTag,
                responseModified,
                hash,
                received
            );
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private const long BackgroundImportThresholdBytes = 8 * 1024 * 1024;

    private static async Task<bool> IsJsonFileAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = File.OpenRead(path);
        var buffer = new byte[16];
        var count = await stream
            .ReadAsync(buffer.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        var index = 0;
        if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            index = 3;
        }

        while (index < count && char.IsWhiteSpace((char)buffer[index]))
        {
            index++;
        }

        return index < count && buffer[index] is (byte)'{' or (byte)'[';
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):0.0} MB"
            : $"{Math.Max(1, bytes / 1024)} KB";

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "删除临时 OpenAPI 文件失败 {Path}", path);
        }
    }

    public static async Task<OpenApiImportDocument> ParseWithExternalReferencesAsync(
        string content,
        string source,
        Uri? baseUri,
        CancellationToken cancellationToken = default,
        HttpClient? client = null,
        string? etag = null,
        string? lastModified = null,
        string? contentHash = null
    )
    {
        var root =
            ParseDocument(content) as JsonObject
            ?? throw new InvalidOperationException("OpenAPI 文档的根节点必须是对象。");
        return await ParseWithExternalReferencesAsync(
                root,
                source,
                baseUri,
                cancellationToken,
                client,
                etag,
                lastModified,
                contentHash
            )
            .ConfigureAwait(false);
    }

    public static async Task<OpenApiImportDocument> ParseWithExternalReferencesAsync(
        JsonObject root,
        string source,
        Uri? baseUri,
        CancellationToken cancellationToken = default,
        HttpClient? client = null,
        string? etag = null,
        string? lastModified = null,
        string? contentHash = null
    )
    {
        if (CountExternalReferences(root) == 0)
        {
            return ParseRoot(root, source, null, etag, lastModified, contentHash);
        }

        var warnings = new List<string>();
        var state = new ExternalReferenceState(client ?? Client, baseUri, warnings);
        await ResolveExternalReferencesAsync(root, state, baseUri, cancellationToken)
            .ConfigureAwait(false);
        if (state.BundleRoot["x-postsharp-external"] is JsonObject bundle)
        {
            root["x-postsharp-external"] = bundle.DeepClone();
        }

        return ParseRoot(root, source, warnings, etag, lastModified, contentHash);
    }

    public static OpenApiImportDocument Parse(
        string content,
        string source,
        IReadOnlyList<string>? extraWarnings = null,
        string? etag = null,
        string? lastModified = null,
        string? contentHash = null
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

        return ParseRoot(document, source, extraWarnings, etag, lastModified, contentHash);
    }

    private static OpenApiImportDocument ParseRoot(
        JsonObject document,
        string source,
        IReadOnlyList<string>? extraWarnings = null,
        string? etag = null,
        string? lastModified = null,
        string? contentHash = null
    )
    {
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
                        pathItem,
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
            Description = (GetText(info?["description"]) ?? string.Empty).Trim(),
            Servers = servers,
            BaseUrl = baseUrl,
            ETag = etag,
            LastModifiedHeader = lastModified,
            ContentHash = contentHash,
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
        JsonObject pathItem,
        string path,
        string method,
        JsonObject operation,
        JsonArray? pathParameters,
        string baseUrl,
        out bool usedSecurity
    )
    {
        var parameters = new List<OpenApiParameterDoc>();
        var query = new List<OpenApiImportField>();
        var headers = new List<OpenApiImportField>();
        var pathValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Operation-level parameters override path-level ones with the same name and location.
        CollectParameters(
            document,
            operation["parameters"] as JsonArray,
            parameters,
            query,
            headers,
            pathValues,
            seen
        );
        CollectParameters(document, pathParameters, parameters, query, headers, pathValues, seen);
        usedSecurity = AddSecurity(document, operation, query, headers, seen);

        var summary = (GetText(operation["summary"]) ?? string.Empty).Trim();
        var operationId = (GetText(operation["operationId"]) ?? string.Empty).Trim();
        var generatedName =
            summary.Length > 0 ? summary
            : operationId.Length > 0 ? operationId
            : $"{method.ToUpperInvariant()} {path}";
        var description = (GetText(operation["description"]) ?? string.Empty).Trim();
        var url = CombineUrl(baseUrl, path);
        foreach (var (name, value) in pathValues)
        {
            if (value.Length > 0)
            {
                url = url.Replace("{" + name + "}", value, StringComparison.Ordinal);
            }
        }

        var body = ResolveBodyDetails(document, operation);
        if (body.ContentType is not null && !HasHeader(headers, "Content-Type"))
        {
            headers.Add(new OpenApiImportField("Content-Type", body.ContentType));
        }

        var responseDocs = BuildResponses(document, operation);
        var doc = new OpenApiOperationDoc
        {
            Method = method.ToUpperInvariant(),
            Path = path,
            OperationId = operationId,
            Summary = summary,
            Description = description,
            Deprecated = ReadBool(operation["deprecated"]),
            TagsText = string.Join(" · ", ReadTags(operation)),
            ServersText = ResolveOperationServers(document, pathItem, operation, baseUrl),
            SecurityText = BuildSecurityText(document, operation),
            GeneratedName = generatedName,
            Url = url,
            Parameters = parameters,
            RequestBody = body.Doc,
            Responses = responseDocs,
        };

        return new OpenApiImportRequest
        {
            Name = generatedName,
            Method = method.ToUpperInvariant(),
            Path = path,
            Url = url,
            BodyMode = body.BodyMode,
            Body = body.Body,
            Query = query,
            Headers = headers,
            FormFields = body.FormFields,
            Doc = doc,
        };
    }

    private static void CollectParameters(
        JsonObject document,
        JsonArray? parameters,
        List<OpenApiParameterDoc> docs,
        List<OpenApiImportField> query,
        List<OpenApiImportField> headers,
        Dictionary<string, string> pathValues,
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
            var location = (GetText(parameter["in"]) ?? string.Empty).Trim();
            var name = (GetText(parameter["name"]) ?? string.Empty).Trim();
            if (location.Length == 0 || name.Length == 0)
            {
                continue;
            }

            if (!seen.Add($"{location}:{name}"))
            {
                continue;
            }

            var value = ExtractParameterValue(document, parameter);
            var schema = ResolveObject(parameter["schema"], document) ?? parameter;
            docs.Add(
                new OpenApiParameterDoc
                {
                    Name = name,
                    Location = location,
                    Type = SchemaTypeText(schema, document),
                    Required = ReadBool(parameter["required"]),
                    Description = (GetText(parameter["description"]) ?? string.Empty).Trim(),
                    Example = value,
                    EnumText = ReadEnumText(schema),
                }
            );

            switch (location)
            {
                case "query":
                    query.Add(new OpenApiImportField(name, value));
                    break;
                case "header":
                    headers.Add(new OpenApiImportField(name, value));
                    break;
                case "path":
                    pathValues[name] = value;
                    break;
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

        var schema = ResolveObject(parameter["schema"], document) ?? parameter;
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

    private sealed record BodyResolution(
        string BodyMode,
        string Body,
        string? ContentType,
        IReadOnlyList<OpenApiImportField> FormFields,
        OpenApiBodyDoc? Doc
    );

    private static BodyResolution ResolveBodyDetails(JsonObject document, JsonObject operation)
    {
        var requestBody = ResolveObject(operation["requestBody"], document);
        if (requestBody?["content"] is JsonObject content && SelectMediaType(content) is { } media)
        {
            var mediaType = media.Key;
            var mediaObject = media.Value;
            var schema = ResolveObject(mediaObject["schema"], document);
            var sample =
                ExtractMediaExample(mediaObject, document) ?? BuildSample(schema, document, 0);
            var fields = CollectSchemaFields(schema, document);
            var sampleText = BuildSampleText(sample, mediaType);
            var doc = new OpenApiBodyDoc
            {
                MediaType = mediaType,
                Required = ReadBool(requestBody["required"]),
                Sample = sampleText,
                Body = sampleText,
                Fields = fields,
            };

            if (IsFormUrlEncoded(mediaType))
            {
                var encodedFields = BuildFormFields(schema, document);
                var encodedBody = EncodeFormBody(encodedFields);
                doc.Sample = encodedBody;
                doc.Body = encodedBody;
                return new BodyResolution(
                    "Text",
                    encodedBody,
                    "application/x-www-form-urlencoded",
                    Array.Empty<OpenApiImportField>(),
                    doc
                );
            }

            if (IsMultipart(mediaType))
            {
                doc.Body = string.Empty;
                var multipartFields = BuildFormFields(schema, document);
                return new BodyResolution("Multipart", string.Empty, null, multipartFields, doc);
            }

            if (IsJsonMediaType(mediaType))
            {
                return new BodyResolution(
                    "Json",
                    sampleText,
                    null,
                    Array.Empty<OpenApiImportField>(),
                    doc
                );
            }

            return new BodyResolution(
                "Text",
                sampleText,
                mediaType,
                Array.Empty<OpenApiImportField>(),
                doc
            );
        }

        var bodyParameters = operation["parameters"] as JsonArray;
        if (bodyParameters is null)
        {
            return new BodyResolution(
                "None",
                string.Empty,
                null,
                Array.Empty<OpenApiImportField>(),
                null
            );
        }

        var consumes = ReadMediaTypes(operation["consumes"] ?? document["consumes"]);
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

            var location = (GetText(resolvedParameter["in"]) ?? string.Empty).Trim();
            var schema = ResolveObject(resolvedParameter["schema"], document);
            if (schema is null)
            {
                continue;
            }

            if (string.Equals(location, "body", StringComparison.OrdinalIgnoreCase))
            {
                var mediaType =
                    consumes.FirstOrDefault(IsJsonMediaType)
                    ?? consumes.FirstOrDefault()
                    ?? "application/json";
                var sample = BuildSample(schema, document, 0);
                var bodyText = BuildSampleText(sample, mediaType);
                return new BodyResolution(
                    IsJsonMediaType(mediaType) ? "Json" : "Text",
                    bodyText,
                    IsJsonMediaType(mediaType) ? null : mediaType,
                    Array.Empty<OpenApiImportField>(),
                    new OpenApiBodyDoc
                    {
                        MediaType = mediaType,
                        Required = ReadBool(resolvedParameter["required"]),
                        Sample = bodyText,
                        Body = bodyText,
                        Fields = CollectSchemaFields(schema, document),
                    }
                );
            }
        }

        var formParameters = bodyParameters
            .OfType<JsonObject>()
            .Select(parameter => ResolveObject(parameter, document))
            .Where(parameter => parameter is not null)
            .Select(parameter => parameter!)
            .Where(parameter =>
                string.Equals(
                    (GetText(parameter["in"]) ?? string.Empty).Trim(),
                    "formData",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToList();
        if (formParameters.Count == 0)
        {
            return new BodyResolution(
                "None",
                string.Empty,
                null,
                Array.Empty<OpenApiImportField>(),
                null
            );
        }

        var isMultipart =
            formParameters.Any(parameter =>
                string.Equals(
                    (GetText(parameter["type"]) ?? string.Empty).Trim(),
                    "file",
                    StringComparison.OrdinalIgnoreCase
                )
            ) || consumes.Any(IsMultipart);
        var formFields = new List<OpenApiImportField>();
        var fieldDocs = new List<OpenApiSchemaFieldDoc>();
        foreach (var parameter in formParameters)
        {
            var name = (GetText(parameter["name"]) ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var value = ExtractParameterValue(document, parameter);
            formFields.Add(new OpenApiImportField(name, value));
            fieldDocs.Add(
                new OpenApiSchemaFieldDoc
                {
                    Name = name,
                    Type = (GetText(parameter["type"]) ?? "string").Trim(),
                    Required = ReadBool(parameter["required"]),
                    Description = (GetText(parameter["description"]) ?? string.Empty).Trim(),
                    Example = value,
                }
            );
        }

        var formMediaType = isMultipart
            ? "multipart/form-data"
            : "application/x-www-form-urlencoded";
        var formDoc = new OpenApiBodyDoc
        {
            MediaType = formMediaType,
            Required = formParameters.Any(parameter => ReadBool(parameter["required"])),
            Sample = isMultipart ? string.Empty : EncodeFormBody(formFields),
            Body = isMultipart ? string.Empty : EncodeFormBody(formFields),
            Fields = fieldDocs,
        };
        return isMultipart
            ? new BodyResolution("Multipart", string.Empty, null, formFields, formDoc)
            : new BodyResolution(
                "Text",
                EncodeFormBody(formFields),
                formMediaType,
                Array.Empty<OpenApiImportField>(),
                formDoc
            );
    }

    private static IReadOnlyList<OpenApiSchemaFieldDoc> CollectSchemaFields(
        JsonObject? schema,
        JsonObject document
    )
    {
        var result = new List<OpenApiSchemaFieldDoc>();
        if (schema is null)
        {
            return result;
        }

        CollectSchemaFields(
            schema,
            document,
            string.Empty,
            false,
            result,
            0,
            new HashSet<JsonObject>(ReferenceEqualityComparer.Instance)
        );
        return result
            .GroupBy(field => field.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static void CollectSchemaFields(
        JsonNode? node,
        JsonObject document,
        string prefix,
        bool required,
        List<OpenApiSchemaFieldDoc> target,
        int depth,
        HashSet<JsonObject> visiting
    )
    {
        if (depth > MaxSchemaDepth)
        {
            return;
        }

        var schema = ResolveObject(node, document);
        if (schema is null || !visiting.Add(schema))
        {
            return;
        }

        try
        {
            if (schema["allOf"] is JsonArray allOf)
            {
                foreach (var candidate in allOf)
                {
                    CollectSchemaFields(
                        candidate,
                        document,
                        prefix,
                        required,
                        target,
                        depth + 1,
                        visiting
                    );
                }
            }

            var requiredNames = ReadRequiredNames(schema);
            if (schema["properties"] is JsonObject properties)
            {
                foreach (var property in properties)
                {
                    var name = prefix.Length == 0 ? property.Key : $"{prefix}.{property.Key}";
                    var propertySchema = ResolveObject(property.Value, document);
                    if (propertySchema is null)
                    {
                        continue;
                    }

                    var propertyRequired = required || requiredNames.Contains(property.Key);
                    var type = (GetText(propertySchema["type"]) ?? string.Empty)
                        .Trim()
                        .ToLowerInvariant();
                    target.Add(CreateFieldDoc(name, propertySchema, propertyRequired, document));

                    if (type == "object" || propertySchema["properties"] is JsonObject)
                    {
                        CollectSchemaFields(
                            propertySchema,
                            document,
                            name,
                            propertyRequired,
                            target,
                            depth + 1,
                            visiting
                        );
                    }
                    else if (type == "array" && propertySchema["items"] is { } items)
                    {
                        CollectSchemaFields(
                            items,
                            document,
                            name + "[]",
                            propertyRequired,
                            target,
                            depth + 1,
                            visiting
                        );
                    }
                }

                return;
            }

            if (schema["items"] is { } arrayItems)
            {
                CollectSchemaFields(
                    arrayItems,
                    document,
                    prefix.Length == 0 ? "[]" : prefix,
                    required,
                    target,
                    depth + 1,
                    visiting
                );
                return;
            }

            if (prefix.Length > 0)
            {
                target.Add(CreateFieldDoc(prefix, schema, required, document));
            }
        }
        finally
        {
            visiting.Remove(schema);
        }
    }

    private static OpenApiSchemaFieldDoc CreateFieldDoc(
        string name,
        JsonObject schema,
        bool required,
        JsonObject document
    ) =>
        new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? "body" : name,
            Type = SchemaTypeText(schema, document),
            Required = required,
            Description = (GetText(schema["description"]) ?? string.Empty).Trim(),
            Example = schema["example"] is { } example ? NodeToText(example) : string.Empty,
            EnumText = ReadEnumText(schema),
        };

    private static IReadOnlyList<OpenApiResponseDoc> BuildResponses(
        JsonObject document,
        JsonObject operation
    )
    {
        if (operation["responses"] is not JsonObject responses)
        {
            return Array.Empty<OpenApiResponseDoc>();
        }

        var result = new List<OpenApiResponseDoc>();
        foreach (var entry in OrderResponses(responses))
        {
            var resolved = ResolveObject(entry.Value, document);
            if (resolved is null)
            {
                continue;
            }

            var samples = new List<OpenApiResponseSampleDoc>();
            var fields = new List<OpenApiSchemaFieldDoc>();
            if (resolved["content"] is JsonObject content)
            {
                foreach (var media in content)
                {
                    if (media.Value is not JsonObject mediaObject)
                    {
                        continue;
                    }

                    var schema = ResolveObject(mediaObject["schema"], document);
                    var sample =
                        ExtractMediaExample(mediaObject, document)
                        ?? BuildSample(schema, document, 0);
                    samples.Add(
                        new OpenApiResponseSampleDoc
                        {
                            MediaType = media.Key,
                            Sample = BuildSampleText(sample, media.Key),
                        }
                    );
                    if (fields.Count == 0)
                    {
                        fields.AddRange(CollectSchemaFields(schema, document));
                    }
                }
            }
            else
            {
                var schema = ResolveObject(resolved["schema"], document);
                var sample = BuildSample(schema, document, 0);
                if (sample is not null)
                {
                    samples.Add(
                        new OpenApiResponseSampleDoc
                        {
                            MediaType = "application/json",
                            Sample = BuildSampleText(sample, "application/json"),
                        }
                    );
                }

                if (resolved["examples"] is JsonObject examples)
                {
                    foreach (var example in examples)
                    {
                        samples.Add(
                            new OpenApiResponseSampleDoc
                            {
                                MediaType = example.Key,
                                Sample = NodeToText(
                                    example.Value is JsonObject exampleObject
                                    && exampleObject["value"] is { } value
                                        ? value
                                        : example.Value ?? JsonValue.Create(string.Empty)!
                                ),
                            }
                        );
                    }
                }

                fields.AddRange(CollectSchemaFields(schema, document));
            }

            result.Add(
                new OpenApiResponseDoc
                {
                    StatusCode = entry.Key,
                    Description = (GetText(resolved["description"]) ?? string.Empty).Trim(),
                    HeadersText = ReadResponseHeaders(document, resolved),
                    Samples = samples,
                    Fields = fields,
                }
            );
        }

        return result;
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> OrderResponses(JsonObject responses)
    {
        var ordered = responses
            .Where(entry => int.TryParse(entry.Key, out _))
            .OrderBy(entry =>
                int.Parse(entry.Key, System.Globalization.CultureInfo.InvariantCulture)
            );
        return ordered
            .Concat(
                responses.Where(entry =>
                    !int.TryParse(entry.Key, out _)
                    && !string.Equals(entry.Key, "default", StringComparison.OrdinalIgnoreCase)
                )
            )
            .Concat(
                responses.Where(entry =>
                    string.Equals(entry.Key, "default", StringComparison.OrdinalIgnoreCase)
                )
            );
    }

    private static string ReadResponseHeaders(JsonObject document, JsonObject response)
    {
        if (response["headers"] is not JsonObject headers)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var header in headers)
        {
            var resolved = ResolveObject(header.Value, document);
            if (resolved is null)
            {
                continue;
            }

            var schema = ResolveObject(resolved["schema"], document);
            var type = SchemaTypeText(schema, document);
            if (type.Length == 0)
            {
                type = (GetText(resolved["type"]) ?? string.Empty).Trim();
            }
            var description = (GetText(resolved["description"]) ?? string.Empty).Trim();
            lines.Add(
                description.Length == 0
                    ? $"{header.Key} ({type})"
                    : $"{header.Key} ({type})：{description}"
            );
        }

        return string.Join("；", lines);
    }

    private static IReadOnlyList<OpenApiImportField> BuildFormFields(
        JsonObject? schema,
        JsonObject document
    )
    {
        var fields = new List<OpenApiImportField>();
        if (schema?["properties"] is not JsonObject properties)
        {
            return fields;
        }

        foreach (var property in properties)
        {
            var propertySchema = ResolveObject(property.Value, document);
            if (propertySchema is null)
            {
                continue;
            }

            if (
                string.Equals(
                    (GetText(propertySchema["format"]) ?? string.Empty).Trim(),
                    "binary",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var type = (GetText(propertySchema["type"]) ?? "string").Trim();
            if (
                string.Equals(type, "object", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "array", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            var sample = BuildSample(propertySchema, document, 0);
            fields.Add(
                new OpenApiImportField(
                    property.Key,
                    sample is null ? string.Empty : NodeToText(sample)
                )
            );
        }

        return fields;
    }

    private static string EncodeFormBody(IReadOnlyList<OpenApiImportField> fields) =>
        string.Join(
            "&",
            fields.Select(field =>
                $"{Uri.EscapeDataString(field.Name)}={Uri.EscapeDataString(field.Value)}"
            )
        );

    private static string BuildSampleText(JsonNode? sample, string mediaType)
    {
        if (sample is null)
        {
            return string.Empty;
        }

        var text =
            IsJsonMediaType(mediaType) || sample is JsonObject || sample is JsonArray
                ? sample.ToJsonString(PrettyJson)
                : NodeToText(sample);
        return text;
    }

    private static bool IsJsonMediaType(string mediaType) =>
        mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static bool IsFormUrlEncoded(string mediaType) =>
        mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    private static bool IsMultipart(string mediaType) =>
        mediaType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ReadMediaTypes(JsonNode? node) =>
        node is JsonArray array
            ? array
                .Select(item => GetText(item) ?? string.Empty)
                .Where(value => value.Length > 0)
                .ToList()
            : Array.Empty<string>();

    private static bool HasHeader(List<OpenApiImportField> headers, string name) =>
        headers.Any(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool ReadBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static HashSet<string> ReadRequiredNames(JsonObject schema)
    {
        if (schema["required"] is not JsonArray required)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return required
            .Select(GetText)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string SchemaTypeText(JsonObject? schema, JsonObject document) =>
        SchemaTypeText(
            schema,
            document,
            new HashSet<JsonObject>(ReferenceEqualityComparer.Instance)
        );

    private static string SchemaTypeText(
        JsonObject? schema,
        JsonObject document,
        HashSet<JsonObject> visiting
    )
    {
        if (schema is null || !visiting.Add(schema))
        {
            return string.Empty;
        }

        try
        {
            var type = (GetText(schema["type"]) ?? string.Empty).Trim().ToLowerInvariant();
            if (type.Length == 0)
            {
                type =
                    schema["properties"] is JsonObject ? "object"
                    : schema["items"] is not null ? "array"
                    : schema["allOf"] is JsonArray ? "object"
                    : string.Empty;
            }

            if (type == "array")
            {
                var itemType = SchemaTypeText(
                    ResolveObject(schema["items"], document),
                    document,
                    visiting
                );
                return itemType.Length == 0 ? "array" : $"array<{itemType}>";
            }

            var format = (GetText(schema["format"]) ?? string.Empty).Trim();
            return type.Length == 0 ? format
                : format.Length == 0 ? type
                : $"{type} ({format})";
        }
        finally
        {
            visiting.Remove(schema);
        }
    }

    private static string ReadEnumText(JsonObject? schema)
    {
        if (schema?["enum"] is not JsonArray values || values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            " / ",
            values.Select(value => NodeToText(value ?? JsonValue.Create(string.Empty)!))
        );
    }

    private static IReadOnlyList<string> ReadTags(JsonObject operation)
    {
        if (operation["tags"] is not JsonArray tags)
        {
            return Array.Empty<string>();
        }

        return tags.Select(GetText)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!.Trim())
            .ToList();
    }

    private static string ResolveOperationServers(
        JsonObject document,
        JsonObject pathItem,
        JsonObject operation,
        string baseUrl
    )
    {
        var source =
            operation["servers"] as JsonArray
            ?? pathItem["servers"] as JsonArray
            ?? document["servers"] as JsonArray;
        if (source is null)
        {
            return baseUrl;
        }

        var urls = source
            .OfType<JsonObject>()
            .Select(ResolveServerUrl)
            .Where(url => url.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return urls.Count == 0 ? baseUrl : string.Join(" · ", urls);
    }

    private static string BuildSecurityText(JsonObject document, JsonObject operation)
    {
        var security = operation["security"] as JsonArray ?? document["security"] as JsonArray;
        if (security is null || security.Count == 0)
        {
            return string.Empty;
        }

        var components = document["components"] as JsonObject;
        var schemes =
            components?["securitySchemes"] as JsonObject
            ?? document["securityDefinitions"] as JsonObject;
        if (schemes is null)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var requirement in security.OfType<JsonObject>())
        {
            foreach (var entry in requirement)
            {
                var scheme = ResolveObject(schemes[entry.Key], document);
                if (scheme is null)
                {
                    continue;
                }

                var type = (GetText(scheme["type"]) ?? string.Empty).Trim();
                var location = (GetText(scheme["in"]) ?? string.Empty).Trim();
                var name = (GetText(scheme["name"]) ?? string.Empty).Trim();
                var text = type.ToLowerInvariant() switch
                {
                    "apikey" => $"API Key「{name}」（{location}）",
                    "http" => $"HTTP {GetText(scheme["scheme"]) ?? "bearer"}",
                    "oauth2" => "OAuth 2.0",
                    "openidconnect" => "OpenID Connect",
                    _ => type,
                };
                if (!lines.Contains(text, StringComparer.Ordinal))
                {
                    lines.Add(text);
                }
            }
        }

        return string.Join(" · ", lines);
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

    private static JsonNode? BuildSample(JsonNode? node, JsonObject document, int depth) =>
        BuildSample(
            node,
            document,
            depth,
            new HashSet<JsonObject>(ReferenceEqualityComparer.Instance)
        );

    private static JsonNode? BuildSample(
        JsonNode? node,
        JsonObject document,
        int depth,
        HashSet<JsonObject> visiting
    )
    {
        if (node is null || depth > MaxSchemaDepth)
        {
            return null;
        }

        var schema = ResolveObject(node, document);
        if (schema is null || !visiting.Add(schema))
        {
            return null;
        }

        try
        {
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
                            if (
                                BuildSample(candidate, document, depth + 1, visiting)
                                is JsonObject part
                            )
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
                        return BuildSample(candidates[0], document, depth + 1, visiting);
                    }
                }
            }

            var type = GetText(schema["type"]);
            switch (type)
            {
                case "object":
                    return BuildObjectSample(schema, document, depth, visiting);

                case "array":
                    var item = BuildSample(schema["items"], document, depth + 1, visiting);
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
                        return BuildObjectSample(schema, document, depth, visiting);
                    }

                    return null;
            }
        }
        finally
        {
            visiting.Remove(schema);
        }
    }

    private static JsonObject BuildObjectSample(
        JsonObject schema,
        JsonObject document,
        int depth,
        HashSet<JsonObject> visiting
    )
    {
        var result = new JsonObject();
        if (schema["properties"] is not JsonObject properties)
        {
            return result;
        }

        foreach (var property in properties)
        {
            var sample = BuildSample(property.Value, document, depth + 1, visiting);
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

        return ResolveReference(
            current,
            document,
            0,
            new HashSet<JsonObject>(ReferenceEqualityComparer.Instance)
        );
    }

    private static JsonObject ResolveReference(
        JsonObject node,
        JsonObject document,
        int depth,
        HashSet<JsonObject> visiting
    )
    {
        var reference = GetText(node["$ref"]);
        if (string.IsNullOrWhiteSpace(reference) || depth > MaxSchemaDepth || !visiting.Add(node))
        {
            return node;
        }

        try
        {
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
                ? ResolveReference(targetObject, document, depth + 1, visiting)
                : node;
        }
        finally
        {
            visiting.Remove(node);
        }
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
                            MaxDepth = 4096,
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

        return node.ToJsonString(CompactJson);
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
