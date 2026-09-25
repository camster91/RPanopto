using System.Net;
using System.Net.Sockets;
using PanoptoScheduler.Core.Updates;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The update check's contract: <see cref="VersionFeed"/> answers questions,
/// it never raises problems. Every failure mode has to come back as
/// <see langword="null"/> — the feed being unreachable, rotten, or
/// mis-edited is the packager's matter, not an operator's, and the one
/// reading that would turn feed trouble into a user-facing dialog is the
/// reading these tests exist to forbid.
/// </summary>
public class VersionFeedTests
{
    /// <summary>
    /// Binds port 0 to have the OS hand back a free port, then releases it.
    /// Per-test, because a fixed port is exactly what cannot be relied on
    /// while several suites run at once.
    /// </summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// Serves exactly one request on a loopback listener and shuts down.
    /// The feed is read once per call, so one response is all a test needs.
    /// Synchronous on purpose: the serving happens on a background task, so
    /// there is nothing here to await — an <c>async</c> signature would be
    /// CS1998 with a return type pretending to be a wait.
    /// </summary>
    /// <param name="status">The status code to answer with.</param>
    /// <param name="body">The body to answer with.</param>
    /// <returns>The URL the feed was served at.</returns>
    private static Uri ServeOnce(int status, string body)
    {
        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        // The response is written on a background task so the caller can
        // await the feed read while this half is still holding the socket.
        _ = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync();
                context.Response.StatusCode = status;
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (ObjectDisposedException)
            {
                // The test ended before a request arrived; nothing to answer.
            }
            finally
            {
                listener.Close();
            }
        });

        return new Uri($"http://localhost:{port}/version.json");
    }

    [Fact]
    public async Task AHealthyFeedNamesTheLatestVersion()
    {
        var feed = ServeOnce(200, """{"version": "1.4.0"}""");

        var info = await VersionFeed.TryReadAsync(feed);

        Assert.NotNull(info);
        Assert.Equal("1.4.0", info.LatestVersion);
    }

    [Fact]
    public async Task AFeedThatAnswers404ReadsAsNothing()
    {
        var feed = ServeOnce(404, "not found");

        Assert.Null(await VersionFeed.TryReadAsync(feed));
    }

    [Fact]
    public async Task AFeedThatAnswersGarbageReadsAsNothing()
    {
        var feed = ServeOnce(200, "this is not json at all");

        Assert.Null(await VersionFeed.TryReadAsync(feed));
    }

    /// <summary>
    /// The one failure that must not be treated as "the newest version":
    /// a feed edited to say something <see cref="Version"/> cannot read.
    /// Answering with it would show a banner for an update nobody can name.
    /// </summary>
    [Fact]
    public async Task AFeedWithAnUnreadableVersionReadsAsNothing()
    {
        var feed = ServeOnce(200, """{"version": "real soon now"}""");

        Assert.Null(await VersionFeed.TryReadAsync(feed));
    }

    [Fact]
    public async Task AFeedThatIsNotThereReadsAsNothing()
    {
        // A port that nothing is listening on: connection refused, which is
        // the shape a rotten feed URL actually produces.
        var feed = new Uri($"http://localhost:{FreePort()}/version.json");

        Assert.Null(await VersionFeed.TryReadAsync(feed));
    }

    [Fact]
    public void AHigherVersionIsNewer()
        => Assert.True(VersionFeed.IsNewer("1.4.0", "1.3.1"));

    [Fact]
    public void TheSameVersionIsNotNewer()
        => Assert.False(VersionFeed.IsNewer("1.3.1", "1.3.1"));

    [Fact]
    public void ALowerVersionIsNotNewer()
        => Assert.False(VersionFeed.IsNewer("1.2.9", "1.3.1"));

    /// <summary>
    /// Whitespace is allowed on either side because the gist is edited by
    /// hand — a trailing newline in the version string must not turn the
    /// whole banner off.
    /// </summary>
    [Fact]
    public void SurroundingWhitespaceDoesNotHideAnUpdate()
        => Assert.True(VersionFeed.IsNewer(" 1.4.0 ", " 1.3.1\n"));

    /// <summary>
    /// The two readings that must never show a banner: a latest that does
    /// not parse (the feed is junk) and a current that does not parse (the
    /// comparison itself is broken). Both answer "no update", because the
    /// app the operator is holding is the app they have, and it works.
    /// </summary>
    [Fact]
    public void NothingReadableMeansNoUpdate()
    {
        Assert.False(VersionFeed.IsNewer("soon", "1.3.1"));
        Assert.False(VersionFeed.IsNewer(null, "1.3.1"));
        Assert.False(VersionFeed.IsNewer("1.4.0", "unknown"));
        Assert.False(VersionFeed.IsNewer("1.4.0", null));
    }
}