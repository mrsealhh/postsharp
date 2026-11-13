using System.Text.Json;

namespace PostSharp.Services;

/// <summary>
/// 把请求体、响应体整理成可交给 <c>CodeBlock</c> 着色的多行 JSON。
/// 文本不是 JSON（含变量占位、XML、纯文本）时原样返回，避免展示内容与真实发送内容不一致。
/// </summary>
public static class JsonDisplay
{
    public static string Beautify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] is not '{' and not '['))
        {
            return text;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return DataJson.Format(document.RootElement, indented: true, relaxed: true);
        }
        catch (JsonException)
        {
            return text;
        }
    }
}
