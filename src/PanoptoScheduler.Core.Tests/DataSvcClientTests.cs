using System.Net;
using System.Text;
using System.Text.Json;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.RateLimiting;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// Serves pages of sessions, so the client's paging can be driven the way the
/// live tenant drives it: a clamped page, and a <c>TotalNumber</c> that says how
/// much is left.
/// </summary>
internal sealed class PagingHandler : HttpMessageHandler
{
    private readonly int _pageSize;
    private readonly bool _honoursPage;
    private readonly int _total;

    public PagingHandler(int total, int pageSize, bool honoursPage = true)
    {
        _total = total;
        _pageSize = pageSize;
        _honoursPage = honoursPage;
    }

    public int Calls { get; private set; }
    public List<int> RequestedPages { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;

        var body = request.Content is null
            ? "{}"
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var page = 0;
        using (var document = JsonDocument.Parse(body))
        {
            // Lower-cased, matching what the endpoint binds.
            if (document.RootElement.TryGetProperty("queryParameters", out var query) &&
                query.TryGetProperty("page", out var pageElement))
            {
                page = pageElement.GetInt32();
            }
        }

        RequestedPages.Add(page);

        // A server that ignores Page serves the first slice every time. That is
        // the case that must not loop.
        var effective = _honoursPage ? page : 0;
        var skip = effective * _pageSize;
        var take = Math.Max(0, Math.Min(_pageSize, _total - skip));

        var rows = Enumerable.Range(skip, take)
            .Select(i => $$"""
                {"SessionID":"00000000-0000-0000-0000-{{i:D12}}","SessionName":"row {{i}}","Status":1}
                """);

        // Assembled rather than interpolated: the envelope ends in two closing
        // braces, which an interpolated raw string reads as the start of a hole.
        var json = "{\"d\":{\"Results\":["
            + string.Join(",", rows)
            + "],\"TotalNumber\":" + _total + "}}";

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }
}

public class DataSvcClientTests
{
    private static (DataSvcClient Client, PagingHandler Handler) Build(
        int total, int pageSize, bool honoursPage = true)
    {
        var handler = new PagingHandler(total, pageSize, honoursPage);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };

