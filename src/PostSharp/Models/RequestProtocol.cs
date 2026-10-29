namespace PostSharp.Models;

/// <summary>Request protocols supported by the composer and the protocol senders.</summary>
public static class RequestProtocol
{
    public const string Http = "HTTP";

    public const string WebSocket = "WebSocket";

    public const string SocketIo = "Socket.IO";

    public const string Tcp = "TCP";

    public const string Soap = "SOAP";

    public const string Grpc = "gRPC";

    public static IReadOnlyList<string> All { get; } = [Http, WebSocket, SocketIo, Tcp, Soap, Grpc];

    /// <summary>Normalizes persisted values, including older workspaces that never stored a protocol.</summary>
    public static string Normalize(string? protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return Http;
        }

        return protocol.Trim().ToLowerInvariant() switch
        {
            "http" or "https" or "rest" => Http,
            "websocket" or "ws" or "wss" => WebSocket,
            "socket.io" or "socketio" or "socket-io" => SocketIo,
            "tcp" or "tcps" or "tls" => Tcp,
            "soap" or "soap11" or "soap12" => Soap,
            "grpc" or "grpcs" => Grpc,
            _ => Http,
        };
    }

    public static bool IsHttp(string? protocol) =>
        string.Equals(Normalize(protocol), Http, StringComparison.Ordinal);
}
