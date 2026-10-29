using System.Text.Json;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class McpServerSecurityTests
{
    private const string Ping = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}";

    // The origin check runs before any workspace access, so these calls never reach the view model.
    private readonly McpServer _server = new(new McpWorkspaceAdapter(null!));

    [Theory]
    [InlineData("https://evil.example", "127.0.0.1:5199")]
    [InlineData("null", "127.0.0.1:5199")]
    [InlineData(null, "rebind.evil.example:5199")]
    public async Task RejectsBrowserPagesAndForeignHosts(string? origin, string host)
    {
        var response = await SendAsync("POST", Ping, origin, host);

        Assert.Equal(403, response.StatusCode);
        Assert.False(HasHeader(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task AcceptsDesktopClientsWithoutCors()
    {
        var response = await SendAsync("POST", Ping, null, "127.0.0.1:5199");

        Assert.Equal(200, response.StatusCode);
        Assert.False(HasHeader(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task RequiresBearerTokenWhenEnabled()
    {
        _server.Policy = new McpAccessPolicy(true, "test-token", false);

        var missing = await SendAsync("POST", Ping, null, "127.0.0.1:5199");
        Assert.Equal(401, missing.StatusCode);
        Assert.True(HasHeader(missing, "WWW-Authenticate"));

        var invalid = await SendAsync("POST", Ping, null, "127.0.0.1:5199", "wrong-token");
        Assert.Equal(401, invalid.StatusCode);

        var accepted = await SendAsync("POST", Ping, null, "127.0.0.1:5199", "test-token");
        Assert.Equal(200, accepted.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyHidesAndRejectsWriteTools()
    {
        _server.Policy = new McpAccessPolicy(false, string.Empty, true);

        var list = await SendAsync(
            "POST",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            null,
            "127.0.0.1:5199"
        );
        using var document = JsonDocument.Parse(list.Body);
        var names = document
            .RootElement.GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("get_request", names);
        Assert.Contains("list_collections", names);
        Assert.DoesNotContain("send_request", names);
        Assert.DoesNotContain("create_request", names);

        var blocked = await SendAsync(
            "POST",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"send_request","arguments":{"id":"00000000-0000-0000-0000-000000000000"}}}""",
            null,
            "127.0.0.1:5199"
        );
        Assert.Equal(403, blocked.StatusCode);
        Assert.Contains("只读模式", blocked.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsSemanticSettingsAndStorageTools()
    {
        var response = await SendAsync(
            "POST",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            null,
            "127.0.0.1:5199"
        );
        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(response.Body);

        using var document = JsonDocument.Parse(response.Body);
        var names = document
            .RootElement.GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("get_settings", names);
        Assert.Contains("update_settings", names);
        Assert.Contains("get_storage_config", names);
        Assert.Contains("sync_storage", names);
        Assert.Contains("switch_storage", names);
        Assert.Contains("import_content", names);
        Assert.Contains("export_workspace", names);
        Assert.Contains("send_request", names);
        Assert.Contains("soap_describe", names);
        Assert.Contains("grpc_describe", names);
        Assert.Contains("create_environment", names);
        Assert.Contains("clear_audit", names);
        Assert.Contains("restore_collection_trash", names);
    }

    [Theory]
    [InlineData("http://localhost:6274", "localhost:5199")]
    [InlineData("http://127.0.0.1:3000", "127.0.0.1:5199")]
    [InlineData("http://[::1]:3000", "[::1]:5199")]
    public async Task EchoesOnlyLocalOrigins(string origin, string host)
    {
        var preflight = await SendAsync("OPTIONS", string.Empty, origin, host);

        Assert.Equal(204, preflight.StatusCode);
        Assert.Equal(origin, preflight.Headers?["Access-Control-Allow-Origin"]);
    }

    private async Task<RawHttpResponse> SendAsync(
        string method,
        string body,
        string? origin,
        string host,
        string? token = null
    )
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = host,
        };
        if (origin is not null)
        {
            headers["Origin"] = origin;
        }

        if (token is not null)
        {
            headers["Authorization"] = $"Bearer {token}";
        }

        var response = await _server.HandleHttpAsync(
            method,
            "/mcp",
            headers,
            body,
            CancellationToken.None
        );
        Assert.NotNull(response);
        return response;
    }

    private static bool HasHeader(RawHttpResponse response, string name) =>
        response.Headers is not null && response.Headers.ContainsKey(name);
}
