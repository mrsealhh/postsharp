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
            "https://example.com/",
            CancellationToken.None,
            sseProgress: progress
        );

        Assert.True(result.IsSse);
        Assert.False(result.SseCancelled);
        Assert.Equal("Hello world", result.SseContent);
        Assert.Equal(3, result.SseEventCount);
        Assert.NotEmpty(progress.Items);
        Assert.Equal("Hello world", progress.Items[^1].Content);
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
            "https://example.com/",
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
            "https://example.com/",
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
