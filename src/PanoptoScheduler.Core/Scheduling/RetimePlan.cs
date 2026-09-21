namespace PanoptoScheduler.Core.Scheduling;

/// <summary>
/// What a bulk retime decided for one recording: the slot it should end up in, or
/// the reason it cannot be moved.
///
/// <para><b>Exactly one of the two is set, and a private constructor is what makes
/// that true.</b> Two public factories are the only ways in, so there is no way to
/// build an entry that is neither a slot nor a refusal — which would otherwise be
/// an entry the report quietly skips. A row that disappears between the selection
/// and the report is the one failure an operator cannot see.</para>
/// </summary>
public sealed record RetimePlanEntry
{
    private RetimePlanEntry(SessionTarget target, SessionRetime? slot, string? refusal)
    {
        Target = target;
        Slot = slot;
        Refusal = refusal;
    }

    /// <summary>The recording this entry is about, as the calendar handed it over.</summary>
    public SessionTarget Target { get; }

    /// <summary>Where it should end up, or null when the plan refused it.</summary>
    public SessionRetime? Slot { get; }

    /// <summary>Why it cannot be moved, or null when <see cref="Slot"/> is set.</summary>
    public string? Refusal { get; }

    public bool CanMove => Slot is not null;

    /// <summary>
    /// A recording the plan can place.
    ///
    /// <para>Both times are relabelled as wall clocks here, at the single point
    /// every planned slot passes through. The room's zone is applied by the
    /// recorder on the way out, so a value that had picked up this machine's
    /// <see cref="DateTimeKind"/> somewhere would be read as an instant and written
    /// back as a time the operator never chose — the same mismatch the whole
    /// time-handling arrangement in this app exists to prevent.</para>
    /// </summary>
    public static RetimePlanEntry Move(SessionTarget target, DateTime start, DateTime end) => new(
        target,
        new SessionRetime(
            target.Id,
            target.CurrentName,
            DateTime.SpecifyKind(start, DateTimeKind.Unspecified),
            DateTime.SpecifyKind(end, DateTimeKind.Unspecified)),
        null);

    /// <summary>A recording the plan cannot place, and the sentence saying why.</summary>
    public static RetimePlanEntry Refuse(SessionTarget target, string why) => new(target, null, why);
}

/// <summary>
/// A plan for a whole selection, in the operator's own order.
///
/// <para>Ordered rather than split into "the movables" and "the refusals" so the
/// report the operator reads has each row where they would look for it. A block of
/// failures at the top of the grid makes the operator match rows up by name.</para>
/// </summary>
public sealed record RetimePlanResult(IReadOnlyList<RetimePlanEntry> Entries)
{
    public int Movable => Entries.Count(e => e.CanMove);

    public int Refused => Entries.Count(e => !e.CanMove);

    /// <summary>
    /// Whether the plan can move anything at all. The bulk UI reads this to decide
    /// whether a preview has earned the right to arm its apply button: a preview in
    /// which every row was refused is information, not permission.
    /// </summary>
    public bool IsEmpty => Movable == 0;
}