        return (new DataSvcClient(http, new RateLimiterRegistry(), new StubAuthenticator()), handler);
    }

    /// <summary>
    /// The server clamps a page well below the size asked for. Reading one
    /// response as the whole set is what a calendar does when it draws a full
    /// grid with sessions missing from it.
    /// </summary>
    [Fact]
    public async Task Follows_pages_until_the_servers_own_count_is_reached()
    {
        var (client, handler) = Build(total: 620, pageSize: 250);

        var sessions = await client.GetAllSessionsAsync([1]);

        Assert.Equal(620, sessions.Count);
        Assert.Equal(3, handler.Calls);
        Assert.Equal([0, 1, 2], handler.RequestedPages);
    }

    /// <summary>
    /// A clamped page is short by definition, so "returned less than I asked
    /// for" would stop after the first request and change nothing. Only the
    /// server's count settles it.
    /// </summary>
    [Fact]
    public async Task Does_not_stop_at_a_page_that_looks_short()
    {
        var (client, _) = Build(total: 400, pageSize: 250);

        Assert.Equal(400, (await client.GetAllSessionsAsync([1])).Count);
    }

    /// <summary>
    /// A server that ignores <c>Page</c> serves the first slice forever. The
    /// duplicate guard is what turns that into an answer instead of a hang.
    /// </summary>
    [Fact]
    public async Task A_server_that_ignores_Page_does_not_loop_forever()
    {
        var (client, handler) = Build(total: 900, pageSize: 250, honoursPage: false);

        var sessions = await client.GetAllSessionsAsync([1]);

        Assert.Equal(250, sessions.Count);
        Assert.Equal(2, handler.Calls);
    }

    /// <summary>
    /// The completeness contract. A walk that reached the server's own count
    /// is complete; one that stopped because <c>Page</c> was ignored is not,
    /// however many rows it holds. The calendar and the conflict check both
    /// have to tell a half-read set from a quiet tenant, and before the flag
    /// existed the two were indistinguishable — the ignored-Page case above
    /// returned 250 of 900 and looked exactly like a tenant with 250 sessions.
    /// </summary>
    [Fact]
    public async Task A_walk_that_reached_the_count_is_Complete_and_an_ignored_Page_is_not()
    {
        var (whole, _) = Build(total: 400, pageSize: 250);
        var (ignored, _) = Build(total: 900, pageSize: 250, honoursPage: false);

        var readWhole = await whole.GetAllSessionsAsync([1]);
        var readPartial = await ignored.GetAllSessionsAsync([1]);

        Assert.True(readWhole.Complete);
        Assert.Equal(400, readWhole.ReportedTotal);

        Assert.False(readPartial.Complete);
        Assert.Equal(900, readPartial.ReportedTotal);
    }

    /// <summary>
    /// The page ceiling ends the walk with the flag down, so a tenant larger
    /// than the ceiling is reported as a partial read rather than drawn as the
    /// whole world.
    /// </summary>
    [Fact]
    public async Task The_page_ceiling_leaves_Complete_false()
    {
        var (client, handler) = Build(total: 100_000, pageSize: 250);

        var sessions = await client.GetAllSessionsAsync([1]);

        Assert.False(sessions.Complete);
        Assert.Equal(40, handler.Calls);
        Assert.Equal(10_000, sessions.Count);
    }

    /// <summary>Every row appears once, however many pages it took.</summary>
    [Fact]
    public async Task Pages_do_not_duplicate_rows()
    {
        var (client, _) = Build(total: 700, pageSize: 100);

        var sessions = await client.GetAllSessionsAsync([1]);

        Assert.Equal(700, sessions.Count);
        Assert.Equal(700, sessions.Select(s => s.SessionID).Distinct().Count());
    }

    /// <summary>
    /// The query fields go out lower-cased, because the endpoint binds this JSON
    /// case-sensitively and <b>ignores</b> a field it does not recognise.
    ///
    /// <para>Measured on the live tenant: sent as <c>Status</c>/<c>MaxResults</c>/
    /// <c>Page</c> the server returned 250 completed sessions out of 2024, with
    /// the filter, the page size and the page number all dropped and no error
    /// anywhere. Sent lower-cased it returned the 221 scheduled ones. A wrong
    /// name here is invisible from the response, so it has to be pinned here.</para>
    /// </summary>
    [Fact]
    public void Names_every_query_field_the_way_the_endpoint_binds_it()
    {
        var body = JsonSerializer.Serialize(
            new GetSessionsRequest
            {
                QueryParameters = new SessionQuery
                {
                    Status = [1],
                    MaxResults = 500,
                    Page = 2,
                    FolderID = "33333333-3333-3333-3333-333333333333",
                },
            });

        Assert.Contains("\"queryParameters\"", body);
        Assert.Contains("\"status\":[1]", body);
        Assert.Contains("\"maxResults\":500", body);
        Assert.Contains("\"page\":2", body);
        Assert.Contains("\"folderID\"", body);

        // The casing that silently binds nothing.
        Assert.DoesNotContain("\"Status\"", body);
        Assert.DoesNotContain("\"MaxResults\"", body);
        Assert.DoesNotContain("\"Page\"", body);
        Assert.DoesNotContain("\"QueryParameters\"", body);
    }

    [Fact]
    public async Task An_empty_listing_is_not_a_failure()
    {
        var (client, handler) = Build(total: 0, pageSize: 250);

        Assert.Empty(await client.GetAllSessionsAsync([1]));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>
    /// The shape that took the calendar's own load down on the live tenant:
    /// <c>Duration</c> arrives as fractional seconds, and the presenter names
    /// arrive as arrays. Declared as <c>long?</c> and <c>string?</c>, both were
    /// a flat <see cref="JsonException"/> on the first row — so the grid never
    /// loaded at all, against the tenant and not against these tests.
    ///
    /// <para>The values are copied from the tenant, not invented:
    /// <c>4462.451</c> is a real duration and <c>[]</c> is what every one of 250
    /// rows carried for the presenter names.</para>
    /// </summary>
    [Fact]
    public void Reads_a_row_shaped_the_way_the_tenant_sends_it()
    {
        const string json = """
            {"d":{"Results":[{
              "SessionID":"8db1b4a6-5c41-4ee6-9153-b3a8013f0f6f",
              "SessionName":"Test recording",
              "Duration":4462.451,
              "PresenterFirstNames":[],
              "PresenterLastNames":[],
              "StartTime":"\/Date(1771877100000)\/",
              "Status":1
            }],"TotalNumber":1}}
            """;

        var payload = JsonSerializer.Deserialize<GetSessionsResponse>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var row = Assert.Single(payload!.D!.Results);

        Assert.Equal(4462.451, row.Duration);
        Assert.Empty(row.PresenterFirstNames!);
        Assert.Empty(row.PresenterLastNames!);

        // Seconds, not minutes. At the minutes scale this is 74 hours.
        Assert.Equal(TimeSpan.FromSeconds(4462.451), row.EffectiveDuration);
    }

    /// <summary>
    /// A whole-number duration still reads — the wire sends both, and a model
    /// that only accepted one would fail on whichever it did not accept.
    /// </summary>
    [Fact]
    public void Reads_an_integral_duration_too()
    {
        const string json = """
            {"d":{"Results":[{"SessionID":"a","Duration":7200,"Status":1}],"TotalNumber":1}}
            """;

        var payload = JsonSerializer.Deserialize<GetSessionsResponse>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(payload!.D!.Results).EffectiveDuration);
    }
}
