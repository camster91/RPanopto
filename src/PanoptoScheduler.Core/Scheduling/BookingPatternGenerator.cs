using System.Globalization;
using System.Text.RegularExpressions;
using PanoptoScheduler.Core.Import;

namespace PanoptoScheduler.Core.Scheduling;

/// <summary>
/// Turns a <see cref="BookingPattern"/> into the rows the grid shows.
///
/// <para><b>Date-major, room-minor.</b> With the dates as the outer loop, a room
/// reads down a column in the editable grid, which is how the term is usually
/// checked — "is every Tuesday there?" is a glance down one column, and the other
/// order makes it a diagonal.</para>
///
/// <para><see cref="Generate"/> refuses to produce anything while
/// <see cref="Problems"/> reports a blocking problem. That is deliberate: a
/// preview built from an impossible pattern would be a list of rows that cannot be
/// booked, and the count on the Preview button would be a lie.</para>
/// </summary>
public static partial class BookingPatternGenerator
{
    /// <summary>
    /// The most rows a pattern may generate.
    ///
    /// <para><b>A ceiling rather than a warning,</b> because the failure it
    /// prevents is not a slow booking, it is a frozen window. A year typed as 2126
    /// against a range ending 2027 is one keystroke, and the grid that tries to
    /// draw the result never comes back.</para>
    /// </summary>
    public const int MaxRows = 2000;

    /// <summary>
    /// The longest range a pattern may span.
    ///
    /// <para>Checked before the dates are walked, so a mistyped year is answered
    /// by arithmetic rather than by enumerating a hundred thousand days to count
    /// them. Generous on purpose: a term is well under a year, and five leaves
    /// room for a multi-year pattern without letting a typo through.</para>
    /// </summary>
    public const int MaxSpanYears = 5;

    /// <summary>Tokens the title format understands.</summary>
    private static readonly string[] KnownTokens = ["room", "date", "dow", "nn", "n", "total"];

    /// <summary>The note left on a row whose room was listed twice for one date.</summary>
    private const string ListedTwice =
        "This room is listed more than once for this date, so only one recording was "
        + "generated for it.";

