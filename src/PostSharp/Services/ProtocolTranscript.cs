using System.Globalization;
using System.Text;

namespace PostSharp.Services;

/// <summary>Builds the human-readable transcript shown in the response pane for protocol requests.</summary>
public sealed class ProtocolTranscript
{
    private const int MaxLength = 2 * 1024 * 1024;
    private const int PreviewLength = 600;

    private readonly StringBuilder _builder = new();

    public void Append(string message)
    {
        if (_builder.Length >= MaxLength)
        {
            return;
        }

        _builder
            .Append('[')
            .Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append("] ")
            .AppendLine(message);
    }

    /// <summary>Appends a direction plus a single-line preview so huge frames stay readable.</summary>
    public void AppendPreview(string direction, string text)
    {
        var preview = text.Replace("\r", " ").Replace("\n", " ");
        if (preview.Length > PreviewLength)
        {
            preview = preview[..PreviewLength] + $"…（共 {text.Length} 字符）";
        }

        Append($"{direction} {preview}");
    }

    public override string ToString() => _builder.ToString();
}
