using PanoptoScheduler.Core.Clients;

namespace PanoptoScheduler.Core.Scheduling;

public enum SessionEditOutcome
{
    Applied,
    WouldApply,
    Failed,
}

public sealed record SessionEditResult
{
    public required Guid SessionId { get; init; }
    public required SessionEditOutcome Outcome { get; init; }
    public required string Message { get; init; }

    public string? SessionName { get; init; }
}

public sealed record BulkEditReport(IReadOnlyList<SessionEditResult> Results)
{
    public int Applied => Results.Count(r => r.Outcome == SessionEditOutcome.Applied);
    public int WouldApply => Results.Count(r => r.Outcome == SessionEditOutcome.WouldApply);
    public int Failed => Results.Count(r => r.Outcome == SessionEditOutcome.Failed);

    /// <summary>
    /// Set when the audit trail is missing rows the run actually performed.
    ///
    /// <para>Carried on the report rather than logged, because it is a statement
    /// about what the operator is looking at: the results below say two hundred
    /// edits went through, and this says there is no record of some of them. A
    /// warning nobody sees is the same as no trail at all, and the person who just
    /// pressed the button is the only one still in a position to do something about
    /// it. Null when the trail is complete — or when there was no trail to write,
    /// which is not a failure.</para>
    /// </summary>
    public string? AuditWarning { get; init; }
}

/// <summary>One recording's new slot, as the room's wall clock.</summary>
/// <param name="CurrentName">For the report. The name is not part of the write.</param>
public sealed record SessionRetime(
    Guid Id,
    string CurrentName,
    DateTime Start,
    DateTime End);

/// <summary>
/// One ticked recording, as the calendar hands it to the bulk tools.
///
/// <para><b>Wider than the id-and-name pair the older operations take, and
/// deliberately the only shape the calendar produces.</b> A retime needs the
/// times it is shifting from and a description edit needs the text it is about to
/// overwrite, so a selection carrying only an id would leave both of them
/// guessing. The operations that genuinely need nothing but an identity still map
/// down to the pair at their call site, which keeps the editor's older contract
/// and its tests unchanged.</para>
/// </summary>
/// <param name="Start">
/// The room's wall clock — see <see cref="Models.PanoptoSession.EffectiveStart"/>.
/// Null when the listing reported no time, which is a real state rather than a
/// missing value: a retime planned from a date this app invented would move a
/// recording to a day nobody chose, so it is reported instead.
/// </param>
/// <param name="End">The room's wall clock, derived as the block is drawn.</param>
/// <param name="Description">
/// The current description, so a write can show what it replaces. This is the
/// field the legacy tool writes the presenter into; see
/// <see cref="Models.PanoptoSession.Description"/> for why it is called that when
/// the payload calls it <c>Abstract</c>.
/// </param>
public sealed record SessionTarget(
    Guid Id,
    string CurrentName,
    DateTime? Start,
    DateTime? End,
    string? Description);

