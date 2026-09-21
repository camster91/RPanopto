using System.Text.Json.Serialization;

namespace PanoptoScheduler.Core.Clients;

/// <summary>
/// The request envelope for <c>Data.svc/GetSessions</c>.
///
/// The body must be wrapped in <c>queryParameters</c>; sending the fields at
/// the top level returns a WCF null-reference fault rather than a validation
/// error, which is worth knowing before debugging it blind.
/// </summary>
public sealed class GetSessionsRequest
{
    [JsonPropertyName("queryParameters")]
    public SessionQuery QueryParameters { get; set; } = new();
}

/// <summary>
/// Query parameters, shaped to match what the Panopto web UI sends.
///
/// <para><b>Every member is named explicitly, and that is load-bearing.</b> The
/// endpoint binds this JSON case-sensitively, so a field spelled <c>Status</c>
/// rather than <c>status</c> is not rejected — it is <i>ignored</i>, and the
/// server answers with its default listing as though nothing had been asked.
/// Measured on the live tenant: the same query with PascalCase names returned
/// 250 completed sessions out of 2024, and with camelCase names returned the 221
/// scheduled ones. The filter, the page size and the page number were all being
/// dropped that way, and nothing about the response said so.</para>
///
/// <para>Attributes rather than a naming policy on the serializer options: the
/// options are the caller's, and a request built anywhere without them would
/// silently go back to being ignored. This is also why
/// <c>queryParameters</c> alone worked before — it was the only member that
/// carried its own name.</para>
///
/// <para>Note on <see cref="StartDate"/> and <see cref="EndDate"/>: these are
/// accepted without error but <b>have no effect</b>. Probing the live tenant
/// with a ±30-day window returned every scheduled session, identical to an
/// unbounded query. They are retained because the UI sends them, but date
/// filtering must be done client-side — see <see cref="DataSvcClient"/>.</para>
/// </summary>
public sealed class SessionQuery
{
    [JsonPropertyName("query")] public string? Query { get; set; }
    [JsonPropertyName("sortColumn")] public int SortColumn { get; set; } = 1;
    [JsonPropertyName("sortAscending")] public bool SortAscending { get; set; }

    /// <summary>
    /// Page size. The server clamps it, so a large value returns a shorter page
    /// rather than everything; <see cref="DataSvcClient.GetAllSessionsAsync"/>
    /// follows <see cref="Page"/> instead of trusting one call to be the whole
    /// set.
    /// </summary>
    [JsonPropertyName("maxResults")] public int MaxResults { get; set; } = 500;

    [JsonPropertyName("page")] public int Page { get; set; }

    // Accepted but ignored — see summary.
    [JsonPropertyName("startDate")] public string? StartDate { get; set; }
    [JsonPropertyName("endDate")] public string? EndDate { get; set; }

    [JsonPropertyName("folderID")] public string? FolderID { get; set; }

    /// <summary>
    /// Lifecycle filter. <c>[1]</c> is Scheduled and is the only value confirmed
    /// against the live tenant. Leaving this null sends no filter at all, which
    /// is what the web UI's own "all sessions" list does —
    /// <see cref="DataSvcClient"/> defaults it to <c>[1]</c> rather than to null,
    /// so a caller that passes nothing gets scheduled sessions, not everything.
    /// </summary>
    [JsonPropertyName("status")] public int[]? Status { get; set; } = [1];

    [JsonPropertyName("bookmarked")] public bool Bookmarked { get; set; }
    [JsonPropertyName("getFolderData")] public bool GetFolderData { get; set; }
    [JsonPropertyName("isSharedWithMe")] public bool IsSharedWithMe { get; set; }
    [JsonPropertyName("isSubscriptionsPage")] public bool IsSubscriptionsPage { get; set; }
    [JsonPropertyName("includeArchived")] public bool IncludeArchived { get; set; } = true;
    [JsonPropertyName("includeArchivedStateCount")] public bool IncludeArchivedStateCount { get; set; }
    [JsonPropertyName("sessionListOnlyArchived")] public bool SessionListOnlyArchived { get; set; }
    [JsonPropertyName("includePlaylists")] public bool IncludePlaylists { get; set; } = true;
}

/// <summary>WCF envelope: <c>{ "d": { "Results": [...], "TotalNumber": n } }</c>.</summary>
public sealed class GetSessionsResponse
{
    [JsonPropertyName("d")]
    public GetSessionsResult? D { get; set; }
}

public sealed class GetSessionsResult
{
    [JsonPropertyName("Results")]
    public List<Models.PanoptoSession> Results { get; set; } = [];

    [JsonPropertyName("TotalNumber")]
    public int TotalNumber { get; set; }
}
