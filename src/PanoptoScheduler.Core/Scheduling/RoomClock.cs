namespace PanoptoScheduler.Core.Scheduling;

/// <summary>
/// Turns a room's wall clock into the instant Panopto's write API expects.
///
/// <para><b>The two directions are not symmetric, and that asymmetry is the whole
/// of this type.</b> A time read back from Panopto is the room's wall clock — the
/// digits that belong on the door — and must be shown as it is. A time written to
/// Panopto must name the instant, because the tenant reads the value against the
/// room's own zone. One booking, written both ways into the same free slot on the
/// live tenant: the wall-clock digits landed four hours before the hour asked for,
/// exactly Toronto's distance from UTC, and the instant landed on the hour.</para>
///
/// <para>The evidence for the wall-clock reading came entirely from the read path,
/// and nothing about a read says anything about a write. That is how a trailing
/// <c>Z</c> described as "not meaning UTC" got written into the comments of
/// <see cref="Clients.SoapXml"/> and believed. It means UTC.</para>
///
/// <para>The failure this prevents is silent: the call succeeds, the app draws the
/// session at the hour it was asked for, and the room is empty then because the
/// recording happened hours earlier.</para>
/// </summary>
public static class RoomClock
{
    /// <summary>The zone the rooms keep time in when the credentials file names none.</summary>
    public const string DefaultZoneId = "America/Toronto";

    /// <summary>
    /// The instant to put on the wire for a wall clock in <paramref name="zone"/>.
    ///
    /// <para>The value is taken as already being in <paramref name="zone"/>: that is
    /// what a schedule file carries, what a calendar cell means, and what a value
    /// read back from Panopto is. Any <see cref="DateTimeKind"/> it arrived with is
    /// discarded rather than honoured, so a value that picked up <c>Local</c> or
    /// <c>Utc</c> somewhere upstream cannot quietly change the hour.</para>
    /// </summary>
    public static DateTime ToWire(DateTime wallClock, TimeZoneInfo zone)
    {
        var naive = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

        // The spring-forward hour does not exist in this zone, and ConvertTimeToUtc
        // throws a bare ArgumentException for it. Named here because that hour is a
        // real one to put in a schedule — a room asked to record at 02:30 on that
        // date means something the clock cannot express — and the bare exception
        // says nothing about which row or which date.
        if (zone.IsInvalidTime(naive))
            throw new InvalidOperationException(
                $"There is no {naive:yyyy-MM-dd HH:mm} in {zone.Id} — the clock jumps forward " +
                "over that hour. Pick a time that exists on that date.");

        return TimeZoneInfo.ConvertTimeToUtc(naive, zone);
    }

    /// <summary>
    /// What the clock on the room's wall says now.
    ///
    /// <para>Derived from UTC through the room's zone rather than read off this
    /// machine. "Is this booking in the past" is a question about the room, and a
    /// laptop that has been somewhere else answers it wrongly by the distance
    /// between the two.</para>
    /// </summary>
    public static DateTime Now(TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);

    /// <summary>
    /// Whether a proposed end time has already gone by, in the room.
    ///
    /// <para><paramref name="end"/> is the room's wall clock and is compared
    /// against the room's wall clock now — never against this machine's. The guard
    /// this replaces compared it against <see cref="DateTime.Now"/>, which is two
    /// clock faces belonging to different places: on a workstation set to UTC it
    /// refused moves up to four hours before the room's own past, and allowed
    /// moves that had already happened.</para>
    /// </summary>
    /// <param name="nowUtc">
    /// The current instant. Defaults to the real clock; tests pass one so the
    /// boundary can be pinned rather than waited for.
    /// </param>
    public static bool WouldLandInThePast(DateTime end, TimeZoneInfo zone, DateTime? nowUtc = null)
    {
        var instant = DateTime.SpecifyKind(nowUtc ?? DateTime.UtcNow, DateTimeKind.Utc);
        var wall = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(instant, zone), DateTimeKind.Unspecified);

        return DateTime.SpecifyKind(end, DateTimeKind.Unspecified) <= wall;
    }

    /// <summary>
    /// The zone named by <paramref name="zoneId"/>, or the default when none is.
    ///
    /// <para><b>A name that will not resolve throws rather than falling back.</b>
    /// Booking into a fallback zone moves the recording by hours and reports
    /// nothing, which is the failure this type exists to prevent; a name that
    /// cannot be honoured stops the write instead.</para>
    /// </summary>
    public static TimeZoneInfo Resolve(string? zoneId)
    {
        var wanted = string.IsNullOrWhiteSpace(zoneId) ? DefaultZoneId : zoneId.Trim();

        if (TimeZoneInfo.TryFindSystemTimeZoneById(wanted, out var zone)) return zone;

        throw new TimeZoneNotFoundException(
            $"This machine has no time zone '{wanted}', so a wall clock cannot be turned into " +
            $"the instant Panopto stores. Set timeZone in credentials.json to a zone it knows " +
            $"— '{DefaultZoneId}' is the default.");
    }
}
