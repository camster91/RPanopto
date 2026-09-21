using System.Text.Json.Serialization;
using PanoptoScheduler.Core.Json;

namespace PanoptoScheduler.Core.Models;

/// <summary>
/// A session as returned by <c>Data.svc/GetSessions</c>.
///
/// Only the fields the calendar and bulk tools actually consume are bound; the
/// endpoint returns ~80, and <see cref="JsonSerializerOptions"/> ignores the
/// rest rather than failing on them.
/// </summary>
public sealed class PanoptoSession
{
    public string? SessionID { get; set; }
    public string? DeliveryID { get; set; }
    public string? SessionName { get; set; }
    public string? DeliveryName { get; set; }

    /// <summary>
    /// The room's wall clock, as <c>Data.svc</c> spells it out. Unspecified
    /// <see cref="DateTimeKind"/> on purpose: these are clock faces, not instants,
    /// and the zone is applied by the recorder rather than carried here. See
    /// <see cref="Json.WcfDateTimeConverter"/>.
    /// </summary>
    [JsonConverter(typeof(WcfDateTimeConverter))]
    public DateTime? ScheduledStartTime { get; set; }

    /// <inheritdoc cref="ScheduledStartTime"/>
    [JsonConverter(typeof(WcfDateTimeConverter))]
    public DateTime? ScheduledEndTime { get; set; }

    /// <inheritdoc cref="ScheduledStartTime"/>
    [JsonConverter(typeof(WcfDateTimeConverter))]
    public DateTime? StartTime { get; set; }

    /// <inheritdoc cref="ScheduledStartTime"/>
    [JsonConverter(typeof(WcfDateTimeConverter))]
    public DateTime? AvailabilityWindowStart { get; set; }

    /// <inheritdoc cref="ScheduledStartTime"/>
    [JsonConverter(typeof(WcfDateTimeConverter))]
    public DateTime? AvailabilityWindowEnd { get; set; }

    /// <summary>
    /// Length in <b>seconds</b>, and fractional — <c>4462.451</c> is a real value
    /// on the live tenant, which is why this is not an integer type. Declared
    /// <c>long?</c> it refused that response outright and the whole session
    /// listing failed to deserialize, taking the calendar down with it.
    /// </summary>
    public double? Duration { get; set; }

    public string? FolderID { get; set; }
    public string? FolderName { get; set; }

    public string? RemoteRecorderID { get; set; }
    public string? RemoteRecorderName { get; set; }

    public bool IsBroadcast { get; set; }
    public bool IsEditable { get; set; }
    public bool HasWriteAccess { get; set; }

    /// <summary>
    /// The session description, which is where the legacy tool puts the presenter.
    ///
    /// <para><b>Bound to <c>Abstract</c>, which is not the name anyone would
    /// guess.</b> Measured on the Rotman tenant by dumping every field the
    /// endpoint returns: there is a field called <c>Abstract</c> — null on 146 of
    /// 213 scheduled sessions and the empty string on the other 67 — and no field
    /// called <c>Description</c> at all. The write side has always been
    /// <c>UpdateSessionDescription</c>, and this property is what lets the app show
    /// what it is about to overwrite.</para>
    ///
    /// <para>Named <c>Description</c> rather than <c>Abstract</c> because that is
    /// what Panopto's own UI calls it; the mapping is explicit rather than left to
    /// the serializer's name matching, which is case-sensitive here and would
    /// silently bind nothing.</para>
    /// </summary>
    [JsonPropertyName("Abstract")]
    public string? Description { get; set; }

    public string? OwnerFullName { get; set; }
    /// <summary>
    /// Presented as arrays of names — <c>[]</c> for a session with no presenters,
    /// which is most of them. Declared as a single <c>string?</c> this refused
    /// the listing the same way <see cref="Duration"/> did.
    /// </summary>
    public List<string>? PresenterFirstNames { get; set; }

    /// <inheritdoc cref="PresenterFirstNames"/>
    public List<string>? PresenterLastNames { get; set; }

    public long? AnalyticsAllTimeViewCount { get; set; }
    public double? AnalyticsCompletionPercentage { get; set; }

    public string? ViewerUrl { get; set; }
    public string? EditorUrl { get; set; }

    public int Status { get; set; }

    /// <summary>
    /// When the session is scheduled to happen.
    ///
    /// <para><b>Do not read <see cref="ScheduledStartTime"/> directly.</b> On the
    /// live tenant it is null for every scheduled session — all 25 rows of a
    /// captured "Scheduled" listing, and across every probe. The time actually
    /// lives in <see cref="StartTime"/>, which the name gives no hint of. For a
    /// scheduled session it holds the planned start: verified to be in the
    /// future, and to agree with the date embedded in the session name
    /// ("… Tues 310PM on 12/22/2026" → 2026-12-22 15:05).</para>
    ///
    /// A calendar built on <see cref="ScheduledStartTime"/> would render empty
    /// while appearing to work, so all callers should come through here.
    ///
    /// <para>The room's wall clock, not an instant — see
    /// <see cref="ScheduledStartTime"/>.</para>
    /// </summary>
    public DateTime? EffectiveStart =>
        ScheduledStartTime ?? StartTime ?? AvailabilityWindowStart;

    /// <summary>
    /// Whether the time is trustworthy rather than inferred from a fallback.
    /// Rows without one are surfaced in the UI instead of being silently
    /// dropped — an invisible session is worse than a flagged one.
    /// </summary>
    public bool HasReliableTime => ScheduledStartTime is not null || StartTime is not null;

