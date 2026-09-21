using System.Globalization;
using System.Xml.Linq;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The time model on the SOAP path: the write side, and the read side that reports
/// clashes.
///
/// <para><b>The two directions are opposite, and getting either wrong produces no
/// error at all.</b> A read is the room's wall clock. A write must name the
/// instant. Both were believed to be wall clocks for a while, because the read was
/// measured against the tenant and the write was inferred from it — and a booking
/// then landed four hours early: accepted, confirmed, drawn correctly here, with
/// the room empty when someone turned up to teach.</para>
///
/// <para>Every assertion here holds in any machine timezone: the zone is named
/// rather than taken from the machine, because a test that only passed in Toronto
/// would be worse than no test — it would look like evidence.</para>
/// </summary>
public class SoapTimeTests
{
    /// <summary>A real value off the live tenant, from a session named "2PM".</summary>
    private const long TwoPmMs = 1789739700000;

    private static XElement Time(string raw)
        => XElement.Parse($"<s><StartTime>{raw}</StartTime></s>");

    /// <summary>The wall clock a WCF value spells out, which is what it means.</summary>
    private static DateTime WallClockOf(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    /// <summary>The rooms' zone, named explicitly rather than read off this machine.</summary>
    private static TimeZoneInfo Toronto => RoomClock.Resolve("America/Toronto");

    // ---- Writing: the scheduling parameters ----------------------------

    /// <summary>
    /// A wall clock goes on the wire as the instant that hour names in the room's
    /// zone, and the offset follows the date.
    ///
    /// <para>This is the assertion the first live booking overturned. The digits
    /// went out unchanged and the tenant, which reads the <c>Z</c> as UTC, stored
    /// the hour four hours earlier. Both forms were then booked into one free slot
    /// on the live tenant: unchanged landed at 10:30, converted landed at 14:30 as
    /// asked.</para>
    /// </summary>
    [Fact]
    public void A_wall_clock_goes_out_as_the_instant_the_room_names()
    {
        // 26 January in Toronto is EST: five hours behind UTC.
        Assert.Equal("2021-01-26T16:10:00Z",
            SoapXml.DateTime(RoomClock.ToWire(new DateTime(2021, 1, 26, 11, 10, 0), Toronto)));

        // 15 July is EDT: four. A fixed offset would be an hour out for half the
        // year, and the whole point of naming the zone is that the date decides.
        Assert.Equal("2026-07-15T18:30:00Z",
            SoapXml.DateTime(RoomClock.ToWire(new DateTime(2026, 7, 15, 14, 30, 0), Toronto)));
    }

    /// <summary>
    /// The value a reschedule feeds back in comes from a read, so its Kind is
    /// Local; a schedule file's rows are Unspecified. It is a wall clock either
    /// way, and the conversion must not depend on the Kind it happens to carry —
    /// honouring a <c>Local</c> marker would move it by this workstation's
    /// distance from the room.
    /// </summary>
    [Fact]
    public void A_value_that_arrived_marked_local_is_still_a_wall_clock()
        => Assert.Equal(
            RoomClock.ToWire(new DateTime(2026, 7, 15, 14, 30, 0), Toronto),
            RoomClock.ToWire(new DateTime(2026, 7, 15, 14, 30, 0, DateTimeKind.Local), Toronto));

    /// <summary>
    /// An instant is what the formatter wants, and the marker it writes means UTC.
    /// </summary>
    [Fact]
    public void An_instant_keeps_the_marker_that_means_utc()
        => Assert.Equal("2026-03-10T14:30:00Z",
            SoapXml.DateTime(new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Utc)));

