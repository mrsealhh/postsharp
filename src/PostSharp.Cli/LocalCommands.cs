using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Cli;

/// <summary>不经过 PostSharp 服务、直接在本机执行流水线的命令。</summary>
internal static class LocalCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 某些托管终端不允许改编码；保持默认即可。
        }

        var command = options.Arguments;
        if (command.Count < 2)
        {
            throw new CliException("--local 需要 pipeline 子命令。");
        }

        return (command[0], command[1]) switch
        {
            ("pipeline", "validate") => ValidateAsync(options, cancellationToken),
            ("pipeline", "graph") => GraphAsync(options),
            ("pipeline", "run") => await RunPipelineAsync(options, cancellationToken),
            _ => throw new CliException("--local 只支持 pipeline validate / graph / run。"),
        };
    }

    private static int ValidateAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var parsed = Parse(options);
        if (options.Json)
        {
            WriteJson(
                new
                {
                    name = parsed.Pipeline.Name,
                    valid = !parsed.HasErrors,
                    stepCount = parsed.Pipeline.Steps.Count,
                    caseCount = parsed.Pipeline.Data.Count,
                    issues = parsed.Issues,
                }
            );
        }
        else
        {
            Console.WriteLine(
                $"{parsed.Pipeline.Name}：{(parsed.HasErrors ? "校验失败" : "校验通过")}"
            );
            foreach (var issue in parsed.Issues)
            {
                Console.WriteLine(
                    $"  {(issue.IsError ? "错误" : "提示")} {issue.Path} {issue.Message}".TrimEnd()
                );
            }
        }

        return parsed.HasErrors ? 1 : 0;
    }

    private static int GraphAsync(CliOptions options)
    {
        var parsed = Parse(options);
        var graph = PipelineVisualization.Build(parsed.Pipeline, parsed.Issues);
        var format = (options.GetOption("--format", null) ?? "mermaid").Trim().ToLowerInvariant();
        switch (format)
        {
            case "json":
                WriteJson(graph);
                break;
            case "dot":
                Console.WriteLine(graph.Dot);
                break;
            case "mermaid":
            case "md":
                Console.WriteLine(graph.Mermaid);
                break;
            default:
                throw new CliException("--format 只支持 mermaid / dot / json。");
        }

        return parsed.HasErrors ? 1 : 0;
    }

    private static async Task<int> RunPipelineAsync(
        CliOptions options,
        CancellationToken cancellationToken
    )
    {
        var parsed = Parse(options);
        if (parsed.HasErrors)
        {
            foreach (var issue in parsed.Issues.Where(issue => issue.IsError))
            {
                Console.Error.WriteLine($"{issue.Path} {issue.Message}".TrimEnd());
            }

            return 1;
        }

        var variables = ReadVariables(options);
        var runOptions = new PipelineRunOptions
        {
            BaseUrl = options.GetOption("--base-url", null),
            Variables = variables,
            DryRun = options.GetOption("--dry-run", null) is not null,
        };

        using var http = new HttpPipelineClient();
        var runner = new PipelineRunner(http);
        var result = await runner
            .RunAsync(parsed.Pipeline, runOptions, cancellationToken)
            .ConfigureAwait(false);

        var reportPath = WriteReport(result, options);
        if (options.Json)
        {
            var node =
                JsonSerializer.SerializeToNode(result, JsonOptions) as JsonObject
                ?? new JsonObject();
            if (reportPath is not null)
            {
                node["reportPath"] = reportPath;
            }

            WriteJson(node);
        }
        else
        {
            Console.WriteLine(
                $"{result.Name}：{(result.Passed ? "通过" : "失败")} · {result.Cases.Count} 个用例 · {result.ElapsedMs} ms"
            );
            foreach (var testCase in result.Cases)
            {
                Console.WriteLine(
                    $"  用例 {testCase.Index + 1}：{(testCase.Passed ? "通过" : "失败")} · {testCase.ElapsedMs} ms"
                );
                foreach (var step in testCase.Steps)
                {
                    var status = step.StatusCode is { } code
                        ? code.ToString(CultureInfo.InvariantCulture)
                        : "-";
                    Console.WriteLine(
                        $"    {(step.Passed ? "✓" : "✗")} {step.Name} {step.Method} {step.Url} · {status} · {step.ElapsedMs} ms"
                    );
                    if (!string.IsNullOrWhiteSpace(step.Error))
                    {
                        Console.WriteLine($"        {step.Error}");
                    }

                    foreach (var assertion in step.Assertions)
                    {
                        Console.WriteLine($"        {assertion}");
                    }
                }
            }

            foreach (var note in result.Notes)
            {
                Console.WriteLine($"  {note}");
            }

            if (reportPath is not null)
            {
                Console.WriteLine($"报告：{reportPath}");
            }
        }

        return result.Passed ? 0 : 1;
    }

    private static PipelineParseResult Parse(CliOptions options)
    {
        var path = RequireArgument(options, 2, "pipeline <命令> <文件>");
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new CliException($"流水线文件不存在：{fullPath}");
        }

        string? dataContent = null;
        string? dataFormat = null;
        if (options.GetOption("--data", null) is { Length: > 0 } dataPath)
        {
            var dataFullPath = Path.GetFullPath(dataPath);
            if (!File.Exists(dataFullPath))
            {
                throw new CliException($"数据文件不存在：{dataFullPath}");
            }

            dataContent = File.ReadAllText(dataFullPath);
            dataFormat = Path.GetExtension(dataFullPath)
                .Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? "json"
                : "csv";
        }

        return PipelineParser.Parse(
            File.ReadAllText(fullPath),
            fullPath,
            baseDirectory: null,
            dataContent,
            dataFormat
        );
    }

    private static IReadOnlyDictionary<string, string> ReadVariables(CliOptions options)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in options.Variables)
        {
            var index = pair.IndexOf('=');
            if (index <= 0)
            {
                throw new CliException($"--var 需要 key=value 形式：{pair}");
            }

            variables[pair[..index]] = pair[(index + 1)..];
        }

        return variables;
    }

    private static string? WriteReport(PipelineRunResult result, CliOptions options)
    {
        var format = options.GetOption("--format", null);
        var copyTo = options.GetOption("--copy-report", null);
        if (string.IsNullOrWhiteSpace(format) && !string.IsNullOrWhiteSpace(copyTo))
        {
            format = "junit";
        }

        if (string.IsNullOrWhiteSpace(format))
        {
            return null;
        }

        var content = PipelineReport.Create(result, format);
        if (!string.IsNullOrWhiteSpace(copyTo))
        {
            var target = Path.GetFullPath(copyTo);
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(target, content, new UTF8Encoding(false));
            return target;
        }

        return PipelineReport.Write(result, format).Path;
    }

    private static string RequireArgument(CliOptions options, int index, string usage)
    {
        if (options.Arguments.Count <= index || string.IsNullOrWhiteSpace(options.Arguments[index]))
        {
            throw new CliException($"缺少参数。用法：postsharp-cli {usage}");
        }

        return options.Arguments[index];
    }

    private static void WriteJson(object value) =>
        Console.WriteLine(
            value is JsonNode node
                ? node.ToJsonString(JsonOptions)
                : JsonSerializer.Serialize(value, JsonOptions)
        );
}
