using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Import;
using PanoptoScheduler.Core.RateLimiting;
using PanoptoScheduler.Core.Scheduling;
using static PanoptoScheduler.Core.Tests.SoapListings;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// A write cut off after it may have left this machine still reaches the audit
/// trail, as a row whose outcome is unknown.
///
/// <para><b>The operator's stop cannot do this any more</b> — the SOAP client sends
/// a write under no token at all, so a stop lands before the send or not until the
/// next row (<see cref="InFlightWriteTests"/> pins that). <b>The client's own
/// timeout still can</b>, and it arrives as a cancellation: the bulk tools rethrew
/// every cancellation before recording the row, so the one write whose fate nobody
/// knows was the one write the trail never mentioned.</para>
///
/// <para>Driven by a real <see cref="HttpClient.Timeout"/> rather than a
/// hand-made exception, so the test covers the shape the transport actually
/// produces and not the shape someone remembered it producing.</para>
/// </summary>
public class InFlightAuditTests
{
    private static readonly Guid One = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Two = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>
    /// Answers every operation from the script, except one, which it holds until
    /// the request is abandoned — the way a tenant that stopped answering looks
    /// from here.
    /// </summary>
    private sealed class HangOn(string operation, ScriptedSoapHandler script) : DelegatingHandler(script)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues("SOAPAction", out var actions)
                && actions.First().Trim('"').EndsWith("/" + operation, StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static PanoptoSoapClient Soap(string hangOn, Action<ScriptedSoapHandler>? configure = null)
    {
        var script = new ScriptedSoapHandler();
        configure?.Invoke(script);

        var http = new HttpClient(new HangOn(hangOn, script))
        {
            BaseAddress = new Uri("https://rotman.ca.panopto.com"),
            Timeout = TimeSpan.FromMilliseconds(300),
        };

        return new PanoptoSoapClient(http, new RateLimiterRegistry(), new StubAuthenticator());
    }

    [Fact]
    public async Task A_rename_that_times_out_on_the_wire_is_in_the_trail_as_unknown()
    {
        var log = new RecordingAuditLog();
        var soap = Soap("UpdateSessionName");
        var editor = new BulkSessionEditor(
            new SessionManagementClient(soap), new RemoteRecorderClient(soap), log);

        // Still rethrown: the window tells a timeout from a stop by this exception.
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.RenameAsync(
            [(One, "Old Name"), (Two, "Old Other")], n => n.Replace("Old", "New"), dryRun: false));

        Assert.IsType<TimeoutException>(error.InnerException);

        var row = Assert.Single(log.Entries);
        Assert.Equal(One, row.SessionId);
        Assert.Equal("Unknown", row.Outcome);
        Assert.Contains("Outcome unknown (stopped in flight)", row.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A chunk is one call for many sessions, so every session it carried is
    /// unknown — not just the first.
    /// </summary>
    [Fact]
    public async Task A_chunk_that_times_out_puts_every_session_it_carried_in_the_trail()
    {
        var log = new RecordingAuditLog();
        var soap = Soap("DeleteSessions");
        var editor = new BulkSessionEditor(
            new SessionManagementClient(soap), new RemoteRecorderClient(soap), log);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.DeleteAsync(
            [(One, "First"), (Two, "Second")], dryRun: false,
            new DestructiveAction(DestructiveAction.DeleteVerb, [One, Two], Acknowledged: true)));

        Assert.Equal(new Guid?[] { One, Two }, log.Sessions);
        Assert.All(log.Entries, e => Assert.Equal("Unknown", e.Outcome));
    }

    /// <summary>
    /// The booking that matters most: a ScheduleRecording that may have created a
    /// recording nobody was told about. The line of the file is its identity,
    /// because there is no session id to give it.
    /// </summary>
    [Fact]
    public async Task A_booking_that_times_out_on_the_wire_is_in_the_trail_as_unknown()
    {
        const string recorder = "11111111-1111-1111-1111-111111111111";
        const string folder = "33333333-3333-3333-3333-333333333333";

        var log = new RecordingAuditLog();
        var soap = Soap("ScheduleRecording", h => h
            .Respond("ListRecorders", RecorderListing((recorder, "JMHH240")))
            .Respond("GetFoldersList", FolderListing((folder, "TestFolder"))));

        var scheduler = new BulkScheduler(
            new RemoteRecorderClient(soap), new SessionManagementClient(soap), log);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunAsync(
            [new ScheduleImportRow
            {
                Line = 7,
                Title = "MGEC611002_STAFF",
                RecorderName = "JMHH240",
                Start = new DateTime(2026, 11, 3, 10, 0, 0),
                End = new DateTime(2026, 11, 3, 11, 0, 0),
                FolderHint = "TestFolder",
            }],
            new BulkScheduleOptions { DryRun = false }));

        var row = Assert.Single(log.Entries);
        Assert.Equal("Unknown", row.Outcome);
        Assert.Null(row.SessionId);
        Assert.Equal("line 7", row.Detail);
        Assert.Contains("Outcome unknown (stopped in flight)", row.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other side of the line: the operator's own stop lands before the send,
    /// so nothing left the machine and no unknown row is invented for it.
    /// </summary>
    [Fact]
    public async Task An_operators_stop_before_the_send_leaves_no_unknown_row()
    {
        var log = new RecordingAuditLog();
        var soap = Soap("nothing-hangs");
        var editor = new BulkSessionEditor(
            new SessionManagementClient(soap), new RemoteRecorderClient(soap), log);

        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.RenameAsync(
            [(One, "Old Name")], n => n.Replace("Old", "New"), dryRun: false, ct: stop.Token));

        Assert.Empty(log.Entries);
    }

    /// <summary>
    /// The rule itself: a timeout is possibly sent whatever the caller's token
    /// says, and a cancellation on the caller's own cancelled token is not.
    /// </summary>
    [Fact]
    public void Only_the_callers_own_stop_counts_as_not_sent()
    {
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        var timeout = new TaskCanceledException("timed out", new TimeoutException());

        Assert.True(PanoptoSoapClient.WriteMayHaveLeft(timeout, CancellationToken.None));
        Assert.True(PanoptoSoapClient.WriteMayHaveLeft(timeout, stopped.Token));
        Assert.False(PanoptoSoapClient.WriteMayHaveLeft(
            new OperationCanceledException(stopped.Token), stopped.Token));
    }
}
