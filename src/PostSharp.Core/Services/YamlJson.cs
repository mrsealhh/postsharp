using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace PostSharp.Services;

/// <summary>把 YAML 转成 <see cref="JsonNode"/> 树，不使用反射，NativeAOT 下同样可用。</summary>
public static class YamlJson
{
    public static JsonNode Parse(string content, string documentLabel)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(content);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
            {
                throw new InvalidOperationException($"{documentLabel} YAML 文档为空。");
            }

            return ConvertNode(stream.Documents[0].RootNode)
                ?? throw new InvalidOperationException($"{documentLabel} YAML 文档没有根节点。");
        }
        catch (YamlException)
        {
            throw new InvalidOperationException(
                $"无法解析 {documentLabel}：请确认内容是合法的 JSON 或 YAML。"
            );
        }
    }

    private static JsonNode? ConvertNode(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                var result = new JsonObject();
                foreach (var entry in mapping.Children)
                {
                    result[entry.Key.ToString()] = ConvertNode(entry.Value);
                }

                return result;

            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (var child in sequence.Children)
                {
                    array.Add(ConvertNode(child));
                }

                return array;

            case YamlScalarNode scalar:
                return ConvertScalar(scalar);

            default:
                return null;
        }
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        if (scalar.Value is null)
        {
            return null;
        }

        if (scalar.Style == ScalarStyle.Plain)
        {
            if (bool.TryParse(scalar.Value, out var boolValue))
            {
                return JsonValue.Create(boolValue);
            }

            if (
                long.TryParse(
                    scalar.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var integer
                )
            )
            {
                return JsonValue.Create(integer);
            }

            if (
                double.TryParse(
                    scalar.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var doubleValue
                )
            )
            {
                return JsonValue.Create(doubleValue);
            }
        }

        return JsonValue.Create(scalar.Value);
    }
}
