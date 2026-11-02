using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PostSharp.Services;

/// <summary>流水线执行期错误：引用不存在、循环数据不是数组等。</summary>
public sealed class PipelineRunException : Exception
{
    public PipelineRunException(string message)
        : base(message) { }
}

/// <summary>
/// 解析 <c>${respA.id}</c> 这类模板：整串只含一个占位符时保留原始类型（数字、对象、
/// 数组），与普通文本混排时按字符串拼接。
/// </summary>
public static class PipelineTemplate
{
    private static readonly Regex Pattern = new(
        @"\$\{\s*([^{}]+?)\s*\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    public static string ResolveText(
        string template,
        IReadOnlyDictionary<string, JsonNode?> roots,
        string location
    )
    {
        if (!template.Contains("${", StringComparison.Ordinal))
        {
            return template;
        }

        var matches = Pattern.Matches(template);
        if (matches.Count == 1 && matches[0].Index == 0 && matches[0].Length == template.Length)
        {
            return Render(Resolve(matches[0].Groups[1].Value, roots, location));
        }

        var builder = new StringBuilder(template.Length + 16);
        var position = 0;
        foreach (Match match in matches)
        {
            builder.Append(template, position, match.Index - position);
            builder.Append(Render(Resolve(match.Groups[1].Value, roots, location)));
            position = match.Index + match.Length;
        }

        builder.Append(template, position, template.Length - position);
        return builder.ToString();
    }

    public static JsonNode? ResolveNode(
        JsonNode? node,
        IReadOnlyDictionary<string, JsonNode?> roots,
        string location
    )
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject source:
                var result = new JsonObject();
                foreach (var entry in source)
                {
                    result[entry.Key] = ResolveNode(entry.Value, roots, $"{location}.{entry.Key}");
                }

                return result;
            case JsonArray array:
                var output = new JsonArray();
                for (var index = 0; index < array.Count; index++)
                {
                    output.Add(ResolveNode(array[index], roots, $"{location}[{index}]"));
                }

                return output;
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (!text.Contains("${", StringComparison.Ordinal))
                {
                    return JsonValue.Create(text);
                }

                var match = Pattern.Match(text);
                if (match.Success && match.Index == 0 && match.Length == text.Length)
                {
                    var resolved = Resolve(match.Groups[1].Value, roots, location);
                    return resolved?.DeepClone();
                }

                return JsonValue.Create(ResolveText(text, roots, location));
            default:
                return node.DeepClone();
        }
    }

    /// <summary>把表达式求值成节点；不是占位符时按普通字符串处理。</summary>
    public static JsonNode? Evaluate(
        string expression,
        IReadOnlyDictionary<string, JsonNode?> roots,
        string location
    )
    {
        var match = Pattern.Match(expression);
        if (
            expression.Contains("${", StringComparison.Ordinal)
            && match.Success
            && match.Index == 0
            && match.Length == expression.Length
        )
        {
            return Resolve(match.Groups[1].Value, roots, location);
        }

        return JsonValue.Create(expression);
    }

    public static JsonNode? Resolve(
        string reference,
        IReadOnlyDictionary<string, JsonNode?> roots,
        string location
    )
    {
        var segments = ParsePath(reference);
        if (segments.Count == 0)
        {
            throw new PipelineRunException($"{location}：引用 ${{{reference}}} 为空。");
        }

        if (segments[0] is not string rootName)
        {
            throw new PipelineRunException($"{location}：引用 ${{{reference}}} 写法无效。");
        }

        if (!roots.TryGetValue(rootName, out var current))
        {
            throw new PipelineRunException(
                $"{location}：引用 ${{{reference}}} 里的 {rootName} 还没有值；请确认它来自更早的步骤、变量或数据行。"
            );
        }

        for (var index = 1; index < segments.Count; index++)
        {
            current = segments[index] switch
            {
                string name when current is JsonObject map => map[name],
                int position when current is JsonArray array => position >= 0
                && position < array.Count
                    ? array[position]
                    : null,
                _ => null,
            };

            if (current is null)
            {
                throw new PipelineRunException($"{location}：引用 ${{{reference}}} 的字段不存在。");
            }
        }

        return current;
    }

    public static JsonNode? GetByPath(JsonNode? root, string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith("$.", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        var segments = ParsePath(trimmed);
        var current = root;
        foreach (var segment in segments)
        {
            current = segment switch
            {
                string name when current is JsonObject map => map.TryGetPropertyValue(
                    name,
                    out var value
                )
                    ? value
                    : null,
                int position when current is JsonArray array => position >= 0
                && position < array.Count
                    ? array[position]
                    : null,
                _ => null,
            };

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static List<object> ParsePath(string reference)
    {
        var segments = new List<object>();
        var current = new StringBuilder();
        for (var index = 0; index < reference.Length; index++)
        {
            var character = reference[index];
            switch (character)
            {
                case '.':
                    if (current.Length > 0)
                    {
                        segments.Add(current.ToString());
                        current.Clear();
                    }

                    break;
                case '[':
                    if (current.Length > 0)
                    {
                        segments.Add(current.ToString());
                        current.Clear();
                    }

                    var end = reference.IndexOf(']', index);
                    if (end < 0)
                    {
                        current.Append(character);
                        break;
                    }

                    var inner = reference[(index + 1)..end].Trim();
                    if (
                        int.TryParse(
                            inner,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var position
                        )
                    )
                    {
                        segments.Add(position);
                    }
                    else
                    {
                        segments.Add(inner.Trim('"', '\''));
                    }

                    index = end;
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        return segments;
    }

    private static string Render(JsonNode? value)
    {
        return value switch
        {
            null => string.Empty,
            JsonValue json when json.TryGetValue<string>(out var text) => text,
            JsonValue json => json.ToJsonString(),
            _ => value.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
        };
    }
}
