using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>
/// Opens a Socket.IO v4 connection over the Engine.IO WebSocket transport, emits one event
/// and records the packets received during the wait window.
/// </summary>
public static class SocketIoProtocolClient
{
    private const int ConnectTimeoutSeconds = 30;
    private const int HandshakeAttempts = 5;

    public static async Task<HttpResult> SendAsync(
        ComposedRequest composed,
        ProtocolSendOptions options,
        CancellationToken cancellationToken,
        int timeoutSeconds = 0
    )
    {
        var stopwatch = Stopwatch.StartNew();
        var transcript = new ProtocolTranscript();
        Uri uri;
        try
        {
            uri = ProtocolUrl.BuildSocketIoUri(composed.Url, options.Path);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or UriFormatException)
        {
            stopwatch.Stop();
            return Failure(transcript, stopwatch.ElapsedMilliseconds, exception.Message);
        }

        using var socket = new ClientWebSocket();
        WebSocketHandshake.Apply(socket, composed.Headers, transcript);
        var connectTimeoutMs =
            Math.Clamp(timeoutSeconds > 0 ? timeoutSeconds : ConnectTimeoutSeconds, 1, 300) * 1000;
        var waitMilliseconds = Math.Clamp(options.WaitMilliseconds, 100, 60000);

        transcript.Append($"→ 连接 {MaskUrl(uri)}");
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            connectTimeout.CancelAfter(connectTimeoutMs);
            await socket.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failure(transcript, stopwatch.ElapsedMilliseconds, "Socket.IO 连接超时。");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            Log.Warning(exception, "Socket.IO 连接失败 {Url}", MaskUrl(uri));
            return Failure(
                transcript,
                stopwatch.ElapsedMilliseconds,
                $"无法连接 Socket.IO：{exception.Message}"
            );
        }

