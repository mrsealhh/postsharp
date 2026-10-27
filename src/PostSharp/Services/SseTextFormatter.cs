using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PostSharp.Services;

/// <summary>Display-only formatting for aggregated SSE content.</summary>
public sealed record SseTextFormatResult(string Text, string Kind);

/// <summary>
/// Formats JSON or XML and normalizes text/Markdown whitespace for the AI aggregate viewer.
/// The source aggregate is intentionally not persisted by this helper.
/// </summary>
public static class SseTextFormatter
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static SseTextFormatResult Apply(string text, bool compact)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SseTextFormatResult(text, "文本");
        }

        var trimmed = text.TrimStart();
        if (trimmed[0] is '{' or '[')
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var json = JsonSerializer.Serialize(
                    document.RootElement,
                    compact ? CompactJsonOptions : IndentedJsonOptions
                );
                return new SseTextFormatResult(json, "JSON");
            }
            catch (JsonException)
            {
                // Not JSON; continue with text formatting.
            }
        }

        if (trimmed[0] == '<')
        {
            try
            {
                var document = XDocument.Parse(text);
                var xml = compact
                    ? document.ToString(SaveOptions.DisableFormatting)
                    : document.ToString();
                return new SseTextFormatResult(xml, "XML");
            }
            catch (XmlException)
            {
                // Not XML; continue with text formatting.
            }
        }

        return new SseTextFormatResult(NormalizeText(text, compact), "文本");
    }

    private static string NormalizeText(string text, bool compact)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var output = new List<string>(lines.Length);
        var blankLines = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                if (compact)
                {
                    continue;
                }

                blankLines++;
                if (blankLines > 1)
                {
                    continue;
                }

                output.Add(string.Empty);
                continue;
            }

            blankLines = 0;
            output.Add(line);
        }

        while (output.Count > 0 && output[0].Length == 0)
        {
            output.RemoveAt(0);
        }

        while (output.Count > 0 && output[^1].Length == 0)
        {
            output.RemoveAt(output.Count - 1);
        }

        return string.Join('\n', output);
    }
}
