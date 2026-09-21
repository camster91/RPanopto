using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// What the room's clock says, as opposed to what this machine's says.
///
/// <para>The guard these cover is the one that refuses a drag into the past. It
/// used to compare the room's wall clock against <see cref="DateTime.Now"/>, which
/// is two clock faces belonging to different places: on a workstation set to UTC it
/// refused moves up to four hours before the room's own past and allowed moves that
/// had already happened. The instant is passed in here so the boundary can be
/// pinned rather than waited for — the zone is named, never taken from the
/// machine.</para>
/// </summary>
public class RoomClockTests
{
    private static TimeZoneInfo Toronto => RoomClock.Resolve("America/Toronto");
    private static TimeZoneInfo Utc => RoomClock.Resolve("UTC");

    /// <summary>
    /// The wall clock in the room, from an instant. 14:30 UTC is 10:30 in Toronto
    /// in January and 14:30 in UTC — the two must not agree.
    /// </summary>
    [Fact]
    public void The_rooms_clock_is_not_this_machines()
    {
        var instant = new DateTime(2026, 1, 26, 14, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 1, 26, 9, 30, 0),
            WallClockAt(instant, Toronto));

        Assert.Equal(new DateTime(2026, 1, 26, 14, 30, 0),
            WallClockAt(instant, Utc));
    }

    /// <summary>The offset follows the date, not a fixed number of hours.</summary>
    [Fact]
    public void The_rooms_clock_follows_daylight_saving()
    {
        // 14:30 UTC in January is EST (-5); in July it is EDT (-4).
        Assert.Equal(new DateTime(2026, 1, 26, 9, 30, 0),
            WallClockAt(new DateTime(2026, 1, 26, 14, 30, 0, DateTimeKind.Utc), Toronto));

        Assert.Equal(new DateTime(2026, 7, 15, 10, 30, 0),
            WallClockAt(new DateTime(2026, 7, 15, 14, 30, 0, DateTimeKind.Utc), Toronto));
    }

    /// <summary>
    /// A booking that has already finished is refused — and "already" is decided
    /// in the room.
    /// </summary>
    [Fact]
    public void A_move_that_has_already_gone_by_is_refused()
    {
        var now = new DateTime(2026, 7, 15, 18, 0, 0, DateTimeKind.Utc);   // 14:00 in the room

        // Ends at 13:00, an hour before the room's now.
        Assert.True(RoomClock.WouldLandInThePast(
            new DateTime(2026, 7, 15, 13, 0, 0), Toronto, now));

        // Ends at 15:00, an hour after it.
        Assert.False(RoomClock.WouldLandInThePast(
            new DateTime(2026, 7, 15, 15, 0, 0), Toronto, now));
    }

    /// <summary>
    /// The boundary is the boundary: a block ending exactly now has gone by, and
    /// one ending a minute later has not.
    /// </summary>
    [Fact]
    public void The_boundary_is_where_the_rooms_clock_says_it_is()
    {
        var now = new DateTime(2026, 7, 15, 18, 0, 0, DateTimeKind.Utc);   // 14:00 in the room

        Assert.True(RoomClock.WouldLandInThePast(
            new DateTime(2026, 7, 15, 14, 0, 0), Toronto, now));

        Assert.False(RoomClock.WouldLandInThePast(
            new DateTime(2026, 7, 15, 14, 1, 0), Toronto, now));
    }

    /// <summary>
    /// The hour the room is in when the two clocks disagree about the date: a
    /// session late in the Toronto evening is already tomorrow in UTC, and the
    /// guard must not treat it as past because of that.
    /// </summary>
    [Fact]
    public void An_evening_session_is_not_past_just_because_utc_has_moved_on()
    {
        // 03:30 UTC on the 16th is 23:30 on the 15th in Toronto.
        var now = new DateTime(2026, 7, 16, 3, 30, 0, DateTimeKind.Utc);

        Assert.False(RoomClock.WouldLandInThePast(
            new DateTime(2026, 7, 15, 23, 45, 0), Toronto, now));
    }

    /// <summary>
    /// The current instant may arrive without a marker — a value built in a test,
    /// or read from a source that dropped it. It means UTC here either way, and
    /// must not be honoured as a local time.
    /// </summary>
    [Fact]
    public void An_unmarked_instant_is_still_read_as_utc()
        => Assert.Equal(
            RoomClock.WouldLandInThePast(new DateTime(2026, 7, 15, 13, 0, 0), Toronto,
                new DateTime(2026, 7, 15, 18, 0, 0, DateTimeKind.Utc)),
            RoomClock.WouldLandInThePast(new DateTime(2026, 7, 15, 13, 0, 0), Toronto,
                new DateTime(2026, 7, 15, 18, 0, 0)));

    /// <summary>
    /// The room's now, against the real clock. Rounded to the minute, because the
    /// two are read a moment apart and a test that demanded equality would fail
    /// whenever it ran across a minute boundary.
    /// </summary>
    [Fact]
    public void The_rooms_now_is_the_rooms_zone()
    {
        var fromClock = RoomClock.Now(Toronto);
        var fromUtc = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Toronto);

        Assert.Equal(DateTimeKind.Unspecified, fromClock.Kind);
        Assert.True(Math.Abs((fromClock - fromUtc).TotalMinutes) < 1,
            $"RoomClock.Now drifted from the same conversion: {fromClock:O} vs {fromUtc:O}");
    }

    private static DateTime WallClockAt(DateTime instant, TimeZoneInfo zone)
        => DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(instant, zone), DateTimeKind.Unspecified);
}
