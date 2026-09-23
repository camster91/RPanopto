using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace PanoptoScheduler.Core.Auth;

/// <summary>
/// OAuth 2.0 authorization-code flow with PKCE, per signed-in user.
///
/// Each team member authorizes as themselves, so Panopto's own permissions
/// decide what they can see and change. The app never holds a shared credential
/// and never sees a password.
/// </summary>
public sealed class OAuthPkceAuthenticator : IPanoptoAuthenticator, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>How long to wait for the user to finish in the browser.</summary>
    public static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The window this instance actually waits for. The app takes the default;
    /// a test shrinks it, because five real minutes is not a wait a suite can
    /// afford and the expiry path has to be exercised as anything else is.
    /// </summary>
    internal TimeSpan AuthorizationWindow { get; init; } = AuthorizationTimeout;

    private readonly OAuthOptions _options;
    private readonly HttpClient _http;
    private readonly ITokenStore? _tokenStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TokenSet? _tokens;

    public OAuthPkceAuthenticator(OAuthOptions options, HttpClient http, ITokenStore? tokenStore = null)
    {
        _options = options;
        _http = http;
        _tokenStore = tokenStore;
    }

    /// <summary>
    /// Raised with the URL the user must visit. The caller opens a browser —
    /// this class does not, so it stays testable and UI-agnostic.
    /// </summary>
    public event Func<string, Task>? AuthorizationUrlReady;

    public TokenSet? Tokens => _tokens;

    public bool IsAuthenticated => _tokens is { IsExpired: false };

    /// <summary>
    /// True when a cached refresh token is available, so the next request can be
    /// served without sending the user to a browser.
    /// </summary>
    public bool HasStoredSession => _tokens is { CanRefresh: true };

    /// <summary>
    /// Set when a session could not be read or written. The session itself still
    /// works — this is a warning to surface, not a failure.
    /// </summary>
    public string? PersistenceWarning { get; private set; }

    /// <summary>
    /// Loads a previously cached session. Returns true when a refresh token was
    /// found, meaning sign-in can be silent.
    /// </summary>
    public bool Restore()
    {
        if (_tokenStore is null) return false;

        try
        {
            _tokens = _tokenStore.Load(_options.TenantUrl, _options.ClientId);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            // A cache that cannot be read is a cache miss, not a crash.
            PersistenceWarning = $"Could not read the saved session: {ex.Message}";
            _tokens = null;
        }

        return _tokens is { CanRefresh: true };
    }

    /// <summary>
    /// Runs the interactive sign-in and stores the resulting tokens.
    /// </summary>
    public async Task SignInAsync(CancellationToken ct = default)
    {
        var verifier = Pkce.CreateVerifier();
        var challenge = Pkce.CreateChallenge(verifier);
        var state = Pkce.CreateVerifier();

        await using var listener = new LoopbackListener(_options.LoopbackPort, _options.CallbackPath);
        listener.Start();

        var url = BuildAuthorizeUrl(challenge, state);

        if (AuthorizationUrlReady is not null)
            await AuthorizationUrlReady(url).ConfigureAwait(false);

        IReadOnlyDictionary<string, string> query;
        try
        {
            query = await listener.WaitForCallbackAsync(AuthorizationWindow, state, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The authorization window ran out with no redirect: the browser
            // step was never finished. Translated, because the bare
            // cancellation says "timed out", and a timeout summary points at
            // Panopto's server and the network — neither of which had been
            // asked anything yet. See SignInWindowExpiredException.
            throw new SignInWindowExpiredException(AuthorizationWindow);
        }

        if (query.TryGetValue("error", out var error))
        {
            query.TryGetValue("error_description", out var description);
            throw new InvalidOperationException(
                $"Panopto refused authorization: {error}. {description}".Trim());
        }

        // State check: without it, a forged callback could bind someone else's
        // authorization code to this session.
        if (!query.TryGetValue("state", out var returnedState) ||
            !string.Equals(returnedState, state, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "OAuth state mismatch — the callback did not originate from this sign-in attempt.");

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("The callback contained no authorization code.");

        // Null on the carry slot: the authorization-code exchange issues the
        // first refresh token, and the response has to bring one or there is
        // nothing to carry forward from.
        _tokens = await ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["code_verifier"] = verifier,
        }, null, ct).ConfigureAwait(false);

        Persist(_tokens);
    }

    /// <summary>Drops tokens without contacting the server.</summary>
    public void SignOut()
    {
        _tokens = null;

        try
        {
            _tokenStore?.Clear(_options.TenantUrl, _options.ClientId);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            PersistenceWarning = $"Signed out, but the saved session could not be removed: {ex.Message}";
        }
    }

    public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        var token = await GetValidAccessTokenAsync(ct).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Returns a usable access token, refreshing first if it has lapsed.
    /// Serialized so concurrent requests trigger one refresh, not one each.
    /// </summary>
    public async Task<string> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        if (_tokens is { IsExpired: false } current) return current.AccessToken;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (_tokens is { IsExpired: false } refreshed) return refreshed.AccessToken;

            if (_tokens is null || !_tokens.CanRefresh)
                throw new InvalidOperationException("Not signed in. Call SignInAsync first.");

            try
            {
                // The previous refresh token rides along for RFC 6749 §6: a
                // refresh grant may omit refresh_token, and dropping the old
                // one then would persist a set that cannot refresh — the next
                // launch would demand an interactive browser sign-in with the
                // previously good token already overwritten on disk.
                var previousRefreshToken = _tokens.RefreshToken;
                _tokens = await ExchangeAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = previousRefreshToken!,
                }, previousRefreshToken, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The server answered and refused this refresh token: it is
                // revoked or spent, so retrying it can never succeed. Drop the
                // whole session (memory and cache) so the next call reports
                // "not signed in" instead of re-burning the same dead token
                // once per row in every bulk run. Network-level failures
                // (HttpRequestException, timeouts) are transient and do not
                // clear anything — a retry may still succeed once Panopto
                // answers again.
                SignOut();
                throw;
            }

            // Panopto may rotate the refresh token, so the cache has to follow
            // the new one or the next launch presents a token that is already spent.
            Persist(_tokens);

            return _tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Persist(TokenSet tokens)
    {
        if (_tokenStore is null) return;

        try
        {
            _tokenStore.Save(_options.TenantUrl, _options.ClientId, tokens);
            PersistenceWarning = null;
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            // The session is live and usable; it just will not survive a
            // restart. Failing here would throw away a sign-in the user has
            // already completed in the browser.
            PersistenceWarning =
                $"Signed in, but the session could not be saved: {ex.Message} "
                + "You will be asked to sign in again next time.";
        }
    }

    /// <summary>
    /// Failures that mean "the cache is unusable" rather than "something is
    /// broken". Anything else is left to propagate.
    /// </summary>
    private static bool IsCacheFailure(Exception ex) => ex
        is IOException
        or UnauthorizedAccessException
        or PlatformNotSupportedException
        or CryptographicException
        or JsonException;

    private string BuildAuthorizeUrl(string challenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = _options.Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };

        var encoded = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return $"{_options.AuthorizeEndpoint}?{encoded}";
    }

    /// <summary>
    /// Exchanges form fields for a token set.
    /// </summary>
    /// <param name="carryRefreshToken">
    /// The refresh token to keep when the response omits one — null on the
    /// authorization-code path, where there is nothing to carry.
    /// </param>
    private async Task<TokenSet> ExchangeAsync(
        Dictionary<string, string> fields,
        string? carryRefreshToken = null,
        CancellationToken ct = default)
    {
        fields["client_id"] = _options.ClientId;
        fields["client_secret"] = _options.ClientSecret;

        using var response = await _http
            .PostAsync(_options.TokenEndpoint, new FormUrlEncodedContent(fields), ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // Deliberately does not echo the submitted form, which carries the secret.
            throw new InvalidOperationException(
                $"Panopto rejected the token request ({(int)response.StatusCode}). " +
                $"Verify the client secret and that the redirect URL is registered exactly. {body}");
        }

        var payload = await response.Content
            .ReadFromJsonAsync<TokenResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Token endpoint returned an empty response.");

        return payload.ToTokenSet(carryRefreshToken);
    }

    public ValueTask DisposeAsync()
    {
        // The gate is deliberately not disposed. The one caller of this is the
        // Exit handler, which runs while bulk work may be mid-refresh and
        // holding the gate: Release() on a disposed semaphore throws
        // ObjectDisposedException from a background task, and disposing does
        // not complete the waiters parked on it. Nothing the gate holds is a
        // resource the operating system needs returned — the process is
        // ending — so an unhandled throw on the way out is all disposal buys.
        return ValueTask.CompletedTask;
    }
}
