using System.Text.Json;
using System.Text.Json.Nodes;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Imports a Postman environment export into a PostSharp environment.</summary>
public static class PostmanEnvironmentImportService
{
    public static EnvironmentImportResult Parse(string content, string source)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Postman 环境文件为空，请选择有效的 JSON 文件。");
        }

        JsonObject root;
        try
        {
            root =
                JsonNode.Parse(content) as JsonObject
                ?? throw new InvalidOperationException(
                    "Postman 环境文件的根节点必须是 JSON 对象。"
                );
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "无法解析 Postman 环境文件，请确认文件是合法的 JSON。",
                exception
            );
        }

        var values = root["values"] as JsonArray ?? root["variable"] as JsonArray;
        if (values is null)
        {
            throw new InvalidOperationException(
                "这不是 Postman 环境文件：缺少 values 或 variable 数组。"
            );
        }

        var warnings = new WarningBag();
        var name = GetText(root["name"]).Trim();
        if (name.Length == 0)
        {
            name = Path.GetFileNameWithoutExtension(source).Trim();
        }

        if (name.Length == 0)
        {
            name = "导入的环境";
        }

        var environment = new ImportedEnvironment { Name = name };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in values)
        {
            if (node is not JsonObject value)
            {
                continue;
            }

            var key = GetText(value["key"]).Trim();
            if (key.Length == 0)
            {
                key = GetText(value["name"]).Trim();
            }

            if (key.Length == 0)
            {
                continue;
            }

            if (!seen.Add(key))
            {
                warnings.Add($"环境变量「{key}」重复，已保留第一个值。");
                continue;
            }

            var enabled =
                GetBool(value["enabled"], defaultValue: true)
                && !GetBool(value["disabled"], defaultValue: false);
            environment.Variables.Add(new ImportedField(key, GetText(value["value"]), enabled));
        }

        if (environment.Variables.Count == 0)
        {
            warnings.Add("环境文件没有有效变量，导入后为空环境。");
        }

        return new EnvironmentImportResult
        {
            Environment = environment,
            Source = source,
            Warnings = warnings.ToList(),
        };
    }

    private static bool GetBool(JsonNode? node, bool defaultValue)
    {
        if (node is not JsonValue value)
        {
            return defaultValue;
        }

        return value.TryGetValue<bool>(out var result) ? result : defaultValue;
    }

    private static string GetText(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return string.Empty;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value.TryGetValue<bool>(out var boolean)
            ? (boolean ? "true" : "false")
            : value.ToJsonString();
    }

    private sealed class WarningBag
    {
        private readonly List<string> _messages = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public void Add(string message)
        {
            if (_seen.Add(message))
            {
                _messages.Add(message);
            }
        }

        public IReadOnlyList<string> ToList() => _messages;
    }
}
