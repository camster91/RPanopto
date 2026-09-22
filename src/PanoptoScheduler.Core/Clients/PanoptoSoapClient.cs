using System.Net;
using System.Text;
using System.Xml.Linq;
using PanoptoScheduler.Core.Auth;
using PanoptoScheduler.Core.RateLimiting;

namespace PanoptoScheduler.Core.Clients;

/// <summary>
/// XML namespaces used by Panopto's SOAP API.
///
/// <para>Every schema Panopto serves sets <c>elementFormDefault="qualified"</c>,
/// so an element's namespace is the one of the schema that <i>declares</i> it,
/// not the one of its type. <c>pagination</c> is declared inside
/// <c>ListRecorders</c> and so lives in <c>tempuri.org</c>, while its children
/// come from <see cref="V40"/>. Getting that backwards produces a fault that
/// says nothing useful.</para>
/// </summary>
public static class PanoptoXml
{
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Tns = "http://tempuri.org/";

    public static readonly XNamespace V40 =
        "http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V40";

    public static readonly XNamespace V42 =
        "http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap";

    public static readonly XNamespace Arrays = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";
    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Marks an optional element as absent. <c>minOccurs="0"</c> is not enough.</summary>
    public static XAttribute Nil => new(Xsi + "nil", "true");
}

/// <summary>A SOAP fault: the call reached Panopto and Panopto refused it.</summary>
public sealed class PanoptoSoapFaultException(string operation, string code, string message)
    : Exception($"Panopto rejected {operation}: {message}")
{
    public string Operation { get; } = operation;
    public string Code { get; } = code;
}

