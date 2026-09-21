namespace PanoptoScheduler.Core.Auth;

/// <summary>
/// Supplies credentials for an outbound Panopto request.
///
/// <para>Kept abstract so callers do not depend on the mechanism, but there is
/// only one: an OAuth bearer token, attached to every request. Verified against
/// the live tenant that the internal <c>Data.svc</c> and the SOAP public API
/// both accept it, so the browser session cookie is not needed for reads or
/// writes — which removes the whole cookie-lifetime problem.</para>
/// </summary>
public interface IPanoptoAuthenticator
{
    ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct = default);

    /// <summary>False when the caller has no usable credential yet.</summary>
    bool IsAuthenticated { get; }
}
