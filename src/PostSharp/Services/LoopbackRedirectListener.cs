using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PostSharp.Services;

/// <summary>
/// Minimal loopback callback receiver for native-app OAuth (RFC 8252). The default browser
/// redirects to http://127.0.0.1:port/callback, and this listener returns the callback URI
/// to the app. It uses a raw TCP listener so Windows URL ACLs are not required.
/// </summary>
public sealed class LoopbackRedirectListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Uri _redirectUri;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<Uri> _callback = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    private LoopbackRedirectListener(TcpListener listener, Uri redirectUri)
    {
        _listener = listener;
        _redirectUri = redirectUri;
        _ = AcceptLoopAsync(_shutdown.Token);
    }

    public static bool IsLoopbackRedirect(string redirectUri) =>
        Uri.TryCreate(redirectUri.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && uri.IsLoopback
        && uri.Port > 0;

    public static LoopbackRedirectListener Start(string redirectUri)
    {
        if (!IsLoopbackRedirect(redirectUri))
        {
            throw new OidcException(
                "系统默认浏览器登录需要 http://127.0.0.1:端口/回调路径 形式的重定向地址。"
            );
        }

        var uri = new Uri(redirectUri.Trim(), UriKind.Absolute);
        var listener = new TcpListener(IPAddress.Loopback, uri.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            listener.Stop();
            throw new OidcException(
                $"本机回调端口 {uri.Port} 被占用，请修改重定向地址的端口或关闭占用程序。",
                exception
            );
        }

        return new LoopbackRedirectListener(listener, uri);
    }

    public async Task<Uri> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() =>
            _callback.TrySetCanceled(cancellationToken)
        );
        return await _callback.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException exception)
            {
                Log.Debug(exception, "本机 OAuth 回调监听结束");
                return;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream,
                    Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true
                );
                var requestLine = await reader
                    .ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[1].StartsWith('/'))
                {
                    await WriteResponseAsync(stream, false, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var target = new Uri(_redirectUri.GetLeftPart(UriPartial.Authority) + parts[1]);
                if (!IsCallback(target))
                {
                    await WriteResponseAsync(stream, false, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(stream, true, cancellationToken).ConfigureAwait(false);
                _callback.TrySetResult(target);
            }
            catch (OperationCanceledException)
            {
                // Window closed or sign-in cancelled.
            }
            catch (Exception exception)
            {
                Log.Debug(exception, "读取本机 OAuth 回调失败");
            }
        }
    }

    private bool IsCallback(Uri callback)
    {
        if (
            !string.Equals(
                callback.AbsolutePath.TrimEnd('/'),
                _redirectUri.AbsolutePath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }

        var query = callback.Query.TrimStart('?');
        return query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(pair =>
            {
                var separator = pair.IndexOf('=');
                var key = separator < 0 ? pair : pair[..separator];
                return string.Equals(key, "code", StringComparison.Ordinal)
                    || string.Equals(key, "error", StringComparison.Ordinal);
            });
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        bool success,
        CancellationToken cancellationToken
    )
    {
        var title = success ? "登录完成" : "不是登录回调";
        var detail = success
            ? "可以关闭此页面并返回 PostSharp。"
            : "请回到 PostSharp 重新点击登录。";
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>{title}</title></head>"
                + "<body style=\"font-family:system-ui,sans-serif;padding:40px\">"
                + $"<h1>{title}</h1><p>{detail}</p></body></html>"
        );
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(success ? "200 OK" : "404 Not Found")}\r\n"
                + "Content-Type: text/html; charset=utf-8\r\n"
                + $"Content-Length: {body.Length}\r\n"
                + "Connection: close\r\n\r\n"
        );
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Opens an authorization URL in the platform default browser.</summary>
public static class SystemBrowserLauncher
{
    public static void Open(Uri uri)
    {
        var url = uri.AbsoluteUri;
        if (OperatingSystem.IsWindows())
        {
            Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Start("open", url);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var candidate in new[] { "xdg-open", "wslview", "sensible-browser" })
            {
                if (TryStart(candidate, url))
                {
                    return;
                }
            }

            // WSL without a Linux browser: hand the URL to Windows.
            if (TryStart("cmd.exe", $"/c start \"\" \"{url}\""))
            {
                return;
            }

            throw new OidcException($"无法打开系统默认浏览器。请手动打开以下地址完成授权：{url}");
        }

        throw new OidcException("当前平台不支持打开系统默认浏览器。");
    }

    private static void Start(ProcessStartInfo startInfo)
    {
        try
        {
            Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            throw new OidcException("无法打开系统默认浏览器，请检查系统默认应用设置。", exception);
        }
    }

    private static void Start(string fileName, string argument)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false };
        startInfo.ArgumentList.Add(argument);
        Start(startInfo);
    }

    private static bool TryStart(string fileName, string argument)
    {
        try
        {
            var info = new ProcessStartInfo(fileName) { UseShellExecute = false };
            info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            // xdg-open/wslview normally exit quickly; a long-running process is treated as launched.
            return !process.WaitForExit(1500) || process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