    /// <summary>
    /// Panopto's word for a delivery nobody named. Measured on the Rotman tenant:
    /// <see cref="DeliveryName"/> is this literal string for <b>every one of 213
    /// scheduled sessions</b>.
    /// </summary>
    public const string DeliveryNamePlaceholder = "default";

    /// <summary>
    /// The recording's own name, or null when there is not a second name worth
    /// showing.
    ///
    /// <para><b>Null in three cases, and the third was measured rather than
    /// guessed.</b> Absent, equal to <see cref="SessionName"/> (saying the same
    /// thing twice under two labels teaches the reader to skip both), and equal to
    /// <see cref="DeliveryNamePlaceholder"/> — which is what the live tenant sends
    /// for every session, and which differs from every session name. A caller that
    /// only compared the two names printed the word "default" as a second title
    /// under every recording on the calendar.</para>
    ///
    /// <para>This lives on the model rather than in the one panel that reads it,
    /// for the same reason <see cref="EffectiveStart"/> does: "this field holds a
    /// placeholder rather than a name" is a fact about the payload, not about how
    /// a panel is laid out. Here it is reachable by a test; in the panel it was
    /// reachable only by reading live traffic, which is how it was found.</para>
    /// </summary>
    public string? SecondName =>
        DeliveryName is { Length: > 0 } delivery
        && !delivery.Trim().Equals(DeliveryNamePlaceholder, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(delivery, SessionName, StringComparison.Ordinal)
            ? delivery
            : null;

    /// <summary>
    /// How long the session runs.
    ///
    /// <para><b><see cref="Duration"/> is in seconds, not minutes.</b> Measured
    /// rather than assumed: a session named "Test recording" reports
    /// <c>3599.535</c> and one named "The Casino Shift — Brian Goldman" reports
    /// <c>5399.316</c>, which are exactly 60 and 90 minutes at the seconds scale
    /// and 60 and 90 <i>hours</i> at the minutes scale. Reading it as minutes drew
    /// every block in the calendar sixty times too tall.</para>
    /// </summary>
    public TimeSpan? EffectiveDuration =>
        ScheduledStartTime is not null && ScheduledEndTime is not null
            ? ScheduledEndTime - ScheduledStartTime
            : Duration is > 0 ? TimeSpan.FromSeconds(Duration.Value) : null;

    /// <summary>
    /// When the recording is due to end, in the room's wall clock.
    ///
    /// <para><b>Derived from the same two pieces the block is drawn from</b> —
    /// <see cref="EffectiveStart"/> plus <see cref="EffectiveDuration"/> — rather
    /// than read from <see cref="ScheduledEndTime"/> directly. A block is drawn as
    /// a start plus a length, and a retime computes a new end from the same pair;
    /// reading a third field here would let the app move a recording to an end
    /// time that does not match the length it just drew. When the server does
    /// report both scheduled times this agrees with <see cref="ScheduledEndTime"/>
    /// by construction.</para>
    ///
    /// <para>Null when there is no start or no length, which is the honest answer:
    /// a retime has nothing to work from, and the caller reports that rather than
    /// moving the recording somewhere arbitrary.</para>
    /// </summary>
    public DateTime? EffectiveEnd =>
        EffectiveStart is { } start && EffectiveDuration is { } length
            ? start + length
            : null;

    /// <summary>
    /// Records a new time after the server has accepted a reschedule, so the grid
    /// can be redrawn without spending a request re-reading a schedule that was
    /// just written.
    ///
    /// <para>Writes to whichever field <see cref="EffectiveStart"/> actually
    /// reads, rather than forcing one: setting <see cref="ScheduledStartTime"/>
    /// to null on a session that had one would change how the duration is
    /// derived as a side effect of moving it.</para>
    /// </summary>
    /// <param name="start">
    /// The room's wall clock. Taken as given, with no zone applied — the caller
    /// has already had the server accept that clock face, and re-deriving it
    /// through a machine's zone is how the block used to redraw hours from where
    /// it was dropped.
    /// </param>
    public void ApplyReschedule(DateTime start, TimeSpan duration)
    {
        if (ScheduledStartTime is not null)
        {
            ScheduledStartTime = start;
            ScheduledEndTime = start + duration;
            return;
        }

        StartTime = start;

        // Seconds, because that is the unit the server reads and writes. Storing
        // minutes here made a moved session sixty times too short until the next
        // refresh replaced it with the server's own number.
        Duration = duration.TotalSeconds;
    }
}

/// <summary>
/// Session lifecycle values.
///
/// Only <see cref="Scheduled"/> is confirmed against the live tenant (the
/// "Scheduled" list view sends <c>status: [1]</c> and returns 219 rows, against
/// 2,004 unfiltered). The remaining members are the conventional Panopto
/// ordering and are labelled conservatively — <see cref="Unknown"/> renders the
/// raw number rather than inventing a name for it.
/// </summary>
public enum SessionStatus
{
    Unknown = 0,
    Scheduled = 1,
    Recording = 2,
    Complete = 3,
}

public static class SessionStatusExtensions
{
    public static SessionStatus ToSessionStatus(this int raw)
        => Enum.IsDefined(typeof(SessionStatus), raw) ? (SessionStatus)raw : SessionStatus.Unknown;

    public static string Label(this int raw) => raw.ToSessionStatus() switch
    {
        SessionStatus.Scheduled => "Scheduled",
        SessionStatus.Recording => "Recording",
        SessionStatus.Complete => "Complete",
        _ => $"Status {raw}",
    };
}
