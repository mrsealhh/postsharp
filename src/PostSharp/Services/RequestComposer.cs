using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>A request with environment variables resolved and query parameters merged.</summary>
public sealed record ComposedRequest(
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body,
    string? ContentType,
    string? EnvironmentName,
    bool IsMultipart = false,
    IReadOnlyList<KeyValuePair<string, string>>? FormFields = null,
    string? UploadFilePath = null,
    string? UploadFieldName = null,
    IReadOnlyList<ComposedUploadFile>? UploadFiles = null,
    bool FollowRedirects = true,
    ProtocolSendOptions? ProtocolOptions = null,
    SoapSendOptions? SoapOptions = null,
    GrpcSendOptions? GrpcOptions = null
);

public sealed record ComposedUploadFile(string FieldName, string FilePath);

/// <summary>Resolved SOAP version, action and envelope.</summary>
public sealed record SoapSendOptions(string Version, string Action, string Envelope);

/// <summary>Resolved unary gRPC target and JSON request.</summary>
public sealed record GrpcSendOptions(
    string Service,
    string Method,
    string RequestJson,
    int DeadlineSeconds,
    bool UseTls
);

/// <summary>Resolved settings for WebSocket, Socket.IO and TCP requests.</summary>
public sealed record ProtocolSendOptions(
    string Protocol,
    string Payload,
    string Format = "Text",
    int WaitMilliseconds = 3000,
    string EventName = "message",
    string Namespace = "/",
    string Path = "/socket.io",
    string Terminator = "None",
    bool UseTls = false
);

