using System.Text.Json;
using PanoptoScheduler.Core.Json;
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The read path: what a value off the wire means.
///
/// <para>Every value Panopto returns is the room's wall clock. This used to be
/// carried as a <see cref="DateTimeOffset"/> stamped with this machine's offset,
/// so that reading <c>LocalDateTime</c> would undo the stamp — a trick that only
/// worked while the stamping zone and the reading zone agreed. These assertions
/// are about digits and about <see cref="DateTimeKind"/>, and none of them depends
/// on where this machine thinks it is.</para>
/// </summary>
public class WcfDateTimeConverterTests
{
    /// <summary>A real value lifted from the live tenant's response.</summary>
    private const long RealEpochMs = 1796303100000;

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new WcfDateTimeConverter() },
    };

    private sealed class Holder
    {
        public DateTime? Start { get; set; }
    }

    private static DateTime? Read(string json)
        => JsonSerializer.Deserialize<Holder>(json, Options)?.Start;

    /// <summary>The wall clock a WCF millisecond count spells out.</summary>
    private static DateTime WallClockOf(long ms)
        => DateTime.SpecifyKind(
            DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, DateTimeKind.Unspecified);

    [Fact]
    public void Reads_wcf_format_as_a_wall_clock()
    {
        // The format the tenant actually emits, and the reading that matched the
        // tenant's own scheduled sessions on every row measured.
        var actual = Read($$"""{"Start":"\/Date({{RealEpochMs}})\/"}""");

        Assert.Equal(WallClockOf(RealEpochMs), actual);
    }

    /// <summary>
    /// The digits are handed over as they arrived, and the value is marked
    /// Unspecified so that nothing downstream can convert them by accident.
    ///
    /// <para>This is the whole point, and it is now structural: no machine zone is
    /// read to produce the value, so there is no zone for a display site to have to
    /// undo. Before, the value came back stamped with this machine's offset and
    /// every consumer had to remember to read <c>LocalDateTime</c>; one that read
    /// <c>UtcDateTime</c> instead moved the recording four hours without saying
    /// so.</para>
    /// </summary>
    [Fact]
    public void The_wall_clock_is_handed_over_unmarked()
    {
        var actual = Read($$"""{"Start":"\/Date({{RealEpochMs}})\/"}""")!.Value;

        Assert.Equal(DateTimeKind.Unspecified, actual.Kind);
        Assert.Equal(WallClockOf(RealEpochMs).Ticks, actual.Ticks);
    }

    [Fact]
    public void The_offset_form_is_taken_at_face_value()
    {
        // An offset here names the zone the value is already expressed in, so the
        // digits are the room's clock either way. Nothing in this tenant's
        // responses has ever carried one; this pins the rule if it starts.
        var plain = Read($$"""{"Start":"\/Date({{RealEpochMs}})\/"}""");
        var offset = Read($$"""{"Start":"\/Date({{RealEpochMs}}-0400)\/"}""");

        Assert.Equal(plain, offset);
    }

    /// <summary>
    /// An ISO value that carries an offset is also the digits it shows.
    ///
    /// <para>This one was a real defect rather than a rule to pin. The offset form
    /// went through <see cref="DateTimeOffset"/> parsing, so the value became an
    /// instant, and a display site reading it as local time showed it shifted by
    /// this machine's distance from UTC — the same four-hours-early failure the
    /// WCF form had, left behind in the branch that looked like it was handling
    /// the well-behaved format.</para>
    /// </summary>
    [Fact]
    public void An_iso_value_with_an_offset_is_not_shifted()
    {
        var actual = Read("""{"Start":"2026-12-01T10:30:00+00:00"}""");

        Assert.Equal(new DateTime(2026, 12, 1, 10, 30, 0), actual);
        Assert.Equal(DateTimeKind.Unspecified, actual!.Value.Kind);
    }

    [Fact]
    public void A_bare_number_reads_the_same_way_as_the_same_value_quoted()
    {
        // The wire always quotes it. If it ever stops, an unquoted count is the
        // same number in the same units and must not become an instant just
        // because of how it was punctuated.
        Assert.Equal(Read($$"""{"Start":"\/Date({{RealEpochMs}})\/"}"""),
            Read($$"""{"Start":{{RealEpochMs}}}"""));
    }

    [Fact]
    public void A_value_read_from_the_wire_survives_being_written_back()
    {
        // Write used to stamp the instant, so a value did not round trip: it came
        // back shifted by this machine's offset. Nothing serialises a session
        // today, which is exactly why the mismatch would have gone unnoticed.
        var original = Read($$"""{"Start":"\/Date({{RealEpochMs}})\/"}""");

        var json = JsonSerializer.Serialize(new Holder { Start = original }, Options);

        Assert.Equal(original, Read(json));
    }

    [Fact]
    public void Reads_iso_8601_when_present()
    {
        Assert.Equal(new DateTime(2026, 12, 1, 10, 30, 0),
            Read("""{"Start":"2026-12-01T10:30:00"}"""));
    }

    [Fact]
    public void Null_stays_null()
    {
        Assert.Null(Read("""{"Start":null}"""));
        Assert.Null(Read("""{"Start":""}"""));
        Assert.Null(Read("""{}"""));
    }

    [Fact]
    public void Unparseable_value_does_not_throw()
    {
        // Better to surface a session with no time than to fail the whole
        // calendar because one record carried an unexpected format.
        Assert.Null(Read("""{"Start":"not a date"}"""));
    }

    /// <summary>
    /// A value can be shaped right and still be unconvertible:
    /// <c>long.MaxValue</c> is the classic .NET "unset" sentinel, it fits the
    /// regex exactly, and the earlier implementation parsed it and threw out
    /// of the read — taking every other session on the page with it, for a
    /// value the contract here says is simply unread.
    /// </summary>
    [Fact]
    public void An_in_shape_but_out_of_range_value_reads_as_null()
    {
        Assert.Null(Read("""{"Start":"/Date(9223372036854775807)/"}"""));
        Assert.Null(Read("""{"Start":9223372036854775807}"""));
    }

    [Fact]
    public void PanoptoSession_wires_the_converter_through_the_attribute()
    {
        // Verifies the [JsonConverter] attribute, not just the converter class.
        var session = JsonSerializer.Deserialize<PanoptoSession>(
            $$"""
            {
              "SessionID": "ba8094eb-e174-453d-b113-b4b800fd353c",
              "SessionName": "Commerce Lecture",
              "Status": 1,
              "StartTime": "\/Date({{RealEpochMs}})\/",
              "RemoteRecorderName": "147",
              "FolderName": "Commerce Fall 2026"
            }
            """);

        Assert.NotNull(session);
        Assert.Equal(WallClockOf(RealEpochMs), session.StartTime);
        Assert.Equal("147", session.RemoteRecorderName);
        Assert.Equal(SessionStatus.Scheduled, session.Status.ToSessionStatus());
    }

    [Fact]
    public void EffectiveStart_uses_StartTime_when_ScheduledStartTime_is_null()
    {
        // The real shape of a scheduled session on the live tenant:
        // ScheduledStartTime is null and StartTime carries the plan.
        var scheduled = new PanoptoSession
        {
            Status = 1,
            ScheduledStartTime = null,
            StartTime = WallClockOf(RealEpochMs),
        };

        Assert.Equal(WallClockOf(RealEpochMs), scheduled.EffectiveStart);
        Assert.True(scheduled.HasReliableTime);
    }

    [Fact]
    public void EffectiveStart_prefers_ScheduledStartTime_when_present()
    {
        var both = new PanoptoSession
        {
            ScheduledStartTime = new DateTime(2026, 12, 17, 9, 0, 0),
            StartTime = new DateTime(2026, 12, 18, 9, 0, 0),
        };

        Assert.Equal(new DateTime(2026, 12, 17, 9, 0, 0), both.EffectiveStart);
    }

    [Fact]
    public void EffectiveStart_falls_back_to_the_availability_window()
    {
        // Last resort: neither scheduled field is populated. The row is still
        // placeable, but flagged as unreliable rather than trusted.
        var inferred = new PanoptoSession
        {
            ScheduledStartTime = null,
            StartTime = null,
            AvailabilityWindowStart = new DateTime(2026, 12, 17, 12, 0, 0),
        };

        Assert.False(inferred.HasReliableTime);
        Assert.Equal(new DateTime(2026, 12, 17, 12, 0, 0), inferred.EffectiveStart);
    }

    [Fact]
    public void Session_status_labels_do_not_invent_names()
    {
        Assert.Equal("Scheduled", 1.Label());
        Assert.Equal("Status 97", 97.Label());
    }
}
