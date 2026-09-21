using System.Globalization;
using System.Xml.Linq;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Clients;

/// <summary>A remote recorder, as listed by <c>ListRecorders</c>.</summary>
public sealed record RemoteRecorder
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    public string? ExternalId { get; init; }
    public string? MachineIp { get; init; }

    /// <summary>Stopped, Recording, Faulted and so on. Shown, never trusted.</summary>
    public string? State { get; init; }
}

/// <summary>
/// What Panopto reports back after a scheduling call.
///
/// <para>A schedule attempt does not throw when the recorder is already booked —
/// it returns successfully with <see cref="ConflictsExist"/> set and the
/// clashing sessions listed. Treating a 200 as success would silently double-book
/// rooms, so callers must read this.</para>
/// </summary>
public sealed record ScheduledRecordingResult
{
    public bool ConflictsExist { get; init; }

    /// <summary>The scheduled session's id, or the ids created by a recurring call.</summary>
    public IReadOnlyList<Guid> SessionIds { get; init; } = [];

    /// <summary>Human-readable clashes, for the import report.</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>
    /// For a single recording, the one id created. <see cref="Guid.Empty"/> when
    /// Panopto scheduled nothing.
    /// </summary>
    public Guid SessionId => SessionIds.Count > 0 ? SessionIds[0] : Guid.Empty;

    public static ScheduledRecordingResult From(XElement? result)
    {
        if (result is null) return new ScheduledRecordingResult();

        return new ScheduledRecordingResult
        {
            ConflictsExist = SoapXml.Bool(result, "ConflictsExist"),
            SessionIds = SoapXml.Guids(result, "SessionIDs"),
            Conflicts = SoapXml.Items(result, "ConflictingSessions")
                .Select(SoapXml.DescribeConflict)
                .Where(s => s.Length > 0)
                .ToList(),
        };
    }
}

/// <summary>
/// Scheduling operations — the ones that create and move recordings.
///
/// <para>Every method here writes to the live tenant. Nothing is cached and
/// nothing is retried automatically: a retried <c>ScheduleRecording</c> is a
/// second recording, not a repeat of the first.</para>
///
/// <para>Times go in as wall clocks and are put on the wire as instants; see
/// <see cref="RoomClock"/>. The zone is taken from the connection rather than from
/// this machine, so a laptop that has been somewhere else does not move a
/// booking.</para>
/// </summary>
/// <param name="soap">The SOAP client, already carrying the legacy cookie.</param>
/// <param name="roomZone">
/// The zone the rooms keep time in. Defaults to
/// <see cref="RoomClock.DefaultZoneId"/>; the app passes the tenant's configured
/// zone.
/// </param>
public sealed class RemoteRecorderClient(PanoptoSoapClient soap, TimeZoneInfo? roomZone = null)
{
    private const string Service = "IRemoteRecorderManagement";
    private const string Path = PanoptoSoapClient.RemoteRecorderManagementPath;

    private readonly TimeZoneInfo _roomZone = roomZone ?? RoomClock.Resolve(null);

    /// <summary>
    /// The zone the rooms keep time in. Exposed because a caller that has to judge
    /// one of these times — whether a retime would land in the past, say — must
    /// judge it against the room's clock and not this machine's, and the zone this
    /// client converts with is the authority on which that is.
    /// </summary>
    public TimeZoneInfo RoomZone => _roomZone;

