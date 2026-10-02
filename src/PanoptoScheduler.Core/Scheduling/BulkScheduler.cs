using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Import;

namespace PanoptoScheduler.Core.Scheduling;

public enum ScheduleOutcomeKind
{
    /// <summary>Panopto accepted the booking with no clash.</summary>
    Scheduled,

    /// <summary>Panopto booked it but reported a clash — someone must look.</summary>
    Conflict,

    /// <summary>
    /// A dry run resolved this row and would book it. Distinct from
    /// <see cref="Scheduled"/> on purpose: a report that calls a rehearsal
    /// "scheduled" cannot be used to decide whether to run it for real.
    /// </summary>
    WouldSchedule,

    /// <summary>Deliberately not attempted; the report says why.</summary>
    Skipped,

    /// <summary>The call failed.</summary>
    Failed,
}

/// <summary>What happened to one row.</summary>
public sealed record ScheduleOutcome
{
    public required int Line { get; init; }
    public required string Title { get; init; }
    public required ScheduleOutcomeKind Kind { get; init; }
    public required string Message { get; init; }

    public Guid SessionId { get; init; }
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>
    /// The scheduled session, or null when this row never reached one.
    ///
    /// <para>A skipped or failed row carries <see cref="Guid.Empty"/> because that is
    /// what the scheduling call left behind, not because nothing is a session id.
    /// Naming the difference once, here, keeps every reader of this record from
    /// having to know that — the audit trail in particular, where an id of all zeros
    /// would read as a real session.</para>
    /// </summary>
    public Guid? SessionIdOrNull => SessionId == Guid.Empty ? null : SessionId;

    public bool Succeeded => Kind is ScheduleOutcomeKind.Scheduled
        or ScheduleOutcomeKind.Conflict
        or ScheduleOutcomeKind.WouldSchedule;
}

public sealed record ScheduleProgress(int Completed, int Total, ScheduleOutcome Outcome);

public sealed record BulkScheduleOptions
{
    /// <summary>
    /// Folder to fall back to when a row names one that does not exist. Matching
    /// the original uploader, which had the same setting.
    /// </summary>
    public string? DefaultFolderName { get; init; }

    /// <summary>
    /// Write the presenter into the session description. The scheduling call has
    /// no field for a presenter, so this is a second call per row.
    /// </summary>
    public bool SetPresenterAsDescription { get; init; } = true;

    /// <summary>
    /// Resolve everything and report what would happen, without writing.
    ///
    /// <para>Default for the UI. A bulk run against a live tenant is not
    /// undoable, and the failure that matters — a mis-parsed date column booking
    /// 400 sessions in the wrong month — is invisible in a report that only
    /// appears afterwards.</para>
    /// </summary>
    public bool DryRun { get; init; } = true;
}

public sealed record BulkScheduleReport(IReadOnlyList<ScheduleOutcome> Outcomes)
{
    /// <summary>Rows Panopto actually booked.</summary>
    public int Scheduled => Outcomes.Count(o => o.Kind == ScheduleOutcomeKind.Scheduled);

    public int Conflicts => Outcomes.Count(o => o.Kind == ScheduleOutcomeKind.Conflict);

    /// <summary>Rows a dry run would book. Always zero in a real run.</summary>
    public int WouldSchedule => Outcomes.Count(o => o.Kind == ScheduleOutcomeKind.WouldSchedule);

    public int Skipped => Outcomes.Count(o => o.Kind == ScheduleOutcomeKind.Skipped);
    public int Failed => Outcomes.Count(o => o.Kind == ScheduleOutcomeKind.Failed);

    /// <summary>True when the tenant was written to, as opposed to a rehearsal.</summary>
    public bool AnyWritesAttempted => Scheduled + Conflicts > 0;

    /// <summary>
    /// Set when the audit trail is missing rows this run performed. See
    /// <see cref="BulkEditReport.AuditWarning"/> — same sentence, same reasoning: the
    /// outline of a booking run is exactly as hard to reconstruct after the fact as
    /// an edit run's, and a warning nobody reads is the same as no trail.
    /// </summary>
    public string? AuditWarning { get; init; }
}

