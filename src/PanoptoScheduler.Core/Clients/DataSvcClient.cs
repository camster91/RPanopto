using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PanoptoScheduler.Core.Auth;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.RateLimiting;

namespace PanoptoScheduler.Core.Clients;

/// <summary>Raised when Panopto returns a non-success status.</summary>
public sealed class PanoptoRequestException(string endpoint, HttpStatusCode status, string body)
    : Exception($"Panopto returned {(int)status} {status} from {endpoint}.")
{
    public string Endpoint { get; } = endpoint;
    public HttpStatusCode Status { get; } = status;
    public string Body { get; } = body;
}

/// <summary>
/// Read path against Panopto's internal <c>Data.svc</c>.
///
/// <para><b>Why this exists.</b> The documented public REST API cannot
/// enumerate scheduled recordings — there is no GET on the collection, and
/// <c>sessions/search</c> is keyword-only. For a calendar that is disqualifying.
/// <c>Data.svc</c> is the web UI's own backend and returns everything the
/// calendar needs, including the remote recorder and per-session engagement
/// counts, in a single call.</para>
///
/// <para><b>Risk.</b> This endpoint is undocumented and Panopto may change it
/// without notice. Everything here is therefore hidden behind this class; if it
/// breaks, the read strategy is replaced without touching the UI.</para>
/// </summary>
public sealed class DataSvcClient(
    HttpClient http,
    RateLimiterRegistry limiters,
    IPanoptoAuthenticator auth)
{
    public const string GetSessionsPath = "/Panopto/Services/Data.svc/GetSessions";

    /// <summary>
    /// Page size asked for. The server clamps below this — see
    /// <see cref="GetAllSessionsAsync"/>.
    /// </summary>
    public const int MaxPageSize = 500;

    /// <summary>A backstop against a server that ignores <c>Page</c> entirely.</summary>
    private const int MaxPages = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// One page of sessions in the given lifecycle states.
    /// </summary>
    public async Task<IReadOnlyList<PanoptoSession>> GetSessionsAsync(
        int[]? status = null,
        int maxResults = MaxPageSize,
        CancellationToken ct = default)
        => (await GetSessionsPageAsync(status, maxResults, 0, ct).ConfigureAwait(false)).Results;

    /// <summary>
    /// Every session in the given lifecycle states, following pages until the
    /// server's own count says the set is complete.
    ///
    /// <para><b>One request is not enough.</b> The server clamps a page below the
    /// <see cref="MaxPageSize"/> asked for, so a single call returns the first
    /// slice and silently omits the rest. For the calendar that is sessions
    /// missing from the grid; for the conflict check it is a busy room reported
    /// as free, which is the one thing that check exists to prevent.</para>
    ///
    /// <para>Completeness is decided by <c>TotalNumber</c> in the response, not
    /// by a page looking short: a clamped page is short by definition, so
    /// "returned less than I asked for" would stop after the first request and
    /// change nothing. Two further guards — no new ids, and a page ceiling —
    /// keep a server that ignores <c>Page</c> from looping forever.</para>
    /// </summary>
    public async Task<IReadOnlyList<PanoptoSession>> GetAllSessionsAsync(
        int[]? status = null,
        CancellationToken ct = default)
    {
        var all = new List<PanoptoSession>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 0; page < MaxPages; page++)
        {
            var (results, total) = await GetSessionsPageAsync(status, MaxPageSize, page, ct)
                .ConfigureAwait(false);

            var added = results
                .Where(s => s.SessionID is null || seen.Add(s.SessionID))
                .ToList();

            all.AddRange(added);

            // The server's count is the authority. A page that adds nothing new
            // means either the set is exhausted or Page is being ignored.
            if (added.Count == 0 || all.Count >= total) break;
        }

        return all;
    }

    private async Task<(IReadOnlyList<PanoptoSession> Results, int TotalNumber)> GetSessionsPageAsync(
        int[]? status,
        int maxResults,
        int page,
        CancellationToken ct)
    {
        var limiter = limiters.For(GetSessionsPath);
        await limiter.WaitAsync(ct).ConfigureAwait(false);

        var request = new GetSessionsRequest
        {
            QueryParameters = new SessionQuery
            {
                Status = status ?? [1],
                MaxResults = maxResults,
                Page = page,
            },
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, GetSessionsPath)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };

        await auth.ApplyAsync(message, ct).ConfigureAwait(false);

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // The tenant is metering harder than our model expects. Back the
            // whole endpoint off rather than hammering through the limit.
            var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
            limiter.PauseFor(retryAfter);
            throw new PanoptoRequestException(GetSessionsPath, response.StatusCode,
                $"Rate limited. Paused this endpoint for {retryAfter.TotalSeconds:0}s.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new PanoptoRequestException(GetSessionsPath, response.StatusCode, Truncate(body));
        }

        var payload = await response.Content
            .ReadFromJsonAsync<GetSessionsResponse>(JsonOptions, ct)
            .ConfigureAwait(false);

        var results = payload?.D?.Results ?? [];

        // TotalNumber is the count of the whole set, not of this page. A server
        // that omits it reports 0, which would read as "already complete".
        // Falling back to the page's own length would be worse: the walk would
        // stop after this page with no error, so only trust a positive count
        // and otherwise leave the walk open — the no-new-ids and page-ceiling
        // guards are what decide then.
        var total = payload?.D?.TotalNumber is > 0 ? payload.D.TotalNumber : int.MaxValue;

        return (results, total);
    }

    /// <summary>
    /// Scheduled sessions within a date window.
    ///
    /// Filtering is client-side by necessity: the endpoint silently ignores
    /// <c>startDate</c>/<c>endDate</c>, so a server-side window would quietly
    /// return everything and the calendar would look correct while being wrong.
    /// The whole set is small, and it takes one call per page to have it in
    /// hand.
    /// </summary>
    /// <param name="from">The room's wall clock, matching what the rows carry; inclusive.</param>
    /// <param name="to">The room's wall clock; exclusive.</param>
    public async Task<IReadOnlyList<PanoptoSession>> GetScheduledSessionsAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        var all = await GetAllSessionsAsync([1], ct).ConfigureAwait(false);

        return all
            .Where(s => s.EffectiveStart is { } start && start >= from && start < to)
            .OrderBy(s => s.EffectiveStart)
            .ToList();
    }

    private static string Truncate(string value, int max = 400)
        => value.Length <= max ? value : value[..max] + "…";
}
