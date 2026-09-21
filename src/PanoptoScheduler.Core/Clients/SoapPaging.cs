using System.Globalization;
using System.Xml.Linq;
using PanoptoScheduler.Core.Diagnostics;

namespace PanoptoScheduler.Core.Clients;

/// <summary>
/// Follows the pages of a SOAP listing until the set is complete.
///
/// <para><b>This exists because "one page holds them all" was a guess, and it was
/// wrong.</b> <c>ListRecorders</c> asked for page 0 with a 250 cap and never
/// followed the rest, so any recorder past the first 250 by name was invisible.
/// That is not only a display gap: the bulk scheduler resolves a room by name and
/// treats a name it cannot find as <c>Skipped</c> rather than <c>Failed</c>, so
/// rows for those rooms were quietly not booked at all.</para>
///
/// <para><b>Two stop conditions, and deliberately not a third.</b> A page that
/// adds nothing new ends the loop — either the set is exhausted or the server is
/// ignoring <c>PageNumber</c>, and neither is worth another request to tell
/// apart. The server's own <c>TotalNumber</c>, when it sends one, ends it
/// exactly.</para>
///
/// <para>The condition that is <i>not</i> here is "a short page means the end".
/// It reads as the obvious rule and it is the wrong one: <see cref="DataSvcClient"/>
/// documents that the server clamps a page below the size asked for, so a short
/// page is the normal case rather than the last one. Stopping on it would return
/// the first clamped page and call it complete — the same truncation in a new
/// place, and harder to notice because the code would look careful.</para>
///
/// <para><see cref="MaxPages"/> is the backstop for the one case the other two
/// do not cover: a tenant genuinely larger than the ceiling. Hitting it logs,
/// because a listing that silently stops short is the failure this type was
/// written to remove.</para>
/// </summary>
internal static class SoapPaging
{
    /// <summary>
    /// How many items to ask for per request. A page size, not a cap — the whole
    /// point of this type is that it is not the latter.
    /// </summary>
    public const int PageSize = 250;

    /// <summary>
    /// The most pages to walk: <see cref="PageSize"/> × this is the ceiling on
    /// how many items any listing can return.
    ///
    /// <para>Readable rather than private because the test that pins the ceiling
    /// has to name it. A test carrying its own copy of the number would pass
    /// after someone changed this one and stop testing anything.</para>
    ///
    /// <para><b>Raised from 20 to 60 on measurement.</b> The Rotman tenant hit the
    /// old ceiling on every sign-in — 5000 recorders, the full 20 pages, and no
    /// <c>TotalNumber</c> to stop on — which the warning this type writes said to
    /// fix by raising this. The room list was being truncated for a real building
    /// rather than for a hypothetical one.</para>
    ///
    /// <para><b>Raising it is close to free,</b> which is what makes this the right
    /// fix rather than a bigger page size: a tenant smaller than the ceiling stops
    /// on the page that adds nothing new and never reaches it, so the cost is
    /// proportional to how much data is really there. Only the genuinely large
    /// tenant pays, and only once per session — the room list is cached, with the
    /// Reload rooms button as the retry.</para>
    /// </summary>
    public const int MaxPages = 60;

    /// <summary>What a paged response names its own count of the full set.</summary>
    private const string TotalField = "TotalNumber";

    /// <summary>
    /// Reads every page of a listing.
    /// </summary>
    /// <param name="operation">The SOAP operation, for the log line.</param>
    /// <param name="fetchPage">Sends one request for the given page index.</param>
    /// <param name="readItems">Pulls the items out of one page's response.</param>
    /// <param name="identity">
    /// A stable key for an item, used to notice a repeat. A page that returns only
    /// items already seen means <c>PageNumber</c> is not being honoured.
    /// </param>
    public static async Task<PagedResult<T>> ReadAllAsync<T>(
        string operation,
        Func<int, CancellationToken, Task<XElement?>> fetchPage,
        Func<XElement?, IEnumerable<T>> readItems,
        Func<T, string?> identity,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fetchPage);
        ArgumentNullException.ThrowIfNull(readItems);
        ArgumentNullException.ThrowIfNull(identity);

        var all = new List<T>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = false;
        var total = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            var result = await fetchPage(page, ct).ConfigureAwait(false);

            // Read the count before the items: it is the authority on when the
            // set is complete, so it has to be in hand when the page is judged.
            total = ReadTotal(result);

            var added = readItems(result)
                .Where(item => identity(item) is not { } key || seen.Add(key))
                .ToList();

            all.AddRange(added);

            // Nothing new: either the set is exhausted, or Page is being ignored
            // and this is the first page again. Both mean stop.
            if (added.Count == 0)
            {
                completed = true;
                break;
            }

            if (total > 0 && all.Count >= total)
            {
                completed = true;
                break;
            }
        }

        if (!completed)
        {
            // Only reachable when the server reports no total and keeps returning
            // fresh items. Say how far it got, so the number in the log is
            // actionable rather than just alarming.
            AppLog.Warn(
                $"{operation}: stopped at the {MaxPages}-page ceiling after {all.Count} item(s)"
                + (total > 0 ? $" of {total}" : " with no total reported")
                + ". The listing is incomplete — raise SoapPaging.MaxPages if the tenant is really this large.");
        }

        // Returned rather than only logged. The log line above was the whole of
        // this type's honesty for one revision, and it was not enough: the callers
        // that act on a missing name never see the log.
        return new PagedResult<T>(all, completed, total);
    }

    /// <summary>
    /// A page's own count of the whole set, or 0 when it does not send one.
    ///
    /// <para>Looked for as a direct child first, which is where a paged response
    /// puts it, then anywhere in the response — a wrapper element in between would
    /// otherwise hide it, and an absent total is read as "already complete", which
    /// is the specific way this goes wrong.</para>
    /// </summary>
    private static int ReadTotal(XElement? result)
    {
        var element = SoapXml.Child(result, TotalField)
            ?? result?.Descendants().FirstOrDefault(e => e.Name.LocalName == TotalField);

        return int.TryParse(SoapXml.Text(element), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}
