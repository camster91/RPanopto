namespace PanoptoScheduler.Core.Scheduling;

/// <summary>
/// A booking pattern kept for next time.
///
/// <para><b>The dates are deliberately not saved.</b> Everything else about a
/// pattern is the same next term — the rooms, the weekdays, the hour, the naming
/// scheme, the presenter — and the dates are the one part that is always wrong.
/// A template carrying last term's range would generate a term's worth of
/// bookings in the past, and that costs more to notice and undo than retyping two
/// dates costs to get right. So the <i>span</i> is stored and
/// <see cref="ToPattern"/> re-anchors it to whatever date the caller names.</para>
///
/// <para>Rooms are saved by name, and a name that no longer resolves is refused
/// at booking time rather than skipped — the rule <see cref="BookingPattern"/>
/// already follows, and the reason a renamed room fails loudly instead of
/// quietly booking nothing.</para>
///
/// <para>Skip dates are saved as dates. They are compared by date, so one that
/// falls outside the new range is harmless: it was given for a week that is not
/// in this term, and a week that is not in this term is not a problem.</para>
/// </summary>
public sealed record BookingTemplate
{
    /// <summary>
    /// What the template is called in the picker. Unique, compared without case,
    /// so saving over an existing name replaces it rather than adding a second
    /// entry that reads the same.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The name, and nothing else. A record's synthesized <c>ToString</c> lists
    /// every property, and two of these are read-only collections — the first
    /// thing that ever interpolated this record (the bulk self-test's verdict)
    /// printed a line of runtime-collection noise where a person's pattern name
    /// should have been. The name is what a template is called everywhere, so it
    /// is what the record calls itself.
    /// </summary>
    public override string ToString() => Name;

    /// <inheritdoc cref="BookingPattern.Recorders"/>
    public IReadOnlyList<string> Recorders { get; init; } = [];

    /// <inheritdoc cref="BookingPattern.Weekdays"/>
    public IReadOnlyList<DayOfWeek> Weekdays { get; init; } = [];

    /// <inheritdoc cref="BookingPattern.Start"/>
    public TimeSpan Start { get; init; }

    /// <inheritdoc cref="BookingPattern.End"/>
    public TimeSpan End { get; init; }

    /// <inheritdoc cref="BookingPattern.SkipDates"/>
    public IReadOnlyList<DateOnly> SkipDates { get; init; } = [];

    /// <inheritdoc cref="BookingPattern.TitleFormat"/>
    public string TitleFormat { get; init; } = string.Empty;

    /// <inheritdoc cref="BookingPattern.Presenter"/>
    public string? Presenter { get; init; }

    /// <inheritdoc cref="BookingPattern.FolderHint"/>
    public string? FolderHint { get; init; }

    /// <inheritdoc cref="BookingPattern.IsBroadcast"/>
    public bool IsBroadcast { get; init; }

    /// <inheritdoc cref="BookingPattern.AllowOvernight"/>
    public bool AllowOvernight { get; init; }

    /// <summary>
    /// How many days the saved range covered, inclusive. One for the single-day
    /// pattern whose <c>From</c> and <c>To</c> were the same date.
    /// </summary>
    public int SpanDays { get; init; } = 1;

    /// <summary>
    /// The longest span a template may carry.
    ///
    /// <para>A guard rather than a preference, and it is here because of where
    /// these live: plain JSON in a folder a person is invited to edit by hand, the
    /// same folder as <c>credentials.json</c>. A mistyped span would otherwise
    /// reach <see cref="DateOnly.AddDays"/> as an overflow, or generate years of
    /// bookings from one press. The generator's own five-year ceiling catches the
    /// second case; this catches it before the file is trusted at all.</para>
    /// </summary>
    public const int MaxSpanDays = 366 * 5;

    /// <summary>Captures the patterns worth keeping, and the span, and drops the dates.</summary>
    public static BookingTemplate From(BookingPattern pattern, string name) => new()
    {
        Name = name.Trim(),
        Recorders = [.. pattern.Recorders],
        Weekdays = [.. pattern.Weekdays],
        Start = pattern.Start,
        End = pattern.End,
        SkipDates = [.. pattern.SkipDates],
        TitleFormat = pattern.TitleFormat,
        Presenter = pattern.Presenter,
        FolderHint = pattern.FolderHint,
        IsBroadcast = pattern.IsBroadcast,
        AllowOvernight = pattern.AllowOvernight,
        SpanDays = Math.Max(1, pattern.To.DayNumber - pattern.From.DayNumber + 1),
    };

    /// <summary>
    /// The pattern this template describes, starting on <paramref name="from"/>.
    ///
    /// <para>The caller names the first day rather than this guessing at one. A
    /// template has no opinion about which term it is being used for, and a guess
    /// — today, the next matching weekday, the start of the month — would be wrong
    /// often enough to be worth not making.</para>
    /// </summary>
    public BookingPattern ToPattern(DateOnly from)
    {
        Validate();

        return new BookingPattern
        {
            Recorders = [.. Recorders],
            From = from,
            To = from.AddDays(SpanDays - 1),
            Weekdays = [.. Weekdays],
            Start = Start,
            End = End,
            SkipDates = [.. SkipDates],
            TitleFormat = TitleFormat,
            Presenter = Presenter,
            FolderHint = FolderHint,
            IsBroadcast = IsBroadcast,
            AllowOvernight = AllowOvernight,
        };
    }

    /// <summary>
    /// Refuses a template that cannot be turned back into a pattern, naming which
    /// one and why. Called on the way in and on the way out, because the file is
    /// editable by hand and both directions can meet a value nobody meant.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("A saved template needs a name.");

        if (SpanDays < 1 || SpanDays > MaxSpanDays)
        {
            throw new InvalidOperationException(
                $"Template \"{Name}\" spans {SpanDays} day(s), which is outside the "
                + $"1 to {MaxSpanDays} a template may carry.");
        }

        // Both times have to be times of day. The form writes and reads them as
        // "2:30 PM", and the one place that turns a TimeSpan into a clock reading
        // refuses anything outside a day — so a hand-edited "1.00:00:00" would
        // otherwise throw out of a command, and a throw out of a command reaches
        // the dispatcher and closes the app. Caught here instead, where the message
        // can name the template, the field and the number.
        foreach (var (field, time) in new[] { ("start", Start), ("end", End) })
        {
            if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            {
                throw new InvalidOperationException(
                    $"Template \"{Name}\" has a {field} of {time}, which is not a time of day. "
                    + "Both times are the room's clock, so they run from 00:00 up to but not "
                    + "including 24:00 — an end earlier than the start is how an overnight "
                    + "booking is written.");
            }
        }
    }
}
