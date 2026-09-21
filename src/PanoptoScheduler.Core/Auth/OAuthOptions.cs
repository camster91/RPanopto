namespace PanoptoScheduler.Core.Auth;

/// <summary>
/// Connection settings for one Panopto tenant.
///
/// <para><b>On the client secret.</b> Panopto has no public-client support: its
/// OAuth server rejects the authorization-code exchange without a secret even
/// for a "Mobile or Desktop Application" client. A desktop binary therefore
/// cannot keep a secret genuinely secret. PKCE is still used, because it
/// defends against authorization-code interception — the secret is a Panopto
/// constraint we cannot remove, not a design choice.</para>
///
/// The secret is never hard-coded and never logged. Load it from a local file
/// outside the repository.
/// </summary>
public sealed record OAuthOptions
{
    public required string TenantUrl { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }

    /// <summary>
    /// Must match a redirect URL registered on the client, character for
    /// character. Panopto rejects the request otherwise.
    /// </summary>
    public string RedirectUri { get; init; } = "http://localhost:51820/oauth/callback";

    /// <summary>
    /// <c>offline_access</c> is requested so Panopto issues a refresh token —
    /// without it the team would have to re-authorize in a browser every hour.
    /// </summary>
    public string Scope { get; init; } = "api offline_access";

    public string AuthorizeEndpoint => Combine("Panopto/oauth2/connect/authorize");
    public string TokenEndpoint => Combine("Panopto/oauth2/connect/token");

    /// <summary>Loopback port parsed out of <see cref="RedirectUri"/>.</summary>
    public int LoopbackPort
    {
        get
        {
            var uri = new Uri(RedirectUri);
            return uri.Port;
        }
    }

    /// <summary>Path the callback arrives on, e.g. <c>/oauth/callback</c>.</summary>
    public string CallbackPath => new Uri(RedirectUri).AbsolutePath;

    private string Combine(string relative)
        => $"{TenantUrl.TrimEnd('/')}/{relative}";
}
