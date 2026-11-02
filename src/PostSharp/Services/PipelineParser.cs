using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PostSharp.Models;

namespace PostSharp.Services;

public sealed record PipelineParseResult(
    AutomationPipeline Pipeline,
    IReadOnlyList<PipelineIssue> Issues
)
{
    public bool HasErrors => Issues.Any(issue => issue.IsError);
}

/// <summary>
/// 把 YAML/JSON 流水线文档编译成 <see cref="AutomationPipeline"/>：解析步骤、断言、重试、
/// 数据行和循环，并做静态校验（重复别名、未定义引用、缺失 url 等）。
/// </summary>
public static class PipelineParser
{
    private const string BaseUrlRoot = "baseUrl";

    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET",
        "POST",
        "PUT",
        "PATCH",
        "DELETE",
        "HEAD",
        "OPTIONS",
        "TRACE",
    };

    private static readonly Regex PlaceholderPattern = new(
        @"\$\{\s*([^{}]+?)\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    public static PipelineParseResult Parse(
        string content,
        string? sourcePath = null,
        string? baseDirectory = null,
        string? dataContent = null,
        string? dataFormat = null
    )
    {
        var issues = new List<PipelineIssue>();
        var directory =
            baseDirectory
            ?? (
                string.IsNullOrWhiteSpace(sourcePath)
                    ? Directory.GetCurrentDirectory()
                    : Path.GetDirectoryName(Path.GetFullPath(sourcePath))
                        ?? Directory.GetCurrentDirectory()
            );

        var root = ParseRoot(content, issues);
        if (root is not JsonObject document)
        {
            issues.Add(new PipelineIssue("$", "流水线根节点必须是对象。", true));
            return new PipelineParseResult(EmptyPipeline(sourcePath, directory), issues);
        }

        var retry = ParseRetry(document["retry"], "retry", issues) ?? new PipelineRetryPolicy();
        var steps = ParseSteps(document["steps"], "steps", issues);
        var data = ParseData(document["data"], directory, dataContent, dataFormat, issues);

        var pipeline = new AutomationPipeline
        {
            Name = Text(document["name"]) ?? "未命名流水线",
            BaseUrl = Text(document["baseUrl"])?.TrimEnd('/'),
            TimeoutSeconds = TimeoutSeconds(document, issues),
            StopOnError = Bool(document["stopOnError"]) ?? true,
            Retry = retry,
            Variables = ParseScalarMap(document["vars"], "vars", issues),
            Headers = ParseScalarMap(DefaultHeaders(document), "defaults.headers", issues),
            Steps = steps,
            Data = data,
            SourcePath = sourcePath,
            SourceDirectory = directory,
        };

        Validate(pipeline, issues);
        return new PipelineParseResult(pipeline, issues);
    }

    private static JsonNode? ParseRoot(string content, ICollection<PipelineIssue> issues)
    {
        var trimmed = content.TrimStart();
        if (trimmed.Length == 0)
        {
            issues.Add(new PipelineIssue("$", "流水线内容为空。", true));
            return null;
        }

        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return JsonNode.Parse(content);
            }
            catch (JsonException exception)
            {
                issues.Add(new PipelineIssue("$", $"JSON 解析失败：{exception.Message}", true));
                return null;
            }
        }

        try
        {
            return YamlJson.Parse(content, "流水线");
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new PipelineIssue("$", exception.Message, true));
            return null;
        }
    }

    private static AutomationPipeline EmptyPipeline(string? sourcePath, string directory) =>
        new() { SourcePath = sourcePath, SourceDirectory = directory };

    private static int TimeoutSeconds(JsonObject document, ICollection<PipelineIssue> issues)
    {
        if (Integer(document["timeoutSeconds"]) is { } seconds)
        {
            return Math.Clamp(seconds, 1, 3600);
        }

        if (Integer(document["timeoutMs"]) is { } milliseconds)
        {
            return Math.Clamp(milliseconds / 1000, 1, 3600);
        }

        if (document["timeoutSeconds"] is not null || document["timeoutMs"] is not null)
        {
            issues.Add(new PipelineIssue("timeout", "超时必须是正整数（秒或毫秒）。", true));
        }

        return 60;
    }

    private static IReadOnlyList<PipelineNode> ParseSteps(
        JsonNode? node,
        string path,
        ICollection<PipelineIssue> issues
    )
    {
        if (node is not JsonArray array)
        {
            if (node is not null)
            {
                issues.Add(new PipelineIssue(path, "steps 必须是数组。", true));
            }

            return Array.Empty<PipelineNode>();
        }

        var steps = new List<PipelineNode>();
        for (var index = 0; index < array.Count; index++)
        {
            var step = ParseNode(array[index], $"{path}[{index}]", issues);
            if (step is not null)
            {
                steps.Add(step);
            }
        }

        return steps;
    }

    private static PipelineNode? ParseNode(
        JsonNode? node,
        string path,
        ICollection<PipelineIssue> issues
    )
    {
        if (node is not JsonObject step)
        {
            issues.Add(new PipelineIssue(path, "步骤必须是对象。", true));
            return null;
        }

        if (step["loop"] is JsonObject loop)
        {
            return ParseLoop(loop, step, path, issues);
        }

        var method = (Text(step["method"]) ?? "GET").ToUpperInvariant();
        if (!Methods.Contains(method))
        {
            issues.Add(
                new PipelineIssue($"{path}.method", $"不支持的 HTTP 方法：{method}。", true)
            );
        }

        var url = Text(step["url"]);
        if (string.IsNullOrWhiteSpace(url))
        {
            issues.Add(new PipelineIssue($"{path}.url", "请求步骤缺少 url。", true));
        }

        return new PipelineNode
        {
            Kind = PipelineNodeKind.Request,
            Id = NormalizeAlias(Text(step["id"]) ?? Text(step["saveAs"]) ?? Text(step["as"])),
            Name = Text(step["name"]) ?? url ?? method,
            Method = method,
            Url = url,
            Headers = ParseHeaderList(step["headers"], $"{path}.headers", issues),
            JsonBody = step["json"]?.DeepClone(),
            RawBody = Text(step["body"]),
            ContentType = Text(step["contentType"]),
            Expectation = ParseExpectation(step["expect"], $"{path}.expect", issues),
            Retry = ParseRetry(step["retry"], $"{path}.retry", issues),
            OnFailure = NormalizeFailure(Text(step["onFailure"])),
        };
    }

    private static PipelineNode? ParseLoop(
        JsonObject loop,
        JsonObject step,
        string path,
        ICollection<PipelineIssue> issues
    )
    {
        var over = Text(loop["over"]) ?? Text(loop["from"]) ?? Text(loop["in"]);
        var alias = NormalizeAlias(Text(loop["as"]));
        if (string.IsNullOrWhiteSpace(over))
        {
            issues.Add(new PipelineIssue($"{path}.loop.over", "循环缺少 over 表达式。", true));
        }

        if (string.IsNullOrWhiteSpace(alias))
        {
            issues.Add(new PipelineIssue($"{path}.loop.as", "循环缺少 as 变量名。", true));
        }

        var children = ParseSteps(loop["steps"] ?? loop["do"], $"{path}.loop.steps", issues);
        if (children.Count == 0)
        {
            issues.Add(new PipelineIssue($"{path}.loop.steps", "循环体 steps 不能为空。", true));
        }

        var max = Integer(loop["max"]) ?? 1000;
        if (max is < 1 or > 10000)
        {
            issues.Add(new PipelineIssue($"{path}.loop.max", "循环次数上限必须在 1-10000。", true));
            max = Math.Clamp(max, 1, 10000);
        }

        return new PipelineNode
        {
            Kind = PipelineNodeKind.Loop,
            Id = NormalizeAlias(Text(step["id"] ?? loop["id"])),
            Name = Text(step["name"]) ?? $"循环 {alias}",
            LoopOver = over,
            LoopAlias = alias,
            LoopIndexAlias = NormalizeAlias(Text(loop["index"])),
            LoopMax = max,
            Children = children,
            OnFailure = NormalizeFailure(Text(step["onFailure"])),
        };
    }

    private static PipelineExpectation ParseExpectation(
        JsonNode? node,
        string path,
        ICollection<PipelineIssue> issues
    )
    {
        if (node is null)
        {
            return new PipelineExpectation();
        }

        if (node is JsonValue)
        {
            return new PipelineExpectation { Statuses = ParseStatuses(node, path, issues) };
        }

        if (node is not JsonObject expect)
        {
            issues.Add(new PipelineIssue(path, "expect 必须是正整数、状态码字符串或对象。", true));
            return new PipelineExpectation();
        }

        var bodyContains = new List<string>();
        var bodyNode = expect["bodyContains"];
        if (bodyNode is JsonValue)
        {
            bodyContains.Add(Text(bodyNode) ?? string.Empty);
        }
        else if (bodyNode is JsonArray bodyArray)
        {
            foreach (var item in bodyArray)
            {
                if (Text(item) is { Length: > 0 } text)
                {
                    bodyContains.Add(text);
                }
            }
        }

        var jsonPath = new List<PipelineJsonAssertion>();
        if (expect["jsonPath"] is JsonObject paths)
        {
            foreach (var entry in paths)
            {
                var assertion = ParseJsonAssertion(
                    entry.Key,
                    entry.Value,
                    $"{path}.jsonPath.{entry.Key}",
                    issues
                );
                if (assertion is not null)
                {
                    jsonPath.Add(assertion);
                }
            }
        }

        return new PipelineExpectation
        {
            Statuses = ParseStatuses(expect["status"], $"{path}.status", issues),
            BodyContains = bodyContains,
            JsonPath = jsonPath,
        };
    }

    private static PipelineJsonAssertion? ParseJsonAssertion(
        string path,
        JsonNode? node,
        string issuePath,
        ICollection<PipelineIssue> issues
    )
    {
        if (node is JsonObject rule)
        {
            if (rule["exists"] is JsonValue)
            {
                return new PipelineJsonAssertion
                {
                    Path = path,
                    Kind = "exists",
                    Expected = rule["exists"]?.DeepClone(),
                };
            }

            if (rule["equals"] is { } equals)
            {
                return new PipelineJsonAssertion
                {
                    Path = path,
                    Kind = "equals",
                    Expected = equals.DeepClone(),
                };
            }

            if (rule["contains"] is { } contains)
            {
                return new PipelineJsonAssertion
                {
                    Path = path,
                    Kind = "contains",
                    Expected = contains.DeepClone(),
                };
            }

            issues.Add(
                new PipelineIssue(
                    issuePath,
                    "断言对象支持 exists / equals / contains 三种写法。",
                    true
                )
            );
            return null;
        }

        return new PipelineJsonAssertion
        {
            Path = path,
            Kind = "equals",
            Expected = node?.DeepClone(),
        };
    }

    private static IReadOnlyList<int> ParseStatuses(
        JsonNode? node,
        string path,
        ICollection<PipelineIssue> issues
    )
    {
        if (node is null)
        {
            return Array.Empty<int>();
        }

        if (node is JsonArray array)
        {
            var statuses = new List<int>();
            foreach (var item in array)
            {
                statuses.AddRange(ParseStatuses(item, path, issues));
            }

            return statuses;
        }

        var text = Text(node);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<int>();
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
        {
            if (status is < 100 or > 599)
            {
                issues.Add(new PipelineIssue(path, $"状态码 {status} 不在 100-599 之间。", true));
                return Array.Empty<int>();
            }

            return new[] { status };
        }

        var normalized = text.Trim().ToLowerInvariant();
        if (normalized is "2xx" or "3xx" or "4xx" or "5xx")
        {
            var start = (normalized[0] - '0') * 100;
            return Enumerable.Range(start, 100).ToArray();
        }

        issues.Add(new PipelineIssue(path, $"无法识别的状态码：{text}。", true));
        return Array.Empty<int>();
    }

    private static PipelineRetryPolicy? ParseRetry(
        JsonNode? node,
        string path,
        ICollection<PipelineIssue> issues
    )
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue)
        {
            var count = Integer(node);
            if (count is null)
            {
                issues.Add(new PipelineIssue(path, "retry 必须是次数或对象。", true));
                return null;
            }

            return new PipelineRetryPolicy { Count = ClampRetry(count.Value, path, issues) };
        }

        if (node is not JsonObject retry)
        {
            issues.Add(new PipelineIssue(path, "retry 必须是次数或对象。", true));
            return null;
        }

        var statuses = new List<int>();
        var onNetwork = true;
        var on = retry["on"] ?? retry["statuses"];
        if (on is JsonArray array)
        {
            foreach (var item in array)
            {
                var text = Text(item);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (text.Equals("network", StringComparison.OrdinalIgnoreCase))
                {
                    onNetwork = true;
                    continue;
                }

                if (text.EndsWith("xx", StringComparison.OrdinalIgnoreCase))
                {
                    statuses.AddRange(ParseStatuses(item, $"{path}.on", issues));
                    continue;
                }

                statuses.AddRange(ParseStatuses(item, $"{path}.on", issues));
            }
        }

        var delay = Integer(retry["delayMs"]) ?? 500;
        return new PipelineRetryPolicy
        {
            Count = ClampRetry(
                Integer(retry["count"]) ?? Integer(retry["times"]) ?? 0,
                path,
                issues
            ),
            DelayMs = Math.Clamp(delay, 0, 60000),
            Statuses = statuses,
            OnNetworkError = Bool(retry["onNetworkError"]) ?? onNetwork,
        };
    }

    private static int ClampRetry(int count, string path, ICollection<PipelineIssue> issues)
    {
        if (count < 0 || count > PipelineRetryPolicy.MaxCount)
        {
            issues.Add(
                new PipelineIssue(path, $"重试次数必须在 0-{PipelineRetryPolicy.MaxCount}。", true)
            );
        }

        return Math.Clamp(count, 0, PipelineRetryPolicy.MaxCount);
    }

    private static IReadOnlyList<PipelineDataRow> ParseData(
        JsonNode? node,
        string directory,
        string? dataContent,
        string? dataFormat,
        ICollection<PipelineIssue> issues
    )
    {
        JsonArray? rows = null;
        if (!string.IsNullOrWhiteSpace(dataContent))
        {
            try
            {
                rows = PipelineDataLoader.Parse(dataContent, dataFormat ?? "csv");
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or JsonException)
            {
                issues.Add(new PipelineIssue("data", exception.Message, true));
            }
        }
        else if (node is JsonArray array)
        {
            rows = array;
        }
        else if (node is JsonObject data)
        {
            if (!string.IsNullOrWhiteSpace(Text(data["content"])))
            {
                try
                {
                    rows = PipelineDataLoader.Parse(
                        Text(data["content"]) ?? string.Empty,
                        Text(data["format"]) ?? "csv"
                    );
                }
                catch (Exception exception)
                    when (exception is InvalidOperationException or JsonException)
                {
                    issues.Add(new PipelineIssue("data", exception.Message, true));
                }
            }
            else if (Text(data["file"]) is { Length: > 0 } file)
            {
                var path = Path.IsPathRooted(file) ? file : Path.Combine(directory, file);
                if (!File.Exists(path))
                {
                    issues.Add(new PipelineIssue("data.file", $"数据文件不存在：{path}。", true));
                }
                else
                {
                    try
                    {
                        rows = PipelineDataLoader.Parse(
                            File.ReadAllText(path),
                            PipelineDataLoader.FormatOf(path)
                        );
                    }
                    catch (Exception exception)
                        when (exception is InvalidOperationException or JsonException)
                    {
                        issues.Add(new PipelineIssue("data.file", exception.Message, true));
                    }
                }
            }
            else if (data["rows"] is JsonArray inline)
            {
                rows = inline;
            }
        }

        if (rows is null)
        {
            return Array.Empty<PipelineDataRow>();
        }

        var result = new List<PipelineDataRow>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row is not JsonObject)
            {
                issues.Add(new PipelineIssue($"data[{index}]", "数据行必须是对象。", true));
                continue;
            }

            result.Add(new PipelineDataRow(index, row.DeepClone()));
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseScalarMap(
        JsonNode? node,
        string path,
        ICollection<PipelineIssue> issues
    )
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is null)
        {
            return result;
        }

        if (node is not JsonObject map)
        {
            issues.Add(new PipelineIssue(path, "必须是键值对象。", true));
            return result;
        }

        foreach (var entry in map)
        {
            if (Text(entry.Value) is { } value)
            {
                result[entry.Key] = value;
            }
        }

        return result;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ParseHeaderList(
        JsonNode? node,
        string path,
        ICollection<PipelineIssue> issues
    )
    {
        var result = new List<KeyValuePair<string, string>>();
        if (node is null)
        {
            return result;
        }

        if (node is not JsonObject map)
        {
            issues.Add(new PipelineIssue(path, "headers 必须是键值对象。", true));
            return result;
        }

        foreach (var entry in map)
        {
            if (Text(entry.Value) is { } value)
            {
                result.Add(new KeyValuePair<string, string>(entry.Key, value));
            }
        }

        return result;
    }

    private static JsonNode? DefaultHeaders(JsonObject document) =>
        document["defaults"] is JsonObject defaults ? defaults["headers"] : null;

    private static string? NormalizeAlias(string? alias) =>
        string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();

    private static string? NormalizeFailure(string? action) =>
        action?.Trim().ToLowerInvariant() switch
        {
            "continue" or "ignore" => "continue",
            "stop" or "abort" => "stop",
            _ => null,
        };

    private static string? Text(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value.ToJsonString();
    }

    private static int? Integer(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<long>(out var wide) && wide is >= int.MinValue and <= int.MaxValue)
        {
            return (int)wide;
        }

        return int.TryParse(
            Text(node),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed
        )
            ? parsed
            : null;
    }

    private static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return bool.TryParse(Text(node), out var parsed) ? parsed : null;
    }

    private static void Validate(AutomationPipeline pipeline, List<PipelineIssue> issues)
    {
        if (pipeline.Steps.Count == 0)
        {
            issues.Add(new PipelineIssue("steps", "流水线至少需要一个步骤。", true));
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal) { BaseUrlRoot, "row", "data" };
        foreach (var name in pipeline.Variables.Keys)
        {
            allowed.Add(name);
        }

        foreach (var row in pipeline.Data)
        {
            if (row.Value is JsonObject values)
            {
                foreach (var key in values.Select(entry => entry.Key))
                {
                    allowed.Add(key);
                }
            }
        }

        var seenAliases = new HashSet<string>(StringComparer.Ordinal);
        WalkNodes(pipeline.Steps, "steps", allowed, seenAliases, issues);
    }

    private static void WalkNodes(
        IReadOnlyList<PipelineNode> nodes,
        string path,
        HashSet<string> allowed,
        HashSet<string> seenAliases,
        List<PipelineIssue> issues
    )
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var nodePath = $"{path}[{index}]";
            if (node.Kind == PipelineNodeKind.Loop)
            {
                ValidateText(node.LoopOver, $"{nodePath}.loop.over", allowed, issues);
                var nested = new HashSet<string>(allowed, StringComparer.Ordinal);
                if (node.LoopAlias is { } alias)
                {
                    nested.Add(alias);
                }

                if (node.LoopIndexAlias is { } indexAlias)
                {
                    nested.Add(indexAlias);
                }

                WalkNodes(node.Children, $"{nodePath}.loop.steps", nested, seenAliases, issues);
                continue;
            }

            ValidateText(node.Url, $"{nodePath}.url", allowed, issues);
            foreach (var header in node.Headers)
            {
                ValidateText(header.Value, $"{nodePath}.headers.{header.Key}", allowed, issues);
            }

            ValidateBody(node.JsonBody, $"{nodePath}.json", allowed, issues);
            ValidateText(node.RawBody, $"{nodePath}.body", allowed, issues);
            foreach (var contains in node.Expectation.BodyContains)
            {
                ValidateText(contains, $"{nodePath}.expect.bodyContains", allowed, issues);
            }

            if (node.Id is { } id)
            {
                if (!seenAliases.Add(id))
                {
                    issues.Add(new PipelineIssue($"{nodePath}.id", $"步骤别名 {id} 重复。", true));
                }

                allowed.Add(id);
            }
        }
    }

    private static void ValidateBody(
        JsonNode? node,
        string path,
        HashSet<string> allowed,
        List<PipelineIssue> issues
    )
    {
        switch (node)
        {
            case JsonObject body:
                foreach (var entry in body)
                {
                    ValidateBody(entry.Value, $"{path}.{entry.Key}", allowed, issues);
                }

                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    ValidateBody(array[index], $"{path}[{index}]", allowed, issues);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                ValidateText(text, path, allowed, issues);
                break;
        }
    }

    private static void ValidateText(
        string? text,
        string path,
        HashSet<string> allowed,
        List<PipelineIssue> issues
    )
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (Match match in PlaceholderPattern.Matches(text))
        {
            var reference = match.Groups[1].Value.Trim();
            var root = reference.Split('.', '[')[0];
            if (!allowed.Contains(root))
            {
                issues.Add(
                    new PipelineIssue(
                        path,
                        $"未定义的引用 ${{{reference}}}；可用的名称：{string.Join("、", allowed.OrderBy(name => name))}。",
                        true
                    )
                );
            }
        }
    }
}