/// <summary>
/// Transport for Panopto's SOAP public API — the write path.
///
/// <para><b>Why SOAP and not REST.</b> The documented REST API is read-mostly:
/// it cannot create or move a scheduled recording. Scheduling lives in
/// <c>RemoteRecorderManagement</c> (schedule, reschedule, recorder lookup) and
/// <c>SessionManagement</c> (rename, move, delete), both SOAP-only. This is the
/// same pair of services the original uploader called.</para>
///
/// <para><b>Why envelopes by hand.</b> A generated WCF proxy would drag
/// <c>System.ServiceModel.*</c> into a .NET 9 app and produce thousands of lines
/// of client code for the eight operations actually used. The services are
/// plain document/literal over HTTPS, so an envelope is a string, and the
/// existing <see cref="HttpClient"/> — bearer token, rate limiting, timeouts —
/// is reused as-is rather than duplicated.</para>
///
/// <para><b>Authentication.</b> These services do <i>not</i> accept the OAuth
/// bearer token. Verified against the live tenant: <c>ListRecorders</c> with a
/// bearer answers <c>System error</c>, and <c>GetFolders</c> answers 200 with an
/// empty list — a success that means "you are nobody", not "you have no
/// folders". The way in is the legacy <c>.ASPXAUTH</c> cookie, obtained by
/// trading the bearer at <c>api/v1/auth/legacyLogin</c> (see
/// <see cref="LegacyCookieProvider"/>). The <c>auth</c> element in the envelope
/// exists for the older still scheme and is deliberately never populated.</para>
/// </summary>
public sealed class PanoptoSoapClient(
    HttpClient http,
    RateLimiterRegistry limiters,
    IPanoptoAuthenticator auth,
    LegacyCookieProvider? cookies = null)
{
    /// <summary>
    /// Sent as the <c>Cookie</c> header when set, in place of the bearer token.
    ///
    /// <para>Fetched and cached by <see cref="LegacyCookieProvider"/> on the
    /// first call. Settable so a caller that already holds one — or a test —
    /// can supply it directly.</para>
    /// </summary>
    public string? AuthCookie { get; set; }

    /// <summary>
    /// Set once the stale-cookie retry has been spent, so it can never happen
    /// a second time however many calls go on to fail.
    /// </summary>
    private bool _cookieRetried;
    public const string RemoteRecorderManagementPath =
        "/Panopto/PublicAPI/4.2/RemoteRecorderManagement.svc";

    public const string SessionManagementPath =
        "/Panopto/PublicAPI/4.6/SessionManagement.svc";

    /// <summary>
    /// Calls one operation and returns its result element, or null when Panopto
    /// returned a nil result.
    /// </summary>
    /// <param name="path">Service path, e.g. <see cref="SessionManagementPath"/>.</param>
    /// <param name="service">
    /// Contract interface name including the leading I — <c>ISessionManagement</c>,
    /// not <c>SessionManagement</c>. The SOAPAction is built from the interface,
    /// so dropping the I makes every call fail as an unrecognised action.
    /// </param>
    /// <param name="request">
    /// The operation element, already populated and in the <c>tempuri.org</c>
    /// namespace.
    /// </param>
    /// <param name="safeToRetry">
    /// Whether a faulted call may be sent a second time.
    ///
    /// <para><b>Defaults to <c>false</c>, and that default is the point.</b> A
    /// retried <c>ScheduleRecording</c> is a second recording, not a repeat of
    /// the first — so no caller may acquire that behaviour by forgetting an
    /// argument. Reads pass <c>true</c>; every write leaves it alone.</para>
    ///
    /// <para>The cost of the safe default is small and lands in the right place:
    /// a stale cookie during a write surfaces as an error the operator can act
    /// on, instead of being papered over by a resend.</para>
    /// </param>
    public async Task<XElement?> InvokeAsync(
        string path,
        string service,
        XElement request,
        CancellationToken ct = default,
        bool safeToRetry = false)
    {
        await EnsureCookieAsync(ct).ConfigureAwait(false);

        try
        {
            return await AttemptAsync(path, service, request, ct).ConfigureAwait(false);
        }
        catch (PanoptoSoapFaultException) when (cookies is not null && safeToRetry && !_cookieRetried)
        {
            // A stale cookie and a genuine refusal are indistinguishable from
            // here — both arrive as a fault — so this re-fetches once and
            // believes whatever comes back.
            //
            // Safe here and only here, because the caller has declared the call
            // repeatable: for a read, asking twice costs one request and returns
            // the same answer.
            //
            // Once per client, deliberately, and not once per cookie: a bulk run
            // that faults on every row would otherwise fetch a fresh cookie for
            // each of them, turning one request per row into three.
            _cookieRetried = true;

            InvalidateAuthCookie();
            await EnsureCookieAsync(ct).ConfigureAwait(false);

            // Not caught: if this one faults too, the caller gets the real
            // reason rather than a retry loop's worth of noise.
            return await AttemptAsync(path, service, request, ct).ConfigureAwait(false);
        }
        catch (PanoptoSoapFaultException) when (cookies is not null && !safeToRetry)
        {
            // A write is never re-sent, whatever the fault said. The booking may
            // already have landed — Panopto can accept a call and then fault on
            // the way back — so a resend risks a second recording with no way to
            // tell which call made it.
            //
            // The cookie is dropped anyway, so a stale one cannot make the same
            // failure repeat: the next call re-fetches and, if that was all it
            // was, succeeds. The failed write still went out exactly once.
            InvalidateAuthCookie();
            throw;
        }
    }

    /// <summary>
    /// Drops every copy of the legacy cookie this client can reach — the one it
    /// holds and the provider's.
    ///
    /// <para><b>Both, always, and from one place.</b> There are two caches here
    /// and emptying either one alone leaves the other live: the provider would
    /// hand back the cookie it still has, and this client would send the copy it
    /// already holds without asking the provider at all. Sign-out is the path
    /// where the difference matters — the cookie authenticates writes on its own,
    /// so a leftover copy lets the next person to sign in write as the person who
    /// just signed out, with their permissions and their name on the record.</para>
    ///
    /// <para>Callers who only want the next call to re-fetch a rejected cookie
    /// want this too: the alternative is a cache that disagrees with itself.</para>
    /// </summary>
    public void InvalidateAuthCookie()
    {
        cookies?.Invalidate();
        AuthCookie = null;
    }

    /// <summary>
    /// Exchanges the bearer for the legacy cookie, once, the first time a call
    /// needs it. Without this the request goes out anonymous and Panopto answers
    /// a 200 with nothing in it.
    /// </summary>
    private async Task EnsureCookieAsync(CancellationToken ct)
    {
        if (cookies is null || AuthCookie is { Length: > 0 }) return;
        AuthCookie = await cookies.GetAsync(ct).ConfigureAwait(false);
    }

    private async Task<XElement?> AttemptAsync(
        string path,
        string service,
        XElement request,
        CancellationToken ct)
    {
        var operation = request.Name.LocalName;

        // Metered per operation, not per service: a bulk run hammers
        // ScheduleRecording while ListRecorders stays untouched, and Panopto
        // counts those separately.
        var limiter = limiters.For($"{path}/{operation}");
        await limiter.WaitAsync(ct).ConfigureAwait(false);

        var envelope = BuildEnvelope(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };

        message.Headers.TryAddWithoutValidation(
            "SOAPAction", $"\"{PanoptoXml.Tns}{service}/{operation}\"");

        if (AuthCookie is { Length: > 0 } cookie)
            message.Headers.TryAddWithoutValidation("Cookie", cookie);
        else
            await auth.ApplyAsync(message, ct).ConfigureAwait(false);

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
            limiter.PauseFor(retryAfter);
            throw new PanoptoRequestException(path, response.StatusCode,
                $"Rate limited on {operation}. Paused for {retryAfter.TotalSeconds:0}s.");
        }

        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // A SOAP fault arrives with HTTP 500, so the fault has to be read out of
        // the body before the status code is treated as a transport failure —
        // otherwise every rejection reads as "Internal Server Error".
        if (TryReadFault(xml) is { } fault)
            throw new PanoptoSoapFaultException(operation, fault.Code, fault.Message);

        if (!response.IsSuccessStatusCode)
            throw new PanoptoRequestException(path, response.StatusCode, Truncate(xml));

        return ReadResult(xml, operation);
    }

    /// <summary>Wraps an operation element in a SOAP 1.1 envelope.</summary>
    public static string BuildEnvelope(XElement request)
        => new XDocument(
            new XElement(PanoptoXml.Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "s", PanoptoXml.Soap.NamespaceName),
                new XElement(PanoptoXml.Soap + "Body", request)))
            .ToString();

    /// <summary>
    /// Pulls <c>&lt;Operation&gt;Result</c> out of a response, or null when the
    /// result is nil.
    /// </summary>
    private static XElement? ReadResult(string xml, string operation)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            // Not XML at all — a proxy or an HTML error page. Say so plainly.
            throw new PanoptoSoapFaultException(operation, "Unreadable",
                $"The response was not XML. {ex.Message} Body began: {Truncate(xml, 200)}");
        }

        var result = document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == operation + "Result");

        if (result is null) return null;
        if (IsNil(result)) return null;

        return result;
    }

    private static (string Code, string Message)? TryReadFault(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var fault = document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Fault");

        if (fault is null) return null;

        var message = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value
                      ?? fault.Value.Trim();

        var code = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultcode")?.Value
                   ?? "Fault";

        return (code, message.Trim());
    }

    /// <summary>
    /// True when an element carries <c>xsi:nil="true"</c>. Panopto returns a
    /// present-but-empty element rather than omitting absent values.
    /// </summary>
    public static bool IsNil(XElement element)
        => string.Equals(
            element.Attribute(PanoptoXml.Xsi + "nil")?.Value
                ?? element.Attributes().FirstOrDefault(a => a.Name.LocalName == "nil")?.Value,
            "true", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int max = 400)
        => value.Length <= max ? value : value[..max] + "…";
}
