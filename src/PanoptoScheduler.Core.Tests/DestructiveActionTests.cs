using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The delete gate's own rules, apart from any tenant.
///
/// <para>These are here rather than only in <see cref="BulkSessionEditorTests"/>
/// because the gate is what stands between a mis-set filter and a term's
/// recordings, and a rule that only holds when the network is scripted is a rule
/// nobody has checked.</para>
/// </summary>
public class DestructiveActionTests
{
    private const string Delete = DestructiveAction.DeleteVerb;

    private static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static DestructiveAction Permit(IEnumerable<Guid> ids, bool acknowledged = true)
        => new(Delete, [.. ids], acknowledged);

    private static Guid[] Sessions(int count) => [.. Enumerable.Range(1, count).Select(Id)];

    /// <summary>
    /// The set is what makes the permission lapse on its own: a selection that
    /// grew after the preview no longer matches what was agreed to.
    /// </summary>
    [Fact]
    public void A_permit_for_one_size_does_not_cover_another()
    {
        var permit = Permit(Sessions(3));

        Assert.True(permit.Permits(Delete, Sessions(3)));
        Assert.False(permit.Permits(Delete, Sessions(4)));
        Assert.False(permit.Permits(Delete, Sessions(2)));
        Assert.False(permit.Permits(Delete, []));
    }

    /// <summary>
    /// The reason this carries ids at all. Swap one ticked session for a
    /// different one and the count is unchanged — the previous version of this
    /// type compared counts, so that swap passed, and a delete would run over a
    /// session nobody had confirmed.
    /// </summary>
    [Fact]
    public void A_permit_for_the_same_count_but_different_sessions_is_refused()
    {
        var permit = Permit([Id(1), Id(2)]);

        // One out, one in: the same two sessions by count, not by identity.
        Assert.False(permit.Permits(Delete, [Id(1), Id(3)]));
    }

    /// <summary>
    /// The order sessions come back from the server in is not something the
    /// operator chose, so it must not be something they are refused for.
    /// </summary>
    [Fact]
    public void The_same_sessions_in_a_different_order_are_accepted()
    {
        var permit = Permit([Id(1), Id(2), Id(3)]);

        Assert.True(permit.Permits(Delete, [Id(3), Id(1), Id(2)]));
    }

    /// <summary>A selection that lost one member is a different set, not a smaller one.</summary>
    [Fact]
    public void A_permit_that_has_lost_one_session_is_refused()
    {
        var permit = Permit(Sessions(3));

        Assert.False(permit.Permits(Delete, Sessions(2)));
    }

    /// <summary>
    /// A count on its own is a number, not consent. Without this the window could
    /// arm the delete by setting a field and never asking anybody.
    /// </summary>
    [Fact]
    public void An_unacknowledged_permit_permits_nothing()
    {
        var permit = Permit(Sessions(3), acknowledged: false);

        Assert.False(permit.Permits(Delete, Sessions(3)));
        Assert.False(permit.Permits(Delete, []));
    }

    /// <summary>
    /// Zero is a real answer, not a wildcard — an empty selection must not
    /// accidentally authorise a full one.
    /// </summary>
    [Fact]
    public void A_permit_for_nothing_permits_only_nothing()
    {
        var permit = Permit([]);

        Assert.True(permit.Permits(Delete, []));
        Assert.False(permit.Permits(Delete, Sessions(1)));
    }

    /// <summary>
    /// The verb was carried from the first version and never checked, which meant
    /// a permit issued for one operation authorised any other that reused it.
    /// </summary>
    [Fact]
    public void A_permit_granted_for_another_operation_does_not_authorise_this_one()
    {
        var permit = Permit(Sessions(2));

        Assert.False(permit.Permits("move", Sessions(2)));
        Assert.Contains("move", permit.Refusal("move", Sessions(2)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A refusal has to say which case it is: "you never confirmed" and "what you
    /// confirmed is not what is selected now" call for different actions, and the
    /// second one has to send the operator back through the preview.
    /// </summary>
    [Fact]
    public void A_refusal_names_the_operation_and_what_changed()
    {
        var refusal = Permit(Sessions(3)).Refusal(Delete, Sessions(5));

        Assert.Contains(Delete, refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3", refusal);
        Assert.Contains("5", refusal);
        Assert.Contains("added", refusal);

        var unconfirmed = Permit(Sessions(3), acknowledged: false).Refusal(Delete, Sessions(3));
        Assert.Contains(Delete, unconfirmed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not confirmed", unconfirmed);
    }

    /// <summary>
    /// The delta is the point of the message: "the set changed" sends the operator
    /// back to the preview to work out what they did, while a count of what was
    /// added and what was removed says whether this is the mistake they think it
    /// is.
    /// </summary>
    [Fact]
    public void A_refusal_says_how_many_were_added_and_how_many_removed()
    {
        // Confirmed 1 and 2; now 1 and 3 — one added, one removed.
        var swap = Permit([Id(1), Id(2)]).Refusal(Delete, [Id(1), Id(3)]);

        Assert.Contains("1 session(s) were added and 1 removed", swap);

        var grew = Permit([Id(1)]).Refusal(Delete, [Id(1), Id(2), Id(3)]);
        Assert.Contains("2 session(s) were added", grew);
        Assert.DoesNotContain("removed", grew);

        var shrank = Permit([Id(1), Id(2)]).Refusal(Delete, [Id(1)]);
        Assert.Contains("1 session(s) were removed", shrank);
        Assert.DoesNotContain("added", shrank);
    }

    /// <summary>
    /// The verb is carried rather than assumed, so a permit issued for one
    /// operation cannot be read as permission for another.
    /// </summary>
    [Fact]
    public void The_permit_records_which_operation_it_was_granted_for()
    {
        Assert.Equal("delete", Permit(Sessions(1)).Verb);
    }

    /// <summary>
    /// The count is derived from the set, so a number on screen cannot describe a
    /// different set from the one the gate will test.
    /// </summary>
    [Fact]
    public void The_count_is_the_size_of_the_set()
    {
        Assert.Equal(3, Permit(Sessions(3)).TargetCount);
        Assert.Equal(0, Permit([]).TargetCount);
    }
}
