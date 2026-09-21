using System.Net;
using PanoptoScheduler.Core.Auth;
using PanoptoScheduler.Core.RateLimiting;

namespace PanoptoScheduler.Core.Clients;

/// <summary>
/// Trades the OAuth bearer token for the legacy <c>.ASPXAUTH</c> cookie, which
/// is the only credential Panopto's SOAP services accept.
///
/// <para><b>Why this exists.</b> The SOAP public API is the write path — it is
/// the only way to schedule, move, rename or delete a recording. It does not
/// accept an OAuth bearer token, and it does not say so clearly: a bearer call
/// comes back either as <c>System error</c> or as a 200 with an empty list,
/// which reads like "no results" rather than "not authenticated". The bearer
/// has to be exchanged first at <c>api/v1/auth/legacyLogin</c>, which returns an
/// <c>.ASPXAUTH</c> cookie and an empty body.</para>
///
/// <para><b>Why the cookie is cached.</b> The exchange is a request against the
/// same metered budget as everything else, and the cookie outlives the access
/// token that produced it. Re-exchanging per call would burn the rate limit for
/// no reason; the cookie is refreshed when it stops being accepted, and
/// <see cref="Invalidate"/> is how a caller reports that.</para>
/// </summary>
public sealed class LegacyCookieProvider(
    HttpClient http,
    RateLimiterRegistry limiters,
    IPanoptoAuthenticator auth)
{
    public const string LegacyLoginPath = "/Panopto/api/v1/auth/legacyLogin";

    /// <summary>Panopto's forms-auth cookie. The only one the SOAP services read.</summary>
    public const string CookieName = ".ASPXAUTH";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cookie;

    /// <summary>
    /// Returns the cookie, fetching one if there is not a usable one already.
    ///
    /// <para>Serialised behind a gate because a bulk run starts many operations
    /// at once: without it, every one of them would find no cookie and fetch its
    /// own.</para>
    /// </summary>
    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        if (_cookie is { Length: > 0 } existing) return existing;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked: the first caller through the gate may have just
            // fetched it while the others were queued.
            if (_cookie is { Length: > 0 } raced) return raced;

            _cookie = await FetchAsync(ct).ConfigureAwait(false);
            return _cookie;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the cached cookie, so the next call fetches a fresh one. Called
    /// when the tenant rejects a request made with it — the cookie has expired,
    /// or an administrator shortened its lifetime.
    /// </summary>
    public void Invalidate() => _cookie = null;

    private async Task<string> FetchAsync(CancellationToken ct)
    {
        var limiter = limiters.For(LegacyLoginPath);
        await limiter.WaitAsync(ct).ConfigureAwait(false);

        using var message = new HttpRequestMessage(HttpMethod.Get, LegacyLoginPath);
        await auth.ApplyAsync(message, ct).ConfigureAwait(false);

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var pause = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
            limiter.PauseFor(pause);
            throw new PanoptoRequestException(LegacyLoginPath, response.StatusCode,
                $"Rate limited exchanging the legacy cookie. Paused for {pause.TotalSeconds:0}s.");
        }

        if (!response.IsSuccessStatusCode)
            throw new PanoptoRequestException(LegacyLoginPath, response.StatusCode,
                "Panopto would not issue a legacy cookie for this session.");

        // Deliberately not a PanoptoRequestException: that type reports the
        // endpoint and status, and here the status is the one thing that is
        // fine. Its message would read "returned 200 OK" and say nothing about
        // what actually went wrong, which is the whole failure this class
        // exists to make visible.
        if (Extract(response) is not { } cookie)
            throw new InvalidOperationException(
                "Panopto accepted the legacy login but returned no .ASPXAUTH cookie, "
                + "so writes cannot be authenticated. This usually means the sign-in "
                + "expired — sign in again.");

        return cookie;
    }

    /// <summary>
    /// Pulls <c>.ASPXAUTH</c> out of a response's <c>Set-Cookie</c> headers.
    ///
    /// <para>Read from the raw header rather than a <c>CookieContainer</c>: the
    /// container is keyed by the handler's idea of the request URI, and the SOAP
    /// services sit under a path the container would not necessarily match. The
    /// value is sent explicitly instead, which is also what makes it possible to
    /// hand the same cookie to a second client.</para>
    /// </summary>
    private static string? Extract(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers)) return null;

        foreach (var header in headers)
        {
            var pair = header.Split(';')[0].Trim();
            if (!pair.StartsWith(CookieName + "=", StringComparison.OrdinalIgnoreCase)) continue;

            var value = pair[(CookieName.Length + 1)..];
            if (!string.IsNullOrWhiteSpace(value)) return pair;
        }

        return null;
    }
}
