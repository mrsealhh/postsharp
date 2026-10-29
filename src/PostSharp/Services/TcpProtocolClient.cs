using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using PostSharp.Models;

namespace PostSharp.Services;

/// <summary>Connects a raw TCP socket (optionally TLS), sends one payload and records the reply.</summary>
public static class TcpProtocolClient
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
        string host;
        int port;
        bool useTls;
        byte[] payload;
        var isHex = string.Equals(options.Format, "Hex", StringComparison.OrdinalIgnoreCase);
        try
        {
            (host, port, useTls) = ProtocolUrl.ParseTcpEndpoint(composed.Url, options.UseTls);
            payload = ProtocolUrl.DecodePayload(options.Payload, options.Format);
            if (!isHex && options.Payload.Length > 0)
            {
                var terminator = ProtocolUrl.TerminatorText(options.Terminator);
                payload = Encoding.UTF8.GetBytes(options.Payload + terminator);
            }
        }
        catch (InvalidOperationException exception)
        {
            stopwatch.Stop();
            return Failure(transcript, stopwatch.ElapsedMilliseconds, exception.Message);
        }

        using var client = new TcpClient();
        var connectTimeoutSeconds = Math.Clamp(
            timeoutSeconds > 0 ? timeoutSeconds : ConnectTimeoutSeconds,
            1,
            300
        );
        transcript.Append($"→ 连接 {(useTls ? "tls" : "tcp")}://{host}:{port}");
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(connectTimeoutSeconds));
            await client.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failure(transcript, stopwatch.ElapsedMilliseconds, "TCP 连接超时。");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            Log.Warning(exception, "TCP 连接失败 {Host} {Port}", host, port);
            transcript.Append($"✕ 连接失败：{exception.Message}");
            return Failure(
                transcript,
                stopwatch.ElapsedMilliseconds,
                $"无法连接 TCP：{exception.Message}"
            );
        }

        transcript.Append("← TCP 已连接");
        Stream stream = client.GetStream();
        if (useTls)
        {
            var secured = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                await secured
                    .AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = host },
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                stream = secured;
                transcript.Append($"← TLS 握手完成（{secured.SslProtocol}）");
            }
            catch (Exception exception)
            {
                transcript.Append($"✕ TLS 握手失败：{exception.Message}");
                await secured.DisposeAsync().ConfigureAwait(false);
                stopwatch.Stop();
                return Failure(
                    transcript,
                    stopwatch.ElapsedMilliseconds,
                    $"TCP TLS 握手失败：{exception.Message}"
                );
            }
        }

        long receivedBytes = 0;
        try
        {
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                transcript.Append(
                    isHex
                        ? $"→ 发送 {payload.Length} 字节（Hex）"
                        : $"→ 发送文本 {payload.Length} 字节"
                );
            }
            else
            {
                transcript.Append("→ 未填写内容，仅建立连接");
            }

            var buffer = new byte[64 * 1024];
            using var captured = new MemoryStream();
            while (true)
            {
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken
                );
                quiet.CancelAfter(Math.Clamp(options.WaitMilliseconds, 100, 60000));
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, quiet.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    transcript.Append("… 等待窗口内没有更多数据");
                    break;
                }
                catch (IOException exception)
                {
                    transcript.Append($"… 连接中断：{exception.Message}");
                    break;
                }

                if (read == 0)
                {
                    transcript.Append("← 服务端关闭了连接");
                    break;
                }

                receivedBytes += read;
                if (captured.Length < MaxCapturedBytes)
                {
                    captured.Write(buffer, 0, read);
                }
            }

            AppendReceived(transcript, captured.ToArray(), isHex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            stopwatch.Stop();
            return Failure(transcript, stopwatch.ElapsedMilliseconds, "请求已取消。");
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "TCP 会话失败 {Host} {Port}", host, port);
            transcript.Append($"✕ 会话失败：{exception.Message}");
        }

        await stream.DisposeAsync().ConfigureAwait(false);
        stopwatch.Stop();
        transcript.Append("← 连接已关闭");
        return new HttpResult(
            HasResponse: true,
            StatusCode: 200,
            ReasonPhrase: "TCP 已连接",
            Body: transcript.ToString(),
            IsJson: false,
            MediaType: "application/tcp",
            ElapsedMs: stopwatch.ElapsedMilliseconds,
            SizeBytes: receivedBytes,
            Headers: Array.Empty<ResponseHeaderView>(),
            Error: null
        );
    }

    private static void AppendReceived(ProtocolTranscript transcript, byte[] data, bool isHex)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (isHex)
        {
            transcript.Append($"← 收到 {data.Length} 字节\n{ProtocolUrl.ToHexDump(data)}");
            return;
        }

        var text = Encoding.UTF8.GetString(data);
        var readable = text.All(character => character is '\r' or '\n' or '\t' or >= ' ')
            ? text
            : ProtocolUrl.ToHexDump(data);
        transcript.AppendPreview($"← 收到 {data.Length} 字节", readable);
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
            MediaType: "application/tcp",
            ElapsedMs: elapsedMs,
            SizeBytes: 0,
            Headers: Array.Empty<ResponseHeaderView>(),
            Error: message
        );
}
