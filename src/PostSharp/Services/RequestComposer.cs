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
    IReadOnlyList<ComposedUploadFile>? UploadFiles = null
);

public sealed record ComposedUploadFile(string FieldName, string FilePath);

public static class RequestComposer
{
    /// <summary>
    /// 组合请求。<paramref name="auth" /> 是这次请求解析出来的认证材料：
    /// 写请求头时不覆盖用户手写的同名头，写查询参数时追加到 URL 末尾。
    /// </summary>
    public static ComposedRequest Compose(
        RequestItem request,
        EnvironmentModel? environment,
        AuthInjection? auth = null
    )
    {
        var method = string.IsNullOrWhiteSpace(request.Method)
            ? "GET"
            : request.Method.Trim().ToUpperInvariant();

        var url = BuildUrl(request, environment);
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

        var bodyMode = request.BodyMode ?? "None";
        var isMultipart = string.Equals(bodyMode, "Multipart", StringComparison.OrdinalIgnoreCase);
        var hasBody =
            !isMultipart && !string.Equals(bodyMode, "None", StringComparison.OrdinalIgnoreCase);
        var body = hasBody ? VariableResolver.Resolve(request.Body, environment) : null;
        if (string.IsNullOrEmpty(body))
        {
            body = null;
        }

        var hasUserContentType = headers.Any(pair =>
            pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
        );

        string? contentType = null;
        if (body is not null && !hasUserContentType)
        {
            contentType = string.Equals(bodyMode, "Json", StringComparison.OrdinalIgnoreCase)
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
                if (!File.Exists(file.FilePath))
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
            uploadFiles
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
    private static string? FindCurlBlocker(RequestItem request, EnvironmentModel? environment)
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
                if (filePath.Length > 0 && !File.Exists(filePath))
                {
                    return $"上传文件不存在：{filePath}";
                }
            }

            return null;
        }

        var legacyPath = VariableResolver.Resolve(request.UploadFilePath, environment).Trim();
        if (legacyPath.Length > 0 && !File.Exists(legacyPath))
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
}
