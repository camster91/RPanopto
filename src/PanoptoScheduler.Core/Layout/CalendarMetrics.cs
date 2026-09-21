namespace PanoptoScheduler.Core.Layout;

/// <summary>
/// The grid's geometry, fitted to the window it is drawn in.
///
/// <para><b>Why this exists.</b> The column width, the hour height and the
/// header height were compile-time constants, and the calendar was therefore a
/// fixed-size drawing. Seven columns at 176px is about 1290px and sixteen hours
/// at 56px is 896px, in a window that opened at 840px tall — so maximizing the
/// window added whitespace, and the axis that actually overflowed could not use
/// the room it had. A calendar that does not fill the window is not one setting
/// away from doing so; the geometry has to be a function of the window.</para>
///
/// <para><b>In Core because it is arithmetic.</b> There is no project reference
/// from the test project to the WPF app, so anything that has to be proved rather
/// than clicked lives here — and a fit that returns a zero column, or that
/// divides by a viewport of zero on the first layout pass, is exactly the kind of
/// bug that shows up as a blank calendar on one machine.</para>
/// </summary>
/// <param name="ColumnWidth">Width of one day column, without its gap.</param>
/// <param name="HourHeight">Height of one hour row.</param>
/// <param name="ColumnGap">Gap to the right of a column, so two days are not flush.</param>
/// <param name="HeaderHeight">The row of day names above the grid.</param>
public readonly record struct CalendarMetrics(
    double ColumnWidth,
    double HourHeight,
    double ColumnGap,
    double HeaderHeight)
{
    /// <summary>
    /// The narrowest a column may be drawn.
    ///
    /// <para>A floor rather than "as small as it fits", because below this a day
    /// column cannot show a title and the grid stops being a calendar — seven
    /// unreadable slivers are worse than a horizontal scrollbar. At the floor the
    /// columns stop shrinking and the scroller takes over.</para>
    /// </summary>
    public const double MinColumnWidth = 120;

    /// <summary>
    /// The widest a column may be drawn. Past this the leftover becomes margin
    /// instead: a two-hour recording stretched across half a 4K screen is a block
    /// nobody can read a time off.
    /// </summary>
    public const double MaxColumnWidth = 260;

    /// <summary>
    /// The shortest an hour may be drawn. Below this the hour labels collide and
    /// a half-hour recording is a line.
    /// </summary>
    public const double MinHourHeight = 36;

    /// <summary>The tallest an hour may be drawn before the day stops fitting.</summary>
    public const double MaxHourHeight = 110;

    /// <summary>
    /// The width the vertical scrollbar takes from the body.
    ///
    /// <para>Subtracted before the columns are divided rather than discovered
    /// afterwards, because a column fitted to the full width sits under the
    /// scrollbar and the last day loses its right edge — the failure looks like a
    /// clipping bug and is an arithmetic one. 17 is the classic Windows
    /// scrollbar; it is a reserve, not a measurement, and being a little generous
    /// costs a few pixels and no legibility.</para>
    /// </summary>
    public const double ScrollbarReserve = 17;

    /// <summary>
    /// What the calendar looked like before it could stretch — the fixed
    /// constants it used to be drawn with, kept as the answer for a viewport that
    /// cannot be measured.
    /// </summary>
    public static readonly CalendarMetrics Default = new(176, 56, 4, 42);

    /// <summary>The horizontal distance between two day columns.</summary>
    public double ColumnPitch => ColumnWidth + ColumnGap;

    /// <summary>The height of the body: every hour row, and nothing else.</summary>
    public double BodyHeight(int hours) => HourHeight * hours;

    /// <summary>The height of the whole grid, header included.</summary>
    public double TotalHeight(int hours) => HeaderHeight + BodyHeight(hours);

    /// <summary>
    /// Fits the grid to a viewport.
    ///
    /// <para>Both axes are clamped rather than scaled freely, so the calendar
    /// stays readable at every window size: the small end stops at a floor and
    /// lets the scrollbar take the overflow, and the large end stops at a ceiling
    /// and leaves the remainder as margin.</para>
    /// </summary>
    /// <param name="viewportWidth">
    /// The width available to the columns — already net of whatever sits beside
    /// them. <see cref="ScrollbarReserve"/> is taken out of this.
    /// </param>
    /// <param name="viewportHeight">The height available to the hour rows.</param>
    /// <param name="columns">How many days are drawn.</param>
    /// <param name="hours">How many hour rows are drawn.</param>
    /// <param name="fallback">
    /// What to return when the viewport cannot be measured, or either count is
    /// zero. The first layout pass really does report zero, so this is a case that
    /// happens on every launch rather than a defensive branch.
    /// </param>
    public static CalendarMetrics Fit(
        double viewportWidth,
        double viewportHeight,
        int columns,
        int hours,
        CalendarMetrics? fallback = null)
    {
        var basis = fallback ?? Default;

        // A viewport that is zero is the normal first pass; one that is NaN
        // arrives from an unmeasured layout. Math.Clamp passes NaN through
        // unchanged, so guarding here is what keeps NaN out of the geometry —
        // and a NaN width renders as nothing at all, with no error anywhere.
        if (columns <= 0 || hours <= 0) return basis;
        if (!IsMeasured(viewportWidth) || !IsMeasured(viewportHeight)) return basis;

        var usableWidth = Math.Max(0, viewportWidth - ScrollbarReserve);

        var column = Math.Clamp(
            usableWidth / columns - basis.ColumnGap,
            MinColumnWidth,
            MaxColumnWidth);

        var hour = Math.Clamp(
            viewportHeight / hours,
            MinHourHeight,
            MaxHourHeight);

        return new CalendarMetrics(column, hour, basis.ColumnGap, basis.HeaderHeight);
    }

    private static bool IsMeasured(double value) =>
        value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
}
