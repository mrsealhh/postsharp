using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Connects a WebSocket, sends one payload and records the frames received during the wait window.</summary>
public static class WebSocketProtocolClient
{
    private const int MaxCapturedBytes = 2 * 1024 * 1024;
    private const int ConnectTimeoutSeconds = 30;

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
        byte[] payload;
        var isBinary = false;
        try
        {
            uri = ProtocolUrl.BuildWebSocketUri(composed.Url);
            payload = ProtocolUrl.DecodePayload(options.Payload, options.Format);
            isBinary = string.Equals(options.Format, "Hex", StringComparison.OrdinalIgnoreCase);
            if (options.Payload.Length == 0)
            {
                payload = [];
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or UriFormatException)
        {
            stopwatch.Stop();
            return Failure(transcript, stopwatch.ElapsedMilliseconds, exception.Message);
        }

        using var socket = new ClientWebSocket();
        WebSocketHandshake.Apply(socket, composed.Headers, transcript);

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        connectTimeout.CancelAfter(
            TimeSpan.FromSeconds(
                Math.Clamp(timeoutSeconds > 0 ? timeoutSeconds : ConnectTimeoutSeconds, 1, 300)
            )
        );

        transcript.Append($"→ 连接 {MaskUrl(uri)}");
        try
        {
            await socket.ConnectAsync(uri, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            transcript.Append("✕ 连接超时");
            return Failure(transcript, stopwatch.ElapsedMilliseconds, "WebSocket 连接超时。");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            Log.Warning(exception, "WebSocket 连接失败 {Url}", MaskUrl(uri));
            transcript.Append($"✕ 连接失败：{exception.Message}");
            return Failure(
                transcript,
                stopwatch.ElapsedMilliseconds,
                $"无法连接 WebSocket：{exception.Message}"
            );
        }

        transcript.Append(
            $"← 已连接（HTTP {HandshakeStatusCode(socket)}"
                + $"，子协议 {(string.IsNullOrEmpty(socket.SubProtocol) ? "无" : socket.SubProtocol)}）"
        );

        long receivedBytes = 0;
        if (payload.Length > 0)
        {
            try
            {
                await socket
                    .SendAsync(
                        payload,
                        isBinary ? WebSocketMessageType.Binary : WebSocketMessageType.Text,
                        true,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                if (isBinary)
                {
                    transcript.Append($"→ 发送二进制帧 {payload.Length} 字节");
                }
                else
                {
                    transcript.AppendPreview("→ 发送文本帧", Encoding.UTF8.GetString(payload));
                }
            }
            catch (Exception exception)
            {
                transcript.Append($"✕ 发送失败：{exception.Message}");
                await CloseQuietlyAsync(socket).ConfigureAwait(false);
                stopwatch.Stop();
                return Result(socket, transcript, stopwatch.ElapsedMilliseconds, receivedBytes);
            }
        }
        else
        {
            transcript.Append("→ 未填写消息，仅建立连接并等待服务端消息");
        }

        receivedBytes = await ReceiveAsync(
                socket,
                options.WaitMilliseconds,
                transcript,
                cancellationToken
            )
            .ConfigureAwait(false);
        await CloseQuietlyAsync(socket).ConfigureAwait(false);
        stopwatch.Stop();
        transcript.Append("← 连接已关闭");
        return Result(socket, transcript, stopwatch.ElapsedMilliseconds, receivedBytes);
    }

    private static async Task<long> ReceiveAsync(
        ClientWebSocket socket,
        int waitMilliseconds,
        ProtocolTranscript transcript,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        long totalBytes = 0;

        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult? completed = null;
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quiet.CancelAfter(Math.Clamp(waitMilliseconds, 100, 60000));

            try
            {
                WebSocketReceiveResult frame;
                do
                {
                    frame = await socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), quiet.Token)
                        .ConfigureAwait(false);
                    if (frame.MessageType == WebSocketMessageType.Close)
                    {
                        completed = frame;
                        break;
                    }

                    totalBytes += frame.Count;
                    if (message.Length < MaxCapturedBytes)
                    {
                        message.Write(buffer, 0, frame.Count);
                    }
                } while (!frame.EndOfMessage);

                completed ??= frame;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                transcript.Append("… 等待窗口内没有更多消息");
                break;
            }
            catch (WebSocketException exception)
            {
                transcript.Append($"… 接收中断：{exception.Message}");
                break;
            }

            if (completed.MessageType == WebSocketMessageType.Close)
            {
                transcript.Append(
                    $"← 关闭帧：{completed.CloseStatus} {completed.CloseStatusDescription}".TrimEnd()
                );
                break;
            }

            if (completed.MessageType == WebSocketMessageType.Binary)
            {
                transcript.Append(
                    $"← 二进制帧 {message.Length} 字节\n{ProtocolUrl.ToHexDump(message.ToArray())}"
                );
            }
            else
            {
                transcript.AppendPreview("← 文本帧", Encoding.UTF8.GetString(message.ToArray()));
            }
        }

        return totalBytes;
    }

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
            Log.Debug(exception, "WebSocket 关闭失败");
        }
    }

    private static HttpResult Result(
        ClientWebSocket socket,
        ProtocolTranscript transcript,
        long elapsedMs,
        long receivedBytes
    )
    {
        var headers = new List<ResponseHeaderView>();
        if (!string.IsNullOrEmpty(socket.SubProtocol))
        {
            headers.Add(new ResponseHeaderView("Sec-WebSocket-Protocol", socket.SubProtocol));
        }

        return new HttpResult(
            HasResponse: true,
            StatusCode: HandshakeStatusCode(socket),
            ReasonPhrase: "WebSocket 已连接",
            Body: transcript.ToString(),
            IsJson: false,
            MediaType: "application/websocket",
            ElapsedMs: elapsedMs,
            SizeBytes: receivedBytes,
            Headers: headers,
            Error: null
        );
    }

    private static int HandshakeStatusCode(ClientWebSocket socket)
    {
        var status = (int)socket.HttpStatusCode;
        return status > 0 ? status : 101;
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
            MediaType: "application/websocket",
            ElapsedMs: elapsedMs,
            SizeBytes: 0,
            Headers: Array.Empty<ResponseHeaderView>(),
            Error: message
        );

    private static string MaskUrl(Uri uri) => uri.GetLeftPart(UriPartial.Path);
}
