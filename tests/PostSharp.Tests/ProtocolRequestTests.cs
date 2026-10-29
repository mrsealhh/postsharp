using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using PostSharp.Models;
using PostSharp.Services;
using Xunit;

namespace PostSharp.Tests;

public sealed class ProtocolRequestTests
{
    [Fact]
    public void ComposerBuildsProtocolOptionsAndLeavesHttpBodyAlone()
    {
        var environment = new EnvironmentModel { Name = "本地" };
        environment.Variables.AddRow("host", "127.0.0.1");
        var request = new RequestItem
        {
            Protocol = RequestProtocol.SocketIo,
            Url = "http://{{host}}:3000",
            BodyMode = "Json",
            Body = "{\"http\":true}",
            MessageBody = "{\"text\":\"{{host}}\"}",
            SocketIoEvent = "chat",
            SocketIoNamespace = "/room",
            SocketIoPath = "/socket.io",
            WaitMilliseconds = 1500,
        };

        var composed = RequestComposer.Compose(request, environment);

        Assert.Null(composed.Body);
        Assert.Null(composed.ContentType);
        var options = Assert.IsType<ProtocolSendOptions>(composed.ProtocolOptions);
        Assert.Equal(RequestProtocol.SocketIo, options.Protocol);
        Assert.Equal("{\"text\":\"127.0.0.1\"}", options.Payload);
        Assert.Equal("chat", options.EventName);
        Assert.Equal("/room", options.Namespace);
        Assert.Equal(1500, options.WaitMilliseconds);
    }

    [Fact]
    public void HttpRequestStillHasNoProtocolOptions()
    {
        var composed = RequestComposer.Compose(
            new RequestItem
            {
                Url = "https://example.com",
                BodyMode = "Text",
                Body = "hi",
            },
            null
        );

        Assert.Null(composed.ProtocolOptions);
        Assert.Equal("hi", composed.Body);
    }

    [Fact]
    public void WebSocketUrlAcceptsHttpScheme()
    {
        Assert.Equal(
            "wss://example.com/ws",
            ProtocolUrl.BuildWebSocketUri("https://example.com/ws").ToString()
        );
        Assert.Equal(
            "ws://example.com/ws",
            ProtocolUrl.BuildWebSocketUri("http://example.com/ws").ToString()
        );
        Assert.Throws<InvalidOperationException>(() =>
            ProtocolUrl.BuildWebSocketUri("example.com/ws")
        );
    }

