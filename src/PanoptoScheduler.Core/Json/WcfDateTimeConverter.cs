using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PanoptoScheduler.Core.Json;

/// <summary>
/// Reads the date formats WCF's <c>Data.svc</c> actually emits, as the room's
/// wall clock.
///
/// <para>These are NOT ISO-8601. WCF writes Microsoft's legacy format:
/// <c>/Date(1758000000000)/</c>, or <c>/Date(1758000000000-0400)/</c> when an
/// offset is present. A naive parse either throws or silently misreads them, and
/// in a scheduling tool a silent misread puts recordings on the wrong day — so
/// both shapes are handled explicitly.</para>
///
/// <para><b>The milliseconds are the room's wall clock, not an instant.</b>
/// Measured against the live tenant rather than inferred: across seven scheduled
/// sessions, reading them as an instant put every one of them four hours before
/// the time written into its own name, while reading them as a wall clock matched
/// all seven to the minute. The zone is applied further down the chain — by the
/// remote recorder's own configuration — which is why the value carries no
/// meaningful offset, and why converting by one moves the recording.</para>
///
/// <para><b>The type says so.</b> This used to hand back a
/// <see cref="DateTimeOffset"/> stamped with the machine's own offset, so that
/// <c>LocalDateTime</c> would undo the stamp and give the digits back. Every
/// consumer therefore had to remember the trick, the trick only worked while the
/// stamping zone and the reading zone were the same one, and the offset itself
/// meant nothing. A <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/>
/// cannot be mistaken for an instant and no machine is consulted to produce it.</para>
///
/// <para>The offset, when the wire carries one, is discarded — it names the zone
/// the digits are already expressed in, so applying it again would move the room's
/// clock rather than correct it. The same rule is applied to an ISO value with an
/// offset, which previously was read as an instant and so displayed shifted by
/// this machine's distance from UTC.</para>
///
/// <para><b>This is the read direction only, and it is not symmetric with the
/// write.</b> Nothing here says what a value means on the way out: a write must
/// name the instant, and the same "it is a wall clock" reasoning applied to the
/// write put a booking four hours early. That direction lives in
/// <see cref="Scheduling.RoomClock"/>.</para>
/// </summary>
public sealed partial class WcfDateTimeConverter : JsonConverter<DateTime?>
{
    // The trailing slash is optional: WCF's JSON emits /Date(…)/ with it, but
    // the SOAP reader has to cope with values that arrive without.
    [GeneratedRegex(@"^/Date\((?<ms>-?\d+)(?<off>[+-]\d{4})?\)/?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex WcfDate();

    /// <summary>
    /// The milliseconds the conversion can name at all: the range
    /// <see cref="DateTimeOffset"/> accepts. A value outside it is
    /// unrecognised rather than fatal, because the contract here is that one
    /// bad value becomes null and the rest of the page still reads — and
    /// <c>/Date(9223372036854775807)/</c>, the classic .NET "unset" sentinel,
    /// is exactly the in-shape, out-of-range value that a parse-first
    /// implementation would abort the whole page over.
    /// </summary>
    private const long MinEpochMs = -62_135_596_800_000;
    private const long MaxEpochMs = 253_402_300_799_999;

    /// <summary>
    /// Parses the date shapes Panopto puts on the wire, into the wall clock they
    /// spell out.
    ///
    /// <para>Public and static because the SOAP reader sees the same formats. The
    /// two had separate implementations of this, which is how the SOAP one came
    /// to handle the <c>/Date(…-0400)/</c> form differently from this one.</para>
    ///
    /// <para>The result always carries <see cref="DateTimeKind.Unspecified"/>,
    /// whatever the wire said. A marker of <c>Local</c> or <c>Utc</c> would invite
    /// exactly the conversion this whole type exists to prevent.</para>
    /// </summary>
    public static bool TryParse(string? raw, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var match = WcfDate().Match(raw);
        if (!match.Success)
        {
            // ISO. A value that names an offset is taken at the digits it shows,
            // for the same reason the WCF form is: the offset names the zone the
            // value is already expressed in. One that does not is the wall clock
            // it looks like anyway.
            if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var iso))
                return false;

            value = DateTime.SpecifyKind(iso.DateTime, DateTimeKind.Unspecified);
            return true;
        }

        // Parsed, not assumed in range: the regex accepts any digit run, and
        // long.Parse would throw out of a TryParse whose whole contract is
        // "unrecognised becomes null, and the page keeps reading".
        if (!long.TryParse(match.Groups["ms"].Value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var epochMs)
            || epochMs is < MinEpochMs or > MaxEpochMs)
        {
            return false;
        }

        value = WallClockOf(epochMs);
        return true;
    }

    /// <summary>
    /// The clock face a WCF millisecond count spells out.
    ///
    /// <para>The count is read as a clock face rather than as an instant: the
    /// digits it names are the room's time, and that is the whole of the measured
    /// model.</para>
    /// </summary>
    private static DateTime WallClockOf(long epochMs)
        => DateTime.SpecifyKind(
            DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime,
            DateTimeKind.Unspecified);

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        if (reader.TokenType == JsonTokenType.String)
            return TryParse(reader.GetString(), out var parsed) ? parsed : null;

        // A bare number is the same value in the same units, so it reads the same
        // way rather than becoming an instant just because it arrived unquoted.
        // The range check is the same one the string path applies: the reader
        // hands back any long it parsed, and the conversion would throw on one
        // outside the DateTimeOffset range instead of returning null.
        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt64(out var epochMs) &&
            epochMs is >= MinEpochMs and <= MaxEpochMs)
            return WallClockOf(epochMs);

        return null;
    }

    /// <summary>
    /// The inverse of <see cref="Read"/>: the wall clock, in the WCF form, so a
    /// value survives a round trip. Writing the instant would not — it would come
    /// back shifted by this machine's distance from UTC.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        var wall = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        var ms = new DateTimeOffset(wall, TimeSpan.Zero).ToUnixTimeMilliseconds();

        writer.WriteStringValue($"/Date({ms})/");
    }
}
