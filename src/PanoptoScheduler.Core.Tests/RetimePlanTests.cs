using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The arithmetic a bulk retime does before anything is written.
///
/// <para>These are the ways a bulk retime goes quietly wrong: a shift that drops a
/// recording's length, a "set the time" that stacks a week onto one day, a row with
/// no reported time that disappears between the selection and the report. None of
/// them is visible in a per-row test of the write, and all of them are visible
/// here.</para>
/// </summary>
public class RetimePlanTests
{
    private static readonly Guid One = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Two = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Three = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static SessionTarget At(Guid id, string name, DateTime start, double minutes, string? description = null)
        => new(id, name, start, start.AddMinutes(minutes), description);

    private static SessionTarget Timeless(Guid id, string name)
        => new(id, name, null, null, null);

    private static SessionTarget NoLength(Guid id, string name, DateTime start)
        => new(id, name, start, null, null);

    private static readonly DateTime Monday = new(2026, 5, 4, 10, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Tuesday = new(2026, 5, 5, 14, 0, 0, DateTimeKind.Unspecified);

    // ---- Shift ----------------------------------------------------------

    [Fact]
    public void A_shift_moves_both_ends_and_keeps_the_length()
    {
        var plan = RetimePlan.Shift([At(One, "A", Monday, 90)], TimeSpan.FromMinutes(60));

        var slot = Assert.Single(plan.Entries).Slot;

        Assert.NotNull(slot);
        Assert.Equal(new DateTime(2026, 5, 4, 11, 0, 0), slot.Start);
        Assert.Equal(new DateTime(2026, 5, 4, 12, 30, 0), slot.End);

        // The length is the thing a wrong implementation loses, and it is the thing
        // the operator can see on the calendar, so it is asserted rather than
        // inferred from the two times above.
        Assert.Equal(TimeSpan.FromMinutes(90), slot.End - slot.Start);
    }

    [Fact]
    public void A_negative_shift_moves_everything_earlier()
    {
        var plan = RetimePlan.Shift([At(One, "A", Monday, 60)], TimeSpan.FromMinutes(-30));

        Assert.Equal(new DateTime(2026, 5, 4, 9, 30, 0), Assert.Single(plan.Entries).Slot!.Start);
    }

    /// <summary>
    /// A shift near midnight lands on the next day, which is ordinary arithmetic on
    /// a wall clock. Clamping it back to 23:59 would silently shorten a late
    /// recording instead, and the operator would find out from the room.
    /// </summary>
    [Fact]
    public void A_shift_can_carry_a_recording_onto_the_next_day()
    {
        var plan = RetimePlan.Shift(
            [At(One, "A", new DateTime(2026, 5, 4, 23, 30, 0), 15)],
            TimeSpan.FromMinutes(60));

        var slot = Assert.Single(plan.Entries).Slot;

        Assert.Equal(new DateTime(2026, 5, 5, 0, 30, 0), slot!.Start);
        Assert.Equal(new DateTime(2026, 5, 5, 0, 45, 0), slot.End);
    }

    /// <summary>
    /// A zero shift would otherwise preview every row as "would be retimed" to
    /// exactly where it already is, and applying it would spend one request per
    /// session writing the times back unchanged.
    /// </summary>
    [Fact]
    public void A_zero_shift_is_refused_rather_than_written_back()
    {
        var plan = RetimePlan.Shift([At(One, "A", Monday, 60)], TimeSpan.Zero);

        Assert.True(plan.IsEmpty);
        Assert.Contains("zero", Assert.Single(plan.Entries).Refusal);
    }

    // ---- Rows that cannot be placed --------------------------------------

    /// <summary>
    /// The two missing values are told apart, because they send the reader to look
    /// at different things: no start is a session the listing has no date for, no
    /// length is a session with a date and no duration.
    /// </summary>
    [Fact]
    public void A_recording_with_no_reported_start_is_refused_by_name()
    {
        var plan = RetimePlan.Shift([Timeless(One, "Mystery")], TimeSpan.FromMinutes(30));

        var entry = Assert.Single(plan.Entries);

        Assert.False(entry.CanMove);
        Assert.Equal("Mystery", entry.Target.CurrentName);
        Assert.Contains("No start time", entry.Refusal);
    }

    [Fact]
    public void A_recording_with_no_reported_length_is_refused_differently()
    {
        var plan = RetimePlan.Shift([NoLength(One, "Half a session", Monday)], TimeSpan.FromMinutes(30));

        var entry = Assert.Single(plan.Entries);

        Assert.False(entry.CanMove);
        Assert.Contains("No length", entry.Refusal);
    }

    /// <summary>
    /// The row the operator cannot see is the one that vanishes. A refused row keeps
    /// its place in the plan, so the report has an entry for every row of the
    /// selection and none of them has to be matched up by name.
    /// </summary>
    [Fact]
    public void A_refused_recording_keeps_its_place_in_the_plan()
    {
        var plan = RetimePlan.Shift(
            [At(One, "A", Monday, 60), Timeless(Two, "Mystery"), At(Three, "C", Monday, 60)],
            TimeSpan.FromMinutes(30));

        Assert.Equal(3, plan.Entries.Count);
        Assert.Equal(2, plan.Movable);
        Assert.Equal(1, plan.Refused);

        Assert.True(plan.Entries[0].CanMove);
        Assert.False(plan.Entries[1].CanMove);
        Assert.True(plan.Entries[2].CanMove);

        Assert.Equal(Two, plan.Entries[1].Target.Id);
    }

    // ---- Setting a time of day -------------------------------------------

    /// <summary>
    /// Each recording keeps its own date. Reading a date from anywhere else — today,
    /// or the first row — would stack a term's selection onto a single day, which
    /// for a week of bookings is every one of them in one room at once.
    /// </summary>
    [Fact]
    public void Setting_a_time_of_day_keeps_each_recording_on_its_own_day()
    {
        var plan = RetimePlan.SetStartTimeOfDay(
            [At(One, "Monday", Monday, 60), At(Two, "Tuesday", Tuesday, 60)],
            TimeSpan.FromHours(9));

        Assert.Equal(new DateTime(2026, 5, 4, 9, 0, 0), plan.Entries[0].Slot!.Start);
        Assert.Equal(new DateTime(2026, 5, 5, 9, 0, 0), plan.Entries[1].Slot!.Start);
    }

    [Fact]
    public void Setting_a_time_of_day_keeps_each_length()
    {
        var plan = RetimePlan.SetStartTimeOfDay([At(One, "A", Monday, 90)], TimeSpan.FromHours(9));

        var slot = Assert.Single(plan.Entries).Slot;

        Assert.Equal(new DateTime(2026, 5, 4, 9, 0, 0), slot!.Start);
        Assert.Equal(TimeSpan.FromMinutes(90), slot.End - slot.Start);
    }

    /// <summary>
    /// Thrown rather than wrapped. A value that arrived as 25 hours would otherwise
    /// become 01:00 for every selected recording — a move nobody asked for, which
    /// reads on screen as a perfectly plausible one.
    /// </summary>
    [Fact]
    public void A_time_of_day_past_midnight_is_refused_rather_than_wrapped()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetimePlan.SetStartTimeOfDay([At(One, "A", Monday, 60)], TimeSpan.FromHours(25)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetimePlan.SetStartTimeOfDay([At(One, "A", Monday, 60)], TimeSpan.FromMinutes(-1)));
    }

