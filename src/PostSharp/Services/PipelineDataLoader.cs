using System.Text.Json.Nodes;

namespace PostSharp.Services;

/// <summary>读取数据驱动矩阵：CSV（首行表头）或 JSON 对象数组。</summary>
public static class PipelineDataLoader
{
    public static JsonArray Parse(string content, string format)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return ParseJson(content);
        }

        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCsv(content);
        }

        throw new InvalidOperationException("数据文件格式只支持 csv 或 json。");
    }

    public static string FormatOf(string path) =>
        string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
            ? "json"
            : "csv";

    private static JsonArray ParseJson(string content)
    {
        var node = JsonNode.Parse(content);
        if (node is JsonArray array)
        {
            return array;
        }

        if (node is JsonObject single)
        {
            return new JsonArray(single);
        }

        throw new InvalidOperationException(
            "数据 JSON 必须是对象数组，例如 [{\"user\":\"alice\"}]。"
        );
    }

    private static JsonArray ParseCsv(string content)
    {
        var rows = ReadCsvRows(content);
        var result = new JsonArray();
        if (rows.Count == 0)
        {
            return result;
        }

        var headers = rows[0];
        for (var index = 1; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Count == 1 && row[0].Length == 0)
            {
                continue;
            }

            var source = new JsonObject();
            for (var column = 0; column < headers.Count; column++)
            {
                source[headers[column]] = column < row.Count ? JsonValue.Create(row[column]) : null;
            }

            result.Add((JsonNode)source);
        }

        return result;
    }

    private static List<List<string>> ReadCsvRows(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < content.Length && content[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }

                    continue;
                }

                field.Append(character);
                continue;
            }

            switch (character)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
