using System.Xml.Linq;

namespace PanoptoScheduler.Core.Clients;

/// <summary>A Panopto folder — where recordings land.</summary>
public sealed record PanoptoFolder
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    public Guid? ParentFolder { get; init; }
    public string? ExternalId { get; init; }
    public bool IsPublic { get; init; }
}

/// <summary>
/// A folder hint that matched no folder exactly and several by containment, so
/// picking one would be a guess.
///
/// <para><b>Thrown rather than returned as null,</b> like
/// <see cref="ListingIncompleteException"/>: null is the statement "no such
/// folder", and a caller handed it would report the hint as not found — sending
/// the operator to look for a typo in a row whose real problem is that it names
/// too much.</para>
/// </summary>
public sealed class AmbiguousFolderException(string hint, IReadOnlyList<string> candidates)
    : Exception(
        $"'{hint}' is part of {candidates.Count} folder names ("
        + string.Join(", ", candidates.Take(5).Select(c => $"'{c}'"))
        + (candidates.Count > 5 ? ", …" : string.Empty)
        + "), so none was picked. Give the folder's full name or its id.")
{
    public string Hint { get; } = hint;

    /// <summary>Every folder name that contained the hint.</summary>
    public IReadOnlyList<string> Candidates { get; } = candidates;
}

/// <summary>
/// Session and folder operations — renaming, moving, deleting.
///
/// <para>Separate from <see cref="RemoteRecorderClient"/> because it is a
/// different service on a different API version (4.6 against 4.2) and is metered
/// separately by Panopto.</para>
/// </summary>
public sealed class SessionManagementClient(PanoptoSoapClient soap)
{
    private const string Service = "ISessionManagement";
    private const string Path = PanoptoSoapClient.SessionManagementPath;

    /// <summary>
    /// Folders matching a search term. An empty query lists the top of the tree.
    ///
    /// <para>Pages, for the same reason <see cref="RemoteRecorderClient.ListRecordersAsync"/>
    /// does. The cap here bit less often — the query narrows the set, so it only
    /// truncated when more than 250 folders matched one hint — but a folder picker
    /// that cannot show every folder is a picker that lies, and two listings that
    /// behave differently for no stated reason is how the next person picks the
    /// wrong one to copy.</para>
    /// </summary>
    public Task<PagedResult<PanoptoFolder>> ListFoldersAsync(
        string? searchQuery = null,
        CancellationToken ct = default)
    {
        var query = string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery;

        return SoapPaging.ReadAllAsync(
            "GetFoldersList",
            // A read, so it is safe to send twice — a stale cookie is worth retrying.
            (page, token) => soap.InvokeAsync(Path, Service, PageRequest(query, page), token, safeToRetry: true),
            result => SoapXml.Items(result, "Results")
                .Select(ReadFolder)
                .Where(f => f is not null)
                .Select(f => f!),
            folder => folder.Id.ToString("D"),
            ct);
    }

    private static XElement PageRequest(string? searchQuery, int page) =>
        SoapXml.Operation("GetFoldersList",
            SoapXml.Element(PanoptoXml.Tns, "request",
                SoapXml.Element(PanoptoXml.V40, "Pagination",
                    SoapXml.Element(PanoptoXml.V40, "MaxNumberResults", SoapPaging.PageSize),
                    SoapXml.Element(PanoptoXml.V40, "PageNumber", page))),
            searchQuery is null
                ? new XElement(PanoptoXml.Tns + "searchQuery", PanoptoXml.Nil)
                : SoapXml.Element(PanoptoXml.Tns, "searchQuery", searchQuery));