    [Fact]
    public void SocketIoUrlAddsEngineIoTransportAndKeepsPath()
    {
        var uri = ProtocolUrl.BuildSocketIoUri("https://example.com/base", "/socket.io");

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("/base/socket.io", uri.AbsolutePath);
        Assert.Contains("EIO=4", uri.Query, StringComparison.Ordinal);
        Assert.Contains("transport=websocket", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void TcpEndpointSupportsSchemesAndIpv6()
    {
        var plain = ProtocolUrl.ParseTcpEndpoint("127.0.0.1:9000", false);
        Assert.Equal(("127.0.0.1", 9000, false), plain);

        var tls = ProtocolUrl.ParseTcpEndpoint("tls://example.com:9443", false);
        Assert.Equal(("example.com", 9443, true), tls);

        var ipv6 = ProtocolUrl.ParseTcpEndpoint("tcp://[::1]:7000", true);
        Assert.Equal(("::1", 7000, true), ipv6);

        Assert.Throws<InvalidOperationException>(() =>
            ProtocolUrl.ParseTcpEndpoint("tcp://127.0.0.1", false)
        );
    }

    [Fact]
    public void HexPayloadAcceptsSeparators()
    {
        var bytes = ProtocolUrl.DecodePayload("0x48 65-6C:6c,6F", "Hex");

        Assert.Equal("Hello", Encoding.UTF8.GetString(bytes));
        Assert.Throws<InvalidOperationException>(() => ProtocolUrl.DecodePayload("xyz", "Hex"));
    }

    [Fact]
    public void SocketIoCodecBuildsAndParsesEvents()
    {
        var packet = SocketIoProtocolCodec.BuildEvent("/room", "chat", "{\"text\":\"hi\"}");
        Assert.Equal("42/room,[\"chat\",{\"text\":\"hi\"}]", packet);

        var parsed = SocketIoProtocolCodec.Parse("42/room,7[\"chat\",{\"text\":\"hi\"}]");
        Assert.Equal("event", parsed.Kind);
        Assert.Equal("/room", parsed.Namespace);
        Assert.Equal("7", parsed.AckId);
        Assert.Contains(
            "chat",
            SocketIoProtocolCodec.DescribeEvent(parsed),
            StringComparison.Ordinal
        );

        var stringPayload = SocketIoProtocolCodec.BuildEvent("/", "chat", "plain text");
        Assert.Equal("42[\"chat\",\"plain text\"]", stringPayload);
        Assert.Equal("40/room,", SocketIoProtocolCodec.BuildNamespaceConnect("/room"));
        Assert.Equal("40", SocketIoProtocolCodec.BuildNamespaceConnect("/"));
    }

    [Fact]
    public async Task TcpRequestSendsPayloadAndCapturesReply()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer);
            var received = Encoding.UTF8.GetString(buffer, 0, read);
            var reply = Encoding.UTF8.GetBytes("pong:" + received);
            await stream.WriteAsync(reply);
            await Task.Delay(50);
        });

        try
        {
            var request = new RequestItem
            {
                Protocol = RequestProtocol.Tcp,
                Url = $"tcp://127.0.0.1:{port}",
                MessageBody = "ping",
                TcpTerminator = "LF",
                WaitMilliseconds = 300,
            };
            var composed = RequestComposer.Compose(request, null);
            var result = await ProtocolRequestService.SendAsync(
                composed,
                composed.ProtocolOptions!,
                CancellationToken.None,
                5
            );

            Assert.True(result.HasResponse);
            Assert.Contains("TCP 已连接", result.Body, StringComparison.Ordinal);
            Assert.Contains("pong:ping", result.Body, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    [Fact]
    public async Task WebSocketRequestSendsTextAndCapturesReply()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/ws/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var accepted = await context.AcceptWebSocketAsync(null);
            var socket = accepted.WebSocket;
            var buffer = new byte[1024];
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            var text = Encoding.UTF8.GetString(buffer, 0, received.Count);
            var reply = Encoding.UTF8.GetBytes("echo:" + text);
            await socket.SendAsync(reply, WebSocketMessageType.Text, true, CancellationToken.None);
            await Task.Delay(100);
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "done",
                CancellationToken.None
            );
        });

        try
        {
            var request = new RequestItem
            {
                Protocol = RequestProtocol.WebSocket,
                Url = $"ws://localhost:{port}/ws/",
                MessageBody = "hello",
                WaitMilliseconds = 500,
            };
            var composed = RequestComposer.Compose(request, null);
            var result = await ProtocolRequestService.SendAsync(
                composed,
                composed.ProtocolOptions!,
                CancellationToken.None,
                5
            );

            Assert.True(result.HasResponse);
            Assert.Contains("echo:hello", result.Body, StringComparison.Ordinal);
            Assert.Equal(101, result.StatusCode);
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    [Fact]
    public async Task SocketIoRequestEmitsEventAndCapturesReply()
    {
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var accepted = await context.AcceptWebSocketAsync(null);
            var socket = accepted.WebSocket;
            await SendAsync(
                socket,
                "0{\"sid\":\"test\",\"pingInterval\":25000,\"pingTimeout\":20000}"
            );
            await ReceiveAsync(socket);
            await SendAsync(socket, "40{\"sid\":\"test\"}");
            var emitted = await ReceiveAsync(socket);
            Assert.StartsWith("42", emitted);
            await SendAsync(socket, "42[\"reply\",{\"ok\":true}]");
            await Task.Delay(100);
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "done",
                CancellationToken.None
            );
        });

        try
        {
            var request = new RequestItem
            {
                Protocol = RequestProtocol.SocketIo,
                Url = $"http://localhost:{port}",
                MessageBody = "{\"text\":\"hi\"}",
                SocketIoEvent = "chat",
                WaitMilliseconds = 500,
            };
            var composed = RequestComposer.Compose(request, null);
            var result = await ProtocolRequestService.SendAsync(
                composed,
                composed.ProtocolOptions!,
                CancellationToken.None,
                5
            );

            Assert.True(result.HasResponse);
            Assert.Contains("命名空间已连接", result.Body, StringComparison.Ordinal);
            Assert.Contains("reply", result.Body, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );

    private static async Task<string> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
