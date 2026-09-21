using System.Net;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.RateLimiting;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

public class BulkSessionEditorTests
{
    private static readonly Guid One = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Two = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Third = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid Folder = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static (BulkSessionEditor Editor, ScriptedSoapHandler Handler) Build(
        Action<ScriptedSoapHandler>? configure = null,
        IBulkAuditLog? auditLog = null)
    {
        var handler = new ScriptedSoapHandler();
        configure?.Invoke(handler);

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://rotman.ca.panopto.com") };
        var soap = new PanoptoSoapClient(http, new RateLimiterRegistry(), new StubAuthenticator());

        // The room zone is pinned rather than resolved from this machine, so a
        // retime test asserts the room's clock and not the workstation's — the
        // exact confusion RoomClock exists to prevent.
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-room", TimeSpan.FromHours(-5), "Test room", "Test room");

        return (new BulkSessionEditor(
            new SessionManagementClient(soap),
            new RemoteRecorderClient(soap, zone),
            auditLog), handler);
    }

    private static IReadOnlyList<(Guid, string)> Targets() => [(One, "Old Name"), (Two, "Other Name")];

    // ---- Dry run --------------------------------------------------------

    /// <summary>
    /// Deleting is irreversible, so the preview has to be reportable and the
    /// tenant untouched.
    /// </summary>
    [Fact]
    public async Task A_delete_dry_run_deletes_nothing()
    {
        var (editor, handler) = Build();

        var report = await editor.DeleteAsync(Targets(), dryRun: true);

        Assert.Equal(2, report.WouldApply);
        Assert.Equal(0, report.Applied);
        Assert.DoesNotContain("DeleteSessions", handler.Calls);
    }

    [Fact]
    public async Task A_delete_dry_run_says_it_is_permanent()
    {
        var (editor, _) = Build();

        var report = await editor.DeleteAsync([(One, "Old Name")], dryRun: true);

        Assert.Contains("permanently", report.Results[0].Message);
    }

    // ---- Rename ---------------------------------------------------------

    [Fact]
    public async Task Renames_from_a_transform()
    {
        var (editor, handler) = Build(h => h.Respond("UpdateSessionName", ""));

        var report = await editor.RenameAsync(
            [(One, "Old Name"), (Two, "Old Other")],
            name => name.Replace("Old", "New"),
            dryRun: false);

        Assert.Equal(2, report.Applied);
        Assert.Equal(2, handler.Calls.Count(c => c == "UpdateSessionName"));
    }

    /// <summary>
    /// A transform that only matches some names must leave the rest alone rather
    /// than issuing a no-op write, which still burns a request against the
    /// per-endpoint limit.
    /// </summary>
    [Fact]
    public async Task A_transform_that_matches_only_some_names_leaves_the_rest_alone()
    {
        var (editor, handler) = Build(h => h.Respond("UpdateSessionName", ""));

        var report = await editor.RenameAsync(
            [(One, "Old Name"), (Two, "Unrelated")],
            name => name.Replace("Old", "New"),
            dryRun: false);

        Assert.Equal(1, report.Applied);
        Assert.Equal(1, report.Failed);
        Assert.Contains("identical", report.Results[1].Message);
        Assert.Equal(1, handler.Calls.Count(c => c == "UpdateSessionName"));
    }

    [Fact]
    public async Task Skips_a_rename_that_changes_nothing()
    {
        var (editor, handler) = Build();

        var report = await editor.RenameAsync(Targets(), name => name, dryRun: false);

        Assert.Equal(2, report.Failed);
        Assert.Empty(handler.Calls);
        Assert.Contains("identical", report.Results[0].Message);
    }

    /// <summary>
    /// A transform that strips everything would otherwise send an empty name,
    /// which Panopto accepts — leaving sessions with no title at all.
    /// </summary>
    [Fact]
    public async Task Refuses_a_rename_that_would_empty_the_name()
    {
        var (editor, handler) = Build();

        var report = await editor.RenameAsync(Targets(), _ => "   ", dryRun: false);

        Assert.Equal(2, report.Failed);
        Assert.Empty(handler.Calls);
        Assert.Contains("empty", report.Results[0].Message);
    }

    [Fact]
    public async Task One_failed_rename_does_not_stop_the_others()
    {
        var (editor, _) = Build(h => h.Respond("UpdateSessionName", """
            <s:Fault xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <faultcode>s:Client</faultcode><faultstring>Denied</faultstring>
            </s:Fault>
            """));

        var report = await editor.RenameAsync(
            Targets(), name => name + " (2026)", dryRun: false);

        Assert.Equal(2, report.Failed);
        Assert.Equal(2, report.Results.Count);
    }

    // ---- Move -----------------------------------------------------------