    /// <summary>
    /// Resolves the folder hint from a schedule row.
    ///
    /// <para>Legacy files carry either a folder GUID or a folder name in the same
    /// column, so both are tried — the same order the original uploader used.
    /// Matching is exact and case-insensitive first; a containment match is only a
    /// fallback, so a hint that names a folder exactly never resolves to a
    /// different one that merely contains the words — and it is taken only when
    /// it is the one such folder in a listing that was read to the end.</para>
    /// </summary>
    /// <exception cref="ListingIncompleteException">
    /// The hint matched nothing in a listing that stopped short. Symmetric with
    /// <see cref="RemoteRecorderClient.FindRecorderAsync"/>, and for the same
    /// reason: null is the statement "no such folder exists", and a truncated read
    /// is not entitled to make it. The caller's next move differs too — a missing
    /// folder is a mistake in the row, an unread listing is a mistake here.
    /// Also thrown when nothing matched exactly and the listing stopped short,
    /// even if something matched partially: an exact match may be on a page
    /// that was never read.
    /// </exception>
    /// <exception cref="AmbiguousFolderException">
    /// Nothing matched exactly and more than one folder name contains the hint.
    /// Not null, for the same reason as above: null says "no such folder", and
    /// several is the opposite of none.
    /// </exception>
    public async Task<PanoptoFolder?> FindFolderAsync(string hint, CancellationToken ct = default)
    {
        var wanted = hint.Trim();
        if (wanted.Length == 0) return null;

        if (Guid.TryParse(wanted, out var id))
        {
            // A guid is already the answer; confirming costs a request per row
            // and the scheduling call rejects a bad id anyway.
            return new PanoptoFolder { Id = id, Name = wanted };
        }

        var matches = await ListFoldersAsync(wanted, ct).ConfigureAwait(false);

        // An exact match is found whether or not the walk finished, so it is
        // returned before the completeness check — throwing there would refuse
        // a resolution that actually succeeded.
        var exact = matches.FirstOrDefault(f => string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // A containment match is not like that. It is only the answer if no
        // exact match exists anywhere, and a listing that stopped short has not
        // read everywhere: "MBA Year 1" on page one would have won over "MBA"
        // sitting unread on page three. So an incomplete listing is the
        // not-found-but-may-exist case here, partial match or not.
        if (!matches.Complete)
            throw new ListingIncompleteException($"a folder matching '{wanted}'", matches.Count, matches.ReportedTotal);

        var partial = matches
            .Where(f => f.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // More than one is a guess, and the order it would be made in is
        // whatever order Panopto listed them — "MBA" against "MBA Year 1",
        // "MBA Year 2" and "EMBA" lands recordings in whichever came first, and
        // the row reads as resolved. Refused instead, by name, so the operator
        // can write the folder out in full.
        if (partial.Count > 1)
            throw new AmbiguousFolderException(wanted, [.. partial.Select(f => f.Name)]);

        return partial.Count == 1 ? partial[0] : null;
    }

    public Task UpdateSessionNameAsync(Guid sessionId, string name, CancellationToken ct = default)
        => soap.InvokeAsync(Path, Service, SoapXml.Operation("UpdateSessionName",
            SoapXml.Element(PanoptoXml.Tns, "sessionId", sessionId.ToString("D")),
            SoapXml.Element(PanoptoXml.Tns, "name", name)), ct);

    /// <summary>
    /// Sets the session description. The legacy tool put the presenter here,
    /// because the scheduling call has no field for it.
    /// </summary>
    public Task UpdateSessionDescriptionAsync(
        Guid sessionId,
        string description,
        CancellationToken ct = default)
        => soap.InvokeAsync(Path, Service, SoapXml.Operation("UpdateSessionDescription",
            SoapXml.Element(PanoptoXml.Tns, "sessionId", sessionId.ToString("D")),
            SoapXml.Element(PanoptoXml.Tns, "description", description)), ct);

    public Task UpdateSessionIsBroadcastAsync(
        Guid sessionId,
        bool isBroadcast,
        CancellationToken ct = default)
        => soap.InvokeAsync(Path, Service, SoapXml.Operation("UpdateSessionIsBroadcast",
            SoapXml.Element(PanoptoXml.Tns, "sessionId", sessionId.ToString("D")),
            SoapXml.Element(PanoptoXml.Tns, "isBroadcast", SoapXml.Boolean(isBroadcast))), ct);

    public Task MoveSessionsAsync(
        IReadOnlyList<Guid> sessionIds,
        Guid folderId,
        CancellationToken ct = default)
        => soap.InvokeAsync(Path, Service, SoapXml.Operation("MoveSessions",
            GuidArray("sessionIds", sessionIds),
            SoapXml.Element(PanoptoXml.Tns, "folderId", folderId.ToString("D"))), ct);

    public Task DeleteSessionsAsync(IReadOnlyList<Guid> sessionIds, CancellationToken ct = default)
        => soap.InvokeAsync(Path, Service, SoapXml.Operation("DeleteSessions",
            GuidArray("sessionIds", sessionIds)), ct);

    /// <summary>
    /// A <c>guid</c> array: the member element belongs to the operation's
    /// namespace, its items to the serialization array namespace.
    /// </summary>
    private static XElement GuidArray(string memberName, IReadOnlyList<Guid> values)
        => SoapXml.Element(PanoptoXml.Tns, memberName,
            values.Select(v => SoapXml.Element(PanoptoXml.Arrays, "guid", v.ToString("D"))));

    private static PanoptoFolder? ReadFolder(XElement element)
    {
        var id = SoapXml.Guid(SoapXml.Child(element, "Id"));
        var name = SoapXml.Text(element, "Name");

        if (id is null || string.IsNullOrWhiteSpace(name)) return null;

        return new PanoptoFolder
        {
            Id = id.Value,
            Name = name!,
            ParentFolder = SoapXml.Guid(SoapXml.Child(element, "ParentFolder")),
            ExternalId = SoapXml.Text(element, "ExternalId"),
            IsPublic = SoapXml.Bool(element, "IsPublic"),
        };
    }
}
