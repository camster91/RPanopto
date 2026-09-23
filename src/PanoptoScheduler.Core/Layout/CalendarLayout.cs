using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Layout;

/// <summary>
/// A session placed on the calendar grid.
///
/// <paramref name="Lane"/> and <paramref name="LaneCount"/> describe horizontal
/// placement: two recorders can record at the same time, so overlapping blocks
/// are given separate lanes and drawn side by side rather than stacked.
/// </summary>
/// <param name="Start">
/// The room's wall clock — the clock face the block is drawn against. A
/// <see cref="DateTimeOffset"/> here would be an instant, and comparing two of
/// those across a daylight-saving boundary is not the same as comparing two clock
/// faces; see <see cref="Arrange"/>.
/// </param>
public sealed record PositionedSession(
    PanoptoSession Session,
    DateTime Start,
    TimeSpan Duration,
    int Lane,
    int LaneCount)
{
    /// <summary>Fraction of the day column this block occupies.</summary>
    public double WidthFraction => 1.0 / LaneCount;

    /// <summary>Fraction of the day column where this block starts.</summary>
    public double LeftFraction => (double)Lane / LaneCount;
}

/// <summary>
/// Positions sessions for a day view.
///
/// Kept out of the UI layer so the packing behaviour can be tested directly —
/// an off-by-one here is invisible until two classes land on the same hour and
/// one silently covers the other.
/// </summary>
public static class CalendarLayout
{
    /// <summary>Used when a session reports no usable duration.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(1);

    /// <summary>Granularity a dragged block lands on.</summary>
    public static readonly TimeSpan DragSnap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Converts the vertical part of a drag into a time shift.
    ///
    /// <para>Snapped, because an unsnapped drop lands on times like 10:03, and
    /// nobody schedules a lecture at 10:03 — the operator would have to correct
    /// the time immediately after moving it, which defeats the point.</para>
    ///
    /// <para>A downward drag is a later start: the grid runs downwards from the
    /// first hour.</para>
    /// </summary>
    public static TimeSpan DragToShift(double pixelDelta, double hourHeight, TimeSpan? snap = null)
    {
        if (hourHeight <= 0) return TimeSpan.Zero;

        var step = (snap ?? DragSnap).TotalMinutes;
        if (step <= 0) return TimeSpan.Zero;

        var minutes = pixelDelta / hourHeight * 60.0;

        return TimeSpan.FromMinutes(Math.Round(minutes / step) * step);
    }