    [Fact]
    public async Task Moves_into_a_folder_resolved_by_name()
    {
        var (editor, handler) = Build(h => h
            // A listing reporting its own total, the way the real service does.
            // Without it the page walk has to send a second request to discover
            // that the one page it got was the whole set — and the count below
            // would then be measuring that request rather than the lookup.
            .Respond("GetFoldersList", SoapListings.FolderListing((Folder.ToString("D"), "Archive")))
            .Respond("MoveSessions", ""));

        var report = await editor.MoveAsync(Targets(), "Archive", dryRun: false);

        Assert.Equal(2, report.Applied);
        Assert.Contains("Move to 'Archive'", report.Results[0].Message);

        // One lookup for the whole batch, not one per session.
        Assert.Equal(1, handler.Calls.Count(c => c == "GetFoldersList"));
    }

    [Fact]
    public async Task Reports_every_session_when_the_target_folder_does_not_exist()
    {
        var (editor, handler) = Build(h => h.Respond("GetFoldersList",
            """<GetFoldersListResponse xmlns="http://tempuri.org/"/>"""));

        var report = await editor.MoveAsync(Targets(), "Nowhere", dryRun: false);

        Assert.Equal(2, report.Failed);
        Assert.All(report.Results, r => Assert.Contains("Nowhere", r.Message));
        Assert.DoesNotContain("MoveSessions", handler.Calls);
    }

    /// <summary>
    /// A move has no fallback folder, so an unread listing stops the operation —
    /// but it must say <b>which</b> fault it was. "No folder matching 'X'" sends
    /// the operator to correct a row that is not wrong; the truth is that this app
    /// stopped reading, and the fix is here.
    ///
    /// <para>The difference from booking is the whole point of the pair. Booking
    /// degrades to the recorder's default with the honest note, because a lecture
    /// landing in a slightly wrong folder is recoverable. Filing a term's
    /// recordings somewhere unexpected is not, so a move refuses instead — and
    /// every row is marked, since the folder is resolved once for the batch and
    /// there is nothing partial about the failure.</para>
    /// </summary>
    [Fact]
    public async Task Refuses_a_move_when_the_folder_listing_is_incomplete()
    {
        var (editor, handler) = Build(h =>
        {
            // No total and fresh folders on every page, so only the ceiling ends
            // the walk and the result reports itself incomplete.
            for (var page = 0; page < SoapPaging.MaxPages; page++)
            {
                h.RespondOnce("GetFoldersList", SoapListings.FolderListingWithoutTotal(
                    [.. Enumerable.Range(page * SoapPaging.PageSize, SoapPaging.PageSize).Select(SoapListings.Folder)]));
            }
        });

        var report = await editor.MoveAsync(Targets(), "Nowhere", dryRun: false);

        Assert.Equal(2, report.Failed);
        Assert.All(report.Results, r => Assert.Contains("stopped after", r.Message));

        // Not the "no such folder" wording — that is a claim about the tenant.
        Assert.All(report.Results, r => Assert.DoesNotContain("No folder matching", r.Message));

        // Nothing moved. A half-applied move into an unverified folder is the
        // outcome this refusal exists to prevent.
        Assert.DoesNotContain("MoveSessions", handler.Calls);
    }

    // ---- Retime ---------------------------------------------------------

    /// <summary>
    /// A fixed instant for the past check. The guard compares the room's wall
    /// clock, which is five hours behind UTC here, so a slot at 12:00 room time on
    /// the pinned date is comfortably ahead of "now" and the test is about the
    /// retime rather than about when it ran.
    /// </summary>
    private static readonly DateTime NowUtc = new(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);

    private static SessionRetime Slot(Guid id, string name, DateTime start, double minutes)
        => new(id, name, start, start.AddMinutes(minutes));

    [Fact]
    public async Task Retimes_a_recording_and_says_where_it_went()
    {
        var (editor, handler) = Build(h => h.Respond("UpdateRecordingTime", ""));

        var report = await editor.RetimeAsync(
            [Slot(One, "Old Name", new DateTime(2026, 5, 4, 12, 0, 0), 60)],
            dryRun: false,
            nowUtc: NowUtc);

        Assert.Equal(1, report.Applied);
        Assert.Contains("Retime to Mon 4 May 12:00", report.Results[0].Message);
        Assert.Contains("UpdateRecordingTime", handler.Calls);
    }

