using System.Net;
using System.Text;
using PostSharp.Models;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class PipelineTests
{
    private const string OrderFlow = """
        name: 订单流程
        baseUrl: http://mock.local
        data:
          rows:
            - { user: alice, amount: 12 }
        steps:
          - id: respA
            name: 创建用户
            method: POST
            url: /api/users
            json:
              name: ${row.user}
              amount: ${row.amount}
            expect:
              status: 201
              jsonPath:
                id: { exists: true }
          - id: respB
            name: 查询订单
            method: GET
            url: /api/orders/${respA.id}
            expect: { status: 200 }
          - name: 更新订单
            method: PATCH
            url: /api/orders/${respA.id}
            json:
              param1: ${respB.id}
            expect: { status: 200 }
        """;

    [Fact]
    public void ParsesYamlPipelineWithAliasesAndData()
    {
        var parsed = PipelineParser.Parse(OrderFlow);

        Assert.False(parsed.HasErrors, Describe(parsed));
        Assert.Equal("订单流程", parsed.Pipeline.Name);
        Assert.Equal(3, parsed.Pipeline.Steps.Count);
        Assert.Single(parsed.Pipeline.Data);
        Assert.Equal("respA", parsed.Pipeline.Steps[0].Id);
        Assert.Equal("/api/orders/${respA.id}", parsed.Pipeline.Steps[1].Url);
        Assert.Equal(201, parsed.Pipeline.Steps[0].Expectation.Statuses[0]);
    }

    [Fact]
    public void ParsesJsonPipeline()
    {
        var json =
            "{\"name\":\"json 流程\",\"baseUrl\":\"http://mock.local\",\"steps\":[{\"id\":\"a\",\"url\":\"/api/a\"},{\"url\":\"/api/b/${a.id}\"}]}";

        var parsed = PipelineParser.Parse(json);

        Assert.False(parsed.HasErrors, Describe(parsed));
        Assert.Equal(2, parsed.Pipeline.Steps.Count);
        Assert.Equal("a", parsed.Pipeline.Steps[0].Id);
    }

    [Fact]
    public void ReportsUnknownPlaceholder()
    {
        var parsed = PipelineParser.Parse(
            """
            steps:
              - method: GET
                url: /api/orders/${missing.id}
            """
        );

        Assert.True(parsed.HasErrors);
        Assert.Contains(
            parsed.Issues,
            issue => issue.Message.Contains("missing", StringComparison.Ordinal)
        );
    }

    [Fact]
    public void ParsesCsvDataContent()
    {
        var parsed = PipelineParser.Parse(
            """
            name: 数据驱动
            baseUrl: http://mock.local
            steps:
              - method: POST
                url: /api/users
                json: { user: "${user}" }
            """,
            dataContent: "user,amount\nalice,12\nbob,20",
            dataFormat: "csv"
        );

        Assert.False(parsed.HasErrors, Describe(parsed));
        Assert.Equal(2, parsed.Pipeline.Data.Count);
    }

    [Fact]
    public async Task RunsChainedFlowAndInjectsCapturedValues()
    {
        var handler = new StubHandler(
            (request, _) =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path == "/api/users")
                {
                    return Json(HttpStatusCode.Created, """{"id":42}""");
                }

                if (path == "/api/orders/42")
                {
                    return request.Method == HttpMethod.Patch
                        ? Json(HttpStatusCode.OK, """{"ok":true}""")
                        : Json(HttpStatusCode.OK, """{"id":7}""");
                }

                return Json(HttpStatusCode.NotFound, """{"error":"missing"}""");
            }
        );
        var runner = new PipelineRunner(new HttpRequestService(new HttpClient(handler)));
        var parsed = PipelineParser.Parse(OrderFlow);

        var result = await runner.RunAsync(
            parsed.Pipeline,
            new PipelineRunOptions(),
            CancellationToken.None
        );

        Assert.True(result.Passed, Describe(result));
        var steps = Assert.Single(result.Cases).Steps;
        Assert.Equal(3, steps.Count);
        Assert.Equal("http://mock.local/api/orders/42", steps[1].Url);
        Assert.Equal(201, steps[0].StatusCode!.Value);
        Assert.Equal("PATCH", handler.Requests[2].Method, ignoreCase: true);
        Assert.Contains("\"param1\":7", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"amount\":12", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetriesUntilAcceptedStatus()
    {
        var handler = new StubHandler(
            (_, attempt) =>
                attempt < 3
                    ? Json(HttpStatusCode.InternalServerError, """{"error":"boom"}""")
                    : Json(HttpStatusCode.OK, """{"ok":true}""")
        );
        var runner = new PipelineRunner(new HttpRequestService(new HttpClient(handler)));
        var parsed = PipelineParser.Parse(
            """
            name: 重试
            baseUrl: http://mock.local
            retry: { count: 2, delayMs: 0, on: [500] }
            steps:
              - id: probe
                method: GET
                url: /api/probe
            """
        );

        var result = await runner.RunAsync(
            parsed.Pipeline,
            new PipelineRunOptions(),
            CancellationToken.None
        );

        Assert.True(result.Passed, Describe(result));
        var step = Assert.Single(Assert.Single(result.Cases).Steps);
        Assert.Equal(3, step.Attempts);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task LoopsOverCapturedArray()
    {
        var handler = new StubHandler(
            (request, _) =>
                request.RequestUri?.AbsolutePath == "/api/items"
                    ? Json(HttpStatusCode.OK, """[{"id":1},{"id":2}]""")
                    : Json(HttpStatusCode.OK, """{"ok":true}""")
        );
        var runner = new PipelineRunner(new HttpRequestService(new HttpClient(handler)));
        var parsed = PipelineParser.Parse(
            """
            name: 循环
            baseUrl: http://mock.local
            steps:
              - id: list
                method: GET
                url: /api/items
              - loop:
                  over: ${list}
                  as: item
                  index: i
                  steps:
                    - method: GET
                      url: /api/items/${item.id}?index=${i}
            """
        );

        var result = await runner.RunAsync(
            parsed.Pipeline,
            new PipelineRunOptions(),
            CancellationToken.None
        );

        Assert.True(result.Passed, Describe(result));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("http://mock.local/api/items/2?index=1", handler.Requests[2].Url);
    }

    [Fact]
    public async Task RunsEveryDataRow()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"ok":true}"""));
        var runner = new PipelineRunner(new HttpRequestService(new HttpClient(handler)));
        var parsed = PipelineParser.Parse(
            """
            name: 数据矩阵
            baseUrl: http://mock.local
            steps:
              - id: created
                method: POST
                url: /api/users
                json: { name: "${user}" }
            """,
            dataContent: "user\nalice\nbob",
            dataFormat: "csv"
        );

        var result = await runner.RunAsync(
            parsed.Pipeline,
            new PipelineRunOptions(),
            CancellationToken.None
        );

        Assert.True(result.Passed, Describe(result));
        Assert.Equal(2, result.Cases.Count);
        Assert.Contains("\"name\":\"alice\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"bob\"", handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailsStepWhenAssertionDoesNotMatch()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"id":1}"""));
        var runner = new PipelineRunner(new HttpRequestService(new HttpClient(handler)));
        var parsed = PipelineParser.Parse(
            """
            name: 断言失败
            baseUrl: http://mock.local
            steps:
              - id: item
                method: GET
                url: /api/items/1
                expect:
                  status: 201
                  jsonPath:
                    id: { equals: 2 }
            """
        );

        var result = await runner.RunAsync(
            parsed.Pipeline,
            new PipelineRunOptions(),
            CancellationToken.None
        );

        Assert.False(result.Passed);
        var step = Assert.Single(Assert.Single(result.Cases).Steps);
        Assert.False(step.Passed);
        Assert.Contains("期望", step.Error, StringComparison.Ordinal);
    }

    private static string Describe(PipelineParseResult parsed) =>
        string.Join("; ", parsed.Issues.Select(issue => $"{issue.Path} {issue.Message}"));

    private static string Describe(PipelineRunResult result) =>
        string.Join(
            "; ",
            result.Cases.SelectMany(item =>
                item.Steps.Where(step => !step.Passed).Select(step => $"{step.Name}: {step.Error}")
            )
        );

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
        private int _count;

        public StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _count++;
            Requests.Add(
                new RecordedRequest(
                    request.Method.Method,
                    request.RequestUri?.ToString() ?? string.Empty,
                    body
                )
            );
            return _responder(request, _count);
        }
    }

    public sealed record RecordedRequest(string Method, string Url, string Body);
}
