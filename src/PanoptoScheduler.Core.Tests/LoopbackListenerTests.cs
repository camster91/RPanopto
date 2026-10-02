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

        var pending = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(15), "xyz");

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
        var pending = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(15), "xyz", timeout.Token);

        await SendAsync(port, "/something-else?code=nope");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    /// <summary>
    /// A request to the callback path with no query string is not the
    /// redirect, and answering it as one was the bug: any page open on this
    /// machine could end a pending sign-in with one query-less GET, leaving
    /// the user an "OAuth state mismatch" that described an attack rather
    /// than a stray request. This test pins the opposite — the listener
    /// 404s it and keeps waiting for the real redirect.
    /// </summary>
    [Fact]
    public async Task A_query_less_request_to_the_callback_path_is_ignored()
    {
        var port = FreePort();
        await using var listener = new LoopbackListener(port, "/oauth/callback");
        listener.Start();

        var pending = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(15), "xyz");

        var answer = await SendAsync(port, "/oauth/callback");
        Assert.Contains("404", answer);

        var callback = await SendAsync(port, "/oauth/callback?code=abc123&state=xyz");
        Assert.Contains("200", callback);

        var query = await pending;
        Assert.Equal("abc123", query["code"]);
    }

    /// <summary>
    /// The same defect one level up: a redirect whose state does not match
    /// this sign-in's is not this sign-in's redirect, and consuming it would
    /// abort the attempt with an error that reads as a forged callback. The
    /// listener keeps waiting for the one carrying the right state.
    /// </summary>
    [Fact]
    public async Task A_redirect_with_a_mismatched_state_is_ignored()
    {
        var port = FreePort();
        await using var listener = new LoopbackListener(port, "/oauth/callback");
        listener.Start();

        var pending = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(15), "the-real-state");

        var answer = await SendAsync(port, "/oauth/callback?code=abc123&state=someone-elses");
        Assert.Contains("404", answer);

        var callback = await SendAsync(port, "/oauth/callback?code=abc123&state=the-real-state");
        Assert.Contains("200", callback);

        var query = await pending;
        Assert.Equal("the-real-state", query["state"]);
    }

    /// <summary>
    /// A connection that says nothing at all cannot park the wait for the
    /// whole five-minute authorization window — the deadline closes it and
    /// the next connection, the real redirect, is still accepted.
    /// </summary>
    [Fact]
    public async Task A_silent_connection_does_not_consume_the_wait()
    {
        var port = FreePort();
        // The real deadline is ten seconds; the point here is that the wait
        // survives a silent connection, not that it waits ten seconds to say
        // so.
        await using var listener = new LoopbackListener(port, "/oauth/callback", TimeSpan.FromMilliseconds(250));
        listener.Start();

        var pending = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(15), "xyz");

        // Connect and send nothing — hold it open until the redirect is done.
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, port);

        var callback = await SendAsync(port, "/oauth/callback?code=abc123&state=xyz");
        Assert.Contains("200", callback);

        var query = await pending;
        Assert.Equal("abc123", query["code"]);
    }

    /// <summary>
    /// A connection that resets — before its request line is read, or before its
    /// 404 can be written — is that connection's problem, not the sign-in's. The
    /// socket error used to escape the loop and end the whole attempt, so a
    /// browser preconnect that hung up early, or a scanner that resets every
    /// socket it opens, failed a sign-in that was about to succeed.
    /// </summary>
    [Fact]
    public async Task A_connection_that_resets_does_not_end_the_wait()
    {
        var port = FreePort();
        await using var listener = new LoopbackListener(port, "/oauth/callback");
        listener.Start();

        var pending = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(15), "xyz");

        // Several, each sending a stray request and then resetting at once, so
        // the listener meets the reset on the read or on the 404 write — wherever
        // the race puts it, both are the same connection's fault.
        for (var i = 0; i < 5; i++)
        {
            using var rude = new TcpClient();
            await rude.ConnectAsync(IPAddress.Loopback, port);
            await rude.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /favicon.ico HTTP/1.1\r\n"));
            rude.LingerState = new LingerOption(true, 0);
            rude.Close();

            // Gives the listener the chance to reach this one before the next.
            await Task.Delay(50);
        }

        Assert.False(pending.IsFaulted, pending.Exception?.ToString());

        var callback = await SendAsync(port, "/oauth/callback?code=abc123&state=xyz");
        Assert.Contains("200", callback);

        var query = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("abc123", query["code"]);
    }

    /// <summary>
    /// The other side of the line: the caller's own cancellation still ends the
    /// wait, so catching one connection's faults has not made the loop
    /// unstoppable.
    /// </summary>
    [Fact]
    public async Task The_callers_cancellation_still_ends_the_wait_after_a_reset()
    {
        var port = FreePort();
        await using var listener = new LoopbackListener(port, "/oauth/callback");
        listener.Start();

        using var stop = new CancellationTokenSource();
        var pending = listener.WaitForCallbackAsync(TimeSpan.FromSeconds(15), "xyz", stop.Token);

        using (var rude = new TcpClient())
        {
            await rude.ConnectAsync(IPAddress.Loopback, port);
            rude.LingerState = new LingerOption(true, 0);
        }

        await Task.Delay(50);
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Requires_Start_before_waiting()
    {
        await using var listener = new LoopbackListener(FreePort(), "/oauth/callback");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.WaitForCallbackAsync(TimeSpan.FromSeconds(1), "xyz"));
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