/// <summary>
/// Turns a selection into the new slots for it — the arithmetic a bulk retime
/// needs, and nothing else.
///
/// <para><b>Pure, and in Core for that reason.</b> This is the part of a bulk
/// retime that can be wrong without anyone noticing: a shift that quietly drops a
/// recording's length, a "set the time" that collapses a week onto one day, a row
/// with no reported time that disappears instead of being reported. None of that
/// is reachable from a test while it lives in a view model, and all of it is
/// reachable from here.</para>
///
/// <para><b>Nothing here is a write, and nothing here is a guard.</b> Whether the
/// new slot is in the room's past, whether the end is after the start, and what
/// Panopto says about a clash are all decided in
/// <see cref="BulkSessionEditor.RetimeAsync(IReadOnlyList{SessionRetime}, bool, IProgress{int}?, DateTime?, CancellationToken)"/>,
/// which is the one path the details panel and the bulk tools share. This decides
/// only where each recording would go.</para>
/// </summary>
public static class RetimePlan
{
    /// <summary>
    /// Moves every recording by the same amount, keeping each one's length.
    /// </summary>
    /// <param name="by">
    /// How far, signed: negative moves earlier. A shift can carry a recording past
    /// midnight onto the next day, which is ordinary arithmetic on a wall clock and
    /// is the honest result — suppressing it would silently shorten a late booking
    /// instead.
    /// </param>
    public static RetimePlanResult Shift(IReadOnlyList<SessionTarget> targets, TimeSpan by)
        => Plan(targets, target =>
        {
            // Refused rather than passed through: a zero shift produces a report in
            // which every row "would be retimed" to exactly where it already is, and
            // pressing apply would then cost a request per session to write the
            // times back unchanged. An operator who typed 0 meant something else.
            if (by == TimeSpan.Zero)
                return RetimePlanEntry.Refuse(target, "The shift is zero, so nothing would move.");

            if (!Times(target, out var start, out var end, out var why))
                return RetimePlanEntry.Refuse(target, why);

            // Both ends by the same amount, so the length is preserved by
            // construction rather than by a subtraction that could drift.
            return RetimePlanEntry.Move(target, start + by, end + by);
        });

    /// <summary>
    /// Puts every recording's start at a given time of day, on the day it is
    /// already on, keeping each one's length.
    /// </summary>
    /// <param name="timeOfDay">
    /// Within one day of midnight. Throws rather than wrapping: a value that has
    /// come through as 25 hours would otherwise become 01:00 for every selected
    /// recording, which is a move nobody asked for and which reads on screen as a
    /// plausible one.
    /// </param>
    public static RetimePlanResult SetStartTimeOfDay(
        IReadOnlyList<SessionTarget> targets,
        TimeSpan timeOfDay)
    {
        if (timeOfDay < TimeSpan.Zero || timeOfDay >= TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(
                nameof(timeOfDay),
                timeOfDay,
                "A time of day must be within one day of midnight.");

        return Plan(targets, target =>
        {
            if (!Times(target, out var start, out var end, out var why))
                return RetimePlanEntry.Refuse(target, why);

            var length = end - start;

            if (length <= TimeSpan.Zero)
                return RetimePlanEntry.Refuse(
                    target, "The end is not after the start, so there is no length to keep.");

            // Each recording's own date, with only the time of day replaced. A
            // selection spanning a term stays spread across it; reading a date from
            // anywhere else — today, or the first row — would stack the whole
            // selection onto one day, which for a week's bookings is 40 recordings
            // in one room at once.
            var moved = start.Date + timeOfDay;

            return RetimePlanEntry.Move(target, moved, moved + length);
        });
    }

    /// <summary>
    /// Reads the two times a move needs, or says which one is missing.
    ///
    /// <para>The two are told apart because they mean different things to whoever
    /// has to fix them: no start is a session the listing has no date for, no length
    /// is a session with a date and no duration. One message covering both would
    /// send the reader to look at the wrong thing.</para>
    /// </summary>
    private static bool Times(
        SessionTarget target,
        out DateTime start,
        out DateTime end,
        out string why)
    {
        start = default;
        end = default;

        if (target.Start is not { } reportedStart)
        {
            why = "No start time is reported for this recording, so there is nothing to move.";
            return false;
        }

        if (target.End is not { } reportedEnd)
        {
            why = "No length is reported for this recording, so there is no end time to move.";
            return false;
        }

        start = reportedStart;
        end = reportedEnd;
        why = "";
        return true;
    }

    private static RetimePlanResult Plan(
        IReadOnlyList<SessionTarget> targets,
        Func<SessionTarget, RetimePlanEntry> place)
        => new([.. targets.Select(place)]);
}