/// <summary>
/// Bulk rename, move, retime, delete and broadcast changes.
///
/// <para>Everything here is destructive or hard to spot after the fact, so every
/// operation takes a dry run and reports per session. Delete in particular is
/// irreversible: Panopto has no undo, and a mis-selected filter that removes a
/// term's recordings is not recoverable from this side.</para>
///
/// <para><b>Holds the recorder client as well as the session one</b>, which it
/// did not used to. Retiming lives on <c>IRemoteRecorder</c>, so a second copy of
/// it would have had to live beside the drag-to-reschedule path — and two copies
/// of a guarded write is one copy that forgets a guard. The zone the rooms keep
/// time in comes along with the client, so the wall-clock conversion is not
/// something a caller here has to remember.</para>
///
/// <para><b>What is <i>not</i> here:</b> any preview. That discipline belongs to
/// the bulk UI, which is the only place a write cannot be reviewed before it
/// happens. A single write from the details panel is made while the operator is
/// looking at the thing being changed, so it is applied directly. The line,
/// stated once: bulk writes are previewed, single writes are not, nothing
/// irreversible happens without a permit.</para>
///
/// <para><b>Every row reaches the audit trail as it completes</b>, when the caller
/// has supplied one — see <see cref="IBulkAuditLog"/>. It is wired into the funnel
/// the rows already pass through rather than into each operation, because that
/// funnel is the single place that sees a row the moment it finishes, and a trail
/// written once at the end records nothing in exactly the case it exists for.</para>
/// </summary>
public sealed class BulkSessionEditor(
    SessionManagementClient sessions,
    RemoteRecorderClient recorders,
    IBulkAuditLog? auditLog = null)
{
    /// <summary>
    /// Where this editor's rows go as they complete.
    ///
    /// <para>Defaults to a sink that records nothing rather than to the app's real
    /// file, so a caller that has not considered a trail is not quietly given one —
    /// the wiring stays a decision each caller makes. The app passes the
    /// file-backed log; a test passes nothing and gets no file.</para>
    /// </summary>
    private readonly IBulkAuditLog _audit = auditLog ?? NullBulkAuditLog.Instance;

    /// <summary>
    /// Reports every row's completion, so a long run is legible while it runs
    /// rather than only afterwards, and records those same rows in the trail.
    ///
    /// <para>Counts rather than a fraction: the caller knows its own total, and a
    /// progress report that carries a total it did not compute is a second place
    /// for the two to disagree.</para>
    ///
    /// <para>Progress and the trail are driven from one call on purpose. A row that
    /// the operator can see on screen but that is missing from the file is the one
    /// inconsistency that would make the trail untrustworthy, and the way to
    /// prevent it is for there to be no second call that could be forgotten.</para>
    /// </summary>
    private static void Report(
        IProgress<int>? progress,
        IReadOnlyList<SessionEditResult> results,
        Audit audit)
    {
        audit.Record(results);
        progress?.Report(results.Count);
    }

    /// <summary>
    /// The report for a finished operation, with whatever the trail has to say
    /// about it.
    ///
    /// <para>Records the rows too, so an operation that returned early — a move
    /// whose folder could not be resolved — still has its rows in the trail. The
    /// cursor makes this free for the operations that already reported every row:
    /// there is nothing left to write by the time they get here.</para>
    /// </summary>
    private static BulkEditReport Finish(IReadOnlyList<SessionEditResult> results, Audit audit)
    {
        audit.Record(results);
        return new BulkEditReport(results) { AuditWarning = audit.Warning };
    }

    /// <summary>
    /// One operation's rows on their way to the trail.
    ///
    /// <para><b>A cursor, not a row.</b> The chunked operations add a hundred
    /// results in one <c>AddRange</c>, so a signature taking the row just finished
    /// would leave those hundred unrecorded — and the hundred-row chunk is the batch
    /// that most needs a trail. This records every row the operation has added since
    /// the last call, whatever size that batch was, and a repeated call for the same
    /// list writes nothing a second time.</para>
    ///
    /// <para>All it adds to <see cref="BulkAuditRun"/> is that cursor, which is why
    /// it is nested here: the run identity and the shortfall sentence are shared with
    /// the booking path, and only the stepping through a growing result list is
    /// particular to this type.</para>
    /// </summary>
    /// <summary>
    /// Feeds one operation's rows to the trail as they complete.
    ///
    /// <para><b>Every call must be handed the same list, still growing.</b> That is
    /// what the cursor assumes: the chunked operations <c>AddRange</c> into a single
    /// list and report it after each chunk, so counting rows already seen is the
    /// only way to record all hundred of an <c>AddRange</c> without recording any
    /// of them twice.</para>
    ///
    /// <para>The one way to get this wrong is to call it with a <i>second</i> list.
    /// The counter has already moved past that list's rows, so the loop would begin
    /// beyond its end and record nothing at all — silently, which is the single
    /// failure a trail exists to rule out. Nothing does that today: the operations
    /// that finish early do so before the first report. Anything added later must
    /// keep it that way, or hand over rows through the same list.</para>
    /// </summary>
    private sealed class Audit(IBulkAuditLog log, string operation, bool dryRun)
    {
        private readonly BulkAuditRun _run = new(log, operation, dryRun);
        private int _recorded;

        public string? Warning => _run.Warning;

        public void Record(IReadOnlyList<SessionEditResult> results)
        {
            for (; _recorded < results.Count; _recorded++)
            {
                var row = results[_recorded];

                _run.Row(row.SessionId, row.SessionName, row.Outcome.ToString(), row.Message);
            }
        }
    }

    /// <summary>
    /// How many sessions one call may carry.
    ///
    /// <para>A bound rather than a target: the point of chunking is that a
    /// 200-session move is two requests instead of two hundred, against a limiter
    /// metered per operation. The cap keeps the SOAP body — and the server's own
    /// work per call — from growing without limit on a term's worth of sessions,
    /// which is the size a bulk run actually reaches.</para>
    /// </summary>
    public const int ChunkSize = 100;

    /// <summary>
    /// Renames sessions from a transformation of the current name.
    ///
    /// <para>Takes a function rather than a fixed string because bulk renaming is
    /// almost always a pattern — a course code that changed, a prefix that needs
    /// adding — not the same literal name for every session, which would make
    /// them indistinguishable.</para>
    /// </summary>
    public async Task<BulkEditReport> RenameAsync(
        IReadOnlyList<(Guid Id, string CurrentName)> targets,
        Func<string, string> transform,
        bool dryRun,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<SessionEditResult>(targets.Count);
        var audit = new Audit(_audit, "rename", dryRun);

        foreach (var (id, currentName) in targets)
        {
            ct.ThrowIfCancellationRequested();

            var name = transform(currentName)?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                results.Add(Fail(id, currentName, "The new name would be empty."));
                Report(progress, results, audit);
                continue;
            }

            if (string.Equals(name, currentName, StringComparison.Ordinal))
            {
                results.Add(Fail(id, currentName, "The new name is identical to the current one."));
                Report(progress, results, audit);
                continue;
            }

            results.Add(await ApplyAsync(
                id, currentName, dryRun,
                $"Rename to '{name}'.",
                c => sessions.UpdateSessionNameAsync(id, name, c),
                ct).ConfigureAwait(false));

            Report(progress, results, audit);
        }

        return Finish(results, audit);
    }

    /// <summary>
    /// Moves sessions into a folder, resolved by name or guid.
    ///
    /// <para><b>A folder that cannot be resolved is reported, never guessed
    /// at.</b> There is no safe fallback here, unlike booking into the recorder's
    /// default: moving a term's recordings into the wrong folder is a silent,
    /// hard-to-notice mistake, so an unresolvable hint fails the whole operation
    /// with every row marked rather than moving anything.</para>
    /// </summary>
    public async Task<BulkEditReport> MoveAsync(
        IReadOnlyList<(Guid Id, string CurrentName)> targets,
        string folderHint,
        bool dryRun,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<SessionEditResult>(targets.Count);
        var audit = new Audit(_audit, "move", dryRun);

        // Resolved once. A bulk move always targets one folder, and looking it
        // up per session would spend the request budget on discovery.
        PanoptoFolder? folder;

        try
        {
            folder = await sessions.FindFolderAsync(folderHint, ct).ConfigureAwait(false);
        }
        catch (ListingIncompleteException ex)
        {
            // Converted to a report rather than left to propagate. The distinction
            // is the same one the scheduler draws for a room: a folder this app
            // did not read that far to find is not evidence that it is absent, and
            // filing that as "no such folder" would send the operator to fix a row
            // that is not broken.
            return Finish([.. targets.Select(t => Fail(t.Id, t.CurrentName, ex.Message))], audit);
        }

        if (folder is null)
        {
            var message = $"No folder matching '{folderHint}'.";

            return Finish([.. targets.Select(t => Fail(t.Id, t.CurrentName, message))], audit);
        }

        // One call per chunk, not per session. MoveSessions has always taken a
        // list; it was only ever handed a single id, so a 200-recording move cost
        // 200 requests against a limiter that meters per operation. It is
        // idempotent — the same recordings in the same folder twice is the same
        // place — which is what makes the chunk safe to re-run after a fault.
        for (var start = 0; start < targets.Count; start += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = targets.Skip(start).Take(ChunkSize).ToList();

            results.AddRange(await ApplyChunkAsync(
                chunk, dryRun,
                $"Move to '{folder.Name}'.",
                c => sessions.MoveSessionsAsync([.. chunk.Select(t => t.Id)], folder.Id, c),
                ct).ConfigureAwait(false));

            Report(progress, results, audit);
        }

        return Finish(results, audit);
    }

    /// <summary>
    /// Moves recordings by the same amount, keeping each one's length. The bulk
    /// "everything an hour later" case.
    ///
    /// <para>The arithmetic is <see cref="RetimePlan.Shift"/>, which is where a row
    /// with no reported time is turned into a refusal naming that row. Nothing here
    /// decides anything the single-recording path does not also decide.</para>
    /// </summary>
    public Task<BulkEditReport> RetimeByAsync(
        IReadOnlyList<SessionTarget> targets,
        TimeSpan by,
        bool dryRun,
        IProgress<int>? progress = null,
        DateTime? nowUtc = null,
        CancellationToken ct = default)
        => RetimeAsync(RetimePlan.Shift(targets, by), dryRun, progress, nowUtc, ct);

    /// <summary>
    /// Puts recordings' starts at a given time of day, each on the day it is
    /// already on, keeping each one's length. The bulk "everything at 9am" case.
    /// </summary>
    /// <inheritdoc cref="RetimeByAsync(IReadOnlyList{SessionTarget}, TimeSpan, bool, IProgress{int}?, DateTime?, CancellationToken)"/>
    public Task<BulkEditReport> RetimeStartAtAsync(
        IReadOnlyList<SessionTarget> targets,
        TimeSpan timeOfDay,
        bool dryRun,
        IProgress<int>? progress = null,
        DateTime? nowUtc = null,
        CancellationToken ct = default)
        => RetimeAsync(RetimePlan.SetStartTimeOfDay(targets, timeOfDay), dryRun, progress, nowUtc, ct);

    /// <summary>
    /// Runs a plan: every entry it could place goes through the same guarded retime
    /// the single-recording path uses, and every entry it refused is reported as a
    /// failure naming why.
    ///
    /// <para>In the plan's order, which is the operator's own, so a row the plan
    /// could not place appears among the rows it could rather than in a block at the
    /// top that has to be matched up by name.</para>
    ///
    /// <para><b>A refused entry is a reported row, never a skipped one.</b> The one
    /// failure an operator cannot see is a recording that was in their selection and
    /// is in neither the results nor the mistakes.</para>
    /// </summary>
    public async Task<BulkEditReport> RetimeAsync(
        RetimePlanResult plan,
        bool dryRun,
        IProgress<int>? progress = null,
        DateTime? nowUtc = null,
        CancellationToken ct = default)
    {
        var results = new List<SessionEditResult>(plan.Entries.Count);
        var audit = new Audit(_audit, "retime", dryRun);

        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();

            results.Add(entry.Slot is { } slot
                ? await RetimeOneAsync(slot, dryRun, nowUtc, ct).ConfigureAwait(false)
                : Fail(entry.Target.Id, entry.Target.CurrentName, entry.Refusal ?? string.Empty));

            Report(progress, results, audit);
        }

        return Finish(results, audit);
    }

    /// <summary>
    /// Moves recordings to new times, given as the room's wall clock.
    ///
    /// <para>Every retime in the app arrives here: the details panel's one, the
    /// drag-to-reschedule path's one, and the bulk path's many. They come in through
    /// the two overloads above and beside this one, and all of them end in
    /// <see cref="RetimeOneAsync"/> — so the wall-clock conversion, the past check
    /// and the clash report exist once. A relative shift is arithmetic the caller
    /// does on the way in; the <i>checks</i> are what must not be duplicated, and
    /// they are not.</para>
    /// </summary>
    /// <param name="targets">Each row carries the slot it should end up in.</param>
    /// <param name="nowUtc">
    /// The current instant, for the past check. Defaults to the real clock;
    /// tests pass one so the boundary is pinned rather than waited for, the same
    /// seam <see cref="RoomClock.WouldLandInThePast"/> already offers and for the
    /// same reason.
    /// </param>
    public async Task<BulkEditReport> RetimeAsync(
        IReadOnlyList<SessionRetime> targets,
        bool dryRun,
        IProgress<int>? progress = null,
        DateTime? nowUtc = null,
        CancellationToken ct = default)
    {
        var results = new List<SessionEditResult>(targets.Count);
        var audit = new Audit(_audit, "retime", dryRun);

        foreach (var row in targets)
        {
            ct.ThrowIfCancellationRequested();

            results.Add(await RetimeOneAsync(row, dryRun, nowUtc, ct).ConfigureAwait(false));

            Report(progress, results, audit);
        }

        return Finish(results, audit);
    }

    /// <summary>
    /// One recording, with the two guards that must not be skipped.
    ///
    /// <para><b>A clash is reported, not swallowed.</b>
    /// <c>UpdateRecordingTime</c> answers with <c>ConflictsExist</c> — a success
    /// carrying a flag — and this used to discard the return value entirely, so a
    /// retime that clashed read as a clean success. It is the same mistake as
    /// treating a SOAP 200 as a booked recording, one layer down.</para>
    /// </summary>
    private async Task<SessionEditResult> RetimeOneAsync(
        SessionRetime row,
        bool dryRun,
        DateTime? nowUtc,
        CancellationToken ct)
    {
        // Checked before the call rather than left to Panopto. A zero-length or
        // backwards recording is a mistake in the input, and the server's answer to
        // it is a fault that says nothing about which row it was.
        if (row.End <= row.Start)
            return Fail(row.Id, row.CurrentName, "The end time is not after the start time.");

        // A drag is easy to fumble and the cost is a room with nobody recording in
        // it, so this is refused here rather than sent and rejected. It also has to
        // be the *room's* past, not this machine's — a workstation set to another
        // zone would otherwise refuse bookings that are still ahead of the room.
        if (RoomClock.WouldLandInThePast(row.End, recorders.RoomZone, nowUtc))
        {
            return Fail(
                row.Id,
                row.CurrentName,
                $"That would put the recording in the past — it ends {row.End:ddd d MMM HH:mm}, "
                + "and the room has already gone past that.");
        }

        return await ApplyAsync(
            row.Id, row.CurrentName, dryRun,
            $"Retime to {row.Start:ddd d MMM HH:mm}–{row.End:HH:mm}.",
            async c =>
            {
                // Throws for a wall clock that does not exist — the spring-forward
                // hour — with a message naming the date, which is exactly what an
                // operator needs and is not something to re-word here.
                var result = await recorders
                    .UpdateRecordingTimeAsync(row.Id, row.Start, row.End, c)
                    .ConfigureAwait(false);

                if (!result.ConflictsExist) return null;

                return result.Conflicts.Count > 0
                    ? " Moved, but something else is booked then: "
                      + string.Join("; ", result.Conflicts) + "."
                    : " Moved, but Panopto reports a clash and did not say with what.";
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rewrites session descriptions from a transformation of the current one.
    ///
    /// <para>A function rather than a string, for the same reason rename takes one:
    /// a bulk presenter change is nearly always per-session, and one literal
    /// description on two hundred recordings makes them indistinguishable.</para>
    ///
    /// <para><b>An empty result is allowed</b>, unlike a rename. Clearing a
    /// description is a real thing to want, so there is nothing here to refuse.</para>
    ///
    /// <para>The legacy tool writes the presenter into this field because the
    /// scheduling call has no field for a presenter, so for imported rows the two
    /// are the same string. Measured on the Rotman tenant, <c>Abstract</c> is the
    /// field that carries it back, so the preview can show what each write would
    /// replace rather than overwriting text nobody looked at.</para>
    ///
    /// <para><b>The read is measured; the write is not, and on this tenant it is
    /// refused.</b> <c>--verify-write</c> books a scratch session, reads
    /// <c>Abstract</c> back through <c>Data.svc</c> and then writes: Panopto
    /// answers <i>"Invalid Session Id … at accessLevel: Creator"</i> for the id
    /// the row genuinely reads back under, and that id and the <c>DeliveryID</c>
    /// <c>ScheduleRecording</c> returns are the only two there are. So a
    /// description write fails for every row on this account, and the failure is
    /// reported per row rather than swallowed — see the note in
    /// <see cref="BulkScheduler"/>. What cannot be told from here is whether the
    /// limit is this account's access level on the recorder's default folder or
    /// Panopto refusing to describe a session that has not recorded yet; editing a
    /// description in Panopto's own dashboard answers that in one click.</para>
    /// </summary>
    public async Task<BulkEditReport> SetDescriptionAsync(
        IReadOnlyList<(Guid Id, string CurrentName, string CurrentDescription)> targets,
        Func<string, string?> transform,
        bool dryRun,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<SessionEditResult>(targets.Count);
        var audit = new Audit(_audit, "description", dryRun);

        foreach (var (id, currentName, currentDescription) in targets)
        {
            ct.ThrowIfCancellationRequested();

            var description = transform(currentDescription) ?? string.Empty;

            if (string.Equals(description, currentDescription, StringComparison.Ordinal))
            {
                results.Add(Fail(id, currentName, "The description is unchanged."));
                Report(progress, results, audit);
                continue;
            }

            results.Add(await ApplyAsync(
                id, currentName, dryRun,
                Describe(description, currentDescription),
                c => sessions.UpdateSessionDescriptionAsync(id, description, c),
                ct).ConfigureAwait(false));

            Report(progress, results, audit);
        }

        return Finish(results, audit);
    }

    /// <summary>
    /// What a description write says it will do, naming the text it replaces.
    ///
    /// <para><b>The current text, not just the new one.</b> This is the field the
    /// legacy tool writes the presenter into, so an operator's descriptions are not
    /// empty — they are the previous presenter. A preview that said only "Set the
    /// description to 'Goldman'" would show a clean-looking operation over text
    /// nobody had read, and the whole reason the current value is carried in the
    /// selection is to be able to print it here.</para>
    ///
    /// <para>The clearing arm cannot meet an already-empty description: the two
    /// being equal is refused above, so "clear" is only ever reached with something
    /// left to clear.</para>
    /// </summary>
    private static string Describe(string description, string currentDescription) => description.Length switch
    {
        0 => $"Clear the description, which now reads '{currentDescription}'.",
        _ when currentDescription.Length == 0 => $"Set the description to '{description}'.",
        _ => $"Set the description to '{description}', replacing '{currentDescription}'.",
    };

    /// <summary>
    /// Rewrites descriptions over a calendar selection, which is the same
    /// operation with the current text read from a field with a different name.
    /// </summary>
    /// <inheritdoc cref="SetDescriptionAsync(IReadOnlyList{ValueTuple{Guid, string, string}}, Func{string, string?}, bool, IProgress{int}?, CancellationToken)"/>
    public Task<BulkEditReport> SetDescriptionAsync(
        IReadOnlyList<SessionTarget> targets,
        Func<string, string?> transform,
        bool dryRun,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
        => SetDescriptionAsync(
            // Null and the empty string both mean "nothing written", and a write
            // that changes nothing is refused below, so they cannot produce
            // different outcomes. Mapped once, here, rather than at each call site:
            // a caller that substituted a placeholder for null would make an
            // untouched session look like a change, and the preview would offer it
            // as one.
            [.. targets.Select(t => (t.Id, t.CurrentName, t.Description ?? string.Empty))],
            transform, dryRun, progress, ct);

    /// <summary>
    /// Deletes sessions. Irreversible — Panopto has no undo, and a recording that
    /// has not been archived is gone.
    ///
    /// <para>A real run requires a <see cref="DestructiveAction"/> permitting this
    /// exact set, and a dry run does not — asking to be shown what would happen is
    /// the thing that ought to come before the confirmation, not after it. The
    /// check sits here rather than in the window so no caller can reach the
    /// tenant's delete without having named what it is about to destroy.</para>
    /// </summary>
    /// <param name="permit">
    /// Required when <paramref name="dryRun"/> is false. Refusing loudly is the
    /// point: an unacknowledged delete that quietly did nothing would leave the
    /// operator believing it had run.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The permit does not cover <paramref name="targets"/>.
    /// </exception>
    public async Task<BulkEditReport> DeleteAsync(
        IReadOnlyList<(Guid Id, string CurrentName)> targets,
        bool dryRun,
        DestructiveAction? permit = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var ids = targets.Select(t => t.Id).ToList();

        if (!dryRun && !(permit?.Permits(DestructiveAction.DeleteVerb, ids) ?? false))
        {
            throw new InvalidOperationException(
                permit?.Refusal(DestructiveAction.DeleteVerb, ids)
                ?? "The delete was not confirmed. Nothing was deleted.");
        }

        var results = new List<SessionEditResult>(targets.Count);
        var audit = new Audit(_audit, "delete", dryRun);

        // Chunked for the same reason the move is, and idempotent for the same
        // reason: a session that is already gone cannot be deleted twice. The
        // permit above was checked against every target before this loop began, so
        // no chunk is sent on an unconfirmed set.
        for (var start = 0; start < targets.Count; start += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = targets.Skip(start).Take(ChunkSize).ToList();

            results.AddRange(await ApplyChunkAsync(
                chunk, dryRun,
                "Delete, permanently.",
                c => sessions.DeleteSessionsAsync([.. chunk.Select(t => t.Id)], c),
                ct).ConfigureAwait(false));

            Report(progress, results, audit);
        }

        return Finish(results, audit);
    }

    public async Task<BulkEditReport> SetBroadcastAsync(
        IReadOnlyList<(Guid Id, string CurrentName)> targets,
        bool isBroadcast,
        bool dryRun,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<SessionEditResult>(targets.Count);
        var audit = new Audit(_audit, "broadcast", dryRun);

        foreach (var (id, currentName) in targets)
        {
            ct.ThrowIfCancellationRequested();

            results.Add(await ApplyAsync(
                id, currentName, dryRun,
                isBroadcast ? "Turn webcasting on." : "Turn webcasting off.",
                c => sessions.UpdateSessionIsBroadcastAsync(id, isBroadcast, c),
                ct).ConfigureAwait(false));

            Report(progress, results, audit);
        }

        return Finish(results, audit);
    }

    /// <summary>
    /// Runs one edit, or reports what it would have done. A failed session is
    /// recorded and the batch continues, so one locked session does not hide the
    /// state of the other two hundred.
    /// </summary>
    /// <param name="apply">
    /// Returns a sentence to append to the result when the call succeeded but
    /// there is something to say about it, or null for the ordinary case.
    ///
    /// <para>The delegate used to return <see cref="Task"/> and the return value
    /// was discarded — which is fine until a call reports success-with-a-flag, as
    /// <c>UpdateRecordingTime</c> does with <c>ConflictsExist</c>. Discarding it
    /// made a clashing retime read as a clean one.</para>
    /// </param>
    private static async Task<SessionEditResult> ApplyAsync(
        Guid id,
        string currentName,
        bool dryRun,
        string description,
        Func<CancellationToken, Task<string?>> apply,
        CancellationToken ct)
    {
        if (dryRun)
        {
            return WouldApply(id, currentName, description);
        }

        try
        {
            var note = await apply(ct).ConfigureAwait(false);

            return Applied(id, currentName, description, note);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(id, currentName, ex.Message);
        }
    }

    /// <summary>
    /// The ordinary case, where the call either works or throws and has nothing
    /// extra to report. Delegates to the form above rather than repeating it, so
    /// there is one place where a result is built.
    /// </summary>
    private static Task<SessionEditResult> ApplyAsync(
        Guid id,
        string currentName,
        bool dryRun,
        string description,
        Func<CancellationToken, Task> apply,
        CancellationToken ct)
        => ApplyAsync(id, currentName, dryRun, description, async c =>
        {
            await apply(c).ConfigureAwait(false);
            return null;
        }, ct);

    private static SessionEditResult WouldApply(Guid id, string currentName, string description) => new()
    {
        SessionId = id,
        SessionName = currentName,
        Outcome = SessionEditOutcome.WouldApply,
        Message = $"Would apply to '{currentName}': {description}",
    };

    private static SessionEditResult Applied(
        Guid id, string currentName, string description, string? note) => new()
    {
        SessionId = id,
        SessionName = currentName,
        Outcome = SessionEditOutcome.Applied,
        Message = $"'{currentName}': {description}" + (note ?? string.Empty),
    };

    /// <summary>
    /// One write covering a whole chunk of sessions, reported as one result per
    /// session.
    ///
    /// <para><b>Only for operations that are idempotent.</b> Applying the same end
    /// state twice leaves the same end state, and that is the entire reason
    /// re-running a chunk that faulted is safe — so the failure message says the
    /// operator may. A non-idempotent write must never come through here:
    /// scheduling a recording twice books it twice. That is the same line
    /// <c>safeToRetry: false</c> already draws in the SOAP client, which is a good
    /// sign it is the right line.</para>
    ///
    /// <para><b>A chunk fault is not a partial result.</b> The call either returned
    /// or it threw. If it threw, this app does not know how far it got, and saying
    /// so is the honest answer rather than marking every session in the chunk as
    /// failed when some of them may well have been moved.</para>
    /// </summary>
    private static async Task<IReadOnlyList<SessionEditResult>> ApplyChunkAsync(
        IReadOnlyList<(Guid Id, string CurrentName)> chunk,
        bool dryRun,
        string description,
        Func<CancellationToken, Task> apply,
        CancellationToken ct)
    {
        if (dryRun)
            return [.. chunk.Select(t => WouldApply(t.Id, t.CurrentName, description))];

        try
        {
            await apply(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message =
                $"{ex.Message} This was one call for {chunk.Count} session(s), so the app "
                + "cannot tell which of them went through. Running it again is safe — the "
                + "same end state applied twice is the same end state.";

            return [.. chunk.Select(t => Fail(t.Id, t.CurrentName, message))];
        }

        return [.. chunk.Select(t => Applied(t.Id, t.CurrentName, description, null))];
    }

    private static SessionEditResult Fail(Guid id, string currentName, string message) => new()
    {
        SessionId = id,
        SessionName = currentName,
        Outcome = SessionEditOutcome.Failed,
        Message = message,
    };
}
