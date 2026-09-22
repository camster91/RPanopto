using PanoptoScheduler.Core.Auth;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Configuration;
using PanoptoScheduler.Core.RateLimiting;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core;

/// <summary>
/// One signed-in session against a tenant, and every client built on it.
///
/// <para>Sign-in opens a browser, so the whole app shares one of these rather
/// than each view building its own. It also means one
/// <see cref="RateLimiterRegistry"/> for the process: Panopto meters per client
/// id, not per connection, so two registries would each believe they held the
/// full budget and together exceed it.</para>
///
/// <para>Reads go through the internal <c>Data.svc</c> because the public REST
/// API cannot enumerate scheduled recordings. Writes go through SOAP, which is
/// the only interface that addresses a scheduled recording by the SessionId the
/// calendar already holds — and the only one that moves or deletes many sessions
/// in a single call.</para>
/// </summary>
public sealed class PanoptoConnection : IAsyncDisposable
{
    private readonly HttpClient _http;

    /// <param name="tokenStore">
    /// Where the refresh token is cached. Defaults to the DPAPI store, which is what
    /// makes sign-in once-per-machine rather than once-per-launch.
    /// </param>
    /// <param name="auditLog">
    /// Where the bulk writers record each session as it completes.
    ///
    /// <para>Passed in rather than created here, and defaulting to a sink that
    /// records nothing. A connection is built by tests and probes as well as by the
    /// app, and a trail that appeared as a side effect of constructing one would
    /// write into the operator's real trail from a test run. Keeping it a parameter
    /// means the app decides to keep a record and nothing else does.</para>
    /// </param>
    public PanoptoConnection(
        PanoptoCredentials credentials,
        ITokenStore? tokenStore = null,
        IBulkAuditLog? auditLog = null)
    {
        _http = new HttpClient
        {
            // Every path handed to these clients is tenant-relative.
            BaseAddress = new Uri(credentials.TenantUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };

        Auth = new OAuthPkceAuthenticator(
            credentials.ToOAuthOptions(),
            _http,
            // The token store is what makes sign-in once-per-machine rather than
            // once-per-launch.
            tokenStore ?? new DpapiTokenStore());

        Auth.AuthorizationUrlReady += url =>
        {
            SignInUrlReady?.Invoke(url);
            return Task.CompletedTask;
        };

        Limiter = new RateLimiterRegistry();

        Reads = new DataSvcClient(_http, Limiter, Auth);

        // Reads go out on the bearer; the SOAP write path needs the legacy
        // cookie the bearer is exchanged for. Verified against the live tenant:
        // without the exchange every SOAP call is anonymous — ListRecorders
        // faults, and GetFolders answers 200 with nothing, which reads like an
        // empty tenant rather than a refused one.
        Cookies = new LegacyCookieProvider(_http, Limiter, Auth);
        Soap = new PanoptoSoapClient(_http, Limiter, Auth, Cookies);

        // Resolved once, at construction, so a zone this machine cannot honour
        // stops the session rather than one booking in the middle of a batch.
        RoomZone = credentials.ResolveTimeZone();

        Recorders = new RemoteRecorderClient(Soap, RoomZone);
        Sessions = new SessionManagementClient(Soap);

        // One trail for both writers. They are the same account changing the same
        // tenant, so two logs would put two run-id sequences in one file and buy
        // nothing — and a run id is only useful if it is unique across everything
        // that wrote that day.
        BulkScheduling = new BulkScheduler(Recorders, Sessions, auditLog);

        // Both clients: retiming lives on the recorder service, and keeping it in
        // this one type is what stops the panel and the bulk path from growing two
        // versions of the same guarded write.
        BulkEditing = new BulkSessionEditor(Sessions, Recorders, auditLog);
    }

    public OAuthPkceAuthenticator Auth { get; }
    public RateLimiterRegistry Limiter { get; }

    public DataSvcClient Reads { get; }

    /// <summary>
    /// Holds the <c>.ASPXAUTH</c> cookie the SOAP services require. Exposed so
    /// signing out can drop it — it is scoped to the session that obtained it,
    /// and keeping it past a sign-out would let the next user write as the
    /// previous one.
    /// </summary>
    public LegacyCookieProvider Cookies { get; }

    public PanoptoSoapClient Soap { get; }

    /// <summary>
    /// The zone the rooms keep time in, from the credentials file. Writes are
    /// converted through it, so it is not a display setting.
    /// </summary>
    public TimeZoneInfo RoomZone { get; }

    public RemoteRecorderClient Recorders { get; }
    public SessionManagementClient Sessions { get; }

    /// <summary>Legacy-file import, dry run by default.</summary>
    public BulkScheduler BulkScheduling { get; }

    /// <summary>Bulk rename, move, delete and webcast changes.</summary>
    public BulkSessionEditor BulkEditing { get; }

    /// <summary>
    /// Raised with the URL the user must visit. The UI opens a browser; a test
    /// just records it.
    /// </summary>
    public event Action<string>? SignInUrlReady;

    /// <summary>Set when the session worked but could not be saved to disk.</summary>
    public string? PersistenceWarning => Auth.PersistenceWarning;

    public bool IsSignedIn { get; private set; }

    /// <summary>
    /// Resumes a saved session. False means the caller must sign in — which is
    /// the normal first-run path, not an error.
    /// </summary>
    public bool Restore()
    {
        if (!Auth.Restore()) return false;

        IsSignedIn = true;
        return true;
    }

    public async Task SignInAsync(CancellationToken ct = default)
    {
        await Auth.SignInAsync(ct).ConfigureAwait(false);
        IsSignedIn = true;
    }

    public void SignOut()
    {
        Auth.SignOut();

        // Dropped as well, and both copies of it. The cookie authenticates
        // writes on its own, so leaving it cached would let the next person to
        // sign in write as the person who just signed out. It is cached twice —
        // in the provider, and in the SOAP client that actually sends it — and
        // invalidating either one alone leaves the other to authenticate the
        // next write.
        Soap.InvalidateAuthCookie();

        IsSignedIn = false;
    }

    public async ValueTask DisposeAsync()
    {
        await Auth.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}
