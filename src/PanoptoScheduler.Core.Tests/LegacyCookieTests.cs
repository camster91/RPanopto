using System.Net;
using System.Text;
using System.Xml.Linq;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.RateLimiting;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// One recorded request: enough to tell the exchange apart from the call it
/// authorises, and to see which credential went out.
/// </summary>
internal sealed record SentRequest(string Path, string? Cookie, string? Authorization);

/// <summary>
/// Replays a script of responses, so a single client can be taken through a
/// fault and its recovery. The last response repeats once the script runs out.
/// </summary>
internal sealed class ScriptedHandler(params Func<HttpResponseMessage>[] script)
    : HttpMessageHandler
{
    public List<SentRequest> Requests { get; } = [];

    private int _index;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new SentRequest(
            request.RequestUri?.AbsolutePath ?? "",
            request.Headers.TryGetValues("Cookie", out var cookies)
                ? string.Join("; ", cookies)
                : null,
            request.Headers.Authorization?.ToString()));

        var step = script[Math.Min(_index, script.Length - 1)];
        _index++;

        return Task.FromResult(step());
    }

    /// <summary>
    /// Matches on a substring, not a suffix: the service paths end in
    /// <c>.svc</c>, so a suffix match against the service name finds nothing and
    /// silently reports zero.
    /// </summary>
    public int CountOf(string path)
        => Requests.Count(r => r.Path.Contains(path, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The legacy cookie exchange, the one retry a read buys, and the retry a write
/// must never get.
///
/// <para>Worth testing because none of it is visible when it goes wrong: a SOAP
/// call made without the cookie is not refused, it is answered — 200, empty —
/// so a broken exchange looks exactly like a tenant with nothing in it. That is
/// how the missing exchange survived a full test suite and a green build.</para>
/// </summary>
public class LegacyCookieTests
{
    private const string CookieHeader = ".ASPXAUTH=v1|abc|def";

    private static HttpResponseMessage Cookie(string value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "text/plain"),
        };

        response.Headers.TryAddWithoutValidation(
            "Set-Cookie", $"{value}; path=/; HttpOnly; Secure");

        return response;
    }

    private static HttpResponseMessage Fault()
    {
        const string body =
            """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
            <s:Fault><faultcode>s:Client</faultcode><faultstring>System error.</faultstring></s:Fault>
            </s:Body></s:Envelope>
            """;

        return new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
    }

    private static HttpResponseMessage Ok()
    {
        const string body =
            """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
            <ListRecordersResult xmlns="http://tempuri.org/"><Count>19</Count></ListRecordersResult>
            </s:Body></s:Envelope>
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
    }

    /// <summary>
    /// The exchange is keyed on path, because SOAP and legacyLogin go out over
    /// the same client and land in the same recording.
    /// </summary>
    private static (PanoptoSoapClient Soap, ScriptedHandler Handler) Build(
        params Func<HttpResponseMessage>[] script)
    {
        var handler = new ScriptedHandler(script);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };
        var limiters = new RateLimiterRegistry();

        var soap = new PanoptoSoapClient(
            http, limiters, new StubAuthenticator(),
            new LegacyCookieProvider(http, limiters, new StubAuthenticator()));

        return (soap, handler);
    }

    private static Task<XElement?> ListAsync(PanoptoSoapClient soap)
        => soap.InvokeAsync(
            PanoptoSoapClient.RemoteRecorderManagementPath,
            "IRemoteRecorderManagement",
            new XElement(PanoptoXml.Tns + "ListRecorders"),
            safeToRetry: true);

    /// <summary>
    /// The same call without the opt-in — i.e. how every write goes out. Kept as
    /// a distinct helper because the difference between these two lines is the
    /// whole of the duplicate-booking rule.
    /// </summary>
    private static Task<XElement?> ScheduleAsync(PanoptoSoapClient soap)
        => soap.InvokeAsync(
            PanoptoSoapClient.RemoteRecorderManagementPath,
            "IRemoteRecorderManagement",
            new XElement(PanoptoXml.Tns + "ScheduleRecording"));

    /// <summary>
    /// The whole point: the cookie from the exchange is what goes on the wire,
    /// and the bearer is left off — sending both would be a request that works
    /// for a reason the tenant has not documented.
    /// </summary>
    [Fact]
    public async Task The_exchanged_cookie_replaces_the_bearer_on_the_soap_call()
    {
        var (soap, handler) = Build(() => Cookie(CookieHeader), Ok);

        await ListAsync(soap);

        var call = handler.Requests.Single(r => r.Path.Contains("RemoteRecorderManagement"));
        Assert.Equal(CookieHeader, call.Cookie);
        Assert.Null(call.Authorization);
    }

    /// <summary>
    /// And the exchange itself must carry the bearer — it is what the bearer is
    /// for, and an unauthenticated exchange returns no cookie at all.
    /// </summary>
    [Fact]
    public async Task The_exchange_itself_uses_the_bearer()
    {
        var (soap, handler) = Build(() => Cookie(CookieHeader), Ok);

        await ListAsync(soap);

        var login = handler.Requests.Single(r => r.Path.Contains("legacyLogin"));
        Assert.Equal("Bearer test-token", login.Authorization);
    }

    /// <summary>
    /// A bulk run issues hundreds of calls. One exchange has to cover them all,
    /// or the run spends its rate limit on cookies.
    /// </summary>
    [Fact]
    public async Task One_exchange_covers_every_later_call()
    {
        var (soap, handler) = Build(() => Cookie(CookieHeader), Ok);

        await ListAsync(soap);
        await ListAsync(soap);
        await ListAsync(soap);

        Assert.Equal(1, handler.CountOf("legacyLogin"));
        Assert.Equal(3, handler.CountOf("RemoteRecorderManagement"));
    }

    /// <summary>
    /// A cookie that has expired and a request Panopto simply refused look
    /// identical from here, so a fault buys exactly one fresh cookie.
    /// </summary>
    [Fact]
    public async Task A_fault_buys_one_fresh_cookie_and_then_works()
    {
        var (soap, handler) = Build(
            () => Cookie(".ASPXAUTH=stale"),
            Fault,
            () => Cookie(".ASPXAUTH=fresh"),
            Ok);

        await ListAsync(soap);

        Assert.Equal(2, handler.CountOf("legacyLogin"));

        var calls = handler.Requests.Where(r => r.Path.Contains("RemoteRecorderManagement")).ToList();
        Assert.Equal(".ASPXAUTH=stale", calls[0].Cookie);
        Assert.Equal(".ASPXAUTH=fresh", calls[1].Cookie);
    }

    /// <summary>
    /// The bound that keeps a bulk run from turning one request per row into
    /// three. Without it, every fault re-fetches a cookie — and a run where the
    /// rows are wrong faults on all of them.
    /// </summary>
    [Fact]
    public async Task A_run_that_faults_throughout_exchanges_the_cookie_only_twice()
    {
        // Spelled out request by request, because the exchange and the call it
        // authorises alternate: the second exchange has to answer with a cookie,
        // and every call after the retry has been spent answers with a fault.
        var (soap, handler) = Build(
            () => Cookie(CookieHeader), Fault,
            () => Cookie(CookieHeader), Fault,
            Fault);

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<PanoptoSoapFaultException>(() => ListAsync(soap));

        // The first call is the expensive one: exchange, fault, exchange, fault.
        // Every call after it goes straight out, however many of them fail.
        Assert.Equal(2, handler.CountOf("legacyLogin"));
        Assert.Equal(6, handler.CountOf("RemoteRecorderManagement"));
    }

    /// <summary>
    /// An exchange that returns no <c>.ASPXAUTH</c> must fail loudly. Carrying
    /// on with no cookie is the silent-anonymous-request failure mode this whole
    /// class exists to prevent.
    /// </summary>
    [Fact]
    public async Task An_exchange_without_a_cookie_is_an_error()
    {
        var empty = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "text/plain"),
        };

        var (soap, _) = Build(empty, Ok);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ListAsync(soap));
        Assert.Contains("no .ASPXAUTH cookie", ex.Message);
    }

    /// <summary>
    /// Signing out has to drop the cookie: it authorises writes by itself, so a
    /// cached one would let the next person to sign in act as the last.
    /// </summary>
    [Fact]
    public async Task A_signed_out_session_does_not_reuse_the_cookie()
    {
        // Scripted with a second cookie, because signing out and back in is a
        // second exchange — without it the handler would replay its last step
        // and the test would fail for the wrong reason.
        var handler = new ScriptedHandler(() => Cookie(".ASPXAUTH=first"), Ok,
            () => Cookie(".ASPXAUTH=second"), Ok);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };
        var limiters = new RateLimiterRegistry();
        var provider = new LegacyCookieProvider(http, limiters, new StubAuthenticator());
        var soap = new PanoptoSoapClient(http, limiters, new StubAuthenticator(), provider);

        await ListAsync(soap);
        Assert.Equal(1, handler.CountOf("legacyLogin"));

        provider.Invalidate();
        soap.AuthCookie = null;

        await ListAsync(soap);
        Assert.Equal(2, handler.CountOf("legacyLogin"));

        var calls = handler.Requests.Where(r => r.Path.Contains("RemoteRecorderManagement")).ToList();
        Assert.Equal(".ASPXAUTH=first", calls[0].Cookie);
        Assert.Equal(".ASPXAUTH=second", calls[1].Cookie);
    }

    /// <summary>
    /// A write is never re-sent, however it failed.
    ///
    /// <para>A retried <c>ScheduleRecording</c> is a second recording, not a
    /// repeat of the first — and because Panopto can accept a call and then fault
    /// on the way back, a resend can double-book a room with nothing in the
    /// report to say which call made which session. Reads are distinguishable
    /// from writes only by the caller saying so, which is what
    /// <c>safeToRetry</c> is for; this pins that the default refuses.</para>
    /// </summary>
    [Fact]
    public async Task A_write_is_never_retried()
    {
        // Scripted so a retry would visibly succeed: a fresh cookie and a 200 sit
        // ready behind the fault. If the write were resent it would land, and the
        // assertion below would see two calls instead of one.
        var (soap, handler) = Build(
            () => Cookie(CookieHeader),
            Fault,
            () => Cookie(".ASPXAUTH=fresh"),
            Ok);

        await Assert.ThrowsAsync<PanoptoSoapFaultException>(() => ScheduleAsync(soap));

        Assert.Equal(1, handler.CountOf("legacyLogin"));
        Assert.Equal(1, handler.CountOf("RemoteRecorderManagement"));
    }

    /// <summary>
    /// The failed write drops the cookie on its way out, so a stale one cannot
    /// make the same failure repeat. The operator gets the fault — and the call
    /// after it re-fetches and works.
    /// </summary>
    [Fact]
    public async Task A_faulted_write_drops_the_cookie_so_the_next_call_heals()
    {
        var (soap, handler) = Build(
            () => Cookie(".ASPXAUTH=stale"),
            Fault,
            () => Cookie(".ASPXAUTH=fresh"),
            Ok);

        await Assert.ThrowsAsync<PanoptoSoapFaultException>(() => ScheduleAsync(soap));

        // The write went out once. This read then re-exchanges and succeeds,
        // which is the whole recovery: one refused call, no silent duplicate.
        await ListAsync(soap);

        Assert.Equal(2, handler.CountOf("legacyLogin"));
    }
}
