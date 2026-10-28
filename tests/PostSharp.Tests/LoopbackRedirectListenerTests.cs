using System.Net;
using System.Net.Sockets;
using PostSharp.Services;

namespace PostSharp.Tests;

public sealed class LoopbackRedirectListenerTests
{
    [Fact]
    public async Task ReceivesAuthorizationCallback()
    {
        var port = FindFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/callback";
        using var listener = LoopbackRedirectListener.Start(redirectUri);
        using var client = new HttpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var response = await client.GetAsync(
            $"{redirectUri}?code=test-code&state=test-state",
            timeout.Token
        );
        var callback = await listener.WaitForCallbackAsync(timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/callback", callback.AbsolutePath);
        Assert.Contains("code=test-code", callback.Query, StringComparison.Ordinal);
        Assert.Contains("state=test-state", callback.Query, StringComparison.Ordinal);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
