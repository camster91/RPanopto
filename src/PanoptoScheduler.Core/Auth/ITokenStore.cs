namespace PanoptoScheduler.Core.Auth;

/// <summary>
/// Persists tokens between runs, so a user who has signed in once is not sent
/// back to the browser on every launch.
///
/// <para>Keyed by tenant and client id together: two people on one machine, or
/// one person against two tenants, must not inherit each other's session.</para>
///
/// <para>Implementations are expected to protect the refresh token at rest. It
/// is a long-lived credential for the signed-in user's Panopto account, and it
/// outlives the access token by weeks.</para>
/// </summary>
public interface ITokenStore
{
    /// <summary>Returns the cached session, or <see langword="null"/> if there is none.</summary>
    TokenSet? Load(string tenantUrl, string clientId);

    /// <summary>Throws if the session could not be written.</summary>
    void Save(string tenantUrl, string clientId, TokenSet tokens);

    void Clear(string tenantUrl, string clientId);
}
