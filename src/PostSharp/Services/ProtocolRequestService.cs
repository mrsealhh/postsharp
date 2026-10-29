using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Routes a composed request to the WebSocket, Socket.IO or TCP sender.</summary>
public static class ProtocolRequestService
{
    public static Task<HttpResult> SendAsync(
        ComposedRequest composed,
        ProtocolSendOptions options,
        CancellationToken cancellationToken,
        int timeoutSeconds = 0
    ) =>
        options.Protocol switch
        {
            RequestProtocol.WebSocket => WebSocketProtocolClient.SendAsync(
                composed,
                options,
                cancellationToken,
                timeoutSeconds
            ),
            RequestProtocol.SocketIo => SocketIoProtocolClient.SendAsync(
                composed,
                options,
                cancellationToken,
                timeoutSeconds
            ),
            RequestProtocol.Tcp => TcpProtocolClient.SendAsync(
                composed,
                options,
                cancellationToken,
                timeoutSeconds
            ),
            _ => throw new InvalidOperationException($"不支持协议 {options.Protocol}。"),
        };
}