    /// <summary>
    /// Converts the horizontal part of a drag into a whole number of days.
    ///
    /// <para>Half a column counts as a move, so a block dropped near a
    /// neighbour's centre goes where the operator aimed rather than springing
    /// back.</para>
    /// </summary>
    public static int DragToDayShift(double pixelDelta, double columnWidth)
    {
        if (columnWidth <= 0) return 0;

        return (int)Math.Round(pixelDelta / columnWidth, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// How far a press may travel and still count as a click rather than a drag.
    /// </summary>
    /// <remarks>
    /// Four pixels, measured on the diagonal. A press is never perfectly still —
    /// a mouse shifts a pixel or two while the button is down, and a trackpad
    /// more — so a threshold of zero would make opening a recording depend on the
    /// operator's hand being steadier than hands are. The figure has to stay well
    /// under the drag snap for the two gestures not to overlap.
    /// </remarks>
    public static readonly double ClickSlop = 4.0;

    /// <summary>
    /// The click threshold to use against a grid whose hours are drawn
    /// <paramref name="hourHeight"/> pixels tall: <see cref="ClickSlop"/>, capped
    /// at half of one drag snap at this scale.
    ///
    /// <para>The hour is fitted to the window, and at the bottom of the fitted
    /// range — 36 pixels an hour — one five-minute snap is 3 pixels, so the fixed
    /// slop was wider than the smallest move the grid could express. A deliberate
    /// 3.5-pixel drag was then read as a click: it opened the details panel
    /// instead of moving the recording, and unlike a move that lands wrong, that
    /// misreading is invisible until the operator looks at the wrong day.</para>
    ///
    /// <para>Half a snap rather than a full one, so a press either sits inside a
    /// single snap or has crossed a boundary — there is no width of travel that
    /// counts as neither gesture. At large hour heights the cap is above the
    /// slop and the slop stands unchanged.</para>
    /// </summary>
    public static double ClickSlopAt(double hourHeight)
    {
        if (double.IsNaN(hourHeight) || hourHeight <= 0) return ClickSlop;

        var halfSnap = hourHeight * DragSnap.TotalMinutes / 60.0 / 2.0;
        return Math.Min(ClickSlop, halfSnap);
    }

    /// <summary>
    /// Whether a press that travelled <paramref name="dx"/> by
    /// <paramref name="dy"/> was a click rather than a drag.
    ///
    /// <para>This is one line of arithmetic and it is in Core because the rule it
    /// expresses is not obvious and cannot be tested where it was written: a
    /// press that opens a recording and a press that moves it start identically,
    /// and the difference only shows up as whichever one the operator did not
    /// mean. Written here, the boundary is a fact; written in an event handler it
    /// is a number somebody chose once.</para>
    ///
    /// <para>Measured as a diagonal distance rather than per-axis, so a press
    /// that wandered 3px right and 3px down — 4.24px of travel — is a drag, while
    /// neither axis alone would have said so.</para>
    /// </summary>
    /// <param name="slop">
    /// Overridable so a caller with an unusual input device can widen it. The
    /// default is <see cref="ClickSlop"/>.
    /// </param>
    public static bool IsClick(double dx, double dy, double? slop = null)
    {
        var limit = slop ?? ClickSlop;

        // A negative limit would make even a motionless press a drag, and NaN
        // would make every press a click. Neither is a threshold, so neither is
        // taken as one.
        if (double.IsNaN(limit) || limit < 0) limit = ClickSlop;

        return Math.Sqrt(dx * dx + dy * dy) <= limit;
    }

    /// <summary>
    /// Places sessions into lanes, ordered by start time.
    ///
    /// Greedy first-fit: each session takes the first lane whose previous
    /// occupant has already ended. Sessions with no usable start time are
    /// dropped — the caller surfaces those separately rather than drawing them
    /// at a fabricated position.
    ///
    /// <para><b>Everything here is the room's wall clock.</b> Ordering and the
    /// overlap test are clock-face comparisons, which is what the grid draws. This
    /// used to compare <see cref="DateTimeOffset"/> values — that is, instants —
    /// while the stamps they carried came from the machine, so a block was measured
    /// against its neighbour across a daylight-saving boundary as though the two
    /// offsets were the same. On 8 March 2026 in Toronto, a block ending at 03:00
    /// and the next starting at 03:00 touch on the clock face; as instants the
    /// second began an hour before the first ended, and was given a lane of its
    /// own. Wall clocks cannot disagree with the clock face about what the clock
    /// face says.</para>
    /// </summary>
    public static IReadOnlyList<PositionedSession> Arrange(
        IEnumerable<PanoptoSession> sessions,
        TimeSpan? defaultDuration = null)
    {
        var fallback = defaultDuration ?? DefaultDuration;

        var ordered = sessions
            .Select(s => (Session: s, Start: s.EffectiveStart))
            .Where(x => x.Start is not null)
            .Select(x => (x.Session, Start: x.Start!.Value))
            .OrderBy(x => x.Start)
            .ToList();

        var laneEnds = new List<DateTime>();
        var placed = new List<(PanoptoSession Session, DateTime Start, TimeSpan Duration, int Lane)>();

        foreach (var (session, start) in ordered)
        {
            var duration = session.EffectiveDuration ?? fallback;
            if (duration <= TimeSpan.Zero) duration = fallback;

            var end = start + duration;

            var lane = laneEnds.FindIndex(existingEnd => existingEnd <= start);
            if (lane < 0)
            {
                laneEnds.Add(end);
                lane = laneEnds.Count - 1;
            }
            else
            {
                laneEnds[lane] = end;
            }

            placed.Add((session, start, duration, lane));
        }

        var laneCount = Math.Max(1, laneEnds.Count);

        return placed
            .Select(p => new PositionedSession(p.Session, p.Start, p.Duration, p.Lane, laneCount))
            .ToList();
    }

    /// <summary>
    /// Sessions grouped into calendar days, in order, for the given window.
    /// Days with nothing scheduled are included so the grid keeps its shape.
    ///
    /// <para><b>The day is the room's day</b>, taken straight off the wall clock,
    /// which is why this needs no zone. It used to take one — defaulting to
    /// <see cref="TimeZoneInfo.Local"/> and converting through it — and that only
    /// landed on the right day because the values had been stamped with this same
    /// machine's zone on the way in, so the two conversions cancelled. Two sites
    /// agreeing by accident is not the same as one site being right, and the
    /// accident would have ended the moment either one changed.</para>
    ///
    /// <para>Each day is laned on its own. Laning the whole window at once meant a
    /// single busy afternoon narrowed every block in every other column.</para>
    /// </summary>
    public static IReadOnlyList<(DateOnly Day, IReadOnlyList<PositionedSession> Sessions)> ArrangeByDay(
        IEnumerable<PanoptoSession> sessions,
        DateOnly fromDay,
        int dayCount)
    {
        var byDay = sessions
            .Select(s => (Session: s, Start: s.EffectiveStart))
            .Where(x => x.Start is not null)
            .GroupBy(x => DateOnly.FromDateTime(x.Start!.Value))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Session).ToList());

        return Enumerable.Range(0, dayCount)
            .Select(offset =>
            {
                var day = fromDay.AddDays(offset);
                return (day, byDay.TryGetValue(day, out var found)
                    ? Arrange(found)
                    : (IReadOnlyList<PositionedSession>)[]);
            })
            .ToList();
    }
}
