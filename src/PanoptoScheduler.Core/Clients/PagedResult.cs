using System.Collections;

namespace PanoptoScheduler.Core.Clients;

/// <summary>
/// A listing read a page at a time, together with whether the walk reached the
/// end of the set.
///
/// <para><b>Why this is a list rather than a wrapper holding one.</b> The count
/// was always true and is not what was missing — the missing thing was whether it
/// was the <i>whole</i> count. Making this an <see cref="IReadOnlyList{T}"/> means
/// every existing caller keeps working and keeps meaning what it meant, and the
/// new fact is available to the three places that have to act on it rather than
/// being imposed on the dozen that do not.</para>
///
/// <para><b>It is not an optional extra.</b> A caller that ignores
/// <see cref="Complete"/> cannot tell "there are no rooms called Event Space 214"
/// from "we stopped reading before we got to them", and those two answers lead to
/// opposite actions: one is a mistake in the row, the other is a mistake in this
/// app. <c>BulkScheduler</c> treats an unresolvable room name as <c>Skipped</c>
/// — a row that does not book and does not complain — which is only the right
/// classification when the listing is known to be whole.</para>
/// </summary>
/// <param name="items">Everything the walk returned, in the order it returned it.</param>
/// <param name="complete">
/// Whether the walk ended because the set was finished, rather than by running
/// out of pages.
/// </param>
/// <param name="reportedTotal">
/// The server's own count of the set, or 0 when it did not send one. Kept for the
/// message rather than the logic: "5000 of 5000" and "5000 of an unknown number"
/// are different sentences to read.
/// </param>
public sealed class PagedResult<T>(
    IReadOnlyList<T> items,
    bool complete,
    int reportedTotal = 0) : IReadOnlyList<T>
{
    /// <summary>Everything the walk returned. Never null.</summary>
    public IReadOnlyList<T> Items { get; } = items;

    /// <summary>
    /// Whether this is the whole set. False means the walk hit its page ceiling
    /// and there is more on the server that this app has not seen.
    /// </summary>
    public bool Complete { get; } = complete;

    /// <summary>The server's own count of the set, or 0 when it sent none.</summary>
    public int ReportedTotal { get; } = reportedTotal;

    public int Count => Items.Count;

    public T this[int index] => Items[index];

    public IEnumerator<T> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Thrown when something was looked for by name in a listing that stopped short,
/// so "it is not here" cannot be told from "we did not read that far".
///
/// <para><b>Thrown rather than returned as null,</b> because null is an answer
/// and this is the absence of one. A caller handed null would report that the
/// room does not exist — a confident, wrong statement about the tenant, made by
/// this app, which is the failure mode the whole paging effort exists to
/// remove.</para>
/// </summary>
public sealed class ListingIncompleteException(string what, int read, int total)
    : Exception(
        $"Looked for {what} in a listing that stopped after {read} item(s)"
        + (total > 0 ? $" of {total}" : " with no total reported")
        + ", so it may be there and simply not read yet. Raise SoapPaging.MaxPages.")
{
    /// <summary>How many items the walk returned before it gave up.</summary>
    public int Read { get; } = read;

    /// <summary>What the server said the full set was, or 0 if it said nothing.</summary>
    public int Total { get; } = total;
}
