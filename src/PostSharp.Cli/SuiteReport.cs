using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PostSharp.Cli;

internal static class SuiteReport
{
    public static string BuildMarkdown(string json)
    {
        var report = Read(json);
        var builder = new StringBuilder();
        builder.AppendLine("# PostSharp 套件报告");
        builder.AppendLine();
        builder.AppendLine($"- 套件：{report.Name}");
        builder.AppendLine(
            $"- 结果：{report.Passed} 通过 / {report.Failed} 失败 / {report.Skipped} 跳过，共 {report.Total} 步"
        );
        builder.AppendLine(
            $"- 耗时：{(report.TotalMilliseconds / 1000d).ToString("0.000", CultureInfo.InvariantCulture)} 秒"
        );
        builder.AppendLine();

        if (report.Steps.Count == 0)
        {
            builder.AppendLine("没有步骤结果。");
            return builder.ToString();
        }

        builder.AppendLine("| 步骤 | 结果 | 耗时 | 状态码 | 错误 |");
        builder.AppendLine("| --- | --- | ---: | ---: | --- |");
        foreach (var step in report.Steps)
        {
            builder.AppendLine(
                $"| {EscapeMarkdown(step.Name)} | {step.ResultText} | {(step.ElapsedMilliseconds / 1000d).ToString("0.000", CultureInfo.InvariantCulture)} 秒 | {step.StatusCode} | {EscapeMarkdown(step.Error)} |"
            );
        }

        return builder.ToString();
    }

    public static string BuildHtml(string json)
    {
        var report = Read(json);
        var title = WebUtility.HtmlEncode($"PostSharp · {report.Name}");
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"zh-CN\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine($"<title>{title}</title>");
        builder.AppendLine(
            "<style>body{font-family:Segoe UI,system-ui,sans-serif;margin:32px;color:#1f2328;background:#fff}table{border-collapse:collapse;width:100%;margin-top:20px}th,td{border:1px solid #d0d7de;padding:8px 10px;text-align:left}th{background:#f6f8fa}td.num{text-align:right;white-space:nowrap}.pass{color:#1a7f37;font-weight:600}.fail{color:#cf222e;font-weight:600}.skip{color:#6e7781;font-weight:600}.summary{display:flex;gap:24px;margin:16px 0;flex-wrap:wrap}.summary b{font-size:20px}</style>"
        );
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine($"<h1>{title}</h1>");
        builder.AppendLine("<div class=\"summary\">");
        builder.AppendLine($"<span>通过 <b>{report.Passed}</b></span>");
        builder.AppendLine($"<span>失败 <b>{report.Failed}</b></span>");
        builder.AppendLine($"<span>跳过 <b>{report.Skipped}</b></span>");
        builder.AppendLine(
            $"<span>总耗时 <b>{(report.TotalMilliseconds / 1000d).ToString("0.000", CultureInfo.InvariantCulture)}s</b></span>"
        );
        builder.AppendLine("</div>");

        if (report.Steps.Count == 0)
        {
            builder.AppendLine("<p>没有步骤结果。</p>");
        }
        else
        {
            builder.AppendLine("<table>");
            builder.AppendLine(
                "<thead><tr><th>步骤</th><th>结果</th><th>耗时</th><th>状态码</th><th>错误</th></tr></thead>"
            );
            builder.AppendLine("<tbody>");
            foreach (var step in report.Steps)
            {
                var css = step.ResultText switch
                {
                    "通过" => "pass",
                    "失败" => "fail",
                    _ => "skip",
                };
                builder.AppendLine(
                    $"<tr><td>{WebUtility.HtmlEncode(step.Name)}</td><td class=\"{css}\">{step.ResultText}</td><td class=\"num\">{(step.ElapsedMilliseconds / 1000d).ToString("0.000", CultureInfo.InvariantCulture)}s</td><td class=\"num\">{step.StatusCode}</td><td>{WebUtility.HtmlEncode(step.Error)}</td></tr>"
                );
            }

            builder.AppendLine("</tbody>");
            builder.AppendLine("</table>");
        }

        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    public static void WriteMarkdown(string json, string path) =>
        WriteFile(path, BuildMarkdown(json), "Markdown");

    public static void WriteHtml(string json, string path) =>
        WriteFile(path, BuildHtml(json), "HTML");

    private static void WriteFile(string path, string content, string label)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        Console.Error.WriteLine($"{label} 报告：{fullPath}");
    }

    private static Report Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var name = Text(root, "suiteName");
        if (name.Length == 0)
        {
            name = Text(root, "name");
        }

        var steps =
            root.TryGetProperty("steps", out var stepsNode)
            && stepsNode.ValueKind == JsonValueKind.Array
                ? stepsNode.EnumerateArray().ToList()
                : [];
        var result = new List<ReportStep>(steps.Count);
        foreach (var step in steps)
        {
            var stepName = Text(step, "name");
            if (stepName.Length == 0)
            {
                stepName = "step";
            }

            var resultText =
                Bool(step, "skipped") ? "跳过"
                : Bool(step, "passed") ? "通过"
                : "失败";
            result.Add(
                new ReportStep(
                    stepName,
                    resultText,
                    Number(step, "elapsedMs"),
                    Number(step, "statusCode"),
                    Text(step, "error")
                )
            );
        }

        return new Report(
            name.Length > 0 ? name : "PostSharp Suite",
            result,
            result.Count(step => step.ResultText == "通过"),
            result.Count(step => step.ResultText == "失败"),
            result.Count(step => step.ResultText == "跳过"),
            result.Sum(step => step.ElapsedMilliseconds)
        );
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private sealed record Report(
        string Name,
        IReadOnlyList<ReportStep> Steps,
        int Passed,
        int Failed,
        int Skipped,
        long TotalMilliseconds
    )
    {
        public int Total => Steps.Count;
    }

    private sealed record ReportStep(
        string Name,
        string ResultText,
        long ElapsedMilliseconds,
        long StatusCode,
        string Error
    );
}
