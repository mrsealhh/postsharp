using System.Net.WebSockets;

namespace PostSharp.Services;

/// <summary>Applies user headers and subprotocols to a client WebSocket handshake.</summary>
internal static class WebSocketHandshake
{
    public static void Apply(
        ClientWebSocket socket,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        ProtocolTranscript transcript
    )
    {
        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (key.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
            {
                foreach (
                    var protocol in value.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                )
                {
                    socket.Options.AddSubProtocol(protocol);
                }

                continue;
            }

            if (
                key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Sec-WebSocket-Version", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            try
            {
                socket.Options.SetRequestHeader(key, value);
            }
            catch (ArgumentException)
            {
                transcript.Append($"… 已跳过受限请求头 {key}");
            }
        }
    }
}