public static class RequestComposer
{
    /// <summary>
    /// 组合请求。<paramref name="auth" /> 是这次请求解析出来的认证材料：
    /// 写请求头时不覆盖用户手写的同名头，写查询参数时追加到 URL 末尾。
    /// </summary>
    public static ComposedRequest Compose(
        RequestItem request,
        EnvironmentModel? environment,
        AuthInjection? auth = null,
        bool validateUploadFiles = true
    )
    {
        var method = string.IsNullOrWhiteSpace(request.Method)
            ? "GET"
            : request.Method.Trim().ToUpperInvariant();

        var protocol = RequestProtocol.Normalize(request.Protocol);
        var protocolOptions = protocol
            is RequestProtocol.WebSocket
                or RequestProtocol.SocketIo
                or RequestProtocol.Tcp
            ? BuildProtocolOptions(request, environment, protocol)
            : null;
        var soapOptions =
            protocol == RequestProtocol.Soap ? BuildSoapOptions(request, environment) : null;
        var grpcOptions =
            protocol == RequestProtocol.Grpc ? BuildGrpcOptions(request, environment) : null;

        var url = protocol is RequestProtocol.Http or RequestProtocol.Soap
            ? BuildUrl(request, environment)
            : BuildProtocolUrl(request, environment);
        var headers = request
            .Headers.ActiveRows.Select(row => new KeyValuePair<string, string>(
                row.Key.Trim(),
                VariableResolver.Resolve(row.Value, environment)
            ))
            .ToList();

        if (auth is { InQuery: false } header)
        {
            if (
                !headers.Any(pair =>
                    pair.Key.Equals(header.Key, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                headers.Add(
                    new KeyValuePair<string, string>(
                        header.Key,
                        VariableResolver.Resolve(header.Value, environment)
                    )
                );
            }
        }
        else if (auth is { InQuery: true } query)
        {
            url = AppendQuery(url, query.Key, VariableResolver.Resolve(query.Value, environment));
        }

        var isHttp = protocol == RequestProtocol.Http;
        var bodyMode = request.BodyMode ?? "None";
        var isMultipart =
            isHttp && string.Equals(bodyMode, "Multipart", StringComparison.OrdinalIgnoreCase);
        var isGraphQl =
            isHttp && string.Equals(bodyMode, "GraphQL", StringComparison.OrdinalIgnoreCase);
        if (isGraphQl || soapOptions is not null)
        {
            method = "POST";
        }

        var hasBody =
            isHttp
            && !isMultipart
            && !string.Equals(bodyMode, "None", StringComparison.OrdinalIgnoreCase);
        string? body;
        if (grpcOptions is not null || protocolOptions is not null)
        {
            body = null;
        }
        else if (soapOptions is not null)
        {
            body = soapOptions.Envelope;
        }
        else if (isGraphQl)
        {
            body = BuildGraphQlBody(request, environment);
        }
        else
        {
            body = hasBody ? VariableResolver.Resolve(request.Body, environment) : null;
        }

        if (string.IsNullOrEmpty(body))
        {
            body = null;
        }

        var hasUserContentType = headers.Any(pair =>
            pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        );

        if (
            soapOptions is { Version: "1.1" }
            && !string.IsNullOrWhiteSpace(soapOptions.Action)
            && !headers.Any(pair =>
                pair.Key.Equals("SOAPAction", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            headers.Add(
                new KeyValuePair<string, string>("SOAPAction", $"\"{soapOptions.Action}\"")
            );
        }

        string? contentType = null;
        if (soapOptions is not null && !hasUserContentType)
        {
            contentType =
                soapOptions.Version == "1.1" ? "text/xml"
                : string.IsNullOrWhiteSpace(soapOptions.Action) ? "application/soap+xml"
                : $"application/soap+xml; action=\"{soapOptions.Action}\"";
        }
        else if (body is not null && !hasUserContentType)
        {
            contentType =
                isGraphQl || string.Equals(bodyMode, "Json", StringComparison.OrdinalIgnoreCase)
                    ? "application/json"
                    : "text/plain";
        }

        List<KeyValuePair<string, string>>? formFields = null;
        string? uploadFilePath = null;
        string? uploadFieldName = null;
        List<ComposedUploadFile>? uploadFiles = null;

        if (isMultipart)
        {
            formFields = request
                .FormFields.ActiveRows.Select(row => new KeyValuePair<string, string>(
                    row.Key.Trim(),
                    VariableResolver.Resolve(row.Value, environment)
                ))
                .ToList();

            uploadFieldName = string.IsNullOrWhiteSpace(request.UploadFieldName)
                ? "file"
                : request.UploadFieldName.Trim();

            var candidates =
                request.UploadFiles.Count > 0
                    ? request
                        .UploadFiles.Select(file => new ComposedUploadFile(
                            string.IsNullOrWhiteSpace(file.FieldName) ? "file" : file.FieldName,
                            VariableResolver.Resolve(file.FilePath, environment).Trim()
                        ))
                        .Where(file => file.FilePath.Length > 0)
                        .ToList()
                    : new List<ComposedUploadFile>();

            if (
                candidates.Count == 0
                && !string.IsNullOrWhiteSpace(
                    VariableResolver.Resolve(request.UploadFilePath, environment)
                )
            )
            {
                candidates.Add(
                    new ComposedUploadFile(
                        uploadFieldName,
                        VariableResolver.Resolve(request.UploadFilePath, environment).Trim()
                    )
                );
            }

            foreach (var file in candidates)
            {
                if (validateUploadFiles && !File.Exists(file.FilePath))
                {
                    throw new InvalidOperationException($"上传文件不存在：{file.FilePath}");
                }
            }

            uploadFiles = candidates;
            if (candidates.Count > 0)
            {
                uploadFilePath = candidates[0].FilePath;
                uploadFieldName = candidates[0].FieldName;
            }
        }

        return new ComposedRequest(
            method,
            url,
            headers,
            body,
            contentType,
            environment?.Name,
            isMultipart,
            formFields,
            uploadFilePath,
            uploadFieldName,
            uploadFiles,
            request.FollowRedirects,
            protocolOptions,
            soapOptions,
            grpcOptions
        );
    }

    private static SoapSendOptions BuildSoapOptions(
        RequestItem request,
        EnvironmentModel? environment
    )
    {
        var version = string.Equals(
            request.SoapVersion?.Trim(),
            "1.2",
            StringComparison.OrdinalIgnoreCase
        )
            ? "1.2"
            : "1.1";
        var action = VariableResolver.Resolve(request.SoapAction, environment).Trim();
        var body = VariableResolver.Resolve(request.MessageBody, environment);
        return new SoapSendOptions(
            version,
            action,
            SoapProtocolService.BuildEnvelope(version, body)
        );
    }

    private static GrpcSendOptions BuildGrpcOptions(
        RequestItem request,
        EnvironmentModel? environment
    )
    {
        var service = VariableResolver
            .Resolve(request.GrpcService, environment)
            .Trim()
            .TrimStart('.');
        var method = VariableResolver.Resolve(request.GrpcMethod, environment).Trim();
        var requestJson = VariableResolver.Resolve(request.MessageBody, environment);
        var deadline = Math.Clamp(request.GrpcDeadlineSeconds, 0, 300);
        return new GrpcSendOptions(service, method, requestJson, deadline, request.GrpcUseTls);
    }

    private static ProtocolSendOptions BuildProtocolOptions(
        RequestItem request,
        EnvironmentModel? environment,
        string protocol
    )
    {
        var format = string.Equals(request.MessageFormat, "Hex", StringComparison.OrdinalIgnoreCase)
            ? "Hex"
            : "Text";
        var waitMilliseconds = Math.Clamp(
            request.WaitMilliseconds <= 0 ? 3000 : request.WaitMilliseconds,
            100,
            60000
        );
        var terminator = request.TcpTerminator?.Trim().ToUpperInvariant() switch
        {
            "LF" => "LF",
            "CRLF" => "CRLF",
            _ => "None",
        };
        var eventName = string.IsNullOrWhiteSpace(request.SocketIoEvent)
            ? "message"
            : request.SocketIoEvent.Trim();
        var socketIoNamespace = string.IsNullOrWhiteSpace(request.SocketIoNamespace)
            ? "/"
            : request.SocketIoNamespace.Trim();
        var socketIoPath = string.IsNullOrWhiteSpace(request.SocketIoPath)
            ? "/socket.io"
            : request.SocketIoPath.Trim();

        return new ProtocolSendOptions(
            protocol,
            VariableResolver.Resolve(request.MessageBody, environment),
            format,
            waitMilliseconds,
            VariableResolver.Resolve(eventName, environment),
            VariableResolver.Resolve(socketIoNamespace, environment),
            VariableResolver.Resolve(socketIoPath, environment),
            terminator,
            request.TcpUseTls
        );
    }

    public static string BuildCurl(ComposedRequest request)
    {
        var lines = new List<string>
        {
            $"curl -X {request.Method} {Quote(request.Url)}",
            "  --compressed",
        };

        foreach (var (key, value) in request.Headers)
        {
            lines.Add($"  -H {Quote($"{key}: {value}")}");
        }

        if (
            !string.IsNullOrWhiteSpace(request.ContentType)
            && !request.Headers.Any(pair =>
                pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            lines.Add($"  -H {Quote($"Content-Type: {request.ContentType}")}");
        }

        if (request.IsMultipart)
        {
            foreach (var field in request.FormFields ?? Array.Empty<KeyValuePair<string, string>>())
            {
                lines.Add($"  -F {Quote($"{field.Key}={field.Value}")}");
            }

            var uploadFiles = request.UploadFiles;
            if (
                (uploadFiles is null || uploadFiles.Count == 0)
                && !string.IsNullOrWhiteSpace(request.UploadFilePath)
            )
            {
                uploadFiles = new[]
                {
                    new ComposedUploadFile(
                        string.IsNullOrWhiteSpace(request.UploadFieldName)
                            ? "file"
                            : request.UploadFieldName,
                        request.UploadFilePath
                    ),
                };
            }

            foreach (var file in uploadFiles ?? Array.Empty<ComposedUploadFile>())
            {
                lines.Add($"  -F {Quote($"{file.FieldName}=@{file.FilePath}")}");
            }
        }
        else if (request.Body is not null)
        {
            lines.Add($"  --data-raw {Quote(request.Body)}");
        }

        return string.Join(" \\\n", lines);
    }

    /// <summary>Builds a PowerShell Invoke-RestMethod command for the current request.</summary>
    public static string BuildPowerShell(
        RequestItem request,
        EnvironmentModel? environment,
        bool validateUploadFiles = true
    )
    {
        var blocker = FindCurlBlocker(request, environment, validateUploadFiles);
        if (blocker is not null)
        {
            return $"# 无法生成 PowerShell：{blocker}";
        }

        try
        {
            return BuildPowerShell(
                Compose(request, environment, validateUploadFiles: validateUploadFiles)
            );
        }
        catch (Exception exception)
        {
            Log.Error(exception, "生成 PowerShell 时出现意外错误 {RequestName}", request.Name);
            return "# 无法生成 PowerShell：请求配置不完整";
        }
    }

    public static string BuildPowerShell(ComposedRequest request)
    {
        var lines = new List<string> { "$headers = @{" };
        foreach (var (key, value) in request.Headers)
        {
            lines.Add($"    '{EscapePowerShell(key)}' = '{EscapePowerShell(value)}'");
        }

        lines.Add("}");
        lines.Add(
            $"Invoke-RestMethod -Uri '{EscapePowerShell(request.Url)}' -Method {request.Method} -Headers $headers"
        );

        if (request.IsMultipart)
        {
            lines.Add("-Form @{");
            foreach (var field in request.FormFields ?? Array.Empty<KeyValuePair<string, string>>())
            {
                lines.Add(
                    $"    '{EscapePowerShell(field.Key)}' = '{EscapePowerShell(field.Value)}'"
                );
            }

            var uploadFiles = request.UploadFiles;
            if (
                (uploadFiles is null || uploadFiles.Count == 0)
                && !string.IsNullOrWhiteSpace(request.UploadFilePath)
            )
            {
                uploadFiles = new[]
                {
                    new ComposedUploadFile(
                        string.IsNullOrWhiteSpace(request.UploadFieldName)
                            ? "file"
                            : request.UploadFieldName,
                        request.UploadFilePath
                    ),
                };
            }

            foreach (var file in uploadFiles ?? Array.Empty<ComposedUploadFile>())
            {
                lines.Add(
                    $"    '{EscapePowerShell(file.FieldName)}' = Get-Item '{EscapePowerShell(file.FilePath)}'"
                );
            }

            lines.Add("}");
        }
        else if (request.Body is not null)
        {
            if (!string.IsNullOrWhiteSpace(request.ContentType))
            {
                lines.Add($"-ContentType '{EscapePowerShell(request.ContentType)}'");
            }

            lines.Add("-Body @'");
            lines.Add(request.Body);
            lines.Add("'@");
        }

        return string.Join("\n", lines);
    }

    /// <summary>Builds a cURL command for the current request, or a comment explaining why it cannot.</summary>
    public static string BuildCurl(
        RequestItem request,
        EnvironmentModel? environment,
        AuthInjection? auth = null
    )
    {
        var blocker = FindCurlBlocker(request, environment);
        if (blocker is not null)
        {
            return $"# 无法生成 cURL：{blocker}";
        }

        try
        {
            return BuildCurl(Compose(request, environment, auth));
        }
        catch (Exception exception)
        {
            Log.Error(exception, "生成 cURL 时出现意外错误 {RequestName}", request.Name);
            return "# 无法生成 cURL：生成时出现意外错误，请检查请求配置。";
        }
    }

    /// <summary>审计落盘前隐藏令牌与密钥，避免把凭据写进审计文件。</summary>
    public static ComposedRequest MaskAuthorization(ComposedRequest request)
    {
        var headers = request
            .Headers.Select(pair =>
                IsSensitiveHeader(pair.Key)
                    ? new KeyValuePair<string, string>(
                        pair.Key,
                        MaskHeaderValue(pair.Key, pair.Value)
                    )
                    : pair
            )
            .ToList();

        return request with
        {
            Headers = headers,
            Url = MaskQuery(request.Url),
        };
    }

    private static bool IsSensitiveHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api-key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static string MaskHeaderValue(string name, string value) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            ? MaskToken(value)
            : "****";

    private static string MaskToken(string value)
    {
        var separator = value.IndexOf(' ');
        return separator > 0 ? value[..separator] + " ****" : "****";
    }

    /// <summary>
    /// 认证写在查询参数时（OAuth 2.0 的 access_token、API Key）也不会以明文进审计；
    /// 键名带 token / key / secret / password 的参数一律遮掉值。
    /// </summary>
    private static string MaskQuery(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query))
        {
            return url;
        }

        var pairs = uri.Query.TrimStart('?').Split('&');
        var masked = false;
        for (var index = 0; index < pairs.Length; index++)
        {
            var separator = pairs[index].IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pairs[index][..separator]);
            if (!IsSensitiveQueryKey(key))
            {
                continue;
            }

            pairs[index] = pairs[index][..separator] + "=****";
            masked = true;
        }

        if (!masked)
        {
            return url;
        }

        var builder = new UriBuilder(uri) { Query = string.Join('&', pairs) };
        return builder.Uri.ToString();
    }

    private static bool IsSensitiveQueryKey(string key) =>
        key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("key", StringComparison.OrdinalIgnoreCase)
        || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static string AppendQuery(string url, string key, string value)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return url + separator + Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value);
    }

