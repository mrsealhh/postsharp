using System.Net;
using System.Text;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class HttpRequestServiceTests
{
    [Fact]
    public async Task SseResponseReportsProgressAndFinalContent()
    {
        const string body = """
            id: evt-1

            data: {"choices":[{"delta":{"content":"Hello"}}]}

            data: {"choices":[{"delta":{"content":" world"}}]}

            data: [DONE]

            """;
        using var client = new HttpClient(
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            })
        );
        var service = new HttpRequestService(client);
        var progress = new CaptureProgress();

        var result = await service.SendAsync(
            new ComposedRequest(
                "GET",
                "https://example.com/events",
                Array.Empty<KeyValuePair<string, string>>(),
                null,
                null,
                null
            ),
            CancellationToken.None,
            sseProgress: progress
        );

        Assert.True(result.IsSse);
        Assert.False(result.SseCancelled);
        Assert.Equal("Hello world", result.SseContent);
        Assert.Equal("evt-1", result.SseLastEventId);
        Assert.Equal(3, result.SseEventCount);
        Assert.NotEmpty(progress.Items);
        Assert.Equal("Hello world", progress.Items[^1].Content);
    }

    [Fact]
    public async Task AliyunDashScopeNativeSseIsAggregated()
    {
        const string body = """
            data: {"output":{"choices":[{"message":{"role":"assistant","reasoning_content":"思考","content":"你"}}]}}

            data: {"output":{"text":"好"}}

            data: [DONE]

            """;
        using var client = new HttpClient(
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            })
        );
        var service = new HttpRequestService(client);

        var result = await service.SendAsync(
            new ComposedRequest(
                "POST",
                "https://dashscope.aliyuncs.com/api/v1/services/aigc/text-generation/generation",
                Array.Empty<KeyValuePair<string, string>>(),
                null,
                null,
                null
            ),
            CancellationToken.None
        );

        Assert.True(result.IsSse);
        Assert.Equal("思考", result.SseThinking);
        Assert.Equal("你好", result.SseContent);
    }

    [Fact]
    public async Task OpenRouterReasoningDetailsAreAggregated()
    {
        const string body = """
            data: {"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","text":"想"}]}}]}

            data: {"choices":[{"delta":{"content":"答"}}]}

            data: [DONE]

            """;
        using var client = new HttpClient(
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            })
        );
        var service = new HttpRequestService(client);

        var result = await service.SendAsync(
            new ComposedRequest(
                "POST",
                "https://openrouter.ai/api/v1/chat/completions",
                Array.Empty<KeyValuePair<string, string>>(),
                null,
                null,
                null
            ),
            CancellationToken.None
        );

        Assert.True(result.IsSse);
        Assert.Equal("想", result.SseThinking);
        Assert.Equal("答", result.SseContent);
    }

    [Fact]
    public async Task SendsLastEventIdHeaderWhenResuming()
    {
        string? seen = null;
        using var client = new HttpClient(
            new StubHttpHandler(request =>
            {
                seen = request.Headers.TryGetValues("Last-Event-ID", out var values)
                    ? values.FirstOrDefault()
                    : null;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n",
                        Encoding.UTF8,
                        "text/event-stream"
                    ),
                };
            })
        );
        var service = new HttpRequestService(client);

        await service.SendAsync(
            new ComposedRequest(
                "GET",
                "https://example.com/events",
                Array.Empty<KeyValuePair<string, string>>(),
                null,
                null,
                null
            ),
            CancellationToken.None,
            lastEventId: "evt-42"
        );

        Assert.Equal("evt-42", seen);
    }

    [Fact]
    public async Task CustomSsePathsOverrideAutomaticParsing()
    {
        const string body = """
            data: {"payload":{"thought":"想","answer":"答"}}

            data: [DONE]

            """;
        using var client = new HttpClient(
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            })
        );
        var service = new HttpRequestService(client)
        {
            SseThinkingPath = "payload.thought",
            SseContentPath = "payload.answer",
        };

        var result = await service.SendAsync(
            new ComposedRequest(
                "POST",
                "https://example.com/events",
                Array.Empty<KeyValuePair<string, string>>(),
                null,
                null,
                null
            ),
            CancellationToken.None
        );

        Assert.Equal("想", result.SseThinking);
        Assert.Equal("答", result.SseContent);
    }

    [Fact]
    public async Task NonSseResponseDoesNotReportSseProgress()
    {
        using var client = new HttpClient(
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
            })
        );
        var service = new HttpRequestService(client);
        var progress = new CaptureProgress();

        var result = await service.SendAsync(
            new ComposedRequest(
                "GET",
                "https://example.com/health",
                Array.Empty<KeyValuePair<string, string>>(),
                null,
                null,
                null
            ),
            CancellationToken.None,
            sseProgress: progress
        );

        Assert.False(result.IsSse);
        Assert.Empty(progress.Items);
    }

    [Fact]
    public async Task CancellationKeepsPartialSseContent()
    {
        using var cancellation = new CancellationTokenSource();
        var firstChunkSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var client = new HttpClient(
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new PartialSseContent(cancellation, firstChunkSent),
            })
        );
        var service = new HttpRequestService(client);

        var task = service.SendAsync(
            new ComposedRequest(
                "GET",
                "https://example.com/events",
                Array.Empty<KeyValuePair<string, string>>(),
                null,
                null,
                null
            ),
            cancellation.Token
        );

        await firstChunkSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        cancellation.Cancel();
        var result = await task;

        Assert.True(result.IsSse);
        Assert.True(result.SseCancelled);
        Assert.Contains("partial", result.SseContent, StringComparison.Ordinal);
    }

    private sealed class PartialSseContent : HttpContent
    {
        private readonly PartialSseStream _stream;

        public PartialSseContent(
            CancellationTokenSource cancellation,
            TaskCompletionSource firstChunkSent
        )
        {
            _stream = new PartialSseStream(cancellation, firstChunkSent);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "text/event-stream"
            );
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(_stream);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context
        )
        {
            return _stream.CopyToAsync(stream);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class PartialSseStream : Stream
    {
        private static readonly byte[] Payload = Encoding.UTF8.GetBytes(
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"
        );

        private readonly TaskCompletionSource _firstChunkSent;
        private int _offset;

        public PartialSseStream(
            CancellationTokenSource cancellation,
            TaskCompletionSource firstChunkSent
        )
        {
            Cancellation = cancellation;
            _firstChunkSent = firstChunkSent;
        }

        public CancellationTokenSource Cancellation { get; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (_offset >= Payload.Length)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            var count = Math.Min(buffer.Length, Payload.Length - _offset);
            Payload.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset >= Payload.Length)
            {
                _firstChunkSent.TrySetResult();
            }

            return count;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CaptureProgress : IProgress<SseProgress>
    {
        public List<SseProgress> Items { get; } = new();

        public void Report(SseProgress value) => Items.Add(value);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(handler(request));
        }
    }
}
