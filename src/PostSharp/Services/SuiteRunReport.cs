using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>A generated run report; <see cref="Content"/> is capped for MCP clients.</summary>
public sealed record SuiteReportExport(
    string Format,
    string FileName,
    string Content,
    int Bytes,
    bool Truncated,
    string? Path = null
);

/// <summary>
/// Renders a completed run as Markdown, HTML, JUnit XML or JSON. Reports are derived from the run
/// model directly, so a step that never ran and a failed assertion stay visible instead of being
/// flattened into a summary line.
/// </summary>
public static class SuiteRunReport
{
    public const int DefaultContentLimit = 60_000;

    public static readonly IReadOnlyList<string> Formats = ["markdown", "html", "junit", "json"];

    public static SuiteReportExport Export(
        TestRunResult run,
        string format,
        int contentLimit = DefaultContentLimit
    )
    {
        var normalized = NormalizeFormat(format);
        var content = Build(run, normalized);
        var limit = Math.Clamp(contentLimit, 1_000, 1_000_000);
        var truncated = content.Length > limit;
        return new SuiteReportExport(
            normalized,
            FileName(run, normalized),
            truncated ? content[..limit] + "\n…（报告已截断）" : content,
            Encoding.UTF8.GetByteCount(content),
            truncated
        );
    }

    public static string Build(TestRunResult run, string format) =>
        NormalizeFormat(format) switch
        {
            "html" => BuildHtml(run),
            "junit" => BuildJunit(run),
            "json" => McpJson.Serialize(run),
            _ => BuildMarkdown(run),
        };

    public static string BuildMarkdown(TestRunResult run)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# PostSharp 套件报告");
        builder.AppendLine();
        builder.AppendLine($"- 套件：{run.SuiteName}");
        builder.AppendLine($"- 运行：{run.Id}");
        builder.AppendLine($"- 时间：{run.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 结果：{Outcome(run)}");
        builder.AppendLine($"- 耗时：{Seconds(TotalElapsedMs(run))}");
        if (run.NodeStates.Count > 0)
        {
            builder.AppendLine($"- 工作流：{run.WorkflowSummary}");
        }

        if (run.Error is not null)
        {
            builder.AppendLine();
            builder.AppendLine($"**预检失败：** {run.Error}");
        }

        builder.AppendLine();
        if (run.Steps.Count == 0)
        {
            builder.AppendLine("没有步骤结果。");
            return builder.ToString();
        }

        builder.AppendLine("| 步骤 | 方法 | 结果 | 状态码 | 耗时 | 尝试 | 失败原因 |");
        builder.AppendLine("| --- | --- | --- | ---: | ---: | ---: | --- |");
        foreach (var step in run.Steps)
        {
            builder.AppendLine(
                $"| {EscapeMarkdown(step.Name)} | {step.Method} | {ResultText(step)} | {StatusText(step)} | {Seconds(step.ElapsedMs)} | {step.Attempts} | {EscapeMarkdown(step.Error ?? string.Empty)} |"
            );
        }

        var assertions = run
            .Steps.SelectMany(step =>
                step.Assertions.Where(assertion => !assertion.Passed)
                    .Select(assertion => (Step: step, Assertion: assertion))
            )
            .ToList();
        if (assertions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 失败断言");
            builder.AppendLine();
            foreach (var (step, assertion) in assertions)
            {
                builder.AppendLine(
                    $"- {EscapeMarkdown(step.Name)} · {EscapeMarkdown(assertion.Label)}：{EscapeMarkdown(assertion.Detail)}"
                );
            }
        }

        if (run.NodeStates.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 节点状态");
            builder.AppendLine();
            builder.AppendLine("| 节点 | 状态 | 耗时 | 说明 |");
            builder.AppendLine("| --- | --- | ---: | --- |");
            foreach (var node in run.NodeStates)
            {
                builder.AppendLine(
                    $"| {EscapeMarkdown(node.Name)} | {node.State} | {Seconds(node.ElapsedMs)} | {EscapeMarkdown(node.Detail ?? string.Empty)} |"
                );
            }
        }