    /// <summary>
    /// Checks the request before composing it, so an incomplete draft (blank URL, missing upload file) is reported
    /// as a hint instead of being logged as a failure.
    /// </summary>
    private static string? FindCurlBlocker(
        RequestItem request,
        EnvironmentModel? environment,
        bool validateUploadFiles = true
    )
    {
        if (
            !TryResolveHttpUri(
                VariableResolver.Resolve(request.Url, environment),
                out _,
                out var problem
            )
        )
        {
            return problem;
        }

        if (
            !string.Equals(
                request.BodyMode ?? "None",
                "Multipart",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return null;
        }

        if (request.UploadFiles.Count > 0)
        {
            foreach (var file in request.UploadFiles)
            {
                var filePath = VariableResolver.Resolve(file.FilePath, environment).Trim();
                if (validateUploadFiles && filePath.Length > 0 && !File.Exists(filePath))
                {
                    return $"上传文件不存在：{filePath}";
                }
            }

            return null;
        }

        var legacyPath = VariableResolver.Resolve(request.UploadFilePath, environment).Trim();
        if (validateUploadFiles && legacyPath.Length > 0 && !File.Exists(legacyPath))
        {
            return $"上传文件不存在：{legacyPath}";
        }

        return null;
    }

    private static bool TryResolveHttpUri(string raw, out Uri uri, out string problem)
    {
        raw = raw.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            uri = null!;
            problem = "请输入请求地址。";
            return false;
        }

        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = "http://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            uri = null!;
            problem = "请求地址无效，请使用 http:// 或 https:// 开头的完整地址。";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            uri = null!;
            problem = "请求地址无效，请使用 http:// 或 https:// 开头的完整地址。";
            return false;
        }

