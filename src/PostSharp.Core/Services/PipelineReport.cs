using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>把流水线运行结果写成 Markdown / JUnit / JSON，供 CI 与报告页直接读取。</summary>
public static class PipelineReport
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Create(PipelineRunResult result, string format)
    {
        return format.ToLowerInvariant() switch
        {
            "json" => CreateJson(result),
            "junit" => CreateJUnit(result),
            _ => CreateMarkdown(result),
        };
    }

    public static (string FileName, string Path) Write(
        PipelineRunResult result,
        string format,
        string? directory = null
    )
    {
        var normalized = format.ToLowerInvariant();
        var extension = normalized switch
        {
            "json" => "json",
            "junit" => "xml",
            _ => "md",
        };
        var fileName =
            $"pipeline-{SafeName(result.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}";
        var target = directory ?? Path.Combine(Environment.CurrentDirectory, "reports");
        Directory.CreateDirectory(target);
        var path = Path.Combine(target, fileName);
        File.WriteAllText(
            path,
            Create(result, normalized),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        return (fileName, path);
    }

    private static string SafeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        var safe = builder.ToString().Trim('-');
        return safe.Length == 0 ? "run" : safe;
    }

    private static string CreateJson(PipelineRunResult result)
    {
        var cases = new JsonArray();
        foreach (var item in result.Cases)
        {
            var steps = new JsonArray();
            foreach (var step in item.Steps)
            {
                steps.Add(
                    (JsonNode)
                        new JsonObject
                        {
                            ["id"] = step.Id,
                            ["name"] = step.Name,
                            ["method"] = step.Method,
                            ["url"] = step.Url,
                            ["statusCode"] = step.StatusCode,
                            ["passed"] = step.Passed,
                            ["attempts"] = step.Attempts,
                            ["elapsedMs"] = step.ElapsedMs,
                            ["error"] = step.Error,
                            ["assertions"] = new JsonArray(
                                step.Assertions.Select(text => (JsonNode)JsonValue.Create(text)!)
                                    .ToArray()
                            ),
                        }
                );
            }

            cases.Add(
                (JsonNode)
                    new JsonObject
                    {
                        ["index"] = item.Index,
                        ["label"] = item.Label,
                        ["passed"] = item.Passed,
                        ["elapsedMs"] = item.ElapsedMs,
                        ["steps"] = steps,
                    }
            );
        }

        var document = new JsonObject
        {
            ["name"] = result.Name,
            ["passed"] = result.Passed,
            ["elapsedMs"] = result.ElapsedMs,
            ["cases"] = cases,
            ["notes"] = new JsonArray(
                result.Notes.Select(text => (JsonNode)JsonValue.Create(text)!).ToArray()
            ),
        };
        return document.ToJsonString(ReportJsonOptions);
    }

    private static string CreateMarkdown(PipelineRunResult result)
    {
        var builder = new StringBuilder();
        builder.Append("# 流水线报告：").AppendLine(result.Name);
        builder.AppendLine();
        builder.Append("- 结果：").AppendLine(result.Passed ? "通过" : "失败");
        builder
            .Append("- 数据组：")
            .AppendLine(result.Cases.Count.ToString(CultureInfo.InvariantCulture));
        builder
            .Append("- 耗时：")
            .Append(result.ElapsedMs.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" ms");
        builder.AppendLine();

        foreach (var item in result.Cases)
        {
            builder
                .Append("## ")
                .Append(item.Label)
                .Append(item.Passed ? " · 通过" : " · 失败")
                .Append("（")
                .Append(item.ElapsedMs.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" ms）");
            builder.AppendLine();
            builder.AppendLine("| 步骤 | 方法 | 地址 | 状态 | 次数 | 结果 |");
            builder.AppendLine("| --- | --- | --- | ---: | ---: | --- |");
            foreach (var step in item.Steps)
            {
                builder
                    .Append("| ")
                    .Append(Escape(step.Name))
                    .Append(" | ")
                    .Append(step.Method)
                    .Append(" | ")
                    .Append(Escape(step.Url))
                    .Append(" | ")
                    .Append(step.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-")
                    .Append(" | ")
                    .Append(step.Attempts.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ")
                    .Append(step.Passed ? "通过" : "失败")
                    .AppendLine(" |");
            }

            builder.AppendLine();
            foreach (var step in item.Steps.Where(step => !step.Passed))
            {
                builder
                    .Append("- 失败：")
                    .Append(step.Name)
                    .Append(" — ")
                    .AppendLine(step.Error ?? "未知原因");
            }
        }

        if (result.Notes.Count > 0)
        {
            builder.AppendLine("## 说明");
            builder.AppendLine();
            foreach (var note in result.Notes)
            {
                builder.Append("- ").AppendLine(note);
            }
        }

        return builder.ToString();
    }

    private static string CreateJUnit(PipelineRunResult result)
    {
        var failures = result.Cases.Sum(item => item.Steps.Count(step => !step.Passed));
        var tests = result.Cases.Sum(item => item.Steps.Count);
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append("<testsuites>");
        builder
            .Append("<testsuite name=\"")
            .Append(EscapeXml(result.Name))
            .Append("\" tests=\"")
            .Append(tests.ToString(CultureInfo.InvariantCulture))
            .Append("\" failures=\"")
            .Append(failures.ToString(CultureInfo.InvariantCulture))
            .Append("\" time=\"")
            .Append((result.ElapsedMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture))
            .AppendLine("\">");
        foreach (var item in result.Cases)
        {
            foreach (var step in item.Steps)
            {
                builder
                    .Append("  <testcase classname=\"")
                    .Append(EscapeXml(result.Name))
                    .Append("\" name=\"")
                    .Append(EscapeXml($"{item.Label} · {step.Name}"))
                    .Append("\" time=\"")
                    .Append(
                        (step.ElapsedMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture)
                    )
                    .Append('"');
                if (step.Passed)
                {
                    builder.AppendLine(" />");
                    continue;
                }

                builder.AppendLine(">");
                builder
                    .Append("    <failure message=\"")
                    .Append(EscapeXml(step.Error ?? "失败"))
                    .AppendLine("\" />");
                builder.AppendLine("  </testcase>");
            }
        }

        builder.AppendLine("</testsuite>");
        builder.AppendLine("</testsuites>");
        return builder.ToString();
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    private static string EscapeXml(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
