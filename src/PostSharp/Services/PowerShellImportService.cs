using System.Text.RegularExpressions;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Parses common PowerShell Invoke-RestMethod / Invoke-WebRequest commands.</summary>
public static class PowerShellImportService
{
    private static readonly Regex UriRegex = new(
        @"-(?:Uri|Url)\s+(?:'(?<value>(?:''|[^'])*)'|""(?<value>(?:`""|[^""])*)""|(?<value>\S+))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex MethodRegex = new(
        @"-Method\s+(?:'(?<value>[^']+)'|""(?<value>[^""]+)""|(?<value>\w+))",
        RegexOptions.IgnoreCase
    );

    private static readonly Regex ContentTypeRegex = new(
        @"-ContentType\s+(?:'(?<value>[^']*)'|""(?<value>[^""]*)""|(?<value>\S+))",
        RegexOptions.IgnoreCase
    );

    private static readonly Regex TimeoutRegex = new(
        @"-TimeoutSec\s+(?<value>\d+)",
        RegexOptions.IgnoreCase
    );

    private static readonly Regex HeadersRegex = new(
        @"-Headers\s+@\{(?<value>.*?)\}",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex FormRegex = new(
        @"-Form\s+@\{(?<value>.*?)\}",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex BodyRegex = new(
        @"-Body\s+(?:@'(?<here>.*?)'@|@""(?<here>.*?)""@|'(?<single>(?:''|[^'])*)'|""(?<double>(?:`""|[^""])*)"")",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    private static readonly Regex HashtableEntryRegex = new(
        @"['""](?<key>[^'""]+)['""]\s*=\s*(?:Get-Item\s+)?['""](?<value>.*?)['""]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline
    );

    public static CurlImportResult Parse(string command)
    {
        var text = (command ?? string.Empty).Replace("`\r\n", " ").Replace("`\n", " ").Trim();
        if (
            !text.StartsWith("Invoke-RestMethod", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("Invoke-WebRequest", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException(
                "不是 PowerShell 请求，请以 Invoke-RestMethod 或 Invoke-WebRequest 开头。"
            );
        }

        var url = MatchValue(UriRegex, text);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("PowerShell 命令缺少 -Uri 或 -Url。");
        }

        var method = (MatchValue(MethodRegex, text) ?? "GET").ToUpperInvariant();
        var contentType = MatchValue(ContentTypeRegex, text);
        var timeout = int.TryParse(MatchValue(TimeoutRegex, text), out var seconds)
            ? Math.Clamp(seconds, 0, 3600)
            : 0;
        var warnings = new List<string>();
        var headers = ParseHashtable(HeadersRegex.Match(text).Groups["value"].Value);
        var form = ParseHashtable(FormRegex.Match(text).Groups["value"].Value);
        var body = ParseBody(text);
        var bodyMode = "None";
        var bodyText = string.Empty;
        var uploadFieldName = "file";
        var uploadFilePath = string.Empty;

        if (form.Count > 0)
        {
            bodyMode = "Multipart";
            var fileCount = 0;
            foreach (var (key, value) in form)
            {
                if (LooksLikePath(value))
                {
                    fileCount++;
                    if (fileCount == 1)
                    {
                        uploadFieldName = key;
                        uploadFilePath = value;
                    }
                    else
                    {
                        warnings.Add(
                            $"PowerShell 表单包含多个文件，MVP 只保留第一个文件字段「{uploadFieldName}」。"
                        );
                    }
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(body))
        {
            bodyText = body;
            bodyMode =
                contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                || body.TrimStart().StartsWith('{')
                || body.TrimStart().StartsWith('[')
                    ? "Json"
                    : "Text";
        }

        var request = new ImportedRequest
        {
            Name = SuggestName(url, method),
            Method = method,
            Url = url,
            BodyMode = bodyMode,
            Body = bodyText,
            TimeoutSeconds = timeout,
            UploadFieldName = uploadFieldName,
            UploadFilePath = uploadFilePath,
        };
        foreach (var (key, value) in form)
        {
            if (!LooksLikePath(value))
            {
                request.FormFields.Add(new ImportedField(key, value));
            }
        }

        foreach (var (key, value) in headers)
        {
            request.Headers.Add(new ImportedField(key, value));
        }

        if (
            !string.IsNullOrWhiteSpace(contentType)
            && !headers.Any(header =>
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            request.Headers.Add(new ImportedField("Content-Type", contentType));
        }

        foreach (
            var flag in new[]
            {
                "-InFile",
                "-Credential",
                "-Certificate",
                "-Proxy",
                "-SessionVariable",
            }
        )
        {
            if (text.Contains(flag, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"PowerShell 参数 {flag} 暂未转换，请在请求编辑器中手工补充。");
            }
        }

        return new CurlImportResult { Request = request, Warnings = warnings };
    }

    private static string? MatchValue(Regex regex, string text)
    {
        var match = regex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["value"].Value;
        return value.Replace("''", "'").Replace("`\"", "\"").Trim();
    }

    private static List<KeyValuePair<string, string>> ParseHashtable(string text)
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (Match match in HashtableEntryRegex.Matches(text))
        {
            entries.Add(
                new KeyValuePair<string, string>(
                    match.Groups["key"].Value.Trim(),
                    match.Groups["value"].Value.Trim()
                )
            );
        }

        return entries;
    }

    private static string ParseBody(string text)
    {
        var match = BodyRegex.Match(text);
        if (!match.Success)
        {
            return string.Empty;
        }

        var value =
            match.Groups["here"].Success ? match.Groups["here"].Value
            : match.Groups["single"].Success ? match.Groups["single"].Value.Replace("''", "'")
            : match.Groups["double"].Value.Replace("`\"", "\"");
        return value.Trim();
    }

    private static bool LooksLikePath(string value)
    {
        return File.Exists(value)
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar)
            || Path.HasExtension(value);
    }

    private static string SuggestName(string url, string method)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var name = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return $"{method} 请求";
    }
}
