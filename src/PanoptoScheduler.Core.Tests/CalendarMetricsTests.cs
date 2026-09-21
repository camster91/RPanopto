using PanoptoScheduler.Core.Layout;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The grid's fit, which is the arithmetic behind "the app does not expand to
/// full screen in calendar mode". A calendar that does not fill its window is a
/// fitting problem, so the fitting is what is tested.
/// </summary>
public class CalendarMetricsTests
{
    private const int Columns = 7;

    /// <summary>Seven hours, so the expected numbers below are easy to read.</summary>
    private const int Hours = 7;

    /// <summary>
    /// The reported symptom, as a fact: a window big enough for wider columns
    /// gets them. The old constants could not, which is the whole reason this
    /// type exists.
    /// </summary>
    [Fact]
    public void A_wider_window_gives_wider_columns()
    {
        var narrow = CalendarMetrics.Fit(1100, 400, Columns, Hours);
        var wide = CalendarMetrics.Fit(1900, 400, Columns, Hours);

        Assert.True(wide.ColumnWidth > narrow.ColumnWidth);
    }

    /// <summary>The other axis, which is the one that actually overflowed.</summary>
    [Fact]
    public void A_taller_window_gives_taller_hours()
    {
        var shortWindow = CalendarMetrics.Fit(1900, 400, Columns, Hours);
        var tallWindow = CalendarMetrics.Fit(1900, 700, Columns, Hours);

        Assert.True(tallWindow.HourHeight > shortWindow.HourHeight);
    }

    /// <summary>
    /// The reserve is taken out before the columns are divided, not discovered
    /// afterwards. Stated as arithmetic rather than as a range, because the
    /// difference between subtracting it and not is one column's worth of
    /// clipping and reads as a rendering bug.
    /// </summary>
    [Fact]
    public void The_scrollbar_is_taken_out_of_the_width_before_the_columns_are_divided()
    {
        const double viewport = 1000;

        var metrics = CalendarMetrics.Fit(viewport, 400, Columns, Hours);

        var expected = (viewport - CalendarMetrics.ScrollbarReserve) / Columns - metrics.ColumnGap;

        Assert.Equal(expected, metrics.ColumnWidth, 6);
    }

    /// <summary>
    /// A window too small to be readable stops at the floor and lets the
    /// scrollbar take the overflow, rather than drawing seven slivers.
    /// </summary>
    [Fact]
    public void Too_small_a_window_stops_at_the_floor_rather_than_a_sliver()
    {
        var metrics = CalendarMetrics.Fit(300, 120, Columns, Hours);

        Assert.Equal(CalendarMetrics.MinColumnWidth, metrics.ColumnWidth);
        Assert.Equal(CalendarMetrics.MinHourHeight, metrics.HourHeight);
    }

    /// <summary>
    /// Very large and the geometry stops growing: the leftover becomes margin.
    /// A stretched hour is unreadable in a different way from a squashed one.
    /// </summary>
    [Fact]
    public void A_very_large_window_stops_at_the_ceiling()
    {
        var metrics = CalendarMetrics.Fit(6000, 4000, Columns, Hours);

        Assert.Equal(CalendarMetrics.MaxColumnWidth, metrics.ColumnWidth);
        Assert.Equal(CalendarMetrics.MaxHourHeight, metrics.HourHeight);
    }

    /// <summary>
    /// The first layout pass reports a viewport of zero, so this happens on every
    /// launch rather than being a defensive branch nobody reaches.
    /// </summary>
    [Fact]
    public void An_unmeasured_viewport_falls_back_rather_than_returning_nothing()
    {
        foreach (var (width, height) in new[]
                 {
                     (0.0, 0.0), (0.0, 400.0), (1000.0, 0.0),
                     (double.NaN, 400.0), (1000.0, double.NaN),
                     (double.PositiveInfinity, 400.0),
                 })
        {
            var metrics = CalendarMetrics.Fit(width, height, Columns, Hours);

            Assert.Equal(CalendarMetrics.Default, metrics);
        }
    }

    /// <summary>
    /// Whatever the window, nothing comes back zero or negative. A zero width
    /// renders as nothing at all and reports no error, which is how a blank
    /// calendar reaches an operator.
    /// </summary>
    [Fact]
    public void No_viewport_and_no_count_produces_a_degenerate_size()
    {
        for (var width = 0.0; width < 3000; width += 137)
        {
            for (var height = 0.0; height < 2000; height += 211)
            {
                var metrics = CalendarMetrics.Fit(width, height, Columns, Hours);

                Assert.True(metrics.ColumnWidth > 0, $"width {width}, height {height}");
                Assert.True(metrics.HourHeight > 0, $"width {width}, height {height}");
                Assert.True(metrics.ColumnGap >= 0);
                Assert.True(metrics.HeaderHeight > 0);
            }
        }
    }

    /// <summary>A count of zero days or hours is a viewport that cannot be used.</summary>
    [Fact]
    public void No_columns_or_no_hours_falls_back()
    {
        Assert.Equal(CalendarMetrics.Default, CalendarMetrics.Fit(1000, 400, 0, Hours));
        Assert.Equal(CalendarMetrics.Default, CalendarMetrics.Fit(1000, 400, Columns, 0));
    }

    /// <summary>
    /// The pitch is what a drag divides by, so it must be the column plus its
    /// gap and must disagree with the plain column width — a pitch that equalled
    /// the width would put a dropped block a gap short of where it was aimed.
    /// </summary>
    [Fact]
    public void The_pitch_is_the_column_plus_its_gap()
    {
        var metrics = CalendarMetrics.Fit(1000, 400, Columns, Hours);

        Assert.Equal(metrics.ColumnWidth + metrics.ColumnGap, metrics.ColumnPitch);
        Assert.NotEqual(metrics.ColumnWidth, metrics.ColumnPitch);
    }

    /// <summary>
    /// The body excludes the header and the total includes it, because the two
    /// are used against different things: the body against the scroller that
    /// holds the hour rows, the total against the window they sit in.
    /// </summary>
    [Fact]
    public void The_body_excludes_the_header_and_the_total_includes_it()
    {
        var metrics = CalendarMetrics.Fit(1000, 400, Columns, 7);

        Assert.Equal(metrics.HourHeight * 7, metrics.BodyHeight(7));
        Assert.Equal(metrics.BodyHeight(7) + metrics.HeaderHeight, metrics.TotalHeight(7));
    }

    /// <summary>
    /// A fallback can be supplied, and the fit keeps its gap and header rather
    /// than inventing new ones — so an app with its own header height does not get
    /// a different one back.
    /// </summary>
    [Fact]
    public void A_supplied_fallback_is_kept_and_only_the_fitted_parts_change()
    {
        var basis = new CalendarMetrics(150, 48, 6, 60);

        Assert.Equal(basis, CalendarMetrics.Fit(0, 0, Columns, Hours, basis));

        var fitted = CalendarMetrics.Fit(1600, 900, Columns, Hours, basis);

        Assert.Equal(6, fitted.ColumnGap);
        Assert.Equal(60, fitted.HeaderHeight);
        Assert.NotEqual(basis.ColumnWidth, fitted.ColumnWidth);
    }
}
