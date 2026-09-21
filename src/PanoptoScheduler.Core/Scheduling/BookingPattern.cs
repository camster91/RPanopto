using PanoptoScheduler.Core.Import;

namespace PanoptoScheduler.Core.Scheduling;

/// <summary>
/// A repeating booking, described rather than enumerated.
///
/// <para><b>This is a way of filling in the grid, not a second way of booking.</b>
/// <see cref="BookingPatternGenerator"/> turns one of these into ordinary
/// <see cref="ScheduleImportRow"/> values — the same records a CSV produces — so
/// the preview, the dry run, the rate limiting and the per-row reporting are all
/// the code that already exists. Nothing downstream of this file knows a pattern
/// was involved, which is the point: a pattern that booked by a different route
/// would be a second set of guards, and one of them would be missing something.</para>
///
/// <para>Times are the room's wall clock with <see cref="DateTimeKind.Unspecified"/>,
/// like every other time in this app. See <see cref="ScheduleImportRow"/>.</para>
/// </summary>
public sealed record BookingPattern
{
    /// <summary>
    /// The rooms to book, by recorder name. A room that does not resolve is the
    /// scheduler's problem, and it already refuses one rather than skipping it.
    /// </summary>
    public IReadOnlyList<string> Recorders { get; init; } = [];

    /// <summary>First and last day, inclusive. A single-day pattern has From == To.</summary>
    public required DateOnly From { get; init; }

    /// <inheritdoc cref="From"/>
    public required DateOnly To { get; init; }

    /// <summary>
    /// Which days of the week recur. Empty is a problem rather than "every day":
    /// a pattern that silently booked seven days a week when the operator meant
    /// to pick some would be a term's worth of wrong bookings.
    /// </summary>
    public IReadOnlyList<DayOfWeek> Weekdays { get; init; } = [];

    /// <summary>Start of day, as the room's clock.</summary>
    public required TimeSpan Start { get; init; }

    /// <inheritdoc cref="Start"/>
    public required TimeSpan End { get; init; }

    /// <summary>
    /// Days to leave out — reading week, a holiday, the day the room is being
    /// painted. Compared by date, so a skip date outside the range is harmless.
    /// </summary>
    public IReadOnlyList<DateOnly> SkipDates { get; init; } = [];

    /// <summary>
    /// The recording name, with tokens: <c>{room}</c>, <c>{date}</c>, <c>{dow}</c>,
    /// <c>{n}</c> (this room's own sequence number), <c>{nn}</c> (the same,
    /// two digits) and <c>{total}</c> (how many dates the pattern covers).
    ///
    /// <para><c>{n}</c> counts per room rather than across the whole run, so
    /// three rooms booked for twelve weeks produce three series of 1 to 12 rather
    /// than one series of 1 to 36 — which is what a person means by "Lecture 5".</para>
    /// </summary>
    public required string TitleFormat { get; init; }

    /// <summary>Written into the session description, which is where the presenter goes.</summary>
    public string? Presenter { get; init; }

    /// <summary>Folder GUID or name, resolved at booking time like a file's would be.</summary>
    public string? FolderHint { get; init; }

    public bool IsBroadcast { get; init; }

    /// <summary>
    /// Whether an end earlier than the start means the next morning. Off by
    /// default, because reading it that way by accident turns a typo into a
    /// booking that runs all night.
    /// </summary>
    public bool AllowOvernight { get; init; }
}
