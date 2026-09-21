using PanoptoScheduler.Core.Layout;
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The two halves of drag-to-reschedule: turning a drag in pixels into a shift,
/// and recording an accepted move without re-reading the schedule.
/// </summary>
public class RescheduleTests
{
    private const double HourHeight = 56;
    private const double ColumnPitch = 180;

    // ---- Pixels to time ------------------------------------------------

    [Fact]
    public void A_drag_of_one_row_is_one_hour()
    {
        Assert.Equal(TimeSpan.FromHours(1), CalendarLayout.DragToShift(HourHeight, HourHeight));
    }

    [Fact]
    public void A_drag_upwards_is_an_earlier_start()
    {
        Assert.Equal(TimeSpan.FromMinutes(-60), CalendarLayout.DragToShift(-HourHeight, HourHeight));
    }

    [Fact]
    public void A_click_with_no_movement_is_no_shift()
    {
        Assert.Equal(TimeSpan.Zero, CalendarLayout.DragToShift(0, HourHeight));
    }

    /// <summary>
    /// The point of snapping: 30px is 32 minutes, which is not a time anyone
    /// meant to choose.
    /// </summary>
    [Fact]
    public void A_drag_lands_on_a_five_minute_step()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), CalendarLayout.DragToShift(30, HourHeight));
    }

    [Fact]
    public void A_drag_too_small_to_round_moves_nothing()
    {
        Assert.Equal(TimeSpan.Zero, CalendarLayout.DragToShift(2, HourHeight));
    }

    /// <summary>
    /// Only zero and negative are degenerate. A tiny positive height is a real
    /// scale — an absurd one, where a 40px drag is 40 hours — but dividing by it
    /// is well defined, so it must not be silently swallowed.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_degenerate_row_height_never_divides_by_zero(double hourHeight)
    {
        Assert.Equal(TimeSpan.Zero, CalendarLayout.DragToShift(40, hourHeight));
    }

    [Fact]
    public void The_snap_can_be_overridden()
    {
        // 30px is 32 minutes; on a 15-minute grid that rounds to half an hour.
        Assert.Equal(TimeSpan.FromMinutes(30),
            CalendarLayout.DragToShift(30, HourHeight, TimeSpan.FromMinutes(15)));
    }

    // ---- Pixels to days ------------------------------------------------

    [Fact]
    public void A_drag_of_one_column_is_one_day()
    {
        Assert.Equal(1, CalendarLayout.DragToDayShift(ColumnPitch, ColumnPitch));
    }

    [Fact]
    public void A_drag_of_half_a_column_counts_as_a_move()
    {
        Assert.Equal(1, CalendarLayout.DragToDayShift(ColumnPitch / 2, ColumnPitch));
    }

    [Fact]
    public void A_nudge_inside_a_column_is_not_a_move()
    {
        Assert.Equal(0, CalendarLayout.DragToDayShift(20, ColumnPitch));
    }

    [Fact]
    public void A_drag_leftwards_is_an_earlier_day()
    {
        Assert.Equal(-2, CalendarLayout.DragToDayShift(-2 * ColumnPitch, ColumnPitch));
    }

    [Fact]
    public void A_degenerate_column_width_never_divides_by_zero()
    {
        Assert.Equal(0, CalendarLayout.DragToDayShift(40, 0));
    }

    // ---- Click or drag --------------------------------------------------

    /// <summary>
    /// A press that does not move is the gesture for opening a recording's
    /// details. It has to stay a click at the far end of the threshold, because
    /// the alternative is that opening a block depends on how still the
    /// operator's hand happened to be.
    /// </summary>
    [Fact]
    public void A_press_that_did_not_move_is_a_click()
    {
        Assert.True(CalendarLayout.IsClick(0, 0));
        Assert.True(CalendarLayout.IsClick(CalendarLayout.ClickSlop, 0));
        Assert.True(CalendarLayout.IsClick(0, CalendarLayout.ClickSlop));
    }

    /// <summary>
    /// Travel is measured as a distance, not per axis: a press that wandered
    /// within the limit on both axes has moved further than the limit, and
    /// treating it as a click would open a recording the operator was dragging.
    /// </summary>
    [Fact]
    public void A_press_that_moved_on_both_axes_is_judged_on_the_diagonal()
    {
        // 3px each way is 4.24px of travel — inside the limit on either axis,
        // outside it together.
        var diagonal = CalendarLayout.ClickSlop * 0.75;

        Assert.True(CalendarLayout.IsClick(diagonal, 0));
        Assert.False(CalendarLayout.IsClick(diagonal, diagonal));
    }

    [Fact]
    public void A_press_that_moved_past_the_threshold_is_a_drag()
    {
        Assert.False(CalendarLayout.IsClick(CalendarLayout.ClickSlop + 0.5, 0));
        Assert.False(CalendarLayout.IsClick(0, -(CalendarLayout.ClickSlop + 0.5)));
    }

    /// <summary>
    /// The threshold has to be small against the grid it is measured on, or the
    /// two gestures swap places: a press the operator means as a drag reads as a
    /// click. Stated in pixels against a column, because that is the unit the
    /// threshold is in — comparing it to the drag snap would be comparing pixels
    /// to minutes and would pass for the wrong reason.
    /// </summary>
    [Fact]
    public void The_click_threshold_is_small_next_to_the_grid()
    {
        Assert.True(CalendarLayout.ClickSlop < ColumnPitch / 10);

        // Travel inside the threshold cannot have changed the day, so a click can
        // never be mistaken for a cross-day move.
        Assert.Equal(0, CalendarLayout.DragToDayShift(CalendarLayout.ClickSlop, ColumnPitch));
    }

    /// <summary>
    /// A caller may widen the threshold for a trackpad, and no caller may hand it
    /// a value that is not a threshold — a negative or NaN limit would otherwise
    /// turn every press into a drag, or every drag into a press.
    /// </summary>
    [Fact]
    public void A_nonsense_threshold_falls_back_rather_than_inverting_the_rule()
    {
        Assert.False(CalendarLayout.IsClick(100, 0, slop: -1));
        Assert.True(CalendarLayout.IsClick(0, 0, slop: double.NaN));
        Assert.True(CalendarLayout.IsClick(20, 0, slop: 25));
    }

    // ---- Recording an accepted move -------------------------------------

    /// <summary>
    /// A session whose time came from StartTime must be updated there. Setting
    /// ScheduledStartTime instead would leave EffectiveStart reading the old
    /// value and the block would not move.
    /// </summary>
    [Fact]
    public void An_accepted_move_updates_the_field_the_calendar_reads()
    {
        var session = new PanoptoSession
        {
            StartTime = new DateTime(2026, 12, 17, 10, 0, 0),
            Duration = 3600,
        };

        session.ApplyReschedule(
            new DateTime(2026, 12, 17, 14, 30, 0), TimeSpan.FromMinutes(90));

        Assert.Equal(new DateTime(2026, 12, 17, 14, 30, 0), session.EffectiveStart!.Value);
        Assert.Equal(TimeSpan.FromMinutes(90), session.EffectiveDuration);
    }

    /// <summary>
    /// When Panopto does supply the scheduled pair, that pair is what the
    /// duration is derived from — so both halves have to move together.
    /// </summary>
    [Fact]
    public void An_accepted_move_updates_a_scheduled_pair_together()
    {
        var session = new PanoptoSession
        {
            ScheduledStartTime = new DateTime(2026, 12, 17, 10, 0, 0),
            ScheduledEndTime = new DateTime(2026, 12, 17, 11, 0, 0),
            // Deliberately different, to prove the scheduled pair wins.
            Duration = 900,
        };

        session.ApplyReschedule(
            new DateTime(2026, 12, 17, 16, 0, 0), TimeSpan.FromMinutes(45));

        Assert.Equal(new DateTime(2026, 12, 17, 16, 0, 0), session.EffectiveStart!.Value);
        Assert.Equal(TimeSpan.FromMinutes(45), session.EffectiveDuration);
        Assert.Equal(900d, session.Duration);
    }

    [Fact]
    public void A_moved_session_keeps_a_reliable_time()
    {
        var session = new PanoptoSession
        {
            StartTime = new DateTime(2026, 12, 17, 10, 0, 0),
            Duration = 3600,
        };

        session.ApplyReschedule(
            new DateTime(2026, 12, 17, 11, 0, 0), TimeSpan.FromHours(1));

        Assert.True(session.HasReliableTime);
    }
}
