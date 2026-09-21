namespace PanoptoScheduler.Core.Scheduling;

/// <summary>
/// The acknowledgement a caller must hold before an irreversible bulk operation
/// will run.
///
/// <para>This exists because "preview first" is not a safety gate. The preview
/// button and the apply button sat next to each other, the apply button was
/// styled red, and the only thing between an operator and a term's recordings
/// was remembering which one they had pressed — with the two words "would
/// change" on screen rather than "would be deleted". A confirmation that lives
/// only in a view model is also only as good as that view model: nothing stopped
/// a script or the probe from calling the editor directly.</para>
///
/// <para><b>It carries the target ids rather than a count, and that is the
/// point.</b> A count was the first version of this, and a count is the wrong
/// shape: swapping one ticked session for a different one leaves the number
/// untouched, so the permit still passed over a set nobody agreed to. Ticking one
/// more session still lapses the permission on its own — nobody has to remember
/// to revoke it, and the check is one a test can state plainly. The set is
/// compared order- and duplicate-insensitively, because the order sessions come
/// back from the server in is not something the operator chose and must not be
/// something they are refused for.</para>
///
/// <para>Naming the operation as well as the set keeps a permission granted for
/// one action from being read as permission for another — the version that
/// carried only a count recorded the verb and never checked it.</para>
/// </summary>
/// <param name="Verb">The operation being authorised, for messages: "delete".</param>
/// <param name="TargetIds">
/// Exactly which sessions the acknowledgement was given for. An empty list is a
/// real answer rather than a wildcard: a permit for nothing authorises nothing.
/// </param>
/// <param name="Acknowledged">
/// Whether a person actually confirmed it. A set on its own is not consent.
/// </param>
public sealed record DestructiveAction(string Verb, IReadOnlyList<Guid> TargetIds, bool Acknowledged)
{
    /// <summary>
    /// The verb for a permanent delete.
    ///
    /// <para>A constant rather than a literal at each end, because the check is now
    /// a string comparison: the window builds the permit and the editor spends it,
    /// and two literals in two files is a typo away from a gate that refuses every
    /// real delete — or, worse, one whose verb check quietly never matches and so
    /// is written off as broken and deleted.</para>
    /// </summary>
    public const string DeleteVerb = "delete";

    /// <summary>
    /// How many things the acknowledgement was given for — for display, derived
    /// from the set so the two cannot drift apart.
    /// </summary>
    public int TargetCount => TargetIds.Count;

    /// <summary>
    /// Whether this authorises <paramref name="verb"/> over exactly
    /// <paramref name="ids"/>.
    /// </summary>
    /// <param name="verb">
    /// What the caller is about to do. Passed in rather than assumed, so a permit
    /// issued for one operation cannot be spent on another.
    /// </param>
    /// <param name="ids">What the caller is about to do it to.</param>
    public bool Permits(string verb, IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(ids);

        if (!Acknowledged) return false;
        if (!VerbMatches(verb)) return false;

        return new HashSet<Guid>(TargetIds).SetEquals(ids);
    }

    /// <summary>
    /// Why an operation was refused, phrased so the operator can see what changed
    /// — usually that the selection grew or shrank after the preview.
    ///
    /// <para>The delta is counted rather than hashed because this message is the
    /// only feedback the operator gets: "the set changed" sends them back to the
    /// preview to work out what they did, while "2 were added and 1 removed" tells
    /// them whether they are looking at the mistake they think they are. A
    /// content hash would refuse just as correctly and say nothing.</para>
    /// </summary>
    public string Refusal(string verb, IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(ids);

        if (!Acknowledged)
            return $"The {verb} was not confirmed. Nothing was deleted.";

        if (!VerbMatches(verb))
            return $"This confirmation was given for a {Verb}, not a {verb}. "
                 + "Nothing was deleted.";

        var confirmed = new HashSet<Guid>(TargetIds);
        var current = new HashSet<Guid>(ids);

        var added = current.Count(id => !confirmed.Contains(id));
        var removed = confirmed.Count(id => !current.Contains(id));

        return $"The {verb} was confirmed for {confirmed.Count} session(s), "
             + $"but {current.Count} are selected now — {Describe(added, removed)} "
             + "since the preview. Preview again and confirm the new set.";
    }

    private bool VerbMatches(string verb) =>
        string.Equals(Verb, verb, StringComparison.OrdinalIgnoreCase);

    private static string Describe(int added, int removed) => (added, removed) switch
    {
        (0, 0) => "the same sessions are selected, so the selection itself is not the problem",
        (_, 0) => $"{added} session(s) were added",
        (0, _) => $"{removed} session(s) were removed",
        _ => $"{added} session(s) were added and {removed} removed",
    };
}