    /// <summary>
    /// Every remote recorder in the tenant, following pages.
    ///
    /// <para>The old uploader called <c>GetSettingsByRecorderName</c>, which no
    /// longer exists in 4.2 — the service was renamed and now exposes
    /// <c>ListRecorders</c>. Listing once and matching locally is also cheaper
    /// than a request per row.</para>
    ///
    /// <para><b>It pages, and it used to not.</b> The first version asked for page
    /// 0 with a 250 cap under a comment claiming recorders were few enough that
    /// one page held them all. That was a guess. Every recorder past the first 250
    /// by name was invisible, and because <c>BulkScheduler</c> classifies a name it
    /// cannot resolve as <c>Skipped</c> rather than <c>Failed</c>, the rows for
    /// those rooms did not book and did not complain.</para>
    ///
    /// <para><b>Measured on the Rotman tenant: 5000 recorders, no
    /// <c>TotalNumber</c>, and the old 20-page ceiling hit on every sign-in.</b>
    /// So "it pages now" was still not the same as "it reads the whole set" — the
    /// ceiling was simply far away enough that nobody had reached it yet. The
    /// result carries whether the walk finished, because a caller given a
    /// truncated list and no way to know it is in exactly the position the paging
    /// was added to fix.</para>
    /// </summary>
    public Task<PagedResult<RemoteRecorder>> ListRecordersAsync(CancellationToken ct = default)
        => SoapPaging.ReadAllAsync(
            "ListRecorders",
            // A read, so it is safe to send twice — a stale cookie is worth retrying.
            (page, token) => soap.InvokeAsync(Path, Service, PageRequest(page), token, safeToRetry: true),
            result => SoapXml.Items(result, "PagedResults")
                .Select(ReadRecorder)
                .Where(r => r is not null)
                .Select(r => r!),
            recorder => recorder.Id.ToString("D"),
            ct);

    private static XElement PageRequest(int page) =>
        SoapXml.Operation("ListRecorders",
            SoapXml.Element(PanoptoXml.Tns, "pagination",
                SoapXml.Element(PanoptoXml.V40, "MaxNumberResults", SoapPaging.PageSize),
                SoapXml.Element(PanoptoXml.V40, "PageNumber", page)),
            SoapXml.Element(PanoptoXml.Tns, "sortBy", "Name"));

    /// <summary>
    /// Resolves a recorder name to its id. Returns null rather than guessing:
    /// scheduling against the wrong room is worse than not scheduling.
    /// </summary>
    /// <exception cref="ListingIncompleteException">
    /// The name is not in the listing and the listing stopped short, so null would
    /// be a claim about the tenant that this app is not entitled to make. Only
    /// thrown on the not-found path — a name that <i>was</i> found is found
    /// whether or not the walk finished.
    /// </exception>
    public async Task<RemoteRecorder?> FindRecorderAsync(string name, CancellationToken ct = default)
    {
        var wanted = name.Trim();
        var recorders = await ListRecordersAsync(ct).ConfigureAwait(false);

        var found = recorders.FirstOrDefault(r => string.Equals(r.Name, wanted, StringComparison.OrdinalIgnoreCase))
            // Legacy files sometimes carry the external id in the name column.
            ?? recorders.FirstOrDefault(r =>
                r.ExternalId is { } external &&
                string.Equals(external, wanted, StringComparison.OrdinalIgnoreCase));

        if (found is null && !recorders.Complete)
            throw new ListingIncompleteException($"a recorder named '{wanted}'", recorders.Count, recorders.ReportedTotal);

        return found;
    }

    /// <summary>The folder Panopto would record into for this recorder, by default.</summary>
    public async Task<Guid> GetDefaultFolderAsync(Guid recorderId, CancellationToken ct = default)
    {
        var request = SoapXml.Operation("GetDefaultFolderForRecorder",
            SoapXml.Element(PanoptoXml.Tns, "remoteRecorderId", recorderId.ToString("D")));

        // A read: asking twice returns the same answer.
        var result = await soap.InvokeAsync(Path, Service, request, ct, safeToRetry: true)
            .ConfigureAwait(false);

        return SoapXml.Guid(result) ?? Guid.Empty;
    }

