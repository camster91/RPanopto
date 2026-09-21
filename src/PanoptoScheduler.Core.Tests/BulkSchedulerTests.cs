using System.Net;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Import;
using PanoptoScheduler.Core.RateLimiting;
using PanoptoScheduler.Core.Scheduling;
using static PanoptoScheduler.Core.Tests.SoapListings;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// Answers each SOAP operation with canned XML, so the pipeline can be exercised
/// without a tenant.
/// </summary>
internal sealed class ScriptedSoapHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _queues = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    /// <summary>
    /// The request bodies, in the same order as <see cref="Calls"/>. Needed to see
    /// what a page request actually asked for — a paging loop that sends the same
    /// page number every time still looks like a loop of successful requests.
    /// </summary>
    public List<string> Bodies { get; } = [];

    public ScriptedSoapHandler Respond(string operation, string body)
    {
        _responses[operation] = body;
        return this;
    }

    /// <summary>
    /// Answers the next call to this operation with the next body in the queue,
    /// for the listings that are read a page at a time.
    ///
    /// <para>The last body queued is kept rather than consumed, so a call past
    /// the end repeats it. Letting it fall through to the empty default would
    /// read as "the server sent nothing", which ends a page walk — and would make
    /// a paging test pass for entirely the wrong reason.</para>
    /// </summary>
    public ScriptedSoapHandler RespondOnce(string operation, string body)
    {
        if (!_queues.TryGetValue(operation, out var queue))
        {
            queue = new Queue<string>();
            _queues[operation] = queue;
        }

        queue.Enqueue(body);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var operation = request.Headers.TryGetValues("SOAPAction", out var actions)
            ? actions.First().Trim('"').Split('/').Last()
            : "unknown";

        Calls.Add(operation);
        Bodies.Add(body);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>{Payload(operation)}</s:Body></s:Envelope>""",
                System.Text.Encoding.UTF8, "text/xml"),
        };
    }

    private string Payload(string operation)
    {
        if (_queues.TryGetValue(operation, out var queue) && queue.Count > 0)
        {
            return queue.Count == 1 ? queue.Peek() : queue.Dequeue();
        }

        return _responses.TryGetValue(operation, out var canned)
            ? canned
            : $"<{operation}Response xmlns=\"http://tempuri.org/\"/>";
    }
}

public class BulkSchedulerTests
{
    private const string RecorderGuid = "11111111-1111-1111-1111-111111111111";
    private const string FolderGuid = "33333333-3333-3333-3333-333333333333";
    private const string SessionGuid = "22222222-2222-2222-2222-222222222222";

    private static string Scheduled(bool conflict = false)
        => $"""
            <ScheduleRecordingResponse xmlns="http://tempuri.org/"><ScheduleRecordingResult>
              <ConflictsExist>{(conflict ? "true" : "false")}</ConflictsExist>
              <SessionIDs xmlns="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
                <guid>{SessionGuid}</guid>
              </SessionIDs>
            </ScheduleRecordingResult></ScheduleRecordingResponse>
            """;

    private static (BulkScheduler Scheduler, ScriptedSoapHandler Handler) Build(
        Action<ScriptedSoapHandler> configure,
        IBulkAuditLog? auditLog = null)
    {
        var handler = new ScriptedSoapHandler();
        configure(handler);

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };
        var soap = new PanoptoSoapClient(http, new RateLimiterRegistry(), new StubAuthenticator());

        return (new BulkScheduler(
            new RemoteRecorderClient(soap),
            new SessionManagementClient(soap),
            auditLog), handler);
    }

    private static ScheduleImportRow Row(
        string title = "MGEC611002_STAFF",
        string recorder = "JMHH240",
        string? folder = "TestFolder",
        string? presenter = "Rosenbaum",
        int line = 1)
        => new()
        {
            Line = line,
            Title = title,
            RecorderName = recorder,
            Start = new DateTime(2021, 1, 26, 11, 10, 0),
            End = new DateTime(2021, 1, 26, 12, 0, 0),
            FolderHint = folder,
            Presenter = presenter,
        };

    // ---- Dry run --------------------------------------------------------

    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        var (scheduler, handler) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder"))));

        var report = await scheduler.RunAsync([Row()], new BulkScheduleOptions { DryRun = true });

        // Reported as "would", never as "scheduled" — otherwise the report
        // cannot be used to decide whether to run it for real.
        Assert.Equal(1, report.WouldSchedule);
        Assert.Equal(0, report.Scheduled);
        Assert.False(report.AnyWritesAttempted);
        Assert.DoesNotContain("ScheduleRecording", handler.Calls);
        Assert.Contains("Would record on JMHH240", report.Outcomes[0].Message);
    }

    // ---- Resolution -----------------------------------------------------

    /// <summary>
    /// One listing per batch, not one per row. Panopto meters per endpoint, and
    /// a per-row lookup would exhaust the budget part-way through a real term's
    /// worth of rows.
    /// </summary>
    [Fact]
    public async Task Looks_the_recorder_list_up_once_for_the_whole_batch()
    {
        var (scheduler, handler) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder"))));

        await scheduler.RunAsync(
            [Row(line: 1), Row(line: 2), Row(line: 3), Row(line: 4)],
            new BulkScheduleOptions { DryRun = true });

        Assert.Equal(1, handler.Calls.Count(c => c == "ListRecorders"));
    }

    [Fact]
    public async Task Looks_a_repeated_folder_up_once()
    {
        var (scheduler, handler) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder"))));

        await scheduler.RunAsync(
            [Row(line: 1), Row(line: 2), Row(line: 3)],
            new BulkScheduleOptions { DryRun = true });

        Assert.Equal(1, handler.Calls.Count(c => c == "GetFoldersList"));
    }

    [Fact]
    public async Task Skips_a_row_whose_recorder_does_not_exist()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240"))));

        var report = await scheduler.RunAsync(
            [Row(recorder: "GHOST")], new BulkScheduleOptions { DryRun = true });

        Assert.Equal(1, report.Skipped);
        Assert.Contains("No recorder named 'GHOST'", report.Outcomes[0].Message);
    }

    /// <summary>
    /// The other half of <see cref="Skips_a_row_whose_recorder_does_not_exist"/>,
    /// and the half that was missing for a whole revision.
    ///
    /// <para><c>Skipped</c> and <c>Failed</c> are different promises to whoever
    /// reads the report. Skipped says the row was wrong; Failed says the booking
    /// did not happen. A room absent from a listing that stopped short is not
    /// evidence the room is wrong — it is evidence this app did not read that far
    /// — and calling it Skipped is how 5000 rooms were unreachable while the
    /// report said every row was fine.</para>
    ///
    /// <para>The row is the same row as the fact above; only the listing differs.
    /// That is the point: nothing about the row changed, and the answer must.</para>
    /// </summary>
    [Fact]
    public async Task Fails_rather_than_skips_an_unresolvable_room_when_the_listing_is_incomplete()
    {
        var (scheduler, handler) = Build(h =>
        {
            // Fresh items on every page and no total: only the ceiling can end
            // this, so the walk reports itself incomplete.
            for (var page = 0; page < SoapPaging.MaxPages; page++)
            {
                h.RespondOnce("ListRecorders", RecorderListingWithoutTotal(
                    [.. Enumerable.Range(page * SoapPaging.PageSize, SoapPaging.PageSize).Select(Recorder)]));
            }

            h.Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder")));
        });

        var report = await scheduler.RunAsync(
            [Row(recorder: "Event Space 214")], new BulkScheduleOptions { DryRun = true });

        Assert.Equal(0, report.Skipped);
        Assert.Equal(1, report.Failed);

        // The message has to send the operator to the app rather than to the row,
        // because that is where the fault is.
        Assert.Contains("the room list is incomplete", report.Outcomes[0].Message);

        // And it must not read as "this room does not exist", which is what the
        // Skipped wording said while meaning exactly that.
        Assert.DoesNotContain("No recorder named 'Event Space 214'.", report.Outcomes[0].Message);
    }

    /// <summary>
    /// The folder side of the same defect, and it resolves the other way: a room
    /// that cannot be resolved fails the row, a folder that cannot be resolved has
    /// somewhere safe to go.
    ///
    /// <para>So the booking still happens — and the sentence an operator reads has
    /// to say <b>why it went to the fallback</b>, because the two reasons ask for
    /// different work. "The folder was not found" sends them to fix the row. "The
    /// folder list was incomplete" sends them to this app, which is where the fault
    /// is. Reporting the second as the first is the same misdirection that made
    /// 5000 rooms unreachable while every row read as fine.</para>
    ///
    /// <para>Nothing was written to the tenant to establish this: the path is
    /// exercised through a dry run, which resolves folders exactly as a real run
    /// does and stops before the scheduling call.</para>
    /// </summary>
    [Fact]
    public async Task Degrades_to_the_fallback_folder_when_the_folder_listing_is_incomplete()
    {
        var (scheduler, handler) = Build(h =>
        {
            // Fresh folders on every page and no total, so only the ceiling ends
            // the walk and the result reports itself incomplete.
            for (var page = 0; page < SoapPaging.MaxPages; page++)
            {
                h.RespondOnce("GetFoldersList", FolderListingWithoutTotal(
                    [.. Enumerable.Range(page * SoapPaging.PageSize, SoapPaging.PageSize).Select(Folder)]));
            }

            h.Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")));
            h.Respond("GetDefaultFolderForRecorder",
                $"""<GetDefaultFolderForRecorderResponse xmlns="http://tempuri.org/"><GetDefaultFolderForRecorderResult>{FolderGuid}</GetDefaultFolderForRecorderResult></GetDefaultFolderForRecorderResponse>""");
        });

        var report = await scheduler.RunAsync(
            [Row(folder: "DoesNotExist", line: 1), Row(folder: "DoesNotExist", line: 2)],
            new BulkScheduleOptions { DryRun = true });

        // Degraded, not refused: both recordings are still going somewhere sane.
        Assert.Equal(2, report.WouldSchedule);
        Assert.Equal(0, report.Failed);

        Assert.All(report.Outcomes, o =>
            Assert.Contains("the folder list was incomplete, so it may exist", o.Message));

        // The claim this app has not earned, and would have made before.
        Assert.All(report.Outcomes, o => Assert.DoesNotContain("'DoesNotExist' was not found", o.Message));

        // The walk is not repeated per row: the unresolved answer is cached along
        // with the resolved one, so a term's worth of rows naming the same folder
        // pays for the truncated listing once.
        Assert.Equal(SoapPaging.MaxPages, handler.Calls.Count(c => c == "GetFoldersList"));
    }

    /// <summary>
    /// The reported bug, end to end: a room that sits past the first page of the
    /// listing used to be unresolvable, and an unresolvable room is <c>Skipped</c>
    /// rather than <c>Failed</c> — so the row did not book and did not complain.
    /// A silent skip is the worst of the three outcomes, because the only evidence
    /// is a recording that never happened.
    /// </summary>
    [Fact]
    public async Task Books_a_room_that_is_only_on_the_second_page_of_the_listing()
    {
        const string SecondPageGuid = "44444444-4444-4444-4444-444444444444";

        var (scheduler, handler) = Build(h => h
            // Page 0 does not hold the room being booked; page 1 does. A reader
            // that stops after the first page never sees it.
            .RespondOnce("ListRecorders", RecorderPage(2, (RecorderGuid, "JMHH240")))
            .RespondOnce("ListRecorders", RecorderPage(2, (SecondPageGuid, "Event Space 214")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder"))));

        var report = await scheduler.RunAsync(
            [Row(recorder: "Event Space 214")], new BulkScheduleOptions { DryRun = true });

        Assert.Equal(0, report.Skipped);
        Assert.Equal(1, report.WouldSchedule);
        Assert.Contains("Would record on Event Space 214", report.Outcomes[0].Message);

        // Two, not one — and not one per row either: the listing is read a page
        // at a time, once for the batch.
        Assert.Equal(2, handler.Calls.Count(c => c == "ListRecorders"));
    }

    [Fact]
    public async Task Falls_back_to_the_configured_default_folder()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "Default"))));

        var report = await scheduler.RunAsync(
            [Row(folder: "DoesNotExist")],
            new BulkScheduleOptions { DryRun = true, DefaultFolderName = "Default" });

        Assert.Equal(1, report.WouldSchedule);
        Assert.Contains("Recorded to 'Default'", report.Outcomes[0].Message);
    }

    /// <summary>
    /// The fallback must be visible. Silently recording a lecture into the wrong
    /// folder is the failure mode this whole report exists to prevent.
    /// </summary>
    [Fact]
    public async Task Says_so_when_it_used_the_fallback_folder()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "Default"))));

        var report = await scheduler.RunAsync(
            [Row(folder: "DoesNotExist")],
            new BulkScheduleOptions { DryRun = true, DefaultFolderName = "Default" });

        Assert.Contains("'DoesNotExist' was not found", report.Outcomes[0].Message);
    }

    [Fact]
    public async Task Falls_back_to_the_recorders_own_default_folder()
    {
        var (scheduler, handler) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing())
            .Respond("GetDefaultFolderForRecorder",
                $"""<GetDefaultFolderForRecorderResponse xmlns="http://tempuri.org/"><GetDefaultFolderForRecorderResult>{FolderGuid}</GetDefaultFolderForRecorderResult></GetDefaultFolderForRecorderResponse>"""));

        var report = await scheduler.RunAsync(
            [Row(folder: "DoesNotExist")], new BulkScheduleOptions { DryRun = true });

        Assert.Equal(1, report.WouldSchedule);
        Assert.Equal(1, handler.Calls.Count(c => c == "GetDefaultFolderForRecorder"));
    }

    [Fact]
    public async Task Skips_when_no_folder_can_be_resolved_at_all()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing()));

        var report = await scheduler.RunAsync(
            [Row(folder: "DoesNotExist")], new BulkScheduleOptions { DryRun = true });

        Assert.Equal(1, report.Skipped);
        Assert.Contains("no default to fall back on", report.Outcomes[0].Message);
    }

    // ---- Scheduling -----------------------------------------------------

    [Fact]
    public async Task Schedules_and_reports_the_session_id()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder")))
            .Respond("ScheduleRecording", Scheduled())
            .Respond("UpdateSessionDescription", ""));

        var report = await scheduler.RunAsync([Row()], new BulkScheduleOptions { DryRun = false });

        Assert.Equal(1, report.Scheduled);
        Assert.Equal(Guid.Parse(SessionGuid), report.Outcomes[0].SessionId);
    }

    [Fact]
    public async Task Sets_the_presenter_as_the_session_description()
    {
        var (scheduler, handler) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder")))
            .Respond("ScheduleRecording", Scheduled())
            .Respond("UpdateSessionDescription", ""));

        await scheduler.RunAsync([Row(presenter: "Stew Mixalot")],
            new BulkScheduleOptions { DryRun = false });

        Assert.Contains("UpdateSessionDescription", handler.Calls);
    }

    [Fact]
    public async Task Skips_the_description_when_the_row_has_no_presenter()
    {
        var (scheduler, handler) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder")))
            .Respond("ScheduleRecording", Scheduled()));

        await scheduler.RunAsync([Row(presenter: null)], new BulkScheduleOptions { DryRun = false });

        Assert.DoesNotContain("UpdateSessionDescription", handler.Calls);
    }

    /// <summary>
    /// A clash is a success with a flag, not an error. Reporting it as booked
    /// without saying so would double-book the room silently.
    /// </summary>
    [Fact]
    public async Task Reports_a_conflict_as_a_conflict()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder")))
            .Respond("ScheduleRecording", """
                <ScheduleRecordingResponse xmlns="http://tempuri.org/"><ScheduleRecordingResult>
                  <ConflictsExist>true</ConflictsExist>
                  <ConflictingSessions xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap">
                    <ScheduledRecordingInfo>
                      <SessionName>STAT500 Lecture</SessionName>
                      <StartTime>2021-01-26T11:30:00</StartTime>
                    </ScheduledRecordingInfo>
                  </ConflictingSessions>
                  <SessionIDs xmlns="http://schemas.microsoft.com/2003/10/Serialization/Arrays">
                    <guid>22222222-2222-2222-2222-222222222222</guid>
                  </SessionIDs>
                </ScheduleRecordingResult></ScheduleRecordingResponse>
                """)
            .Respond("UpdateSessionDescription", ""));

        var report = await scheduler.RunAsync([Row()], new BulkScheduleOptions { DryRun = false });

        Assert.Equal(1, report.Conflicts);
        Assert.Contains("STAT500 Lecture", report.Outcomes[0].Message);
    }

    [Fact]
    public async Task A_scheduling_call_that_returns_no_session_is_a_failure()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder")))
            .Respond("ScheduleRecording",
                """<ScheduleRecordingResponse xmlns="http://tempuri.org/"><ScheduleRecordingResult><ConflictsExist>false</ConflictsExist></ScheduleRecordingResult></ScheduleRecordingResponse>"""));

        var report = await scheduler.RunAsync([Row()], new BulkScheduleOptions { DryRun = false });

        Assert.Equal(1, report.Failed);
        Assert.Contains("scheduled nothing", report.Outcomes[0].Message);
    }

    /// <summary>
    /// The recording exists at this point. Failing the whole row would push the
    /// operator to re-run the batch and double-book everything that succeeded.
    /// </summary>
    [Fact]
    public async Task A_failed_description_update_still_counts_as_booked()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder")))
            .Respond("ScheduleRecording", Scheduled())
            .Respond("UpdateSessionDescription", """
                <s:Fault xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
                  <faultcode>s:Client</faultcode><faultstring>Denied</faultstring>
                </s:Fault>
                """));

        var report = await scheduler.RunAsync([Row()], new BulkScheduleOptions { DryRun = false });

        Assert.Equal(1, report.Scheduled);
        Assert.Contains("booked", report.Outcomes[0].Message);
        Assert.Contains("presenter could not be set", report.Outcomes[0].Message);
    }

    /// <summary>One unusable row must not abandon the rest of the batch.</summary>
    [Fact]
    public async Task One_failing_row_does_not_stop_the_batch()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder")))
            .Respond("ScheduleRecording", """<s:Fault xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><faultcode>s:Client</faultcode><faultstring>Nope</faultstring></s:Fault>"""));

        var report = await scheduler.RunAsync(
            [Row(line: 1), Row(line: 2)], new BulkScheduleOptions { DryRun = false });

        Assert.Equal(2, report.Outcomes.Count);
        Assert.Equal(2, report.Failed);
    }

    [Fact]
    public async Task Reports_progress_for_every_row()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder"))));

        var seen = new List<ScheduleProgress>();
        var progress = new Progress<ScheduleProgress>(seen.Add);

        var report = await scheduler.RunAsync(
            [Row(line: 1), Row(line: 2), Row(line: 3)],
            new BulkScheduleOptions { DryRun = true },
            progress);

        Assert.Equal(3, report.Outcomes.Count);

        // Progress<T> marshals onto the sync context, so give it a moment.
        for (var i = 0; i < 50 && seen.Count < 3; i++) await Task.Delay(10);

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen[^1].Total);
    }

    [Fact]
    public async Task An_empty_batch_is_an_empty_report()
    {
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240"))));

        var report = await scheduler.RunAsync([], new BulkScheduleOptions { DryRun = true });

        Assert.Empty(report.Outcomes);
        Assert.False(report.AnyWritesAttempted);
    }

    // ---- The audit trail -------------------------------------------------

    /// <summary>
    /// A booking run leaves the same trail an edit run does, and the two rows here
    /// are the two shapes it has to hold: one that reached a session, and one that
    /// was refused before it had any.
    ///
    /// <para><b>The refused row still gets a row.</b> The scheduled one is the easy
    /// half — it has an id to record. The skipped one has only its line of the file
    /// and its title, and a trail that dropped it would be a record of what was
    /// booked that says nothing about what was passed over, which is the half
    /// somebody chasing a missing recording actually needs.</para>
    /// </summary>
    [Fact]
    public async Task A_booking_run_leaves_a_row_for_every_row_of_the_file()
    {
        var log = new RecordingAuditLog();
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder")))
            .Respond("ScheduleRecording", Scheduled())
            .Respond("UpdateSessionDescription", ""), log);

        var report = await scheduler.RunAsync(
            [Row(line: 1), Row(recorder: "GHOST", line: 2)],
            new BulkScheduleOptions { DryRun = false });

        Assert.Equal(1, report.Scheduled);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(2, log.Entries.Count);

        Assert.All(log.Entries, e => Assert.Equal("book", e.Operation));
        Assert.All(log.Entries, e => Assert.False(e.DryRun));

        // One run, not two, however many rows it produced.
        Assert.Single(log.Entries.Select(e => e.RunId).Distinct());

        // The booked row carries the session the scheduling call returned.
        var booked = log.Entries[0];
        Assert.Equal("Scheduled", booked.Outcome);
        Assert.Equal<Guid?>(Guid.Parse(SessionGuid), booked.SessionId);

        // The refused row names no session, and holds its line of the file instead —
        // which is the only identity it has.
        var skipped = log.Entries[1];
        Assert.Equal("Skipped", skipped.Outcome);
        Assert.Null(skipped.SessionId);
        Assert.Equal("line 2", skipped.Detail);
    }

    /// <summary>
    /// A preview is recorded and flagged, exactly as it is for an edit run. The
    /// rehearsal is what the operator approved before pressing the button, so a
    /// trail holding only the bookings cannot say what the run was authorised
    /// against.
    /// </summary>
    [Fact]
    public async Task A_booking_preview_reaches_the_trail_flagged_as_one()
    {
        var log = new RecordingAuditLog();
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder"))), log);

        await scheduler.RunAsync([Row()], new BulkScheduleOptions { DryRun = true });

        var only = Assert.Single(log.Entries);

        Assert.True(only.DryRun);
        Assert.Equal("WouldSchedule", only.Outcome);
    }

    /// <summary>
    /// A trail that cannot be written is reported on the report rather than thrown
    /// or swallowed. Thrown would fail a booking that had already gone through —
    /// and a re-run of a booking is a second recording, not a repeat of the first,
    /// which makes this the worst place in the app for that particular mistake.
    /// </summary>
    [Fact]
    public async Task A_booking_trail_that_cannot_be_written_is_reported_on_the_report()
    {
        var log = new RecordingAuditLog { FailFrom = 0 };
        var (scheduler, _) = Build(h => h
            .Respond("ListRecorders", RecorderListing((RecorderGuid, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((FolderGuid, "TestFolder")))
            .Respond("ScheduleRecording", Scheduled())
            .Respond("UpdateSessionDescription", ""), log);

        var report = await scheduler.RunAsync([Row()], new BulkScheduleOptions { DryRun = false });

        Assert.Equal(1, report.Scheduled);
        Assert.Empty(log.Entries);
        Assert.Contains("1 of 1", Assert.IsType<string>(report.AuditWarning));
    }
}