        uri = parsed;
        problem = string.Empty;
        return true;
    }

    private static string BuildGraphQlBody(RequestItem request, EnvironmentModel? environment)
    {
        var query = VariableResolver.Resolve(request.GraphQlQuery, environment);
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new InvalidOperationException("GraphQL Query 不能为空。");
        }

        var payload = new JsonObject { ["query"] = query };
        var variablesText = VariableResolver.Resolve(request.GraphQlVariables, environment).Trim();
        if (variablesText.Length > 0)
        {
            JsonNode? variables;
            try
            {
                variables = JsonNode.Parse(variablesText);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "GraphQL Variables 必须是合法的 JSON 对象。",
                    exception
                );
            }

            if (variables is not JsonObject variablesObject)
            {
                throw new InvalidOperationException("GraphQL Variables 必须是 JSON 对象。");
            }

            payload["variables"] = variablesObject.DeepClone();
        }

        if (!string.IsNullOrWhiteSpace(request.GraphQlOperationName))
        {
            payload["operationName"] = request.GraphQlOperationName.Trim();
        }

        return payload.ToJsonString();
    }

    /// <summary>Protocol URLs are not HTTP URLs: keep the scheme and only append enabled query rows.</summary>
    private static string BuildProtocolUrl(RequestItem request, EnvironmentModel? environment)
    {
        var url = VariableResolver.Resolve(request.Url, environment).Trim();
        var additions = request
            .Params.ActiveRows.Select(row =>
                $"{Uri.EscapeDataString(row.Key.Trim())}={Uri.EscapeDataString(VariableResolver.Resolve(row.Value, environment))}"
            )
            .ToList();
        if (additions.Count == 0)
        {
            return url;
        }

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return url + separator + string.Join("&", additions);
    }

    private static string BuildUrl(RequestItem request, EnvironmentModel? environment)
    {
        if (
            !TryResolveHttpUri(
                VariableResolver.Resolve(request.Url, environment),
                out var uri,
                out var problem
            )
        )
        {
            throw new InvalidOperationException(problem);
        }

        var query = uri.Query.TrimStart('?');
        var additions = request
            .Params.ActiveRows.Select(row =>
                $"{Uri.EscapeDataString(row.Key.Trim())}={Uri.EscapeDataString(VariableResolver.Resolve(row.Value, environment))}"
            )
            .ToList();

        if (additions.Count == 0)
        {
            return uri.ToString();
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.IsNullOrEmpty(query)
                ? string.Join("&", additions)
                : query + "&" + string.Join("&", additions),
        };

        return builder.Uri.ToString();
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string EscapePowerShell(string value) => value.Replace("'", "''");
}