    /// <summary>
    /// Books a recording. Check <see cref="ScheduledRecordingResult.ConflictsExist"/>
    /// on the way out.
    /// </summary>
    /// <param name="start">The room's wall clock, not an instant.</param>
    /// <param name="end">The room's wall clock, not an instant.</param>
    public async Task<ScheduledRecordingResult> ScheduleAsync(
        string name,
        Guid folderId,
        bool isBroadcast,
        DateTime start,
        DateTime end,
        IReadOnlyList<Guid> recorderIds,
        CancellationToken ct = default)
    {
        // tempuri.org, not the serialization-arrays namespace. The wrapper is
        // declared inside ScheduleRecording, so it belongs to the contract
        // schema; only its children come from V40. Sent under Arrays, the
        // element does not bind at all and the server faults with
        // "Value cannot be null. Parameter name: recorderSettings" — which
        // reads like a missing argument rather than a misplaced namespace.
        var settings = SoapXml.Element(PanoptoXml.Tns, "recorderSettings",
            recorderIds.Select(id => SoapXml.Element(PanoptoXml.V40, "RecorderSettings",
                SoapXml.Element(PanoptoXml.V40, "RecorderId", id.ToString("D")),
                // Both feeds on. The legacy tool always sent false/false, and
                // suppressing either would silently drop half the recording.
                SoapXml.Element(PanoptoXml.V40, "SuppressPrimary", "false"),
                SoapXml.Element(PanoptoXml.V40, "SuppressSecondary", "false"))));

        var request = SoapXml.Operation("ScheduleRecording",
            SoapXml.Element(PanoptoXml.Tns, "name", name),
            SoapXml.Element(PanoptoXml.Tns, "folderId", folderId.ToString("D")),
            SoapXml.Element(PanoptoXml.Tns, "isBroadcast", SoapXml.Boolean(isBroadcast)),
            SoapXml.Element(PanoptoXml.Tns, "start", SoapXml.DateTime(RoomClock.ToWire(start, _roomZone))),
            SoapXml.Element(PanoptoXml.Tns, "end", SoapXml.DateTime(RoomClock.ToWire(end, _roomZone))),
            settings);

        var result = await soap.InvokeAsync(Path, Service, request, ct).ConfigureAwait(false);

        return ScheduledRecordingResult.From(result);
    }

    /// <summary>
    /// Moves an already-scheduled recording — the drag-to-reschedule path.
    ///
    /// <para>Preferred over delete-and-recreate because it keeps the session id,
    /// and with it the folder, the description, and anything already linked to
    /// the session.</para>
    /// </summary>
    /// <param name="start">The room's wall clock, not an instant.</param>
    /// <param name="end">The room's wall clock, not an instant.</param>
    public async Task<ScheduledRecordingResult> UpdateRecordingTimeAsync(
        Guid sessionId,
        DateTime start,
        DateTime end,
        CancellationToken ct = default)
    {
        var request = SoapXml.Operation("UpdateRecordingTime",
            SoapXml.Element(PanoptoXml.Tns, "sessionId", sessionId.ToString("D")),
            SoapXml.Element(PanoptoXml.Tns, "start", SoapXml.DateTime(RoomClock.ToWire(start, _roomZone))),
            SoapXml.Element(PanoptoXml.Tns, "end", SoapXml.DateTime(RoomClock.ToWire(end, _roomZone))));

        var result = await soap.InvokeAsync(Path, Service, request, ct).ConfigureAwait(false);

        return ScheduledRecordingResult.From(result);
    }

    private static RemoteRecorder? ReadRecorder(XElement element)
    {
        var id = SoapXml.Guid(SoapXml.Child(element, "Id"));
        var name = SoapXml.Text(element, "Name");

        if (id is null || string.IsNullOrWhiteSpace(name)) return null;

        return new RemoteRecorder
        {
            Id = id.Value,
            Name = name!,
            ExternalId = SoapXml.Text(element, "ExternalId"),
            MachineIp = SoapXml.Text(element, "MachineIP"),
            State = SoapXml.Text(element, "State"),
        };
    }
}
