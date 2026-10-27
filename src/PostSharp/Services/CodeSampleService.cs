using System.Globalization;
using System.Text;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed record CodeLanguageOption(
    string Id,
    string DisplayName,
    string Highlight,
    string FileName
)
{
    public override string ToString() => DisplayName;
}

/// <summary>Generates runnable request snippets from the composed request (variables already resolved).</summary>
public static class CodeSampleService
{
    public static IReadOnlyList<CodeLanguageOption> Languages { get; } =
    [
        new("curl", "cURL", "bash", "request.sh"),
        new("powershell", "PowerShell", "powershell", "request.ps1"),
        new("csharp", "C# HttpClient", "csharp", "Program.cs"),
        new("javascript", "JavaScript fetch", "javascript", "request.mjs"),
        new("python", "Python requests", "python", "request.py"),
        new("java", "Java HttpClient", "java", "Main.java"),
        new("go", "Go net/http", "go", "main.go"),
    ];

    public static string Generate(
        string languageId,
        RequestItem request,
        EnvironmentModel? environment
    )
    {
        ComposedRequest composed;
        try
        {
            composed = RequestComposer.Compose(request, environment, validateUploadFiles: false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "生成调用代码失败 {RequestName}", request.Name);
            return $"# 无法生成调用代码：{exception.Message}";
        }

        return languageId switch
        {
            "curl" => RequestComposer.BuildCurl(composed),
            "powershell" => RequestComposer.BuildPowerShell(
                request,
                environment,
                validateUploadFiles: false
            ),
            "csharp" => BuildCSharp(composed),
            "javascript" => BuildJavaScript(composed),
            "python" => BuildPython(composed),
            "java" => BuildJava(composed),
            "go" => BuildGo(composed),
            _ => RequestComposer.BuildCurl(composed),
        };
    }

