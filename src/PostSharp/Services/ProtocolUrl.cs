using System.Globalization;
using System.Text;

namespace PostSharp.Services;

/// <summary>URL, endpoint and payload helpers shared by the WebSocket, Socket.IO and TCP senders.</summary>
public static class ProtocolUrl
{
    public static Uri BuildWebSocketUri(string url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("WebSocket 地址不能为空。");
        }

        text = ReplaceScheme(text, "https://", "wss://");
        text = ReplaceScheme(text, "http://", "ws://");
        if (
            !text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException(
                "WebSocket 地址必须以 ws://、wss://、http:// 或 https:// 开头。"
            );
        }

        return new Uri(text, UriKind.Absolute);
    }

    public static Uri BuildSocketIoUri(string url, string path)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("Socket.IO 地址不能为空。");
        }

        text = ReplaceScheme(text, "wss://", "https://");
        text = ReplaceScheme(text, "ws://", "http://");
        if (
            !text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
        {
            text = "http://" + text;
        }

        var baseUri = new Uri(text, UriKind.Absolute);
        var isSecure = string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var requestedPath = string.IsNullOrWhiteSpace(path) ? "/socket.io" : path.Trim();
        if (!requestedPath.StartsWith('/'))
        {
            requestedPath = "/" + requestedPath;
        }

        requestedPath = requestedPath.TrimEnd('/');
        var fullPath =
            basePath.Length > 0
            && (
                string.Equals(basePath, requestedPath, StringComparison.OrdinalIgnoreCase)
                || basePath.EndsWith(requestedPath, StringComparison.OrdinalIgnoreCase)
            )
                ? basePath
                : basePath + requestedPath;

        var query = ParseQuery(baseUri.Query);
        query["EIO"] = "4";
        query["transport"] = "websocket";
        var queryText = string.Join(
            "&",
            query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"
            )
        );

        var builder = new UriBuilder(baseUri)
        {
            Scheme = isSecure ? "wss" : "ws",
            Path = fullPath,
            Query = queryText,
        };
        return builder.Uri;
    }

    public static (string Host, int Port, bool UseTls) ParseTcpEndpoint(string url, bool useTls)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("TCP 地址不能为空。");
        }

        var tlsFromScheme = false;
        if (text.StartsWith("tcps://", StringComparison.OrdinalIgnoreCase))
        {
            tlsFromScheme = true;
            text = "tcp://" + text[7..];
        }
        else if (text.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
        {
            tlsFromScheme = true;
            text = "tcp://" + text[6..];
        }
        else if (text.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase))
        {
            tlsFromScheme = true;
            text = "tcp://" + text[6..];
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "tcp://" + text;
        }

        if (!text.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("TCP 地址必须写成 host:port 或 tcp://host:port。");
        }

        var authority = text[6..].TrimEnd('/');
        if (authority.Length == 0)
        {
            throw new InvalidOperationException("TCP 地址缺少主机名。");
        }

        string host;
        string portText;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
            {
                throw new InvalidOperationException("TCP 地址的 IPv6 主机缺少右方括号。");
            }

            host = authority[1..close];
            var remainder = authority[(close + 1)..];
            portText = remainder.StartsWith(':') ? remainder[1..] : string.Empty;
        }
        else
        {
            var separator = authority.LastIndexOf(':');
            if (separator < 0)
            {
                throw new InvalidOperationException(
                    "TCP 地址缺少端口，例如 tcp://127.0.0.1:9000。"
                );
            }

            host = authority[..separator];
            portText = authority[(separator + 1)..];
        }

        if (
            !int.TryParse(
                portText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var port
            ) || port is < 1 or > 65535
        )
        {
            throw new InvalidOperationException("TCP 端口必须是 1 到 65535 之间的整数。");
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("TCP 地址缺少主机名。");
        }

        return (host, port, useTls || tlsFromScheme);
    }

    public static byte[] DecodePayload(string payload, string format)
    {
        if (!string.Equals(format, "Hex", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetBytes(payload ?? string.Empty);
        }

        var digits = new StringBuilder();
        var text = (payload ?? string.Empty).Replace(
            "0x",
            string.Empty,
            StringComparison.OrdinalIgnoreCase
        );
        foreach (var character in text)
        {
            if (Uri.IsHexDigit(character))
            {
                digits.Append(character);
            }
            else if (character is '-' or ':' or ',' || char.IsWhiteSpace(character))
            {
                continue;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Hex 内容包含非法字符“{character}”，只允许 0-9、A-F 和分隔符。"
                );
            }
        }

        if (digits.Length == 0)
        {
            return [];
        }

        if (digits.Length % 2 != 0)
        {
            throw new InvalidOperationException("Hex 内容的字符数必须是偶数。");
        }

        try
        {
            return Convert.FromHexString(digits.ToString());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Hex 内容无法解析。", exception);
        }
    }

    public static string TerminatorText(string terminator) =>
        terminator.Trim().ToUpperInvariant() switch
        {
            "LF" => "\n",
            "CRLF" => "\r\n",
            _ => string.Empty,
        };

    public static string ToHexDump(ReadOnlySpan<byte> bytes, int maxBytes = 4096)
    {
        var builder = new StringBuilder();
        var limit = Math.Min(bytes.Length, maxBytes);
        for (var offset = 0; offset < limit; offset += 16)
        {
            var count = Math.Min(16, limit - offset);
            for (var index = 0; index < count; index++)
            {
                builder.Append(bytes[offset + index].ToString("X2", CultureInfo.InvariantCulture));
                builder.Append(index == count - 1 ? string.Empty : " ");
            }

            builder.AppendLine();
        }

        if (bytes.Length > limit)
        {
            builder.AppendLine($"…（共 {bytes.Length} 字节，仅显示前 {limit} 字节）");
        }

        return builder.ToString().TrimEnd();
    }

    private static string ReplaceScheme(string text, string from, string to) =>
        text.StartsWith(from, StringComparison.OrdinalIgnoreCase) ? to + text[from.Length..] : text;

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? string.Empty : part[(separator + 1)..];
            if (key.Length > 0)
            {
                result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
            }
        }

        return result;
    }
}
