using System.Globalization;
using PanoptoScheduler.Core.Layout;
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Tests;

public class CalendarLayoutTests
{
    private static PanoptoSession At(int hour, double durationHours, string recorder = "147")
        => new()
        {
            SessionID = Guid.NewGuid().ToString(),
            SessionName = $"{recorder} course at {hour}",
            Status = 1,
            RemoteRecorderName = recorder,
            StartTime = new DateTime(2026, 12, 17, hour, 5, 0),
            // Seconds — the unit the wire uses. This helper said minutes, which
            // is why no test caught a calendar drawing every block 60x too tall.
            Duration = durationHours * 3600,
        };

    [Fact]
    public void Sessions_are_ordered_by_start()
    {
        var placed = CalendarLayout.Arrange([At(15, 1), At(9, 1), At(12, 1)]);

        Assert.Equal([9, 12, 15], placed.Select(p => p.Start.Hour));
    }

    [Fact]
    public void Non_overlapping_sessions_share_a_lane()
    {
        var placed = CalendarLayout.Arrange([At(9, 1), At(11, 1), At(13, 1)]);

        Assert.All(placed, p => Assert.Equal(0, p.Lane));
        Assert.All(placed, p => Assert.Equal(1, p.LaneCount));
    }

    [Fact]
    public void Overlapping_sessions_get_separate_lanes()
    {
        // Two recorders running at once must not draw on top of each other.
        var placed = CalendarLayout.Arrange([
            At(9, 2, "147"),
            At(9, 2, "142"),
        ]);

        Assert.Equal(2, placed.Select(p => p.Lane).Distinct().Count());
        Assert.All(placed, p => Assert.Equal(2, p.LaneCount));
    }

    [Fact]
    public void Lane_is_reused_once_the_previous_session_ends()
    {
        var placed = CalendarLayout.Arrange([
            At(9, 1, "147"),    // lane 0
            At(9, 1, "142"),    // lane 1
            At(10, 1, "147"),   // lane 0 is free again
        ]);

        var last = placed.Single(p => p.Start.Hour == 10);
        Assert.Equal(0, last.Lane);
    }

    [Fact]
    public void Touching_sessions_do_not_overlap()
    {
        // 09:00-10:00 and 10:00-11:00 are adjacent, not concurrent.
        var placed = CalendarLayout.Arrange([
            new PanoptoSession
            {
                StartTime = new DateTime(2026, 12, 17, 9, 0, 0),
                Duration = 3600,
            },
            new PanoptoSession
            {
                StartTime = new DateTime(2026, 12, 17, 10, 0, 0),
                Duration = 3600,
            },
        ]);

        Assert.All(placed, p => Assert.Equal(0, p.Lane));
    }

    [Fact]
    public void Width_fractions_divide_the_column()
    {
        var placed = CalendarLayout.Arrange([At(9, 2, "147"), At(9, 2, "142")]);

        Assert.All(placed, p => Assert.Equal(0.5, p.WidthFraction, 3));
        Assert.Equal([0.0, 0.5], placed.Select(p => Math.Round(p.LeftFraction, 3)).Order());
    }

    [Fact]
    public void Sessions_without_a_start_time_are_excluded()
    {
        // Better to surface these separately than to invent a position for them.
        var placed = CalendarLayout.Arrange([
            At(9, 1),
            new PanoptoSession { SessionName = "no time", StartTime = null, ScheduledStartTime = null },
        ]);

        Assert.Single(placed);
    }

    [Fact]
    public void Missing_duration_falls_back_to_the_default()
    {
        var placed = CalendarLayout.Arrange(
            [new PanoptoSession
            {
                StartTime = new DateTime(2026, 12, 17, 9, 0, 0),
                Duration = null,
            }],
            defaultDuration: TimeSpan.FromMinutes(90));

        Assert.Equal(TimeSpan.FromMinutes(90), placed[0].Duration);
    }

    [Fact]
    public void ArrangeByDay_keeps_empty_days_in_the_grid()
    {
        var days = CalendarLayout.ArrangeByDay(
            [At(9, 1)],
            new DateOnly(2026, 12, 16),
            dayCount: 5);

        Assert.Equal(5, days.Count);
        Assert.Single(days[1].Sessions);                       // 17th has the session
        Assert.Empty(days[0].Sessions);                        // 16th is empty
        Assert.Equal(new DateOnly(2026, 12, 16), days[0].Day);
    }

    // ---- Daylight saving, and the room's own day ------------------------