    /// <summary>
    /// Setting a time needs the length, so a recording whose end is not after its
    /// start has nothing to keep — and saying that is better than moving it to a
    /// zero-length slot the server would reject without naming the row.
    /// </summary>
    [Fact]
    public void A_recording_whose_end_is_not_after_its_start_has_no_length_to_keep()
    {
        var plan = RetimePlan.SetStartTimeOfDay(
            [new SessionTarget(One, "A", Monday, Monday, null)],
            TimeSpan.FromHours(9));

        var entry = Assert.Single(plan.Entries);

        Assert.False(entry.CanMove);
        Assert.Contains("not after the start", entry.Refusal);
    }

    // ---- Wall clocks -----------------------------------------------------

    /// <summary>
    /// A planned slot is a clock face, never an instant.
    ///
    /// <para>Feeding a value that has picked up this machine's kind is the realistic
    /// version of the bug: the room's zone is applied by the recorder on the way out,
    /// so a local-kind value would be converted a second time and land an hour or
    /// five away from where it was dropped. Relabelled at the single point every
    /// planned slot passes through.</para>
    /// </summary>
    [Fact]
    public void Planned_times_are_wall_clocks_even_when_the_input_was_not()
    {
        var local = DateTime.SpecifyKind(Monday, DateTimeKind.Local);

        var plan = RetimePlan.Shift([At(One, "A", local, 60)], TimeSpan.FromMinutes(30));

        var slot = Assert.Single(plan.Entries).Slot;

        Assert.Equal(DateTimeKind.Unspecified, slot!.Start.Kind);
        Assert.Equal(DateTimeKind.Unspecified, slot.End.Kind);
    }

    // ---- The entry invariant ---------------------------------------------

    /// <summary>
    /// An entry is a slot or a refusal, never neither and never both. An entry that
    /// was neither would be a row the report skips — the one failure an operator
    /// cannot see, because it is absent rather than wrong.
    /// </summary>
    [Fact]
    public void An_entry_carries_exactly_one_of_a_slot_or_a_refusal()
    {
        var moved = RetimePlanEntry.Move(At(One, "A", Monday, 60), Monday, Monday.AddMinutes(60));
        var refused = RetimePlanEntry.Refuse(At(One, "A", Monday, 60), "no");

        Assert.NotNull(moved.Slot);
        Assert.Null(moved.Refusal);

        Assert.Null(refused.Slot);
        Assert.NotNull(refused.Refusal);
    }
}