    /// <summary>
    /// The bug the return value was being thrown away for.
    /// <c>UpdateRecordingTime</c> answers <c>ConflictsExist</c> — success carrying
    /// a flag — and the old delegate returned <see cref="Task"/>, so the flag was
    /// dropped and a clashing retime read as a clean success. Someone would then
    /// find out from the room.
    /// </summary>
    [Fact]
    public async Task Reports_a_clash_on_a_retime_rather_than_calling_it_clean()
    {
        var (editor, _) = Build(h => h.Respond("UpdateRecordingTime", """
            <UpdateRecordingTimeResponse xmlns="http://tempuri.org/"><UpdateRecordingTimeResult>
              <ConflictsExist>true</ConflictsExist>
              <ConflictingSessions xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap">
                <ScheduledRecordingInfo>
                  <SessionName>STAT500 Lecture</SessionName>
                  <StartTime>2026-05-04T12:30:00</StartTime>
                </ScheduledRecordingInfo>
              </ConflictingSessions>
            </UpdateRecordingTimeResult></UpdateRecordingTimeResponse>
            """));

        var report = await editor.RetimeAsync(
            [Slot(One, "Old Name", new DateTime(2026, 5, 4, 12, 0, 0), 60)],
            dryRun: false,
            nowUtc: NowUtc);

        // Still applied — the move happened — but the row must not read as clean.
        Assert.Equal(1, report.Applied);
        Assert.Contains("something else is booked", report.Results[0].Message);
        Assert.Contains("STAT500 Lecture", report.Results[0].Message);
    }

    /// <summary>
    /// The room's past, not this machine's. The zone is pinned to UTC−5 in
    /// <c>Build</c>, so a slot that has passed in the room is refused whatever the
    /// workstation's clock says — the failure the old <see cref="DateTime.Now"/>
    /// guard caused in both directions.
    /// </summary>
    [Fact]
    public async Task Refuses_a_retime_into_the_rooms_past()
    {
        var (editor, handler) = Build();

        var report = await editor.RetimeAsync(
            [Slot(One, "Old Name", new DateTime(2026, 5, 1, 9, 0, 0), 60)],
            dryRun: false,
            nowUtc: NowUtc);

        Assert.Equal(1, report.Failed);
        Assert.Contains("in the past", report.Results[0].Message);
        Assert.DoesNotContain("UpdateRecordingTime", handler.Calls);
    }

    [Fact]
    public async Task Refuses_a_retime_whose_end_is_not_after_its_start()
    {
        var (editor, handler) = Build();

        var report = await editor.RetimeAsync(
            [new SessionRetime(One, "Old Name", new DateTime(2026, 5, 4, 12, 0, 0), new DateTime(2026, 5, 4, 12, 0, 0))],
            dryRun: false,
            nowUtc: NowUtc);

        Assert.Equal(1, report.Failed);
        Assert.Contains("not after the start", report.Results[0].Message);
        Assert.DoesNotContain("UpdateRecordingTime", handler.Calls);
    }

    [Fact]
    public async Task A_retime_dry_run_writes_nothing()
    {
        var (editor, handler) = Build();

        var report = await editor.RetimeAsync(
            [Slot(One, "Old Name", new DateTime(2026, 5, 4, 12, 0, 0), 60)],
            dryRun: true,
            nowUtc: NowUtc);

        Assert.Equal(1, report.WouldApply);
        Assert.DoesNotContain("UpdateRecordingTime", handler.Calls);
    }

    // ---- Bulk retime, over a calendar selection --------------------------

    /// <summary>One ticked recording, as the calendar hands it over.</summary>
    private static SessionTarget Selected(Guid id, string name, DateTime start, double minutes)
        => new(id, name, start, start.AddMinutes(minutes), null);

    /// <summary>
    /// One call per recording, and that is the API's line rather than a preference:
    /// <c>UpdateRecordingTime</c> takes a single session id and has no multi-session
    /// form, unlike move and delete. The chunking they got cannot be applied here,
    /// which is exactly the kind of thing that gets "fixed" later by someone who
    /// assumes it was an oversight — so the count is asserted.
    /// </summary>
    [Fact]
    public async Task A_bulk_shift_sends_one_call_per_recording()
    {
        var (editor, handler) = Build(h => h.Respond("UpdateRecordingTime", ""));

        var report = await editor.RetimeByAsync(
            [
                Selected(One, "A", new DateTime(2026, 5, 4, 12, 0, 0), 60),
                Selected(Two, "B", new DateTime(2026, 5, 5, 12, 0, 0), 60),
            ],
            TimeSpan.FromMinutes(60),
            dryRun: false,
            nowUtc: NowUtc);

        Assert.Equal(2, report.Applied);
        Assert.Equal(2, handler.Calls.Count(c => c == "UpdateRecordingTime"));
    }