/// <summary>
/// Turns a parsed legacy file into scheduled recordings.
///
/// <para><b>Resolve first, then write.</b> Recorders and folders are looked up
/// once, up front, and cached. Panopto meters requests per endpoint and per
/// client, so a per-row lookup would spend the whole budget on discovery and
/// fail part-way through the batch — leaving some sessions booked and no way to
/// tell which.</para>
///
/// <para><b>No automatic retries.</b> A retried <c>ScheduleRecording</c> is a
/// second recording, not a repeat of the first. A failed row is reported and
/// left alone.</para>
/// </summary>
public sealed class BulkScheduler(
    RemoteRecorderClient recorders,
    SessionManagementClient sessions,
    IBulkAuditLog? auditLog = null)
{
    /// <summary>
    /// Where this scheduler's rows go as they complete. Defaults to a sink that
    /// records nothing; see the same field on <see cref="BulkSessionEditor"/> for why
    /// the real file is not the default.
    /// </summary>
    private readonly IBulkAuditLog _audit = auditLog ?? NullBulkAuditLog.Instance;

    public async Task<BulkScheduleReport> RunAsync(
        IReadOnlyList<ScheduleImportRow> rows,
        BulkScheduleOptions options,
        IProgress<ScheduleProgress>? progress = null,
        CancellationToken ct = default)
    {
        var outcomes = new List<ScheduleOutcome>(rows.Count);
        var run = new BulkAuditRun(_audit, "book", options.DryRun);
        var completed = 0;

        // Progress and the trail, from one call — the same reason the bulk editor
        // does it here: a row the operator can see on screen but that is missing from
        // the file is the one inconsistency that would make the trail untrustworthy.
        void Report(ScheduleOutcome outcome)
        {
            outcomes.Add(outcome);

            run.Row(
                outcome.SessionIdOrNull,
                outcome.Title,
                outcome.Kind.ToString(),
                outcome.Message,
                // The line of the file is a refused row's only real identity, so it
                // goes in the machine-readable field where it can be searched for.
                $"line {outcome.Line}" + (outcome.Conflicts.Count > 0
                    ? $"; conflicts: {string.Join("; ", outcome.Conflicts)}"
                    : string.Empty));

            progress?.Report(new ScheduleProgress(++completed, rows.Count, outcome));
        }

        // One listing for the whole batch, shared by every row.
        var recorderList = await recorders.ListRecordersAsync(ct).ConfigureAwait(false);

        // Folder hints repeat heavily across a term's worth of rows; resolving
        // each one once keeps the request count proportional to the number of
        // distinct rooms, not the number of sessions.
        var folderCache = new Dictionary<string, FolderResolution>(StringComparer.OrdinalIgnoreCase);
        // The recorder-default fallback is the same story for ids: a term's
        // rows touch a handful of recorders many times over, and the lookup is
        // a metered GetDefaultFolderForRecorder call per row without this.
        // Cached per recorder id, so the dry run pays for the rooms it names
        // once each, and the apply run pays the same again.
        var defaultFolderCache = new Dictionary<Guid, Guid>();
        var defaultFolder = (await ResolveFolderAsync(
            folderCache, options.DefaultFolderName ?? string.Empty, ct).ConfigureAwait(false)).Folder;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                Report(await ScheduleOneAsync(
                    row, recorderList, folderCache, defaultFolderCache, defaultFolder, options, run, ct)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad row must not abandon the rest of the batch; the
                // report carries the reason.
                Report(new ScheduleOutcome
                {
                    Line = row.Line,
                    Title = row.Title,
                    Kind = ScheduleOutcomeKind.Failed,
                    Message = ex.Message,
                });
            }
        }

        return new BulkScheduleReport(outcomes) { AuditWarning = run.Warning };
    }

    private async Task<ScheduleOutcome> ScheduleOneAsync(
        ScheduleImportRow row,
        PagedResult<RemoteRecorder> recorderList,
        Dictionary<string, FolderResolution> folderCache,
        Dictionary<Guid, Guid> defaultFolderCache,
        PanoptoFolder? defaultFolder,
        BulkScheduleOptions options,
        BulkAuditRun run,
        CancellationToken ct)
    {
        ScheduleOutcome Skip(string why) => new()
        {
            Line = row.Line,
            Title = row.Title,
            Kind = ScheduleOutcomeKind.Skipped,
            Message = why,
        };

        var wanted = row.RecorderName.Trim();

        var recorder =
            recorderList.FirstOrDefault(r => string.Equals(r.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? recorderList.FirstOrDefault(r =>
                r.ExternalId is { } external &&
                string.Equals(external, wanted, StringComparison.OrdinalIgnoreCase));

        if (recorder is null)
        {
            // Skipped and Failed are different promises. Skipped says "this row
            // was wrong"; Failed says "this row did not happen". A name that is
            // absent from a listing that stopped short is not evidence the room
            // is absent — it is evidence this app did not read that far — and
            // filing it as the first while meaning the second is how 5000 rooms
            // went missing without a single complaint in the report.
            if (!recorderList.Complete)
            {
                return new ScheduleOutcome
                {
                    Line = row.Line,
                    Title = row.Title,
                    Kind = ScheduleOutcomeKind.Failed,
                    Message = $"No recorder named '{row.RecorderName}' in the "
                              + $"{recorderList.Count} that were read"
                              + (recorderList.ReportedTotal > 0
                                  ? $" of {recorderList.ReportedTotal} in the tenant"
                                  : string.Empty)
                              + " — the room list is incomplete, so this name may be real.",
                };
            }

            return Skip($"No recorder named '{row.RecorderName}'.");
        }

        var (folder, unresolvedNote) =
            await ResolveFolderAsync(folderCache, row.FolderHint, ct).ConfigureAwait(false);

        var folderNote = string.Empty;
        if (folder is null)
        {
            folder = defaultFolder;
            if (folder is null)
            {
                if (!defaultFolderCache.TryGetValue(recorder.Id, out var fallback))
                {
                    fallback = await recorders.GetDefaultFolderAsync(recorder.Id, ct).ConfigureAwait(false);
                    defaultFolderCache[recorder.Id] = fallback;
                }

                if (fallback == Guid.Empty)
                {
                    return Skip(
                        $"No folder for '{row.FolderHint ?? "(none)"}' and no default to fall back on."
                        + (unresolvedNote.Length > 0 ? $" {unresolvedNote}." : string.Empty));
                }

                folder = new PanoptoFolder { Id = fallback, Name = "(recorder default)" };
            }

            // The reason differs and the difference matters: "was not found" is a
            // statement about the tenant, and a listing that stopped short has not
            // earned it. Both notes lead to the same safe place, so this stays a
            // note rather than becoming a failure — the recording gets booked
            // either way, and only the explanation changes.
            folderNote = unresolvedNote.Length > 0
                ? $" Recorded to '{folder.Name}' because {unresolvedNote}."
                : string.IsNullOrWhiteSpace(row.FolderHint)
                    // A blank hint never reached the resolver, so nothing was
                    // searched and nothing was "not found" — the tenant is
                    // fine; the row just says nothing about where it goes.
                    // "'' was not found" claims a search that never happened.
                    ? $" Recorded to '{folder.Name}' because the row names no folder."
                    : $" Recorded to '{folder.Name}' because '{row.FolderHint}' was not found.";
        }

        if (options.DryRun)
        {
            // The one refusal the real run makes that the rehearsal would not
            // otherwise see: RoomClock.ToWire throws for a wall clock the room's
            // zone jumps over, and it only runs when the write is built. Checked
            // here, both ends, with the sentence the run would fail the row with —
            // a 22:00–02:30 booking on the spring-forward Saturday has a start
            // that exists and an end that does not, and a preview that only looked
            // at the start would approve it. Failed, not Skipped, because Failed is
            // what the run itself reports for it.
            if ((RoomClock.MissingHour(row.Start, recorders.RoomZone)
                 ?? RoomClock.MissingHour(row.End, recorders.RoomZone)) is { } missing)
            {
                return new ScheduleOutcome
                {
                    Line = row.Line,
                    Title = row.Title,
                    Kind = ScheduleOutcomeKind.Failed,
                    Message = missing,
                };
            }

            return new ScheduleOutcome
            {
                Line = row.Line,
                Title = row.Title,
                Kind = ScheduleOutcomeKind.WouldSchedule,
                Message = $"Would record on {recorder.Name} at "
                          + $"{row.Start:ddd d MMM HH:mm}–{row.End:HH:mm}.{folderNote}",
            };
        }

        ScheduledRecordingResult result;

        try
        {
            result = await recorders.ScheduleAsync(
                row.Title, folder.Id, row.IsBroadcast, row.Start, row.End, [recorder.Id], ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (PanoptoSoapClient.WriteMayHaveLeft(ex, ct))
        {
            // A timeout on the booking itself: the request may have reached
            // Panopto, and a ScheduleRecording that reached it may have created
            // the recording. The run still ends — RunAsync rethrows, and the
            // window tells the operator to check the tenant before booking that
            // row again — but the trail hears about the row first. It used to be
            // rethrown untouched, so the one booking that might exist unreported
            // was also the one the trail had no line for. Written straight to the
            // run with an outcome of its own: the outcome list is dropped with the
            // exception, and "Failed" would say it did not happen, which nobody
            // knows.
            run.Row(
                null,
                row.Title,
                "Unknown",
                $"Outcome unknown (stopped in flight): booking on {recorder.Name} at "
                + $"{row.Start:ddd d MMM HH:mm}–{row.End:HH:mm} was sent and then cut off "
                + $"({ex.Message}), so the recording may or may not exist. Check the tenant "
                + "before booking this row again — a second attempt is a second recording.",
                $"line {row.Line}");
            throw;
        }

        if (result.SessionId == Guid.Empty)
        {
            return new ScheduleOutcome
            {
                Line = row.Line,
                Title = row.Title,
                Kind = ScheduleOutcomeKind.Failed,
                Message = "Panopto accepted the call but scheduled nothing."
                          + (result.ConflictsExist ? " It reported a conflict." : string.Empty),
                Conflicts = result.Conflicts,
            };
        }

        var presenterNote = string.Empty;

        if (options.SetPresenterAsDescription && !string.IsNullOrWhiteSpace(row.Presenter))
        {
            // A failure here must not fail the row: the recording exists, and
            // saying otherwise would make the operator re-run and double-book.
            // It is reported in the row's own message instead, which is what the
            // presenterNote below is for — the presenter is the one part of a row
            // that is allowed to fail quietly-ish rather than not at all.
            //
            // Expected to fail on the Rotman tenant. Measured: the write is
            // refused as "Invalid Session Id … at accessLevel: Creator", and the
            // id passed here is the DeliveryID while the id the row reads back
            // under is its SessionID — neither is accepted. See the note on
            // BulkSessionEditor.SetDescriptionAsync.
            try
            {
                await sessions.UpdateSessionDescriptionAsync(result.SessionId, row.Presenter!, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                presenterNote = $" The recording was booked, but the presenter could not be set: {ex.Message}";
            }
        }

        return new ScheduleOutcome
        {
            Line = row.Line,
            Title = row.Title,
            Kind = result.ConflictsExist ? ScheduleOutcomeKind.Conflict : ScheduleOutcomeKind.Scheduled,
            Message = (result.ConflictsExist
                          ? $"Booked on {recorder.Name} with a clash: {string.Join("; ", result.Conflicts)}."
                          : $"Booked on {recorder.Name} at {row.Start:ddd d MMM HH:mm}–{row.End:HH:mm}.")
                      + folderNote + presenterNote,
            SessionId = result.SessionId,
            Conflicts = result.Conflicts,
        };
    }

    /// <summary>
    /// A folder hint resolved as far as it can be, and what to say about it.
    ///
    /// <para><see cref="Note"/> is empty for the ordinary cases — a folder that
    /// was found, or one that genuinely is not there. It carries a sentence only
    /// when the listing stopped short or the hint named several folders, where
    /// "was not found" would be a claim this app is not entitled to make.</para>
    /// </summary>
    private sealed record FolderResolution(PanoptoFolder? Folder, string Note);

    /// <summary>
    /// Resolves a folder hint, remembering the answer — including "not found", so
    /// a name that does not exist is not searched once per row.
    ///
    /// <para><b>An incomplete listing is caught here rather than left to
    /// propagate.</b> <see cref="SessionManagementClient.FindFolderAsync"/> throws
    /// on that path, which is right for a caller that needs certainty — a bulk move
    /// into the wrong folder is a real error. Booking is not that caller: a hint it
    /// cannot resolve has a safe fallback, and the fallback is what it already does
    /// for a name that truly does not exist. Letting the throw out would turn a
    /// degraded booking into no booking at all, and the default-folder lookup
    /// happens outside the per-row guard, so it would end the whole run.</para>
    /// </summary>
    private async Task<FolderResolution> ResolveFolderAsync(
        Dictionary<string, FolderResolution> cache,
        string? hint,
        CancellationToken ct)
    {
        var key = hint?.Trim() ?? string.Empty;
        if (key.Length == 0) return new FolderResolution(null, string.Empty);

        if (cache.TryGetValue(key, out var cached)) return cached;

        FolderResolution resolution;

        try
        {
            resolution = new FolderResolution(
                await sessions.FindFolderAsync(key, ct).ConfigureAwait(false),
                string.Empty);
        }
        catch (ListingIncompleteException)
        {
            // The exception's own message is aimed at whoever has to raise the
            // page ceiling; this is the sentence an operator reads on a row.
            resolution = new FolderResolution(
                null,
                "the folder list was incomplete, so it may exist");
        }
        catch (AmbiguousFolderException ex)
        {
            // The same safe fallback, for the same reason, and again with its
            // own sentence: "was not found" would send the operator hunting for
            // a typo, when the row's real problem is that it names several
            // folders at once.
            resolution = new FolderResolution(
                null,
                $"'{key}' is part of {ex.Candidates.Count} folder names, so none was guessed");
        }

        cache[key] = resolution;

        return resolution;
    }
}