    /// <summary>
    /// An unconverted wall clock is refused rather than sent.
    ///
    /// <para>A guard at the last point before the wire, because this is exactly the
    /// mistake that shipped: the digits looked right, nothing threw, and the
    /// recording happened four hours earlier. The zone belongs to the room rather
    /// than to this formatter, so there is nothing here that could convert
    /// correctly — refusing is the only honest answer.</para>
    /// </summary>
    [Fact]
    public void An_unconverted_wall_clock_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            SoapXml.DateTime(new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Unspecified)));

        Assert.Throws<ArgumentException>(() =>
            SoapXml.DateTime(new DateTime(2026, 3, 10, 14, 30, 0, DateTimeKind.Local)));
    }

    // ---- The zone the conversion is done in ----------------------------

    /// <summary>
    /// The default is the rooms' zone, and it resolves on this machine.
    /// </summary>
    [Fact]
    public void The_rooms_zone_resolves_by_default()
        => Assert.Equal(RoomClock.DefaultZoneId, RoomClock.Resolve(null).Id);

    /// <summary>
    /// A named zone this machine cannot honour stops the write instead of falling
    /// back to another one. A silent fallback would book the wrong hour with
    /// nothing anywhere to say so.
    /// </summary>
    [Fact]
    public void A_zone_name_that_does_not_resolve_is_an_error()
        => Assert.Throws<TimeZoneNotFoundException>(() => RoomClock.Resolve("Mars/Olympus_Mons"));

    /// <summary>
    /// An hour the clock jumps over has no instant to be written as, and says so
    /// with the date rather than as a bare argument error. In Toronto that is
    /// 02:30 on 8 March 2026.
    /// </summary>
    [Fact]
    public void An_hour_the_clock_jumps_over_cannot_be_booked()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            RoomClock.ToWire(new DateTime(2026, 3, 8, 2, 30, 0), Toronto));

        Assert.Contains("2026-03-08 02:30", error.Message, StringComparison.Ordinal);
    }

    // ---- Reading: what the tenant sends back ---------------------------

    /// <summary>
    /// WCF's milliseconds are the room's clock. Read as an instant they land four
    /// hours early on this machine, which the tenant's own session names
    /// disproved on seven rows out of seven.
    /// </summary>
    [Fact]
    public void A_wcf_value_reads_back_as_the_wall_clock_it_spells_out()
        => Assert.Equal(WallClockOf(TwoPmMs),
            SoapXml.DateTime(Time($"/Date({TwoPmMs})/"), "StartTime"));

    /// <summary>
    /// An offset in WCF's format names the zone the value is already expressed
    /// in, so the digits are the room's clock either way.
    /// </summary>
    [Fact]
    public void The_offset_form_spells_out_the_same_wall_clock()
        => Assert.Equal(WallClockOf(TwoPmMs),
            SoapXml.DateTime(Time($"/Date({TwoPmMs}-0400)/"), "StartTime"));

    /// <summary>
    /// The same value without the trailing slash. The JSON reader never sees this
    /// shape; the SOAP one has to cope with it, and a parse that silently
    /// returned null would show a clash with no time against it.
    /// </summary>
    [Fact]
    public void An_offset_value_without_the_trailing_slash_still_parses()
        => Assert.Equal(WallClockOf(TwoPmMs),
            SoapXml.DateTime(Time($"/Date({TwoPmMs}-0400)"), "StartTime"));

    /// <summary>
    /// A bare ISO value carries no zone, so it means the wall clock it looks
    /// like.
    /// </summary>
    [Fact]
    public void A_bare_iso_value_is_read_as_the_wall_clock_it_looks_like()
        => Assert.Equal(new DateTime(2026, 3, 10, 14, 30, 0),
            SoapXml.DateTime(Time("2026-03-10T14:30:00"), "StartTime"));

    [Fact]
    public void An_unreadable_time_is_null_rather_than_a_throw()
    {
        Assert.Null(SoapXml.DateTime(Time("not a date at all"), "StartTime"));
        Assert.Null(SoapXml.DateTime(Time(""), "StartTime"));
        Assert.Null(SoapXml.DateTime(XElement.Parse("<s/>"), "StartTime"));
    }

    // ---- The consequence a user sees -----------------------------------

    /// <summary>
    /// Where the reader's output lands: the clash list shown after a booking and
    /// after an import. It formats the value directly, so a shifted value here
    /// reads as a time nobody recognises.
    /// </summary>
    [Fact]
    public void A_clash_is_reported_in_the_rooms_wall_clock()
    {
        var element = XElement.Parse($"""
            <ConflictingSession>
              <SessionName>MGT1000 Lecture</SessionName>
              <StartTime>/Date({TwoPmMs})/</StartTime>
            </ConflictingSession>
            """);

        var expected = WallClockOf(TwoPmMs)
            .ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

        Assert.Equal($"MGT1000 Lecture ({expected})", SoapXml.DescribeConflict(element));
    }

    [Fact]
    public void A_clash_with_no_readable_time_still_names_the_session()
        => Assert.Equal("MGT1000 Lecture (already booked)",
            SoapXml.DescribeConflict(XElement.Parse(
                """<ConflictingSession><SessionName>MGT1000 Lecture</SessionName></ConflictingSession>""")));
}
