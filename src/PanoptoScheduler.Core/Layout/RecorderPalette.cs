namespace PanoptoScheduler.Core.Layout;

/// <summary>
/// A colour, as three bytes.
///
/// <para>Deliberately not <c>System.Windows.Media.Color</c>: this type lives in
/// Core, which has no WPF reference, and it is what lets the assignment below be
/// tested without a UI thread. The app converts on the way to a brush, which is
/// four lines in one place.</para>
/// </summary>
public readonly record struct PaletteColour(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Gives each remote recorder its own colour, so a block's stripe says which room
/// it is without reading the label.
///
/// <para><b>Assignment is by name, not by first sight.</b> The colour used to be
/// handed out in the order recorders happened to appear in the week's session
/// list, which is the order Panopto returns them — so the same week could come
/// back reordered, and every room on screen would change colour between one
/// refresh and the next. Sorting the names first makes the assignment a function
/// of the set of rooms rather than of the order they arrived in, so a refresh
/// that finds the same rooms draws the same colours.</para>
///
/// <para>What it does not survive is the set changing: a recorder scheduled for
/// the first time this week, sorting before the others, shifts them along. That
/// is a real limitation and it is the reason the calendar carries a legend: the
/// stripe is a convenience, and the answer to "which room is this" has to be
/// readable somewhere that is not a colour.</para>
///
/// <para><b>Twelve, not six.</b> Six wrapped on the seventh recorder, so a
/// fourteen-room building had rooms sharing stripes permanently — and the two
/// that collided were whichever happened to sort first and seventh, which is
/// nobody's idea of a pair. Twelve puts the wrap past a single week's plausible
/// room count.</para>
///
/// <para><b>The order of the list is the point.</b> Hues alternate around the
/// wheel — roughly 180° between each entry and the next — so two rooms drawn
/// side by side are never two neighbouring hues. The old list put a green at
/// index 1 and a red at index 2, which is the one pair that red-green colour
/// blindness cannot separate at all; those two are now as far apart as twelve
/// entries allow.</para>
/// </summary>
public static class RecorderPalette
{
    /// <summary>
    /// The stripes, in assignment order. Hues are interleaved rather than walked
    /// in sequence, so consecutive entries are far apart in hue — including the
    /// last back to the first, which is the pair that collides when the list
    /// wraps.
    /// </summary>
    public static IReadOnlyList<PaletteColour> Colours { get; } =
    [
        new(0x2F, 0x6F, 0xED),   // blue
        new(0xE0, 0x7A, 0x1E),   // orange
        new(0x7A, 0x4F, 0xD1),   // violet
        new(0x6E, 0x9B, 0x1F),   // yellow-green
        new(0xC9, 0x40, 0x7F),   // magenta
        new(0x10, 0x99, 0x6B),   // spring
        new(0xCE, 0x3B, 0x32),   // red
        new(0x0E, 0x8C, 0x9E),   // teal
        new(0x4B, 0x3F, 0xA8),   // indigo
        new(0x9A, 0x8B, 0x12),   // olive
        new(0x9A, 0x3F, 0xC4),   // purple
        new(0x2E, 0x7D, 0x32),   // green
    ];

    /// <summary>How many distinct recorders can be told apart before the list repeats.</summary>
    public static int Size => Colours.Count;

    /// <summary>
    /// The colour for every distinct name given, keyed case-insensitively the way
    /// the rest of the app compares recorder names.
    ///
    /// <para>Takes the whole set at once rather than one name at a time, because
    /// a colour that depends on the position in a sorted list cannot be decided
    /// until the list is known. The caller rebuilds a week at a time, so it has
    /// the whole set anyway.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, PaletteColour> Assign(IEnumerable<string> recorders)
    {
        ArgumentNullException.ThrowIfNull(recorders);

        var assigned = new Dictionary<string, PaletteColour>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var name in Names(recorders))
        {
            assigned[name] = Colours[index % Size];
            index++;
        }

        return assigned;
    }

    /// <summary>
    /// The distinct names in the order the colours are handed out.
    ///
    /// <para>Public because the calendar's legend has to list the rooms in the
    /// same order the stripes were assigned, and re-deriving it from the
    /// dictionary would depend on an enumeration order that is not promised.
    /// The two stay together by being the same code.</para>
    ///
    /// <para>Takes nullable names on purpose: dropping the nulls and the blanks
    /// is the whole of what this does. Declaring the parameter non-nullable said
    /// the opposite — that callers had already filtered — and pushed the job onto
    /// each of them instead. Callers here hand it
    /// <c>PanoptoSession.RemoteRecorderName</c>, which is null for a session with
    /// no recorder assigned, and that is a real state rather than a mistake to be
    /// asserted away.</para>
    /// </summary>
    public static IReadOnlyList<string> Names(IEnumerable<string?> recorders) =>
        recorders
            // Select rather than a cast because Where cannot narrow the null
            // away: after this the compiler still believes the sequence can hold
            // nulls, and only the projection here says otherwise. OfType<string>
            // would filter a second time to the same effect, less plainly.
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            // The tiebreak is there because OrderBy is a stable sort: two names
            // that compare equal ignoring case would otherwise keep whichever
            // order they arrived in, which is the dependency this type exists to
            // remove.
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToList();
}
