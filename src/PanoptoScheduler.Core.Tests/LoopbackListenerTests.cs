using System.Net;
using System.Net.Sockets;
using System.Text;
using PanoptoScheduler.Core.Auth;

namespace PanoptoScheduler.Core.Tests;

public class LoopbackListenerTests
{
    /// <summary>
    /// The browser asks for /favicon.ico alongside the redirect, so the listener
    /// has to answer a request, stay up, and answer another one.
    ///
    /// <para>This is the regression test for a bug that made sign-in fail with
    /// "The operation is not allowed on non-connected sockets": the request
    /// reader disposed the <see cref="NetworkStream"/> it had read from, so the
    /// response write later in the same connection hit a closed socket. Asserting
    /// on the status line is what catches it — the query would still parse
    /// correctly even when the reply never reaches the browser.</para>
    /// </summary>
    [Fact]
    public async Task Serves_a_stray_request_then_captures_the_callback_on_the_same_connection_style()
    {
        var port = FreePort();
        await using var listener = new LoopbackListener(port, "/oauth/callback");
        listener.Start();

        var pending = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(15));

        var stray = await SendAsync(port, "/favicon.ico");
        Assert.Contains("404", stray);

        var callback = await SendAsync(port, "/oauth/callback?code=abc123&state=xyz");
        Assert.Contains("200", callback);
        Assert.Contains("Authorization complete", callback);

        var query = await pending;
        Assert.Equal("abc123", query["code"]);
        Assert.Equal("xyz", query["state"]);
    }

    [Fact]
    public async Task Ignores_a_request_for_a_different_path()
    {
        var port = FreePort();
        await using var listener = new LoopbackListener(port, "/oauth/callback");
        listener.Start();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var pending = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(15), timeout.Token);

        await SendAsync(port, "/something-else?code=nope");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Captures_a_callback_that_carries_no_query_string()
    {
        var port = FreePort();
        await using var listener = new LoopbackListener(port, "/oauth/callback");
        listener.Start();

        var pending = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(15));
        await SendAsync(port, "/oauth/callback");

        Assert.Empty(await pending);
    }

    [Fact]
    public async Task Requires_Start_before_waiting()
    {
        await using var listener = new LoopbackListener(FreePort(), "/oauth/callback");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.WaitForCallbackAsync(TimeSpan.FromSeconds(1)));
    }

    private static async Task<string> SendAsync(int port, string target)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);

        var stream = client.GetStream();
        var request = $"GET {target} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Binds port 0 to have the OS hand back a free port, then releases it. The
    /// listener needs an explicit port because the redirect URI registered with
    /// Panopto is fixed.
    /// </summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
