namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// Canned SOAP listings, shared by the tests that read a listing a page at a time
/// and the tests that drive the scheduler through one.
///
/// <para>One copy of the wire shape on purpose. Two test files each carrying
/// their own idea of what a paged response looks like is two chances to keep
/// testing a shape the server stopped sending.</para>
///
/// <para><c>TotalNumber</c> is the size of the <b>whole set</b>, not of the page
/// it arrives on. That is the field's meaning and the reason it can end a page
/// walk exactly — a page reporting its own length would declare the set complete
/// after page 0 and hide every page after it, which is the bug being fixed.</para>
/// </summary>
internal static class SoapListings
{
    /// <summary>A recorder listing that is the whole set, reporting its own size.</summary>
    public static string RecorderListing(params (string Id, string Name)[] recorders)
        => RecorderPage(recorders.Length, recorders);

    /// <summary>
    /// One page of a larger recorder listing. <paramref name="total"/> is the size
    /// of the whole set.
    /// </summary>
    public static string RecorderPage(int total, params (string Id, string Name)[] recorders)
        => $"""
            <ListRecordersResponse xmlns="http://tempuri.org/"><ListRecordersResult>
              <PagedResults xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap">
                {Recorders(recorders)}
              </PagedResults>
              <TotalNumber>{total}</TotalNumber>
            </ListRecordersResult></ListRecordersResponse>
            """;

    /// <summary>
    /// A recorder listing with no <c>TotalNumber</c> element at all — a server
    /// that does not report one, which is the case the page walk has to survive
    /// without treating an absent total as "already complete".
    /// </summary>
    public static string RecorderListingWithoutTotal(params (string Id, string Name)[] recorders)
        => $"""
            <ListRecordersResponse xmlns="http://tempuri.org/"><ListRecordersResult>
              <PagedResults xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V42.Soap">
                {Recorders(recorders)}
              </PagedResults>
            </ListRecordersResult></ListRecordersResponse>
            """;

    /// <summary>A folder listing that is the whole set, reporting its own size.</summary>
    public static string FolderListing(params (string Id, string Name)[] folders)
        => FolderPage(folders.Length, folders);

    public static string FolderPage(int total, params (string Id, string Name)[] folders)
        => $"""
            <GetFoldersListResponse xmlns="http://tempuri.org/"><GetFoldersListResult>
              <Results xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V40">
                {string.Concat(folders.Select(f => $"<Folder><Id>{f.Id}</Id><Name>{f.Name}</Name></Folder>"))}
              </Results>
              <TotalNumber>{total}</TotalNumber>
            </GetFoldersListResult></GetFoldersListResponse>
            """;

    /// <summary>A recorder with a distinct id and name, for building large pages.</summary>
    public static (string Id, string Name) Recorder(int index)
        => ($"{index:D8}-0000-0000-0000-000000000000", $"Room {index:D4}");

    /// <summary>
    /// A folder listing with no <c>TotalNumber</c>, the folder-side twin of
    /// <see cref="RecorderListingWithoutTotal"/>.
    ///
    /// <para>Needed because the folder lookup is the second place a name is
    /// resolved against a paged listing, and it resolves to a different action:
    /// booking has a safe fallback folder, so an unread listing degrades rather
    /// than fails, and the sentence the operator reads has to say which of the two
    /// happened.</para>
    /// </summary>
    public static string FolderListingWithoutTotal(params (string Id, string Name)[] folders)
        => $"""
            <GetFoldersListResponse xmlns="http://tempuri.org/"><GetFoldersListResult>
              <Results xmlns="http://schemas.datacontract.org/2004/07/Panopto.Server.Services.PublicAPI.V40">
                {string.Concat(folders.Select(f => $"<Folder><Id>{f.Id}</Id><Name>{f.Name}</Name></Folder>"))}
              </Results>
            </GetFoldersListResult></GetFoldersListResponse>
            """;

    /// <summary>A folder with a distinct id and name, for building large pages.</summary>
    public static (string Id, string Name) Folder(int index)
        => ($"{index:D8}-1111-1111-1111-111111111111", $"Folder {index:D4}");

    private static string Recorders((string Id, string Name)[] recorders)
        => string.Concat(recorders.Select(r =>
            $"<RemoteRecorder><Id>{r.Id}</Id><Name>{r.Name}</Name></RemoteRecorder>"));
}
