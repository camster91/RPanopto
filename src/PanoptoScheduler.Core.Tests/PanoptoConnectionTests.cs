using PanoptoScheduler.Core.Configuration;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// A connection that fails to construct lets go of the client it made for itself,
/// and only that one.
///
/// <para>A throwing constructor hands nothing back, so a client it created can
/// never be disposed by anyone else. The throw is a designed one: a time zone this
/// machine does not know stops the connection on purpose, and the way out is to
/// fix the file and build another — each attempt leaking a client and its
/// connection pool until this was fixed.</para>
/// </summary>
public class PanoptoConnectionTests
{
    private sealed class TrackedHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Nothing should be sent while constructing.");

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static PanoptoCredentials WithZone(string zone) => new()
    {
        TenantUrl = "https://rotman.ca.panopto.com",
        ClientId = "test-client",
        ClientSecret = "secret",
        TimeZone = zone,
    };

    [Fact]
    public void A_client_it_made_is_disposed_when_construction_fails()
    {
        var handler = new TrackedHandler();

        Assert.Throws<TimeZoneNotFoundException>(() => new PanoptoConnection(
            WithZone("Nowhere/Not_A_Zone"), new FakeTokenStore(), auditLog: null, http: null,
            _ => new HttpClient(handler)));

        Assert.True(handler.Disposed);
    }

    [Fact]
    public void A_client_that_was_handed_in_is_left_alone_when_construction_fails()
    {
        var handler = new TrackedHandler();
        using var http = new HttpClient(handler);

        Assert.Throws<TimeZoneNotFoundException>(() => new PanoptoConnection(
            WithZone("Nowhere/Not_A_Zone"), new FakeTokenStore(), http: http));

        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task A_client_it_made_is_kept_when_construction_succeeds()
    {
        var handler = new TrackedHandler();

        var connection = new PanoptoConnection(
            WithZone("America/Toronto"), new FakeTokenStore(), auditLog: null, http: null,
            _ => new HttpClient(handler));

        Assert.False(handler.Disposed);

        await connection.DisposeAsync();
        Assert.True(handler.Disposed);
    }
}