    /// <summary>
    /// A row the plan could not place is reported where it sits in the operator's
    /// selection, among the rows that moved — not swept into a block at the top of
    /// the grid that has to be matched up by name.
    /// </summary>
    [Fact]
    public async Task A_row_the_plan_could_not_place_is_reported_among_the_rows_that_moved()
    {
        var (editor, handler) = Build();

        var report = await editor.RetimeByAsync(
            [
                Selected(One, "A", new DateTime(2026, 5, 4, 12, 0, 0), 60),
                new SessionTarget(Two, "Mystery", null, null, null),
                Selected(Third, "C", new DateTime(2026, 5, 6, 12, 0, 0), 60),
            ],
            TimeSpan.FromMinutes(60),
            dryRun: true,
            nowUtc: NowUtc);

        Assert.Equal(3, report.Results.Count);

        Assert.Equal(SessionEditOutcome.WouldApply, report.Results[0].Outcome);
        Assert.Equal(SessionEditOutcome.Failed, report.Results[1].Outcome);
        Assert.Contains("No start time", report.Results[1].Message);
        Assert.Equal(Two, report.Results[1].SessionId);
        Assert.Equal(SessionEditOutcome.WouldApply, report.Results[2].Outcome);

        // A preview writes nothing, including for the rows it could have placed.
        Assert.DoesNotContain("UpdateRecordingTime", handler.Calls);
    }

    /// <summary>
    /// The room's past is still the room's past on the bulk path. The plan decides
    /// where a recording would go and is deliberately ignorant of the clock; the
    /// guard that holds the panel's single drag is the one that holds this, because
    /// both end in the same per-row method.
    /// </summary>
    [Fact]
    public async Task A_bulk_shift_into_the_rooms_past_is_refused()
    {
        var (editor, handler) = Build();

        // 08:00 room time on the pinned date, and the room has reached 15:00 — so a
        // half-hour shift leaves it well behind the room's own clock even though the
        // workstation's clock is a different question entirely.
        var report = await editor.RetimeByAsync(
            [Selected(One, "A", new DateTime(2026, 5, 1, 8, 0, 0), 60)],
            TimeSpan.FromMinutes(30),
            dryRun: false,
            nowUtc: NowUtc);

        Assert.Equal(1, report.Failed);
        Assert.Contains("in the past", report.Results[0].Message);
        Assert.DoesNotContain("UpdateRecordingTime", handler.Calls);
    }

    // ---- Description ----------------------------------------------------

    [Fact]
    public async Task Sets_a_description_per_session()
    {
        var (editor, handler) = Build(h => h.Respond("UpdateSessionDescription", ""));

        var report = await editor.SetDescriptionAsync(
            [(One, "A", ""), (Two, "B", "")],
            _ => "Rosenbaum",
            dryRun: false);

        Assert.Equal(2, report.Applied);
        Assert.Contains("Set the description to 'Rosenbaum'", report.Results[0].Message);
        Assert.Equal(2, handler.Calls.Count(c => c == "UpdateSessionDescription"));
    }

    /// <summary>
    /// Clearing is a real thing to want, so an empty result is applied rather than
    /// refused — unlike a rename, where an empty name would make two recordings
    /// indistinguishable.
    /// </summary>
    [Fact]
    public async Task Clears_a_description()
    {
        var (editor, handler) = Build(h => h.Respond("UpdateSessionDescription", ""));

        var report = await editor.SetDescriptionAsync(
            [(One, "A", "Rosenbaum")], _ => string.Empty, dryRun: false);

        Assert.Equal(1, report.Applied);
        Assert.Contains("Clear the description", report.Results[0].Message);
    }

    [Fact]
    public async Task Skips_a_description_that_is_already_what_was_asked_for()
    {
        var (editor, handler) = Build();

        var report = await editor.SetDescriptionAsync(
            [(One, "A", "Rosenbaum")], _ => "Rosenbaum", dryRun: false);

        Assert.Equal(1, report.Failed);
        Assert.Contains("unchanged", report.Results[0].Message);
        Assert.DoesNotContain("UpdateSessionDescription", handler.Calls);
    }

    /// <summary>
    /// The preview names the text it would overwrite.
    ///
    /// <para>This is the reason the calendar's selection carries the current
    /// description at all. The field holds the previous presenter — the legacy tool
    /// writes it there because the scheduling call has no presenter — so a preview
    /// naming only the new text would show a clean-looking operation over text
    /// nobody had read.</para>
    /// </summary>
    [Fact]
    public async Task A_description_preview_names_the_text_it_replaces()
    {
        var (editor, handler) = Build();

        var report = await editor.SetDescriptionAsync(
            [new SessionTarget(One, "A", null, null, "Goldman")],
            _ => "Rosenbaum",
            dryRun: true);

        Assert.Contains("replacing 'Goldman'", report.Results[0].Message);
        Assert.DoesNotContain("UpdateSessionDescription", handler.Calls);
    }

    /// <summary>
    /// A selection row whose description is absent and one whose description is the
    /// empty string are the same row, so "write nothing" is refused for both instead
    /// of being sent as a request that spends a slot on the limiter to change
    /// nothing. Pinned because the mapping is a decision, not an accident: some
    /// other placeholder standing in for null would make an untouched session look
    /// like a change.
    /// </summary>
    [Fact]
    public async Task A_null_description_and_an_empty_one_are_the_same_row()
    {
        var (editor, handler) = Build();

        var report = await editor.SetDescriptionAsync(
            [
                new SessionTarget(One, "A", null, null, null),
                new SessionTarget(Two, "B", null, null, ""),
            ],
            _ => "",
            dryRun: false);

        Assert.Equal(2, report.Failed);
        Assert.Empty(handler.Calls);
    }

