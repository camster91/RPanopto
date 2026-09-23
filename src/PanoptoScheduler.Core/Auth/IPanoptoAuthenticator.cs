namespace PanoptoScheduler.Core.Auth;

/// <summary>
/// Supplies credentials for an outbound Panopto request.
///
/// <para>Kept abstract so callers do not depend on the mechanism, but there is
/// only one: an OAuth bearer token, attached to every request. Verified
/// against the live tenant that the internal <c>Data.svc</c> accepts it, so
/// reads need no browser session cookie.</para>
///
/// <para><b>The bearer is not a write credential.</b> The SOAP public API
/// answers a bearer-authenticated call with a 200 and an empty body — "you
/// are nobody" — so every write is sent under a legacy <c>.ASPXAUTH</c> cookie
/// that <c>LegacyCookieProvider</c> exchanges for once per run. Do not delete
/// that exchange or re-wire <c>PanoptoSoapClient</c> to this interface: nothing
/// throws, the calendar simply comes up empty and <c>ScheduleRecording</c>
/// fails silently. <c>LegacyCookieTests</c> pins this.</para>
/// </summary>
public interface IPanoptoAuthenticator
{
    ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct = default);

    /// <summary>False when the caller has no usable credential yet.</summary>
    bool IsAuthenticated { get; }
}
