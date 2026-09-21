using PanoptoScheduler.Core.Layout;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The legend as an inventory.
///
/// <para>The bug these pin: the legend was built from the week's sessions, so a
/// room with no booking that week could not appear in it — and it was read,
/// reasonably, as the list of the tenant's rooms. It looked like an inventory and
/// was a key.</para>
/// </summary>
public class RoomInventoryTests
{
    /// <summary>
    /// This week's rooms come first, and within that group they keep the order
    /// <see cref="RecorderPalette.Names"/> returns — which is sorted, and is the
    /// order <see cref="RecorderPalette.Assign"/> hands colours out in. What must
    /// not happen is the two groups interleaving: a reader scanning the row is
    /// looking for what is on screen, and finding it among 406 rooms that are not
    /// is the problem the grouping exists to solve.
    /// </summary>
    [Fact]
    public void Lists_week_rooms_before_the_rest()
    {
        var inventory = RoomInventory.Build(
            tenantRooms: ["Alpha", "Beta", "Gamma", "Delta"],
            weekRecorders: ["Gamma", "Alpha"]);

        Assert.Equal(["Alpha", "Gamma", "Beta", "Delta"], inventory.Rows.Select(r => r.Name));
        Assert.Equal([true, true, false, false], inventory.Rows.Select(r => r.IsOnScreen));
    }

    /// <summary>
    /// Index for index, the week group has to match the order the colours were
    /// handed out in, or every stripe in the bar is paired with the wrong room.
    /// </summary>
    [Fact]
    public void Keeps_week_rooms_in_the_order_colours_were_assigned_in()
    {
        string[] week = ["JMHH240", "Event Space 214", "Alpha"];

        var inventory = RoomInventory.Build(
            tenantRooms: ["Alpha", "Beta", "Event Space 214", "JMHH240"],
            weekRecorders: week);

        Assert.Equal(
            RecorderPalette.Names(week),
            inventory.Rows.Where(r => r.IsOnScreen).Select(r => r.Name));
    }

    /// <summary>
    /// A room that exists but is not booked this week is listed and carries no
    /// stripe — the distinction the row has to make visible.
    /// </summary>
    [Fact]
    public void Lists_a_room_with_no_booking_this_week()
    {
        var inventory = RoomInventory.Build(
            tenantRooms: ["JMHH240", "Event Space 214"],
            weekRecorders: ["JMHH240"]);

        var empty = Assert.Single(inventory.Rows, r => r.Name == "Event Space 214");
        Assert.False(empty.IsOnScreen);
    }

    /// <summary>
    /// An unassigned session has no recorder name and is not a room. Counting it
    /// as one is what made the old status line over-report.
    /// </summary>
    [Fact]
    public void Does_not_list_an_unassigned_session_as_a_room()
    {
        var inventory = RoomInventory.Build(
            tenantRooms: ["JMHH240"],
            weekRecorders: [null, "", "   ", "JMHH240"]);

        Assert.Equal(["JMHH240"], inventory.Rows.Select(r => r.Name));
        Assert.Equal(1, inventory.WeekRoomCount);
    }

    /// <summary>
    /// A session can name a room the tenant listing does not have — a recorder
    /// that has since been deleted, or a name that does not match. There is a
    /// booking on screen for it, so hiding it would be the same truncation this
    /// type exists to remove.
    /// </summary>
    [Fact]
    public void Keeps_a_week_room_the_tenant_listing_does_not_have()
    {
        var inventory = RoomInventory.Build(
            tenantRooms: ["JMHH240"],
            weekRecorders: ["JMHH240", "Retired Room 12"]);

        Assert.Equal(2, inventory.TotalRoomCount);
        Assert.Contains(inventory.Rows, r => r is { Name: "Retired Room 12", IsOnScreen: true });
    }

    [Fact]
    public void Counts_a_room_once_however_the_name_is_cased()
    {
        var inventory = RoomInventory.Build(
            tenantRooms: ["jmhh240", "JMHH242"],
            weekRecorders: ["JMHH240"]);

        Assert.Equal(["JMHH240", "JMHH242"], inventory.Rows.Select(r => r.Name));
        Assert.Equal(2, inventory.TotalRoomCount);
        Assert.Equal(1, inventory.WeekRoomCount);
    }

    /// <summary>
    /// The header's count is the list's own length, so it cannot say 412 above a
    /// list of 6.
    /// </summary>
    [Fact]
    public void The_summary_counts_what_the_list_holds()
    {
        var inventory = RoomInventory.Build(
            tenantRooms: ["Alpha", "Beta", "Gamma", "Delta", "Epsilon"],
            weekRecorders: ["Alpha", "Beta"]);

        Assert.Equal("2 of 5 rooms", inventory.Summary);
        Assert.Equal(inventory.Rows.Count, inventory.TotalRoomCount);
        Assert.True(inventory.HasRooms);
    }

    [Fact]
    public void An_empty_tenant_and_an_empty_week_is_an_empty_inventory()
    {
        var inventory = RoomInventory.Build(tenantRooms: [], weekRecorders: []);

        Assert.Empty(inventory.Rows);
        Assert.Equal("0 of 0 rooms", inventory.Summary);
        Assert.False(inventory.HasRooms);
    }
}
