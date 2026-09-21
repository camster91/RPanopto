namespace PanoptoScheduler.Core.Layout;

/// <summary>One line in the room legend.</summary>
public sealed record RoomLegendRow
{
    /// <summary>The room's name, as Panopto reports it.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// True when this room has a session in the week on screen, so it carries a
    /// stripe of its own colour.
    ///
    /// <para>False for a room that exists in the tenant but is not booked this
    /// week. It is still listed — the room exists, and "which rooms are there" is
    /// a different question from "what is on screen" — and it carries no stripe,
    /// because the stripe means <i>you can see this right now</i> and the legend
    /// is where that distinction has to be visible.</para>
    /// </summary>
    public required bool IsOnScreen { get; init; }
}

/// <summary>
/// The legend, told as an inventory rather than as a key.
///
/// <para><b>Why this exists.</b> The legend row at the top of the calendar used to
/// be built from the sessions loaded for the displayed week, so a room with no
/// booking that week could not appear in it at all — and it was read, reasonably,
/// as a list of the tenant's rooms. It was a key for what was on screen and looked
/// like an inventory, and no wording fixes that; the list has to actually be the
/// inventory.</para>
///
/// <para><b>Week rooms keep their assigned colour and their order.</b> The first
/// rows come from <see cref="RecorderPalette.Names"/> over the week's sessions
/// verbatim, because that is the order <see cref="RecorderPalette.Assign"/> hands
/// out colours in — index for index, row <c>i</c>'s stripe is the colour the
/// palette gave row <c>i</c>. Re-ordering that group, or interleaving the rest
/// into it, would silently pair every stripe with the wrong room.</para>
///
/// <para>Colours are deliberately still assigned over the <i>week's</i> set: on a
/// 412-room tenant, assigning over every room would give the six rooms on screen
/// indices far enough apart to stop being distinguishable, which is the whole
/// thing the palette is for.</para>
///
/// <para>A room named by a session but absent from the tenant listing — a recorder
/// that has since been deleted, or a name that does not match — still appears, in
/// the week-first group, because there is a booking on screen for it. Hiding it
/// would be the same truncation this type was written to remove.</para>
/// </summary>
public sealed record RoomInventory
{
    public required IReadOnlyList<RoomLegendRow> Rows { get; init; }

    /// <summary>Rooms with at least one booking in the week on screen.</summary>
    public int WeekRoomCount => Rows.Count(row => row.IsOnScreen);

    /// <summary>
    /// Every room in the list.
    ///
    /// <para>Derived rather than stored, so the header cannot disagree with the
    /// list beneath it. A count carried separately is a count that eventually
    /// says 412 above a list of 6.</para>
    /// </summary>
    public int TotalRoomCount => Rows.Count;

    public bool HasRooms => Rows.Count > 0;

    /// <summary>
    /// "6 of 412 rooms" — the header's count, so the row can never be read as
    /// complete when it is not. The App prefixes "This week — ", which is where
    /// the tense belongs.
    /// </summary>
    public string Summary => $"{WeekRoomCount} of {TotalRoomCount} rooms";

    /// <summary>
    /// Builds the legend from the tenant's rooms and the week's sessions.
    /// </summary>
    /// <param name="tenantRooms">
    /// Every room the tenant reports — <c>ListRecordersAsync</c>. Nulls and blanks
    /// are dropped by <see cref="RecorderPalette.Names"/>, which is also what keeps
    /// an unassigned session from being listed as a room.
    /// </param>
    /// <param name="weekRecorders">
    /// The recorder name on each session in the week on screen. Nulls and blanks
    /// are the unassigned ones and are not rooms.
    /// </param>
    public static RoomInventory Build(
        IEnumerable<string?> tenantRooms,
        IEnumerable<string?> weekRecorders)
    {
        ArgumentNullException.ThrowIfNull(tenantRooms);
        ArgumentNullException.ThrowIfNull(weekRecorders);

        // Palette order, not sorted order: index for index this is what Assign
        // handed out, so the stripe for row i is the colour of row i.
        var onScreen = RecorderPalette.Names(weekRecorders);
        var claimed = new HashSet<string>(onScreen, StringComparer.OrdinalIgnoreCase);

        var rows = new List<RoomLegendRow>(onScreen.Count);

        foreach (var name in onScreen)
        {
            rows.Add(new RoomLegendRow { Name = name, IsOnScreen = true });
        }

        foreach (var name in RecorderPalette.Names(tenantRooms))
        {
            // Add returns false for one already claimed by the week group, so this
            // both filters and de-duplicates without a second lookup.
            if (claimed.Add(name))
            {
                rows.Add(new RoomLegendRow { Name = name, IsOnScreen = false });
            }
        }

        return new RoomInventory { Rows = rows };
    }
}