    private static string HeadersWithContentType(ComposedRequest request)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in request.Headers)
        {
            builder.Append("    ").AppendLine(key + ": " + value);
        }

        if (
            !string.IsNullOrWhiteSpace(request.ContentType)
            && !request.Headers.Any(pair =>
                pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            builder.Append("    Content-Type: ").AppendLine(request.ContentType);
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildCSharp(ComposedRequest request)
    {
        var builder = new StringBuilder();
        builder
            .AppendLine("using System;")
            .AppendLine("using System.IO;")
            .AppendLine("using System.Net.Http;")
            .AppendLine("using System.Text;")
            .AppendLine();
        builder.AppendLine("using var client = new HttpClient();");
        builder
            .Append("using var request = new HttpRequestMessage(HttpMethod.")
            .Append(MethodTitle(request.Method))
            .Append(", ")
            .Append(CSharpString(request.Url))
            .AppendLine(");");
        foreach (var (key, value) in request.Headers)
        {
            builder
                .Append("request.Headers.TryAddWithoutValidation(")
                .Append(CSharpString(key))
                .Append(", ")
                .Append(CSharpString(value))
                .AppendLine(");");
        }

        if (request.IsMultipart)
        {
            builder.AppendLine("var content = new MultipartFormDataContent();");
            foreach (var field in request.FormFields ?? Array.Empty<KeyValuePair<string, string>>())
            {
                builder
                    .Append("content.Add(new StringContent(")
                    .Append(CSharpString(field.Value))
                    .Append("), ")
                    .Append(CSharpString(field.Key))
                    .AppendLine(");");
            }

            foreach (var file in request.UploadFiles ?? Array.Empty<ComposedUploadFile>())
            {
                builder
                    .Append("content.Add(new StreamContent(File.OpenRead(")
                    .Append(CSharpString(file.FilePath))
                    .Append(")), ")
                    .Append(CSharpString(file.FieldName))
                    .Append(", Path.GetFileName(")
                    .Append(CSharpString(file.FilePath))
                    .AppendLine(")));");
            }

            builder.AppendLine("request.Content = content;");
        }
        else if (request.Body is not null)
        {
            builder
                .Append("request.Content = new StringContent(")
                .Append(CSharpString(request.Body))
                .Append(", Encoding.UTF8, ")
                .Append(CSharpString(MediaTypeName(request.ContentType)))
                .AppendLine(");");
        }

        builder
            .AppendLine()
            .AppendLine("using var response = await client.SendAsync(request);")
            .AppendLine("var responseBody = await response.Content.ReadAsStringAsync();")
            .AppendLine("Console.WriteLine($\"{(int)response.StatusCode} {response.StatusCode}\");")
            .AppendLine("Console.WriteLine(responseBody);");
        return builder.ToString();
    }

    private static string BuildJavaScript(ComposedRequest request)
    {
        var builder = new StringBuilder();
        if (request.IsMultipart)
        {
            var files = request.UploadFiles ?? Array.Empty<ComposedUploadFile>();
            if (files.Count > 0)
            {
                builder.AppendLine("import { openAsBlob } from \"node:fs\";").AppendLine();
            }

            builder.AppendLine("const form = new FormData();");
            foreach (var field in request.FormFields ?? Array.Empty<KeyValuePair<string, string>>())
            {
                builder
                    .Append("form.append(")
                    .Append(JavaScriptString(field.Key))
                    .Append(", ")
                    .Append(JavaScriptString(field.Value))
                    .AppendLine(");");
            }

            foreach (var file in files)
            {
                builder
                    .Append("form.append(")
                    .Append(JavaScriptString(file.FieldName))
                    .Append(", await openAsBlob(")
                    .Append(JavaScriptString(file.FilePath))
                    .Append("), ")
                    .Append(JavaScriptString(Path.GetFileName(file.FilePath)))
                    .AppendLine(");");
            }

            builder.AppendLine();
        }

        builder
            .Append("const response = await fetch(")
            .Append(JavaScriptString(request.Url))
            .AppendLine(", {");
        builder.Append("  method: ").Append(JavaScriptString(request.Method)).AppendLine(",");
        var headers = HeadersWithContentType(request);
        if (headers.Length > 0)
        {
            builder.AppendLine("  headers: {");
            foreach (var line in headers.Split(Environment.NewLine))
            {
                var separator = line.IndexOf(':', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    continue;
                }

                builder
                    .Append("    ")
                    .Append(JavaScriptString(line[..separator].Trim()))
                    .Append(": ")
                    .Append(JavaScriptString(line[(separator + 1)..].Trim()))
                    .AppendLine(",");
            }

            builder.AppendLine("  },");
        }

        if (request.IsMultipart)
        {
            builder.AppendLine("  body: form,");
        }
        else if (request.Body is not null)
        {
            builder.Append("  body: ").Append(JavaScriptString(request.Body)).AppendLine(",");
        }

        builder
            .AppendLine("});")
            .AppendLine()
            .AppendLine("console.log(response.status, await response.text());");
        return builder.ToString();
    }

    private static string BuildPython(ComposedRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("import requests").AppendLine();
        builder.AppendLine("headers = {");
        foreach (var (key, value) in request.Headers)
        {
            builder
                .Append("    ")
                .Append(PythonString(key))
                .Append(": ")
                .Append(PythonString(value))
                .AppendLine(",");
        }

        if (
            !string.IsNullOrWhiteSpace(request.ContentType)
            && !request.Headers.Any(pair =>
                pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            builder
                .Append("    \"Content-Type\": ")
                .Append(PythonString(request.ContentType))
                .AppendLine(",");
        }

        builder.AppendLine("}").AppendLine();
        if (request.IsMultipart)
        {
            builder.AppendLine("data = {");
            foreach (var field in request.FormFields ?? Array.Empty<KeyValuePair<string, string>>())
            {
                builder
                    .Append("    ")
                    .Append(PythonString(field.Key))
                    .Append(": ")
                    .Append(PythonString(field.Value))
                    .AppendLine(",");
            }

            builder.AppendLine("}").AppendLine();
            builder.AppendLine("files = {");
            foreach (var file in request.UploadFiles ?? Array.Empty<ComposedUploadFile>())
            {
                builder
                    .Append("    ")
                    .Append(PythonString(file.FieldName))
                    .Append(": open(")
                    .Append(PythonString(file.FilePath))
                    .AppendLine(", \"rb\"),");
            }

            builder
                .AppendLine("}")
                .AppendLine()
                .Append("response = requests.")
                .Append(PythonMethod(request.Method))
                .Append("(")
                .Append(PythonString(request.Url))
                .AppendLine(", headers=headers, data=data, files=files)");
        }
        else
        {
            builder
                .Append("response = requests.")
                .Append(PythonMethod(request.Method))
                .Append("(")
                .Append(PythonString(request.Url))
                .Append(", headers=headers");
            if (request.Body is not null)
            {
                builder.Append(", data=").Append(PythonString(request.Body));
            }

            builder.AppendLine(")");
        }

        builder.AppendLine().AppendLine("print(response.status_code, response.text)");
        return builder.ToString();
    }

    private static string BuildJava(ComposedRequest request)
    {
        var builder = new StringBuilder();
        builder
            .AppendLine("import java.io.ByteArrayOutputStream;")
            .AppendLine("import java.net.URI;")
            .AppendLine("import java.net.http.HttpClient;")
            .AppendLine("import java.net.http.HttpRequest;")
            .AppendLine("import java.net.http.HttpResponse;")
            .AppendLine("import java.nio.charset.StandardCharsets;");
        if (request.IsMultipart)
        {
            builder
                .AppendLine("import java.nio.file.Files;")
                .AppendLine("import java.nio.file.Path;");
        }

        builder
            .AppendLine()
            .AppendLine("public class Main {")
            .AppendLine("    public static void main(String[] args) throws Exception {")
            .AppendLine("        var client = HttpClient.newHttpClient();");
        builder
            .Append("        var builder = HttpRequest.newBuilder(URI.create(")
            .Append(JavaString(request.Url))
            .AppendLine("));");
        foreach (var (key, value) in request.Headers)
        {
            builder
                .Append("        builder.header(")
                .Append(JavaString(key))
                .Append(", ")
                .Append(JavaString(value))
                .AppendLine(");");
        }

        if (request.IsMultipart)
        {
            builder
                .AppendLine("        var boundary = \"----PostSharpBoundary\";")
                .AppendLine("        var body = new ByteArrayOutputStream();");
            foreach (var field in request.FormFields ?? Array.Empty<KeyValuePair<string, string>>())
            {
                builder
                    .Append(
                        "        body.write((\"--\" + boundary + \"\\r\\nContent-Disposition: form-data; name=\\\"\" + "
                    )
                    .Append(JavaString(field.Key))
                    .Append(" + \"\\\"\\r\\n\\r\\n\" + ")
                    .Append(JavaString(field.Value))
                    .AppendLine(" + \"\\r\\n\").getBytes(StandardCharsets.UTF_8));");
            }

            foreach (var file in request.UploadFiles ?? Array.Empty<ComposedUploadFile>())
            {
                var fileName = Path.GetFileName(file.FilePath);
                builder
                    .Append(
                        "        body.write((\"--\" + boundary + \"\\r\\nContent-Disposition: form-data; name=\\\"\" + "
                    )
                    .Append(JavaString(file.FieldName))
                    .Append(" + \"\\\"; filename=\\\"\" + ")
                    .Append(JavaString(fileName))
                    .AppendLine(
                        " + \"\\\"\\r\\nContent-Type: application/octet-stream\\r\\n\\r\\n\").getBytes(StandardCharsets.UTF_8));"
                    );
                builder
                    .Append("        body.write(Files.readAllBytes(Path.of(")
                    .Append(JavaString(file.FilePath))
                    .AppendLine(")));");
                builder.AppendLine(
                    "        body.write(\"\\r\\n\".getBytes(StandardCharsets.UTF_8));"
                );
            }

            builder
                .AppendLine(
                    "        body.write((\"--\" + boundary + \"--\\r\\n\").getBytes(StandardCharsets.UTF_8));"
                )
                .AppendLine(
                    "        builder.header(\"Content-Type\", \"multipart/form-data; boundary=\" + boundary);"
                )
                .AppendLine(
                    "        builder.method(\"POST\", HttpRequest.BodyPublishers.ofByteArray(body.toByteArray()));"
                );
        }
        else if (request.Body is not null)
        {
            if (!string.IsNullOrWhiteSpace(request.ContentType))
            {
                builder
                    .Append("        builder.header(\"Content-Type\", ")
                    .Append(JavaString(request.ContentType))
                    .AppendLine(");");
            }

            builder
                .Append("        builder.method(")
                .Append(JavaString(request.Method))
                .Append(", HttpRequest.BodyPublishers.ofString(")
                .Append(JavaString(request.Body))
                .AppendLine("));");
        }
        else
        {
            builder
                .Append("        builder.method(")
                .Append(JavaString(request.Method))
                .AppendLine(", HttpRequest.BodyPublishers.noBody());");
        }

        builder
            .AppendLine("        var request = builder.build();")
            .AppendLine(
                "        var response = client.send(request, HttpResponse.BodyHandlers.ofString());"
            )
            .AppendLine(
                "        System.out.println(response.statusCode() + \" \" + response.body());"
            )
            .AppendLine("    }")
            .AppendLine("}");
        return builder.ToString();
    }

    private static string BuildGo(ComposedRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("package main").AppendLine().AppendLine("import (");
        if (request.IsMultipart)
        {
            builder
                .AppendLine("    \"bytes\"")
                .AppendLine("    \"fmt\"")
                .AppendLine("    \"io\"")
                .AppendLine("    \"mime/multipart\"")
                .AppendLine("    \"net/http\"")
                .AppendLine("    \"os\"");
        }
        else
        {
            builder
                .AppendLine("    \"fmt\"")
                .AppendLine("    \"io\"")
                .AppendLine("    \"net/http\"");
            if (request.Body is not null)
            {
                builder.AppendLine("    \"strings\"");
            }
        }

        builder.AppendLine(")").AppendLine().AppendLine("func main() {");

        if (request.IsMultipart)
        {
            builder
                .AppendLine("    var body bytes.Buffer")
                .AppendLine("    writer := multipart.NewWriter(&body)");
            foreach (var field in request.FormFields ?? Array.Empty<KeyValuePair<string, string>>())
            {
                builder
                    .Append("    writer.WriteField(")
                    .Append(GoString(field.Key))
                    .Append(", ")
                    .Append(GoString(field.Value))
                    .AppendLine(")");
            }

            var fileIndex = 0;
            foreach (var file in request.UploadFiles ?? Array.Empty<ComposedUploadFile>())
            {
                fileIndex++;
                builder
                    .Append("    part")
                    .Append(fileIndex)
                    .Append(", err := writer.CreateFormFile(")
                    .Append(GoString(file.FieldName))
                    .Append(", ")
                    .Append(GoString(Path.GetFileName(file.FilePath)))
                    .AppendLine(")")
                    .AppendLine("    if err != nil {")
                    .AppendLine("        panic(err)")
                    .AppendLine("    }")
                    .Append("    data")
                    .Append(fileIndex)
                    .Append(", err := os.ReadFile(")
                    .Append(GoString(file.FilePath))
                    .AppendLine(")")
                    .AppendLine("    if err != nil {")
                    .AppendLine("        panic(err)")
                    .AppendLine("    }")
                    .Append("    part")
                    .Append(fileIndex)
                    .AppendLine(".Write(data" + fileIndex + ")");
            }

            builder
                .AppendLine("    writer.Close()")
                .Append("    req, err := http.NewRequest(")
                .Append(GoString(request.Method))
                .Append(", ")
                .Append(GoString(request.Url))
                .AppendLine(", &body)");
        }
        else
        {
            builder
                .Append("    req, err := http.NewRequest(")
                .Append(GoString(request.Method))
                .Append(", ")
                .Append(GoString(request.Url))
                .Append(", ");
            builder.AppendLine(
                request.Body is null ? "nil)" : "strings.NewReader(" + GoString(request.Body) + "))"
            );
        }

        builder
            .AppendLine("    if err != nil {")
            .AppendLine("        panic(err)")
            .AppendLine("    }");
        foreach (var (key, value) in request.Headers)
        {
            builder
                .Append("    req.Header.Set(")
                .Append(GoString(key))
                .Append(", ")
                .Append(GoString(value))
                .AppendLine(")");
        }

        if (request.IsMultipart)
        {
            builder.AppendLine(
                "    req.Header.Set(\"Content-Type\", writer.FormDataContentType())"
            );
        }
        else if (
            !string.IsNullOrWhiteSpace(request.ContentType)
            && !request.Headers.Any(pair =>
                pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            builder
                .Append("    req.Header.Set(\"Content-Type\", ")
                .Append(GoString(request.ContentType))
                .AppendLine(")");
        }

        builder
            .AppendLine("    resp, err := http.DefaultClient.Do(req)")
            .AppendLine("    if err != nil {")
            .AppendLine("        panic(err)")
            .AppendLine("    }")
            .AppendLine("    defer resp.Body.Close()")
            .AppendLine("    responseBody, _ := io.ReadAll(resp.Body)")
            .AppendLine("    fmt.Println(resp.Status, string(responseBody))")
            .AppendLine("}");
        return builder.ToString();
    }

    private static string MethodTitle(string method) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(method.ToLowerInvariant());

    private static string PythonMethod(string method) =>
        method.ToLowerInvariant() switch
        {
            "get" => "get",
            "post" => "post",
            "put" => "put",
            "patch" => "patch",
            "delete" => "delete",
            "head" => "head",
            "options" => "options",
            _ => "request",
        };

    private static string MediaTypeName(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? "application/json" : contentType;

    private static string CSharpString(string value) =>
        "\""
        + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
        + "\"";

    private static string JavaScriptString(string value) =>
        "\""
        + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
        + "\"";

    private static string PythonString(string value) =>
        "\""
        + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
        + "\"";

    private static string JavaString(string value) => CSharpString(value);

    private static string GoString(string value) => CSharpString(value);
}
