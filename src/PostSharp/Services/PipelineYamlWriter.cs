using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace PostSharp.Services;

/// <summary>把流水线 JSON 树写成可读 YAML（数组/对象/标量），供图像编辑器保存。</summary>
public static class PipelineYamlWriter
{
    public static string Write(JsonNode node)
    {
        var builder = new StringBuilder();
        EmitValue(builder, node, 0, inlineFirst: false);
        return builder.ToString();
    }

    private static void EmitValue(
        StringBuilder builder,
        JsonNode? node,
        int indent,
        bool inlineFirst
    )
    {
        switch (node)
        {
            case JsonObject map:
                EmitObject(builder, map, indent, inlineFirst);
                break;
            case JsonArray array:
                EmitArray(builder, array, indent);
                break;
            default:
                builder.Append(Scalar(node)).Append('\n');
                break;
        }
    }

    private static void EmitObject(
        StringBuilder builder,
        JsonObject map,
        int indent,
        bool inlineFirst
    )
    {
        var first = true;
        foreach (var entry in map)
        {
            var prefix = first && inlineFirst ? string.Empty : new string(' ', indent);
            builder.Append(prefix).Append(Key(entry.Key)).Append(':');
            if (entry.Value is JsonObject or JsonArray)
            {
                builder.Append('\n');
                EmitValue(builder, entry.Value, indent + 2, inlineFirst: false);
            }
            else
            {
                builder.Append(' ').Append(Scalar(entry.Value)).Append('\n');
            }

            first = false;
        }

        if (first)
        {
            builder.Append("{}\n");
        }
    }

    private static void EmitArray(StringBuilder builder, JsonArray array, int indent)
    {
        foreach (var item in array)
        {
            builder.Append(new string(' ', indent)).Append('-');
            switch (item)
            {
                case JsonObject map:
                    builder.Append(' ');
                    EmitObject(builder, map, indent + 2, inlineFirst: true);
                    break;
                case JsonArray nested:
                    builder.Append('\n');
                    EmitArray(builder, nested, indent + 2);
                    break;
                default:
                    builder.Append(' ').Append(Scalar(item)).Append('\n');
                    break;
            }
        }
    }

    private static string Scalar(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag))
            {
                return flag ? "true" : "false";
            }

            if (value.TryGetValue<long>(out var integer))
            {
                return integer.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number.ToString("R", CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<string>(out var text))
            {
                return Quote(text);
            }
        }

        return Quote(node.ToJsonString());
    }

    private static string Quote(string text) =>
        "\""
        + text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
        + "\"";

    private static string Key(string key) =>
        key.Length > 0
        && key.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
            ? key
            : Quote(key);
}