        return builder.ToString();
    }

    public static string BuildHtml(TestRunResult run)
    {
        var title = WebUtility.HtmlEncode($"PostSharp · {run.SuiteName}");
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
        builder.AppendLine(
            $"<p>运行 {WebUtility.HtmlEncode(run.Id.ToString())} · {run.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {Seconds(TotalElapsedMs(run))}</p>"
        );
        builder.AppendLine("<div class=\"summary\">");
        builder.AppendLine($"<span>通过 <b>{run.Passed}</b></span>");
        builder.AppendLine($"<span>失败 <b>{run.Failed}</b></span>");
        builder.AppendLine($"<span>跳过 <b>{run.Skipped}</b></span>");
        builder.AppendLine($"<span>未运行 <b>{run.NotRun}</b></span>");
        builder.AppendLine("</div>");

        if (run.Error is not null)
        {
            builder.AppendLine(
                $"<p class=\"fail\">预检失败：{WebUtility.HtmlEncode(run.Error)}</p>"
            );
        }

        if (run.Steps.Count == 0)
        {
            builder.AppendLine("<p>没有步骤结果。</p>");
        }
        else
        {
            builder.AppendLine("<table>");
            builder.AppendLine(
                "<thead><tr><th>步骤</th><th>方法</th><th>结果</th><th>状态码</th><th>耗时</th><th>尝试</th><th>失败原因</th></tr></thead>"
            );
            builder.AppendLine("<tbody>");
            foreach (var step in run.Steps)
            {
                var css = CssClass(step);
                builder.AppendLine(
                    $"<tr><td>{WebUtility.HtmlEncode(step.Name)}</td><td>{WebUtility.HtmlEncode(step.Method)}</td><td class=\"{css}\">{ResultText(step)}</td><td class=\"num\">{StatusText(step)}</td><td class=\"num\">{Seconds(step.ElapsedMs)}</td><td class=\"num\">{step.Attempts}</td><td>{WebUtility.HtmlEncode(step.Error ?? string.Empty)}</td></tr>"
                );
            }

            builder.AppendLine("</tbody>");
            builder.AppendLine("</table>");
        }

        var assertions = run
            .Steps.SelectMany(step =>
                step.Assertions.Where(assertion => !assertion.Passed)
                    .Select(assertion => (Step: step, Assertion: assertion))
            )
            .ToList();
        if (assertions.Count > 0)
        {
            builder.AppendLine("<h2>失败断言</h2>");
            builder.AppendLine("<ul>");
            foreach (var (step, assertion) in assertions)
            {
                builder.AppendLine(
                    $"<li>{WebUtility.HtmlEncode(step.Name)} · {WebUtility.HtmlEncode(assertion.Label)}：{WebUtility.HtmlEncode(assertion.Detail)}</li>"
                );
            }

            builder.AppendLine("</ul>");
        }

        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    public static string BuildJunit(TestRunResult run)
    {
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using (var stream = new MemoryStream())
        {
            using (var writer = XmlWriter.Create(stream, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("testsuites");
                writer.WriteStartElement("testsuite");
                writer.WriteAttributeString("name", run.SuiteName);
                writer.WriteAttributeString(
                    "tests",
                    run.Total.ToString(CultureInfo.InvariantCulture)
                );
                writer.WriteAttributeString(
                    "failures",
                    run.Failed.ToString(CultureInfo.InvariantCulture)
                );
                writer.WriteAttributeString("errors", "0");
                writer.WriteAttributeString(
                    "skipped",
                    run.Skipped.ToString(CultureInfo.InvariantCulture)
                );
                writer.WriteAttributeString("time", SecondsAttribute(TotalElapsedMs(run)));
                writer.WriteAttributeString(
                    "timestamp",
                    run.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture)
                );

                foreach (var step in run.Steps)
                {
                    writer.WriteStartElement("testcase");
                    writer.WriteAttributeString("name", step.Name);
                    writer.WriteAttributeString("classname", run.SuiteName);
                    writer.WriteAttributeString("time", SecondsAttribute(step.ElapsedMs));
                    if (step.Skipped)
                    {
                        writer.WriteStartElement("skipped");
                        if (!string.IsNullOrWhiteSpace(step.Error))
                        {
                            writer.WriteAttributeString("message", step.Error);
                        }

                        writer.WriteEndElement();
                    }
                    else if (!step.Passed)
                    {
                        writer.WriteStartElement("failure");
                        writer.WriteAttributeString("message", step.Error ?? "步骤失败");
                        writer.WriteString(FailureDetail(step));
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }

            builder.Append(Encoding.UTF8.GetString(stream.ToArray()));
        }

        return builder.ToString();
    }

    public static string NormalizeFormat(string? format)
    {
        var normalized = (format ?? "markdown").Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "md" or "markdown" => "markdown",
            "html" or "htm" => "html",
            "junit" or "xml" => "junit",
            "json" => "json",
            _ => throw new InvalidOperationException(
                $"不支持的格式 {format}；可用格式：{string.Join("、", Formats)}。"
            ),
        };
    }

    private static string FileName(TestRunResult run, string format)
    {
        var extension = format switch
        {
            "html" => "html",
            "junit" => "xml",
            "json" => "json",
            _ => "md",
        };
        var name = string.Concat(
            (string.IsNullOrWhiteSpace(run.SuiteName) ? "suite" : run.SuiteName).Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '-' : character
            )
        );
        name = name.Trim().Trim('.');
        if (name.Length == 0)
        {
            name = "suite";
        }

        if (name.Length > 60)
        {
            name = name[..60];
        }

        return $"{name}-{run.Timestamp:yyyyMMdd-HHmmss}-{run.Id.ToString("N")[..8]}.{extension}";
    }

    private static string Outcome(TestRunResult run) =>
        $"{run.Passed} 通过 / {run.Failed} 失败 / {run.Skipped} 跳过"
        + (run.NotRun > 0 ? $" / {run.NotRun} 未运行" : string.Empty)
        + $"，共 {run.Total} 步";

    private static string ResultText(TestStepResult step) =>
        step.Skipped ? "跳过"
        : step.Passed ? "通过"
        : "失败";

    private static string CssClass(TestStepResult step) =>
        step.Skipped ? "skip"
        : step.Passed ? "pass"
        : "fail";

    private static string StatusText(TestStepResult step) =>
        step.StatusCode > 0 ? step.StatusCode.ToString(CultureInfo.InvariantCulture) : "无响应";

    private static long TotalElapsedMs(TestRunResult run) => run.Steps.Sum(step => step.ElapsedMs);

    private static string Seconds(long milliseconds) =>
        (milliseconds / 1000d).ToString("0.000", CultureInfo.InvariantCulture) + " 秒";

    private static string SecondsAttribute(long milliseconds) =>
        (milliseconds / 1000d).ToString("0.000", CultureInfo.InvariantCulture);

    private static string FailureDetail(TestStepResult step)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(step.Error))
        {
            lines.Add(step.Error);
        }

        foreach (var assertion in step.Assertions.Where(assertion => !assertion.Passed))
        {
            lines.Add($"{assertion.Label}：{assertion.Detail}");
        }

        return lines.Count == 0 ? "步骤失败" : string.Join("\n", lines);
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
