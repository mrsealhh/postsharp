using System.Net;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class PipelineHttpClientTests
{
    [Fact]
    public async Task SendsMethodHeadersAndBodyThroughThePipelineClient()
    {
        var handler = new RecordingHandler();
        using var client = new HttpPipelineClient(handler);
        var response = await client.SendAsync(
            new PipelineHttpRequest(
                "POST",
                "http://unit.test/echo",
                [new KeyValuePair<string, string>("X-Test", "yes")],
                "{\"a\":1}",
                "application/json",
                FollowRedirects: true
            ),
            timeoutSeconds: 5,
            CancellationToken.None
        );

        Assert.True(response.HasResponse);
        Assert.Equal(201, response.StatusCode);
        Assert.Equal("ok", response.Body);
        Assert.Null(response.Error);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("yes", handler.Header);
        Assert.Equal("{\"a\":1}", handler.Body);
        Assert.Equal("application/json", handler.ContentType);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Header { get; private set; }

        public string? Body { get; private set; }

        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Method = request.Method;
            Header = request.Headers.TryGetValues("X-Test", out var values)
                ? values.FirstOrDefault()
                : null;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            return new HttpResponseMessage((HttpStatusCode)201)
            {
                Content = new StringContent("ok"),
            };
        }
    }
}