    /// <summary>The note left on a row that crosses midnight.</summary>
    private const string Overnight = "Runs into the following morning.";

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Everything wrong with the pattern, in the order a person would want to fix
    /// it. Empty means it can generate.
    ///
    /// <para>Returns strings rather than an error type with a line number, unlike
    /// the file reader: a line number is what makes a bad CSV row actionable, and a
    /// pattern has no lines — the thing to fix is a control on a form, and the form
    /// is on screen.</para>
    /// </summary>
    public static IReadOnlyList<string> Problems(BookingPattern pattern, TimeZoneInfo? roomZone = null)
    {
        var problems = new List<string>();

        // All-blank counts as none: a multi-select that kept an empty row would
        // otherwise report no problem at all and then generate nothing, which reads
        // as the app having quietly ignored the pattern.
        if (pattern.Recorders.Count == 0 || pattern.Recorders.All(r => r.Trim().Length == 0))
            problems.Add("No rooms are selected, so there is nothing to book.");

        if (pattern.Weekdays.Count == 0)
            problems.Add("No days of the week are ticked, so no date would be booked.");

        if (pattern.To < pattern.From)
            problems.Add($"The range ends ({pattern.To:yyyy-MM-dd}) before it starts ({pattern.From:yyyy-MM-dd}).");

        if (pattern.TitleFormat.Trim().Length == 0)
            problems.Add("The recording name is empty, so every session would be nameless.");

        if (pattern.Start == pattern.End)
            problems.Add("The start and end times are the same, so every session would be zero minutes long.");

        if (pattern.End < pattern.Start && !pattern.AllowOvernight)
        {
            problems.Add(
                $"The end time ({Clock(pattern.End)}) is before the start ({Clock(pattern.Start)}). "
                + "Tick the overnight box if that is meant to run into the next morning.");
        }

        var unknown = UnknownTokens(pattern.TitleFormat);
        if (unknown.Count > 0)
        {
            problems.Add($"The name uses {string.Join(", ", unknown.Select(t => $"{{{t}}}"))}, "
                         + $"which is not one of {string.Join(", ", KnownTokens.Select(t => $"{{{t}}}"))}.");
        }

        // The span first, so a mistyped year is answered without walking it.
        if (pattern.To >= pattern.From)
        {
            var span = pattern.To.DayNumber - pattern.From.DayNumber;

            if (span > MaxSpanYears * 366)
            {
                problems.Add($"The range spans {(span / 366.0):0.#} years. "
                             + $"Shorten it to under {MaxSpanYears}, or check the dates for a typo.");
            }
        }

        if (problems.Count > 0) return problems;

        var dates = Dates(pattern).ToList();
        var rows = (long)dates.Count * pattern.Recorders.Count;

        if (dates.Count == 0)
        {
            problems.Add($"No date between {pattern.From:yyyy-MM-dd} and {pattern.To:yyyy-MM-dd} "
                         + "falls on a ticked day of the week.");
            return problems;
        }

        if (rows > MaxRows)
        {
            problems.Add($"{dates.Count} date(s) across {pattern.Recorders.Count} room(s) is "
                         + $"{rows} recordings, over the {MaxRows} this can generate at once. "
                         + "Narrow the range or book it in parts.");
            return problems;
        }

        // The hour that does not exist, which is real twice a year in Toronto and
        // is the one fault that would otherwise reach the operator as a dozen
        // separate failures. RoomClock.ToWire throws for a wall clock the zone
        // jumps over, and this says so once, before any of it is drawn.
        if (roomZone is not null)
        {
            var landInTheGap = dates
                .Where(d => roomZone.IsInvalidTime(d.ToDateTime(TimeOnly.FromTimeSpan(pattern.Start))))
                .ToList();

            if (landInTheGap.Count > 0)
            {
                problems.Add(
                    $"{landInTheGap.Count} of {dates.Count} date(s) would start inside the hour "
                    + $"that does not exist in {roomZone.Id} — the clock jumps forward over it, "
                    + $"first on {landInTheGap[0]:yyyy-MM-dd}. Pick a start time outside that hour, "
                    + "or book those dates separately.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The rows a pattern produces, or an empty list when it has blocking
    /// problems. Warnings ride on the rows the way a file's do, so the grid shows
    /// them the same way for both sources.
    /// </summary>
    public static IReadOnlyList<ScheduleImportRow> Generate(
        BookingPattern pattern, TimeZoneInfo? roomZone = null)
    {
        if (Problems(pattern, roomZone).Count > 0) return [];

        var dates = Dates(pattern).ToList();

        var rows = new List<ScheduleImportRow>(dates.Count * pattern.Recorders.Count);

        // Per room, not per run: "Lecture 5" means the fifth Tuesday of that
        // course, not the fifth booking in the batch.
        var sequence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // The row a (date, room) pair produced, so a repeat can be folded into the
        // row that survived it. A set alone would let the repeat through silently,
        // and Panopto would then hold two recordings for one room.
        //
        // The key uppercases the room because recorder names resolve
        // case-insensitively when they are booked: "RSM 1210" and "rsm 1210" are
        // one recorder, and treating them as two would be the same double booking
        // reached by a different spelling.
        var survivors = new Dictionary<(DateOnly Date, string Room), int>();

        var line = 0;

        foreach (var date in dates)
        {
            foreach (var recorder in pattern.Recorders)
            {
                var room = recorder.Trim();
                if (room.Length == 0) continue;

                if (survivors.TryGetValue((date, room.ToUpperInvariant()), out var kept))
                {
                    // The note goes on the row that survived, because that is the
                    // row a person is looking at when they wonder where the second
                    // one went. A warning on a row that was never added is a
                    // warning nobody reads.
                    rows[kept] = rows[kept] with { Warnings = [.. rows[kept].Warnings, ListedTwice] };
                    continue;
                }

                var start = date.ToDateTime(TimeOnly.FromTimeSpan(pattern.Start));
                var end = date.ToDateTime(TimeOnly.FromTimeSpan(pattern.End));

                var warnings = new List<string>();

                if (end < start)
                {
                    end = end.AddDays(1);
                    warnings.Add(Overnight);
                }

                var n = sequence.TryGetValue(room, out var sofar) ? sofar + 1 : 1;
                sequence[room] = n;

                line++;

                survivors[(date, room.ToUpperInvariant())] = rows.Count;

                rows.Add(new ScheduleImportRow
                {
                    Line = line,
                    Title = Title(pattern.TitleFormat, room, date, n, dates.Count),
                    RecorderName = room,
                    Start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified),
                    End = DateTime.SpecifyKind(end, DateTimeKind.Unspecified),
                    Presenter = Blank(pattern.Presenter),
                    FolderHint = Blank(pattern.FolderHint),
                    IsBroadcast = pattern.IsBroadcast,
                    Warnings = warnings,
                });
            }
        }

        return rows;
    }

    /// <summary>
    /// Every date the pattern covers, in order, with the skip dates left out.
    /// </summary>
    private static IEnumerable<DateOnly> Dates(BookingPattern pattern)
    {
        for (var date = pattern.From; date <= pattern.To; date = date.AddDays(1))
        {
            if (!pattern.Weekdays.Contains(date.DayOfWeek)) continue;
            if (pattern.SkipDates.Contains(date)) continue;

            yield return date;
        }
    }

    /// <summary>
    /// Substitutes the tokens. <c>{nn}</c> is replaced before <c>{n}</c>, because
    /// the shorter token is a prefix of the longer and the other order would leave
    /// a stray <c>n</c> after every padded number.
    /// </summary>
    private static string Title(string format, string room, DateOnly date, int n, int total)
        => format
            .Replace("{room}", room, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{dow}", date.ToString("ddd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{nn}", n.ToString("D2", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{n}", n.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{total}", total.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Trim();

    /// <summary>Token names the format uses that this does not understand.</summary>
    private static IReadOnlyList<string> UnknownTokens(string format)
        => TokenPattern().Matches(format)
            .Select(m => m.Groups[1].Value)
            .Where(name => !KnownTokens.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// A clock time the way the import's own warnings spell it, so a pattern's
    /// complaint and a file's read the same.
    /// </summary>
    private static string Clock(TimeSpan time)
        => DateTime.MinValue.Add(time).ToString("h:mm tt", CultureInfo.InvariantCulture);

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
