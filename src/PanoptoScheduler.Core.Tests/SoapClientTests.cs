using System.Net;
using System.Text;
using System.Xml.Linq;
using PanoptoScheduler.Core.Auth;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.RateLimiting;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

/// <summary>Attaches a fixed bearer token, so nothing here needs OAuth.</summary>
internal sealed class StubAuthenticator : IPanoptoAuthenticator
{
    public bool IsAuthenticated => true;

    public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        request.Headers.Authorization = new("Bearer", "test-token");
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Captures the request body and returns one canned response, so the tests can
/// assert on the exact XML Panopto would receive.
/// </summary>
internal sealed class RecordingHandler(HttpStatusCode status, string response)
    : HttpMessageHandler
{
    public string? SentBody { get; private set; }
    public string? SentAction { get; private set; }
    public string? SentAuthorization { get; private set; }
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        SentBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        SentAction = request.Headers.TryGetValues("SOAPAction", out var actions)
            ? actions.First()
            : null;

        SentAuthorization = request.Headers.Authorization?.ToString();

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(response, Encoding.UTF8, "text/xml"),
        };
    }
}

public class SoapClientTests
{
    private const string Envelope =
        """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>%s</s:Body></s:Envelope>""";

    private static (PanoptoSoapClient Soap, RecordingHandler Handler) Build(
        string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler(status, Envelope.Replace("%s", body));
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://rotman.ca.panopto.com"),
        };