    /// <summary>A session at a wall clock on a given day, in a named room zone.</summary>
    private static PanoptoSession On(string day, int hour, double durationHours)
        => new()
        {
            SessionID = Guid.NewGuid().ToString(),
            SessionName = $"{day} {hour}:00",
            Status = 1,
            StartTime = DateTime.Parse($"{day} {hour}:00", CultureInfo.InvariantCulture),
            Duration = durationHours * 3600,
        };

    /// <summary>
    /// A block ending at 03:00 and the next starting at 03:00 share a lane, even
    /// though they sit either side of the hour Toronto jumps over.
    ///
    /// <para>Measured before the fix, and this is what it caught: with instants,
    /// the 03:00 block began an hour <i>before</i> the 01:00 block ended, because
    /// the two carried different offsets, so it was given a lane of its own and
    /// the grid drew two half-width blocks where one clock-face column was
    /// wanted.</para>
    /// </summary>
    [Fact]
    public void A_block_ending_at_the_spring_forward_hour_still_touches_its_neighbour()
    {
        var placed = CalendarLayout.Arrange([
            On("2026-03-08", 1, 2),   // 01:00 - 03:00, on the EST side
            On("2026-03-08", 3, 2),   // 03:00 - 05:00, on the EDT side
        ]);

        Assert.All(placed, p => Assert.Equal(0, p.Lane));
        Assert.All(placed, p => Assert.Equal(1, p.LaneCount));
    }

    /// <summary>
    /// The same week either side of the transition lays out identically, because
    /// the grid is drawn against a clock face and the clock face is what the
    /// change is about.
    /// </summary>
    [Fact]
    public void A_week_spanning_the_transition_lays_out_like_any_other_week()
    {
        PanoptoSession[] AcrossTheChange() =>
        [
            On("2026-03-07", 9, 2), On("2026-03-07", 13, 2),
            On("2026-03-08", 9, 2), On("2026-03-08", 13, 2),
            On("2026-03-09", 9, 2),
        ];

        PanoptoSession[] AnOrdinaryWeek() =>
        [
            On("2026-03-14", 9, 2), On("2026-03-14", 13, 2),
            On("2026-03-15", 9, 2), On("2026-03-15", 13, 2),
            On("2026-03-16", 9, 2),
        ];

        var across = CalendarLayout.ArrangeByDay(AcrossTheChange(), new DateOnly(2026, 3, 2), 7);
        var ordinary = CalendarLayout.ArrangeByDay(AnOrdinaryWeek(), new DateOnly(2026, 3, 9), 7);

        Assert.Equal(
            across.SelectMany(d => d.Sessions).Select(p => (p.Start.TimeOfDay, p.Lane, p.LaneCount)),
            ordinary.SelectMany(d => d.Sessions).Select(p => (p.Start.TimeOfDay, p.Lane, p.LaneCount)));
    }

    /// <summary>
    /// A day is the room's day. A session at 09:00 belongs to the day whose date
    /// is on it, whatever any machine's zone would make of that hour.
    /// </summary>
    [Fact]
    public void A_day_is_the_rooms_day()
    {
        var days = CalendarLayout.ArrangeByDay(
            [On("2026-03-08", 9, 1), On("2026-03-08", 23, 1)],
            new DateOnly(2026, 3, 2),
            7);

        var theDay = days.Single(d => d.Day == new DateOnly(2026, 3, 8));
        Assert.Equal(2, theDay.Sessions.Count);
        Assert.All(days.Where(d => d.Day != new DateOnly(2026, 3, 8)),
            d => Assert.Empty(d.Sessions));
    }

    /// <summary>
    /// Lanes are counted within a day. Laning the whole window at once meant one
    /// busy afternoon narrowed every block in every other column.
    /// </summary>
    [Fact]
    public void A_busy_day_does_not_narrow_the_other_columns()
    {
        var days = CalendarLayout.ArrangeByDay(
            [
                On("2026-03-09", 9, 2), On("2026-03-09", 9, 2), On("2026-03-09", 9, 2),
                On("2026-03-10", 9, 2),
            ],
            new DateOnly(2026, 3, 9),
            2);

        var busy = days.Single(d => d.Day == new DateOnly(2026, 3, 9));
        var quiet = days.Single(d => d.Day == new DateOnly(2026, 3, 10));

        Assert.All(busy.Sessions, p => Assert.Equal(3, p.LaneCount));
        Assert.All(quiet.Sessions, p => Assert.Equal(1, p.LaneCount));
        Assert.Equal(1.0, quiet.Sessions[0].WidthFraction);
    }
}