        transcript.Append("← WebSocket 已连接，等待 Engine.IO 握手");
        long receivedBytes = 0;
        try
        {
            var open = await ReceiveTextAsync(socket, connectTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (open is null || !open.StartsWith('0'))
            {
                transcript.Append("✕ 未收到 Engine.IO open 包（0{...}）");
                await CloseQuietlyAsync(socket).ConfigureAwait(false);
                stopwatch.Stop();
                return Failure(
                    transcript,
                    stopwatch.ElapsedMilliseconds,
                    "服务端没有返回 Engine.IO open 包，请确认地址和 Socket.IO 路径。"
                );
            }

            receivedBytes += Encoding.UTF8.GetByteCount(open);
            transcript.AppendPreview("← Engine.IO open", open[1..]);

            var socketIoNamespace = SocketIoProtocolCodec.NormalizeNamespace(options.Namespace);
            var connectPacket = SocketIoProtocolCodec.BuildNamespaceConnect(socketIoNamespace);
            await SendTextAsync(socket, connectPacket, cancellationToken).ConfigureAwait(false);
            transcript.Append(
                socketIoNamespace == "/"
                    ? "→ 连接默认命名空间"
                    : $"→ 连接命名空间 {socketIoNamespace}"
            );

            var connected = await WaitForNamespaceConnectAsync(
                    socket,
                    transcript,
                    waitMilliseconds,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (!connected)
            {
                transcript.Append("… 未收到命名空间连接回执，仍尝试发送事件");
            }

            var eventPacket = SocketIoProtocolCodec.BuildEvent(
                socketIoNamespace,
                options.EventName,
                options.Payload
            );
            await SendTextAsync(socket, eventPacket, cancellationToken).ConfigureAwait(false);
            transcript.AppendPreview("→ 发送事件", eventPacket);

            var result = await ReceiveLoopAsync(
                    socket,
                    transcript,
                    waitMilliseconds,
                    cancellationToken,
                    receivedBytes
                )
                .ConfigureAwait(false);
            receivedBytes = result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CloseQuietlyAsync(socket).ConfigureAwait(false);
            stopwatch.Stop();
            return Failure(transcript, stopwatch.ElapsedMilliseconds, "请求已取消。");
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Socket.IO 会话失败 {Url}", MaskUrl(uri));
            transcript.Append($"✕ 会话失败：{exception.Message}");
        }

        await CloseQuietlyAsync(socket).ConfigureAwait(false);
        stopwatch.Stop();
        transcript.Append("← 连接已关闭");
        return new HttpResult(
            HasResponse: true,
            StatusCode: 200,
            ReasonPhrase: "Socket.IO 已连接",
            Body: transcript.ToString(),
            IsJson: false,
            MediaType: "application/socket.io",
            ElapsedMs: stopwatch.ElapsedMilliseconds,
            SizeBytes: receivedBytes,
            Headers: Array.Empty<ResponseHeaderView>(),
            Error: null
        );
    }

    private static async Task<bool> WaitForNamespaceConnectAsync(
        ClientWebSocket socket,
        ProtocolTranscript transcript,
        int waitMilliseconds,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; attempt < HandshakeAttempts; attempt++)
        {
            var text = await ReceiveTextAsync(socket, waitMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            if (text is null)
            {
                return false;
            }

            var packet = SocketIoProtocolCodec.Parse(text);
            switch (packet.Kind)
            {
                case "ping":
                    await SendTextAsync(socket, "3" + packet.Data, cancellationToken)
                        .ConfigureAwait(false);
                    transcript.Append("← ping，已回复 pong");
                    continue;
                case "connect":
                    transcript.AppendPreview("← 命名空间已连接", packet.Data);
                    return true;
                case "error" or "connect_error":
                    transcript.AppendPreview("✕ 命名空间连接失败", packet.Data);
                    return false;
                case "disconnect":
                    transcript.Append("← 服务端断开命名空间");
                    return false;
                default:
                    transcript.AppendPreview("←", packet.Raw);
                    continue;
            }
        }

        return false;
    }

    private static async Task<long> ReceiveLoopAsync(
        ClientWebSocket socket,
        ProtocolTranscript transcript,
        int waitMilliseconds,
        CancellationToken cancellationToken,
        long receivedBytes
    )
    {
        var totalBytes = receivedBytes;
        while (true)
        {
            var text = await ReceiveTextAsync(socket, waitMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            if (text is null)
            {
                transcript.Append("… 等待窗口内没有更多消息");
                return totalBytes;
            }

            totalBytes += Encoding.UTF8.GetByteCount(text);
            var packet = SocketIoProtocolCodec.Parse(text);
            switch (packet.Kind)
            {
                case "ping":
                    await SendTextAsync(socket, "3" + packet.Data, cancellationToken)
                        .ConfigureAwait(false);
                    transcript.Append("← ping，已回复 pong");
                    continue;
                case "pong":
                    transcript.Append("← pong");
                    continue;
                case "event":
                    transcript.AppendPreview(
                        "← 事件",
                        $"{packet.Namespace} {SocketIoProtocolCodec.DescribeEvent(packet)}"
                    );
                    continue;
                case "ack":
                    transcript.AppendPreview(
                        "← ack" + (packet.AckId.Length == 0 ? string.Empty : $" #{packet.AckId}"),
                        packet.Data
                    );
                    continue;
                case "connect":
                    transcript.AppendPreview("← 命名空间已连接", packet.Data);
                    continue;
                case "disconnect":
                    transcript.Append("← 服务端断开命名空间");
                    return totalBytes;
                case "error" or "connect_error":
                    transcript.AppendPreview("✕ 服务端错误", packet.Data);
                    return totalBytes;
                case "close":
                    transcript.Append("← 连接已关闭");
                    return totalBytes;
                case "open":
                    transcript.AppendPreview("← Engine.IO open", packet.Data);
                    continue;
                default:
                    transcript.AppendPreview("←", packet.Raw);
                    continue;
            }
        }
    }

    private static async Task<string?> ReceiveTextAsync(
        ClientWebSocket socket,
        int timeoutMilliseconds,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Clamp(timeoutMilliseconds, 100, 60000));
        try
        {
            WebSocketReceiveResult frame;
            do
            {
                frame = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token)
                    .ConfigureAwait(false);
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (message.Length < 2 * 1024 * 1024)
                {
                    message.Write(buffer, 0, frame.Count);
                }
            } while (!frame.EndOfMessage);

            return Encoding.UTF8.GetString(message.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (WebSocketException exception)
        {
            Log.Debug(exception, "Socket.IO 接收中断");
            return null;
        }
    }

    private static Task SendTextAsync(
        ClientWebSocket socket,
        string text,
        CancellationToken cancellationToken
    ) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text,
            true,
            cancellationToken
        );

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket
                .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Socket.IO 关闭失败");
        }
    }

    private static HttpResult Failure(
        ProtocolTranscript transcript,
        long elapsedMs,
        string message
    ) =>
        new(
            HasResponse: false,
            StatusCode: 0,
            ReasonPhrase: string.Empty,
            Body: transcript.ToString(),
            IsJson: false,
            MediaType: "application/socket.io",
            ElapsedMs: elapsedMs,
            SizeBytes: 0,
            Headers: Array.Empty<ResponseHeaderView>(),
            Error: message
        );

    private static string MaskUrl(Uri uri) => uri.GetLeftPart(UriPartial.Path);
}