    // ---- Progress -------------------------------------------------------

    /// <summary>
    /// The edit path had no progress at all, so a term's worth of renames was an
    /// opaque multi-minute wait. The counts are per row and include the rows that
    /// failed, because a bar that only moves on success stops moving at exactly the
    /// moment someone is watching it.
    /// </summary>
    [Fact]
    public async Task Reports_progress_for_every_row_including_the_ones_that_failed()
    {
        var (editor, _) = Build(h => h.Respond("UpdateSessionName", ""));

        var seen = new ProgressCollector();

        var report = await editor.RenameAsync(
            [(One, "A"), (Two, "B"), (Guid.NewGuid(), "C")],
            n => n == "B" ? "B" : n + " (renamed)",
            dryRun: false,
            progress: seen);

        // Two renamed, one refused for being identical.
        Assert.Equal(2, report.Applied);
        Assert.Equal(1, report.Failed);
        Assert.Equal([1, 2, 3], seen.Values);
    }

    /// <summary>
    /// Collects progress inline. <see cref="Progress{T}"/> posts to a
    /// synchronisation context, or to the thread pool when there is none, so its
    /// callbacks can land after the awaited call returns and the assertion above
    /// would become a race that passes most of the time.
    /// </summary>
    private sealed class ProgressCollector : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
    }

    // ---- Broadcast ------------------------------------------------------

    [Fact]
    public async Task Turns_webcasting_on()
    {
        var (editor, handler) = Build(h => h.Respond("UpdateSessionIsBroadcast", ""));

        var report = await editor.SetBroadcastAsync([(One, "A")], isBroadcast: true, dryRun: false);

        Assert.Equal(1, report.Applied);
        Assert.Contains("on", report.Results[0].Message);
    }

    [Fact]
    public async Task A_dry_run_covers_broadcast_changes_too()
    {
        var (editor, handler) = Build();

        var report = await editor.SetBroadcastAsync(Targets(), isBroadcast: false, dryRun: true);

        Assert.Equal(2, report.WouldApply);
        Assert.Empty(handler.Calls);
    }

    /// <summary>
    /// An empty selection is an empty report — but a real delete still has to be
    /// authorised, even to destroy nothing, so the rule has no special case to
    /// get wrong.
    /// </summary>
    [Fact]
    public async Task An_empty_selection_is_an_empty_report()
    {
        var (editor, handler) = Build();

        var report = await editor.DeleteAsync([], dryRun: false,
            permit: new DestructiveAction(DestructiveAction.DeleteVerb, [], Acknowledged: true));

        Assert.Empty(report.Results);
        Assert.Empty(handler.Calls);
    }

    // ---- The delete gate -------------------------------------------------

    /// <summary>
    /// The whole reason the gate exists: a real delete with nothing behind it
    /// must not reach the tenant.
    /// </summary>
    [Fact]
    public async Task A_real_delete_with_no_permit_is_refused_and_sends_nothing()
    {
        var (editor, handler) = Build(h => h.Respond("DeleteSessions", ""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => editor.DeleteAsync(Targets(), dryRun: false));

        Assert.Contains("not confirmed", error.Message);
        Assert.Empty(handler.Calls);
    }

    /// <summary>
    /// A permit for the set that was previewed lapses when the set changes, which
    /// is what makes the selection safe to edit between previewing and applying.
    /// </summary>
    [Fact]
    public async Task A_permit_for_a_different_set_does_not_authorise_this_one()
    {
        var (editor, handler) = Build(h => h.Respond("DeleteSessions", ""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => editor.DeleteAsync(Targets(), dryRun: false,
                permit: new DestructiveAction(DestructiveAction.DeleteVerb, [One], Acknowledged: true)));

        Assert.Contains("confirmed for 1", error.Message);
        Assert.Contains("2 are selected", error.Message);
        Assert.Empty(handler.Calls);
    }

    /// <summary>
    /// The gap the ids were added to close: the same number of sessions, one of
    /// them swapped for another. A permit that compared counts passed this, and
    /// the delete then ran over a session nobody had confirmed.
    /// </summary>
    [Fact]
    public async Task A_permit_for_a_swapped_session_of_the_same_count_is_refused()
    {
        var (editor, handler) = Build(h => h.Respond("DeleteSessions", ""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => editor.DeleteAsync(Targets(), dryRun: false,
                permit: new DestructiveAction(
                    DestructiveAction.DeleteVerb, [One, Folder], Acknowledged: true)));

        Assert.Contains("1 session(s) were added and 1 removed", error.Message);
        Assert.Equal(0, handler.Calls.Count(c => c == "DeleteSessions"));
    }

    /// <summary>A set with nobody behind it is not consent.</summary>
    [Fact]
    public async Task A_set_that_was_never_acknowledged_does_not_authorise_a_delete()
    {
        var (editor, handler) = Build(h => h.Respond("DeleteSessions", ""));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => editor.DeleteAsync(Targets(), dryRun: false,
                permit: new DestructiveAction(
                    DestructiveAction.DeleteVerb, [One, Two], Acknowledged: false)));

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task A_permitted_delete_deletes()
    {
        var (editor, handler) = Build(h => h.Respond("DeleteSessions", ""));

        var report = await editor.DeleteAsync(Targets(), dryRun: false,
            permit: new DestructiveAction(DestructiveAction.DeleteVerb, [One, Two], Acknowledged: true));

        Assert.Equal(2, report.Applied);

        // One call for both, not one each: they are inside the same chunk. The
        // count is asserted rather than left out because it is the difference
        // between this delete and the per-session shape it used to have, and a
        // report that says "2 applied" looks identical either way.
        Assert.Equal(1, handler.Calls.Count(c => c == "DeleteSessions"));
    }

    /// <summary>
    /// Asking what would happen is what comes before the confirmation, so the
    /// preview must stay reachable without one — otherwise the operator cannot
    /// find out what they are being asked to confirm.
    /// </summary>
    [Fact]
    public async Task A_preview_needs_no_permit()
    {
        var (editor, handler) = Build();

        var report = await editor.DeleteAsync(Targets(), dryRun: true);

        Assert.Equal(2, report.WouldApply);
        Assert.Empty(handler.Calls);
    }

    // ---- Chunking ---------------------------------------------------------

    /// <summary>
    /// A run of <paramref name="count"/> sessions, numbered so an id can be
    /// placed in the batch it belongs to.
    /// </summary>
    private static IReadOnlyList<(Guid, string)> Many(int count) =>
        [.. Enumerable.Range(1, count).Select(i => (Numbered(i), $"Session {i}"))];

    private static Guid Numbered(int i) =>
        Guid.Parse($"00000000-0000-0000-0000-{i:000000000000}");

    /// <summary>
    /// The whole point of chunking: a move of more than a hundred sessions is
    /// bounded calls, not one per session, against a limiter metered per
    /// operation. The count is the assertion — an implementation that quietly went
    /// back to one call per session would still produce a correct-looking report.
    /// </summary>
    [Fact]
    public async Task A_move_is_sent_as_one_call_per_chunk()
    {
        var (editor, handler) = Build(h => h
            .Respond("GetFoldersList", SoapListings.FolderListing((Folder.ToString("D"), "Archive")))
            .Respond("MoveSessions", ""));

        var report = await editor.MoveAsync(Many(250), "Archive", dryRun: false);

        Assert.Equal(250, report.Applied);
        Assert.Equal(3, handler.Calls.Count(c => c == "MoveSessions"));
    }

    [Fact]
    public async Task A_delete_is_sent_as_one_call_per_chunk()
    {
        var targets = Many(150);

        var (editor, handler) = Build(h => h.Respond("DeleteSessions", ""));

        var report = await editor.DeleteAsync(targets, dryRun: false,
            permit: new DestructiveAction(
                DestructiveAction.DeleteVerb, [.. targets.Select(t => t.Item1)], Acknowledged: true));

        Assert.Equal(150, report.Applied);
        Assert.Equal(2, handler.Calls.Count(c => c == "DeleteSessions"));
    }

    /// <summary>
    /// A bounded call count is only worth having if the batch carries the sessions
    /// it is supposed to. Asserted on the boundary — the hundredth session is in
    /// the first call and the hundred-and-first is not — because off-by-one at a
    /// chunk edge is the way this goes wrong, and it goes wrong silently: the
    /// sessions past the edge are simply never moved.
    /// </summary>
    [Fact]
    public async Task A_chunk_carries_every_id_up_to_its_edge_and_no_further()
    {
        var (editor, handler) = Build(h => h
            .Respond("GetFoldersList", SoapListings.FolderListing((Folder.ToString("D"), "Archive")))
            .Respond("MoveSessions", ""));

        await editor.MoveAsync(Many(150), "Archive", dryRun: false);

        var moves = handler.Bodies
            .Where((_, i) => handler.Calls[i] == "MoveSessions")
            .ToList();

        Assert.Equal(2, moves.Count);

        Assert.Contains(Numbered(1).ToString("D"), moves[0]);
        Assert.Contains(Numbered(100).ToString("D"), moves[0]);
        Assert.DoesNotContain(Numbered(101).ToString("D"), moves[0]);

        Assert.Contains(Numbered(101).ToString("D"), moves[1]);
        Assert.Contains(Numbered(150).ToString("D"), moves[1]);
    }

    /// <summary>
    /// A chunk that faults is not a partial result. The app did not see which
    /// sessions went through, so marking them all failed would be a claim it cannot
    /// support — and every row has to say that re-running is safe, which is only
    /// true because a move is idempotent. The two must be written together: if this
    /// operation ever stopped being idempotent, that sentence would become a lie
    /// that costs the operator a duplicated action.
    /// </summary>
    [Fact]
    public async Task A_faulted_chunk_says_the_outcome_is_unknown_and_re_running_is_safe()
    {
        var (editor, _) = Build(h => h
            .Respond("GetFoldersList", SoapListings.FolderListing((Folder.ToString("D"), "Archive")))
            .Respond("MoveSessions", """
                <s:Fault xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
                  <faultcode>s:Client</faultcode><faultstring>Server busy</faultstring>
                </s:Fault>
                """));

        var report = await editor.MoveAsync(Many(150), "Archive", dryRun: false);

        Assert.Equal(150, report.Failed);
        Assert.All(report.Results, r => Assert.Contains("cannot tell which of them went through", r.Message));
        Assert.All(report.Results, r => Assert.Contains("Running it again is safe", r.Message));
    }

    /// <summary>
    /// The permit is checked against every target before the first chunk is sent,
    /// so a refused delete sends nothing at all — not one chunk before noticing.
    /// </summary>
    [Fact]
    public async Task A_refused_delete_sends_no_chunk_at_all()
    {
        var (editor, handler) = Build(h => h.Respond("DeleteSessions", ""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.DeleteAsync(
            Many(150), dryRun: false,
            permit: new DestructiveAction(
                DestructiveAction.DeleteVerb, [Numbered(1)], Acknowledged: true)));

        Assert.DoesNotContain("DeleteSessions", handler.Calls);
    }

    // ---- The audit trail -------------------------------------------------

    /// <summary>
    /// One press of one button is one run: every row it produced carries the same
    /// id. Without that, "what did I change on Tuesday" is a question about
    /// timestamps, which is the thing a run id exists to replace.
    /// </summary>
    [Fact]
    public async Task Every_row_of_one_operation_carries_the_same_run_id()
    {
        var log = new RecordingAuditLog();
        var (editor, _) = Build(h => h.Respond("UpdateSessionName", ""), log);

        await editor.RenameAsync(Targets(), _ => "New Name", dryRun: false);

        Assert.Equal(2, log.Entries.Count);

        var run = Assert.Single(log.Entries.Select(e => e.RunId).Distinct());

        Assert.NotEqual(Guid.Empty, run);
        Assert.All(log.Entries, e => Assert.Equal("rename", e.Operation));
        Assert.All(log.Entries, e => Assert.Equal("Applied", e.Outcome));
        Assert.All(log.Entries, e => Assert.False(e.DryRun));
        Assert.Equal(new Guid?[] { One, Two }, log.Sessions);
    }

    /// <summary>
    /// Two presses are two runs, even in the same second on the same editor — which
    /// is the distinction a timestamp cannot make.
    /// </summary>
    [Fact]
    public async Task Two_operations_are_two_runs()
    {
        var log = new RecordingAuditLog();
        var (editor, _) = Build(h => h.Respond("UpdateSessionName", ""), log);

        await editor.RenameAsync(Targets(), _ => "First", dryRun: false);
        await editor.RenameAsync(Targets(), _ => "Second", dryRun: false);

        Assert.Equal(2, log.Entries.Select(e => e.RunId).Distinct().Count());
    }

    /// <summary>
    /// <b>The fact the cursor exists for.</b> A chunked move produces its results a
    /// hundred at a time in one <c>AddRange</c>, so a trail driven from "the row
    /// that just finished" would record one row of the hundred — and the chunk is
    /// the batch that most needs a trail, because it is the one that writes the most
    /// in the fewest calls. Asserted at 250 so there are three chunks and two of
    /// them are not the one the operation happens to report last.
    /// </summary>
    [Fact]
    public async Task Every_row_of_a_chunked_move_reaches_the_trail()
    {
        var log = new RecordingAuditLog();
        var (editor, _) = Build(h => h
            .Respond("GetFoldersList", SoapListings.FolderListing((Folder.ToString("D"), "Archive")))
            .Respond("MoveSessions", ""), log);

        var report = await editor.MoveAsync(Many(250), "Archive", dryRun: false);

        Assert.Equal(250, report.Applied);
        Assert.Equal(250, log.Entries.Count);

        // Every session, once — not one row recorded three times.
        var recorded = log.Sessions.ToHashSet();

        Assert.Equal(250, recorded.Count);
        Assert.Contains<Guid?>(Numbered(1), recorded);
        Assert.Contains<Guid?>(Numbered(100), recorded);
        Assert.Contains<Guid?>(Numbered(101), recorded);
        Assert.Contains<Guid?>(Numbered(250), recorded);
    }

    /// <summary>
    /// A preview belongs in the trail, flagged as one. It is the thing the operator
    /// was shown before pressing the button, so a trail holding only the writes
    /// cannot answer what a run was authorised against.
    /// </summary>
    [Fact]
    public async Task A_preview_reaches_the_trail_flagged_as_one()
    {
        var log = new RecordingAuditLog();
        var (editor, _) = Build(auditLog: log);

        await editor.DeleteAsync(Targets(), dryRun: true);

        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.True(e.DryRun));
        Assert.All(log.Entries, e => Assert.Equal("WouldApply", e.Outcome));
        Assert.All(log.Entries, e => Assert.Equal("delete", e.Operation));
    }

    /// <summary>
    /// A refused row is a row: the operation did not happen, and the trail has to
    /// say so rather than holding only the successful writes — a file of nothing but
    /// successes cannot be used to work out what was left undone.
    /// </summary>
    [Fact]
    public async Task A_refused_row_reaches_the_trail_as_a_failure()
    {
        var log = new RecordingAuditLog();
        var (editor, _) = Build(h => h.Respond("UpdateSessionName", ""), log);

        await editor.RenameAsync(
            [(One, "Old Name")], _ => "Old Name", dryRun: false);

        var only = Assert.Single(log.Entries);

        Assert.Equal("Failed", only.Outcome);
        Assert.Equal(One, only.SessionId);
        Assert.Equal("Old Name", only.SessionName);
    }

    /// <summary>
    /// A move that returns before its loop — because the folder could not be
    /// resolved — still gets its rows into the trail.
    ///
    /// <para>This is the only path where the report's own construction does the
    /// recording rather than the per-row report, so without a fact here that half of
    /// the funnel is untested. It matters because the operation <i>was</i> attempted:
    /// a trail that held only moves which got as far as writing would answer "what
    /// did this account change" while being silent on what it tried and failed
    /// to.</para>
    /// </summary>
    [Fact]
    public async Task A_move_refused_for_its_folder_still_reaches_the_trail()
    {
        var log = new RecordingAuditLog();
        var (editor, _) = Build(h => h.Respond("GetFoldersList",
            """<GetFoldersListResponse xmlns="http://tempuri.org/"/>"""), log);

        var report = await editor.MoveAsync(Targets(), "Nowhere", dryRun: false);

        Assert.Equal(2, report.Failed);
        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, e => Assert.Equal("move", e.Operation));
        Assert.All(log.Entries, e => Assert.Equal("Failed", e.Outcome));

        // The rows landed, so there is nothing to warn about.
        Assert.Null(report.AuditWarning);
    }

    /// <summary>
    /// A trail that cannot be written is reported on the report, once, with the
    /// count — and the run is otherwise untouched. Both halves matter: an exception
    /// here would fail a rename that had already gone through, and silence would let
    /// the operator believe two hundred edits were recorded when none were.
    /// </summary>
    [Fact]
    public async Task A_trail_that_cannot_be_written_is_reported_once_on_the_report()
    {
        var log = new RecordingAuditLog { FailFrom = 0 };
        var (editor, _) = Build(h => h.Respond("UpdateSessionName", ""), log);

        var report = await editor.RenameAsync(Targets(), _ => "New Name", dryRun: false);

        Assert.Equal(2, report.Applied);
        Assert.Empty(log.Entries);

        var warning = Assert.IsType<string>(report.AuditWarning);

        // The count and the reassurance, both in the one sentence.
        Assert.Contains("2 of 2", warning);
        Assert.Contains("The run itself was unaffected.", warning);
    }

    /// <summary>
    /// A partial failure counts what is missing, not what was attempted — the number
    /// the operator needs is how much of the record they have lost.
    /// </summary>
    [Fact]
    public async Task A_partly_written_trail_reports_how_much_is_missing()
    {
        var log = new RecordingAuditLog { FailFrom = 1 };
        var (editor, _) = Build(h => h.Respond("UpdateSessionName", ""), log);

        var report = await editor.RenameAsync(Targets(), _ => "New Name", dryRun: false);

        Assert.Single(log.Entries);
        Assert.Contains("1 of 2", Assert.IsType<string>(report.AuditWarning));
    }

    /// <summary>
    /// The ordinary case is silent. An editor built with no log — which is every
    /// caller that has not thought about a trail — must not produce a warning about
    /// a trail it was never given.
    /// </summary>
    [Fact]
    public async Task A_run_with_no_trail_reports_no_warning()
    {
        var (editor, _) = Build(h => h.Respond("UpdateSessionName", ""));

        var report = await editor.RenameAsync(Targets(), _ => "New Name", dryRun: false);

        Assert.Equal(2, report.Applied);
        Assert.Null(report.AuditWarning);
    }
}