        return (new PanoptoSoapClient(http, new RateLimiterRegistry(), new StubAuthenticator()), handler);
    }

    // ---- Transport ------------------------------------------------------

    [Fact]
    public async Task Sends_a_soap_11_envelope()
    {
        var (soap, handler) = Build(
            """<ListRecordersResponse xmlns="http://tempuri.org/"><ListRecordersResult/></ListRecordersResponse>""");

        await soap.InvokeAsync(PanoptoSoapClient.RemoteRecorderManagementPath,
            "IRemoteRecorderManagement", SoapXml.Operation("ListRecorders"));

        Assert.NotNull(handler.SentBody);
        Assert.Contains("http://schemas.xmlsoap.org/soap/envelope/", handler.SentBody);
        Assert.Contains("<ListRecorders", handler.SentBody);
    }

    /// <summary>
    /// The SOAPAction is built from the contract interface. Dropping the leading
    /// I makes every call fail as an unrecognised action, which is a confusing
    /// failure to debug from the outside.
    /// </summary>
    [Fact]
    public async Task Sends_the_interface_prefixed_soap_action()
    {
        var (soap, handler) = Build(
            """<ListRecordersResponse xmlns="http://tempuri.org/"><ListRecordersResult/></ListRecordersResponse>""");

        await soap.InvokeAsync(PanoptoSoapClient.RemoteRecorderManagementPath,
            "IRemoteRecorderManagement", SoapXml.Operation("ListRecorders"));

        Assert.Equal("\"http://tempuri.org/IRemoteRecorderManagement/ListRecorders\"", handler.SentAction);
    }

    [Fact]
    public async Task Sends_the_bearer_token()
    {
        var (soap, handler) = Build(
            """<ListRecordersResponse xmlns="http://tempuri.org/"><ListRecordersResult/></ListRecordersResponse>""");

        await soap.InvokeAsync(PanoptoSoapClient.RemoteRecorderManagementPath,
            "IRemoteRecorderManagement", SoapXml.Operation("ListRecorders"));

        Assert.Equal("Bearer test-token", handler.SentAuthorization);
    }

    // ---- Faults ---------------------------------------------------------

    /// <summary>
    /// A SOAP fault arrives as HTTP 500 with the real reason in the body. If the
    /// status code is checked first, every rejection reads as "Internal Server
    /// Error" and the actual cause is lost.
    /// </summary>
    [Fact]
    public async Task Surfaces_the_fault_reason_rather_than_the_status_code()
    {
        var (soap, _) = Build("""
            <s:Fault xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <faultcode>s:Client</faultcode>
              <faultstring>Recorder not found</faultstring>
            </s:Fault>
            """, HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<PanoptoSoapFaultException>(() =>
            soap.InvokeAsync(PanoptoSoapClient.RemoteRecorderManagementPath,
                "IRemoteRecorderManagement", SoapXml.Operation("ListRecorders")));

        Assert.Contains("Recorder not found", error.Message);
        Assert.Equal("s:Client", error.Code);
        Assert.Contains("ListRecorders", error.Operation);
    }

    /// <summary>
    /// A 200 whose body is not XML at all — a captive portal or a proxy, not
    /// Panopto. The message has to say the body was unreadable rather than
    /// failing later with an empty result that looks like "no recorders".
    /// </summary>
    [Fact]
    public async Task Reports_a_non_xml_success_body_clearly()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "not xml < at all");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };
        var soap = new PanoptoSoapClient(http, new RateLimiterRegistry(), new StubAuthenticator());

        var error = await Assert.ThrowsAsync<PanoptoSoapFaultException>(() =>
            soap.InvokeAsync(PanoptoSoapClient.RemoteRecorderManagementPath,
                "IRemoteRecorderManagement", SoapXml.Operation("ListRecorders")));

        Assert.Contains("not XML", error.Message);
    }

    /// <summary>
    /// A gateway error is not a SOAP fault, so the status code is the only thing
    /// that explains it and must survive.
    /// </summary>
    [Fact]
    public async Task An_error_status_with_an_unparseable_body_keeps_the_status_code()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadGateway, "<html>Gateway timeout</html>");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };
        var soap = new PanoptoSoapClient(http, new RateLimiterRegistry(), new StubAuthenticator());

        var error = await Assert.ThrowsAsync<PanoptoRequestException>(() =>
            soap.InvokeAsync(PanoptoSoapClient.RemoteRecorderManagementPath,
                "IRemoteRecorderManagement", SoapXml.Operation("ListRecorders")));

        Assert.Equal(HttpStatusCode.BadGateway, error.Status);
    }

    /// <summary>
    /// Answers a scripted sequence of responses, so a test can have one call
    /// fail and the next succeed. <see cref="RecordingHandler"/> answers one
    /// canned response to every call, which cannot express "the retry is what
    /// we are testing".
    /// </summary>
    internal sealed class SequencedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(responses[Math.Min(_index++, responses.Length - 1)]);
        }
    }

    /// <summary>
    /// A 429 must pause the endpoint, not just throw. Deleting the
    /// <c>PauseFor</c> call — or only the <c>Retry-After</c> parsing —
    /// leaves the app hammering an endpoint the server has just metered for
    /// the rest of a bulk run, and the earlier version of this test asserted
    /// nothing but the throw, which the 429 path raises regardless of any
    /// pause. So the second call is timed: the limiter's wait must hold it
    /// back for at least the server's Retry-After before it goes out.
    /// </summary>
    [Fact]
    public async Task A_429_pauses_the_endpoint_for_the_retry_after()
    {
        var retryAfter = TimeSpan.FromMilliseconds(400);
        var handler = new SequencedHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter) },
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    Envelope.Replace("%s",
                        """<ListRecordersResponse xmlns="http://tempuri.org/"><ListRecordersResult/></ListRecordersResponse>"""),
                    Encoding.UTF8, "text/xml"),
            });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };
        var soap = new PanoptoSoapClient(http, new RateLimiterRegistry(), new StubAuthenticator());

        var operation = SoapXml.Operation("ListRecorders");

        await Assert.ThrowsAsync<PanoptoRequestException>(() =>
            soap.InvokeAsync(PanoptoSoapClient.RemoteRecorderManagementPath,
                "IRemoteRecorderManagement", operation));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await soap.InvokeAsync(PanoptoSoapClient.RemoteRecorderManagementPath,
            "IRemoteRecorderManagement", operation);
        watch.Stop();

        Assert.Equal(2, handler.Calls);
        Assert.True(watch.Elapsed >= retryAfter,
            $"the endpoint was not paused: the second call went out after {watch.Elapsed.TotalMilliseconds:0}ms"
            + $" and the server asked for {retryAfter.TotalMilliseconds:0}ms.");
    }

    // ---- Results --------------------------------------------------------

    [Fact]
    public async Task A_nil_result_reads_as_no_result()
    {
        var (soap, _) = Build("""
            <GetDefaultFolderForRecorderResponse xmlns="http://tempuri.org/">
              <GetDefaultFolderForRecorderResult xmlns:i="http://www.w3.org/2001/XMLSchema-instance" i:nil="true"/>
            </GetDefaultFolderForRecorderResponse>
            """);

        Assert.Null(await soap.InvokeAsync("", "IRemoteRecorderManagement",
            SoapXml.Operation("GetDefaultFolderForRecorder")));
    }

    // ---- Recorder lookup ------------------------------------------------

    private static string Recorders(string xml)
        => $"""<ListRecordersResponse xmlns="http://tempuri.org/"><ListRecordersResult>{xml}</ListRecordersResult></ListRecordersResponse>""";

    [Fact]
    public async Task Lists_recorders()
    {
        var (soap, _) = Build(Recorders("""
            <PagedResults xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap">
              <RemoteRecorder>
                <Id>11111111-1111-1111-1111-111111111111</Id>
                <Name>JMHH240</Name>
                <ExternalId>SCRIBE-2175680</ExternalId>
                <State>Stopped</State>
              </RemoteRecorder>
            </PagedResults>
            """));

        var recorders = await new RemoteRecorderClient(soap).ListRecordersAsync();

        Assert.Single(recorders);
        Assert.Equal("JMHH240", recorders[0].Name);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), recorders[0].Id);
        Assert.Equal("Stopped", recorders[0].State);
    }

    [Fact]
    public async Task Finds_a_recorder_by_name_ignoring_case()
    {
        var (soap, _) = Build(Recorders("""
            <PagedResults xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap">
              <RemoteRecorder>
                <Id>11111111-1111-1111-1111-111111111111</Id>
                <Name>JMHH240</Name>
              </RemoteRecorder>
            </PagedResults>
            """));

        var found = await new RemoteRecorderClient(soap).FindRecorderAsync("jmhh240");

        Assert.NotNull(found);
        Assert.Equal("JMHH240", found!.Name);
    }

    /// <summary>Legacy rows sometimes put the recorder's external id in the name column.</summary>
    [Fact]
    public async Task Falls_back_to_the_external_id()
    {
        var (soap, _) = Build(Recorders("""
            <PagedResults xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap">
              <RemoteRecorder>
                <Id>11111111-1111-1111-1111-111111111111</Id>
                <Name>JMHH240</Name>
                <ExternalId>SCRIBE-2175680</ExternalId>
              </RemoteRecorder>
            </PagedResults>
            """));

        var found = await new RemoteRecorderClient(soap).FindRecorderAsync("SCRIBE-2175680");

        Assert.Equal("JMHH240", found!.Name);
    }

    [Fact]
    public async Task An_unknown_recorder_is_null_rather_than_a_guess()
    {
        var (soap, _) = Build(Recorders("""
            <PagedResults xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap">
              <RemoteRecorder>
                <Id>11111111-1111-1111-1111-111111111111</Id>
                <Name>JMHH240</Name>
              </RemoteRecorder>
            </PagedResults>
            """));

        Assert.Null(await new RemoteRecorderClient(soap).FindRecorderAsync("NOWHERE"));
    }

    /// <summary>A recorder with no id cannot be scheduled against, so it is dropped.</summary>
    [Fact]
    public async Task Skips_a_recorder_with_no_id()
    {
        var (soap, _) = Build(Recorders("""
            <PagedResults xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap">
              <RemoteRecorder><Name>NoId</Name></RemoteRecorder>
              <RemoteRecorder>
                <Id>11111111-1111-1111-1111-111111111111</Id>
                <Name>JMHH240</Name>
              </RemoteRecorder>
            </PagedResults>
            """));

        var recorders = await new RemoteRecorderClient(soap).ListRecordersAsync();

        Assert.Single(recorders);
        Assert.Equal("JMHH240", recorders[0].Name);
    }

    // ---- Scheduling -----------------------------------------------------

    [Fact]
    public async Task Sends_the_recorder_settings_with_both_feeds_enabled()
    {
        var (soap, handler) = Build("""
            <ScheduleRecordingResponse xmlns="http://tempuri.org/">
              <ScheduleRecordingResult><ConflictsExist>false</ConflictsExist>
                <SessionIDs xmlns:a="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
                  <a:guid>22222222-2222-2222-2222-222222222222</a:guid>
                </SessionIDs>
              </ScheduleRecordingResult>
            </ScheduleRecordingResponse>
            """);

        var client = new RemoteRecorderClient(soap);

        var result = await client.ScheduleAsync(
            "MGEC611002_STAFF",
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            isBroadcast: true,
            new DateTime(2021, 1, 26, 11, 10, 0),
            new DateTime(2021, 1, 26, 12, 0, 0),
            [Guid.Parse("11111111-1111-1111-1111-111111111111")]);

        var sent = handler.SentBody!;

        Assert.Contains("<name>MGEC611002_STAFF</name>", sent);
        Assert.Contains("<isBroadcast>true</isBroadcast>", sent);
        Assert.Contains("<SuppressPrimary>false</SuppressPrimary>", sent);
        Assert.Contains("<SuppressSecondary>false</SuppressSecondary>", sent);
        Assert.Contains("11111111-1111-1111-1111-111111111111", sent);

        Assert.False(result.ConflictsExist);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), result.SessionId);
    }

    /// <summary>
    /// Every element of the schedule request sits in the namespace the schema
    /// that declares it uses.
    ///
    /// <para>Substring assertions cannot see a namespace, which is how
    /// <c>recorderSettings</c> shipped under the serialization-arrays namespace
    /// and the tenant answered <c>Value cannot be null. Parameter name:
    /// recorderSettings</c> — a fault that reads like a missing argument rather
    /// than a misplaced wrapper. The wrapper is declared inside
    /// <c>ScheduleRecording</c>, so it is <c>tempuri.org</c>; the settings
    /// inside it are declared in <c>V40</c>.</para>
    /// </summary>
    [Fact]
    public async Task Puts_every_element_in_the_namespace_its_schema_declares()
    {
        var (soap, handler) = Build("""
            <ScheduleRecordingResponse xmlns="http://tempuri.org/">
              <ScheduleRecordingResult><ConflictsExist>false</ConflictsExist></ScheduleRecordingResult>
            </ScheduleRecordingResponse>
            """);

        await new RemoteRecorderClient(soap).ScheduleAsync(
            "T",
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            isBroadcast: false,
            new DateTime(2021, 1, 26, 11, 10, 0),
            new DateTime(2021, 1, 26, 12, 0, 0),
            [Guid.Parse("11111111-1111-1111-1111-111111111111")]);

        XNamespace soapNs = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace tns = "http://tempuri.org/";
        XNamespace v40 =
            "http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V40";

        var operation = XDocument.Parse(handler.SentBody!)
            .Root!.Element(soapNs + "Body")!.Element(tns + "ScheduleRecording");

        Assert.NotNull(operation);

        foreach (var name in new[] { "name", "folderId", "isBroadcast", "start", "end", "recorderSettings" })
            Assert.NotNull(operation!.Element(tns + name));

        // A RecorderId that failed to bind is a recording that never happens.
        var settings = operation!.Element(tns + "recorderSettings")!
            .Element(v40 + "RecorderSettings");

        Assert.NotNull(settings);
        Assert.Equal("11111111-1111-1111-1111-111111111111", settings!.Element(v40 + "RecorderId")?.Value);
        Assert.Equal("false", settings.Element(v40 + "SuppressPrimary")?.Value);
        Assert.Equal("false", settings.Element(v40 + "SuppressSecondary")?.Value);
    }

    /// <summary>
    /// The digits sent are the instant the room's wall clock names — not the wall
    /// clock.
    ///
    /// <para>This test asserted the opposite until the first live booking was read
    /// back: sending 11:10 unchanged stored 07:10 on the tenant, four hours early,
    /// which is exactly Toronto's distance from UTC. The expectation before that
    /// came from reading the legacy uploader's source rather than measuring the
    /// tenant. The zone is named here so the assertion does not depend on where
    /// this machine thinks it is; see <see cref="RoomClock"/>.</para>
    /// </summary>
    [Fact]
    public async Task Sends_times_as_the_instant_the_room_names()
    {
        var (soap, handler) = Build("""
            <ScheduleRecordingResponse xmlns="http://tempuri.org/">
              <ScheduleRecordingResult><ConflictsExist>false</ConflictsExist></ScheduleRecordingResult>
            </ScheduleRecordingResponse>
            """);

        await new RemoteRecorderClient(soap, RoomClock.Resolve("America/Toronto")).ScheduleAsync(
            "T", Guid.Empty, false,
            new DateTime(2021, 1, 26, 11, 10, 0, DateTimeKind.Local),
            new DateTime(2021, 1, 26, 12, 0, 0, DateTimeKind.Local),
            [Guid.Empty]);

        // 26 January in Toronto is EST, five hours behind UTC.
        Assert.Contains("<start>2021-01-26T16:10:00Z</start>", handler.SentBody);
        Assert.Contains("<end>2021-01-26T17:00:00Z</end>", handler.SentBody);
    }

    /// <summary>
    /// A clash comes back as a success with ConflictsExist set. Reading only the
    /// status would double-book the room.
    /// </summary>
    [Fact]
    public async Task Reports_a_conflict_as_a_conflict()
    {
        var (soap, _) = Build("""
            <ScheduleRecordingResponse xmlns="http://tempuri.org/">
              <ScheduleRecordingResult>
                <ConflictsExist>true</ConflictsExist>
                <ConflictingSessions xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap">
                  <ScheduledRecordingInfo>
                    <SessionName>STAT500 Lecture</SessionName>
                    <StartTime>2021-01-26T11:30:00</StartTime>
                  </ScheduledRecordingInfo>
                </ConflictingSessions>
              </ScheduleRecordingResult>
            </ScheduleRecordingResponse>
            """);

        var result = await new RemoteRecorderClient(soap).ScheduleAsync(
            "T", Guid.Empty, false,
            new DateTime(2021, 1, 26, 11, 0, 0),
            new DateTime(2021, 1, 26, 12, 0, 0),
            [Guid.Empty]);

        Assert.True(result.ConflictsExist);
        Assert.Single(result.Conflicts);
        Assert.Contains("STAT500 Lecture", result.Conflicts[0]);
    }

    /// <summary>
    /// The drag-to-reschedule call, including the times it sends.
    ///
    /// <para>The times were unasserted here for a while, which left the one write
    /// path a user reaches by fumbling a mouse drop with no test on its wire
    /// format at all — the session id and the SOAPAction were pinned, the digits
    /// beside them were not.</para>
    ///
    /// <para>Both <see cref="DateTimeKind"/>s appear deliberately.
    /// <c>Kind=Local</c> is what <c>RescheduleAsync</c> produces from a read-back
    /// value, and <c>Unspecified</c> is what an imported row carries. Both are wall
    /// clocks and both must reach the wire as the same instant — honouring either
    /// marker would move the recording by this workstation's distance from the
    /// room, which is the bug this pair of assertions was rewritten for.</para>
    /// </summary>
    [Fact]
    public async Task Reschedules_with_the_session_id_and_the_instant_the_room_names()
    {
        var (soap, handler) = Build("""
            <UpdateRecordingTimeResponse xmlns="http://tempuri.org/">
              <UpdateRecordingTimeResult><ConflictsExist>false</ConflictsExist></UpdateRecordingTimeResult>
            </UpdateRecordingTimeResponse>
            """);

        await new RemoteRecorderClient(soap, RoomClock.Resolve("America/Toronto"))
            .UpdateRecordingTimeAsync(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                new DateTime(2021, 1, 27, 9, 0, 0, DateTimeKind.Local),
                new DateTime(2021, 1, 27, 10, 0, 0));

        Assert.Contains("<sessionId>22222222-2222-2222-2222-222222222222</sessionId>", handler.SentBody);

        // EST again: nine in the room is fourteen hundred UTC.
        Assert.Contains("<start>2021-01-27T14:00:00Z</start>", handler.SentBody);
        Assert.Contains("<end>2021-01-27T15:00:00Z</end>", handler.SentBody);
        Assert.Contains("\"http://tempuri.org/IRemoteRecorderManagement/UpdateRecordingTime\"",
            handler.SentAction);
    }

    // ---- Session management ---------------------------------------------

    [Fact]
    public async Task Moves_sessions_with_the_guids_in_the_array_namespace()
    {
        var (soap, handler) = Build("""<MoveSessionsResponse xmlns="http://tempuri.org/"/>""");

        await new SessionManagementClient(soap).MoveSessionsAsync(
            [Guid.Parse("22222222-2222-2222-2222-222222222222")],
            Guid.Parse("33333333-3333-3333-3333-333333333333"));

        var sent = handler.SentBody!;

        Assert.Contains("http://schemas.microsoft.com/2003/10/Serialization/Arrays", sent);
        Assert.Contains("<sessionIds", sent);
        Assert.Contains("<folderId>33333333-3333-3333-3333-333333333333</folderId>", sent);
        Assert.Contains("\"http://tempuri.org/ISessionManagement/MoveSessions\"", handler.SentAction);
    }

    [Fact]
    public async Task Lists_folders_by_search_query()
    {
        var (soap, handler) = Build("""
            <GetFoldersListResponse xmlns="http://tempuri.org/">
              <GetFoldersListResult>
                <Results xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V40">
                  <Folder><Id>33333333-3333-3333-3333-333333333333</Id><Name>TestFolder</Name></Folder>
                </Results>
              </GetFoldersListResult>
            </GetFoldersListResponse>
            """);

        var folders = await new SessionManagementClient(soap).ListFoldersAsync("TestFolder");

        Assert.Contains("<searchQuery>TestFolder</searchQuery>", handler.SentBody);
        Assert.Single(folders);
        Assert.Equal("TestFolder", folders[0].Name);
    }

    [Fact]
    public async Task An_empty_search_query_is_sent_as_nil_not_as_an_empty_string()
    {
        var (soap, handler) = Build("""<GetFoldersListResponse xmlns="http://tempuri.org/"/>""");

        await new SessionManagementClient(soap).ListFoldersAsync();

        Assert.Contains("nil=\"true\"", handler.SentBody);
    }

    /// <summary>A folder hint that is already a guid needs no lookup.</summary>
    [Fact]
    public async Task A_guid_folder_hint_resolves_without_a_request()
    {
        var (soap, handler) = Build("""<GetFoldersListResponse xmlns="http://tempuri.org/"/>""");

        var folder = await new SessionManagementClient(soap)
            .FindFolderAsync("33333333-3333-3333-3333-333333333333");

        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), folder!.Id);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task An_unknown_folder_name_is_null_rather_than_a_guess()
    {
        var (soap, _) = Build("""
            <GetFoldersListResponse xmlns="http://tempuri.org/">
              <GetFoldersListResult>
                <Results xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V40">
                  <Folder><Id>33333333-3333-3333-3333-333333333333</Id><Name>SomethingElse</Name></Folder>
                </Results>
              </GetFoldersListResult>
            </GetFoldersListResponse>
            """);

        var folder = await new SessionManagementClient(soap).FindFolderAsync("TestFolder");

        // "SomethingElse" does not contain "TestFolder", so nothing matches.
        Assert.Null(folder);
    }

    [Fact]
    public async Task Deletes_sessions()
    {
        var (soap, handler) = Build("""<DeleteSessionsResponse xmlns="http://tempuri.org/"/>""");

        await new SessionManagementClient(soap).DeleteSessionsAsync(
            [Guid.Parse("22222222-2222-2222-2222-222222222222")]);

        Assert.Contains("\"http://tempuri.org/ISessionManagement/DeleteSessions\"", handler.SentAction);
        Assert.Contains("22222222-2222-2222-2222-222222222222", handler.SentBody);
    }

    // ---- Helpers --------------------------------------------------------

    [Fact]
    public void Envelope_puts_the_operation_in_the_contract_namespace()
    {
        var envelope = PanoptoSoapClient.BuildEnvelope(SoapXml.Operation("ListRecorders"));

        var parsed = XDocument.Parse(envelope);
        var operation = parsed.Descendants().Single(e => e.Name.LocalName == "ListRecorders");

        Assert.Equal(PanoptoXml.Tns, operation.Name.Namespace);
    }

    [Fact]
    public void Guids_skips_empty_and_nil_entries()
    {
        var root = XElement.Parse("""
            <root><SessionIDs xmlns="http://schemas.microsoft.com/2003/10/Serialization/Arrays"
                              xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <guid>22222222-2222-2222-2222-222222222222</guid>
              <guid>00000000-0000-0000-0000-000000000000</guid>
              <guid i:nil="true"/>
            </SessionIDs></root>
            """);

        var guids = SoapXml.Guids(root, "SessionIDs");

        Assert.Single(guids);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), guids[0]);
    }

    [Fact]
    public void Nil_is_detected_whatever_the_prefix()
    {
        var element = XElement.Parse(
            """<v xmlns:i="http://www.w3.org/2001/XMLSchema-instance" i:nil="true"/>""");

        Assert.True(PanoptoSoapClient.IsNil(element));
    }
}
