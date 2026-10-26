using System.Text.Json;

namespace PostSharp.Services;

/// <summary>Evaluates a small dotted JSON path such as <c>$.data.token</c> or <c>items[0].id</c>.</summary>
public static class JsonValuePath
{
    public static bool TryEvaluate(string body, string path, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var current = document.RootElement;
            var normalized = path.Trim();
            if (normalized == "$")
            {
                value = Format(current);
                return true;
            }

            if (normalized.StartsWith("$.", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }
            else if (normalized.StartsWith('$'))
            {
                normalized = normalized[1..];
            }

            foreach (var segment in normalized.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = segment;
                var bracket = name.IndexOf('[');
                if (bracket >= 0)
                {
                    var indexText = name[(bracket + 1)..].TrimEnd(']');
                    name = name[..bracket];
                    if (name.Length > 0)
                    {
                        if (
                            current.ValueKind != JsonValueKind.Object
                            || !current.TryGetProperty(name, out current)
                        )
                        {
                            return false;
                        }
                    }

                    if (
                        !int.TryParse(indexText, out var index)
                        || current.ValueKind != JsonValueKind.Array
                        || index < 0
                        || index >= current.GetArrayLength()
                    )
                    {
                        return false;
                    }

                    current = current[index];
                    continue;
                }

                if (
                    current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(name, out current)
                )
                {
                    return false;
                }
            }

            value = Format(current);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Format(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null => "null",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText(),
        };
    }
}
