using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PanoptoScheduler.Core.Json;

namespace PanoptoScheduler.Core.Clients;

/// <summary>
/// Builds and reads the SOAP message bodies.
///
/// <para>Reading matches on local name and ignores namespaces. Panopto spreads
/// its response types across half a dozen namespaces (<c>V40</c>, <c>V42.Soap</c>,
/// the serialization arrays), and pinning each one would mean the reader breaks
/// whenever a field moves between API versions. Local names have been stable
/// across every version in use.</para>
/// </summary>
internal static partial class SoapXml
{
    /// <summary>An operation element, in the <c>tempuri.org</c> contract namespace.</summary>
    public static XElement Operation(string name, params XElement[] children)
        => new(PanoptoXml.Tns + name, children);

    public static XElement Element(XNamespace ns, string name, object? content)
        => new(ns + name, content);

    public static XElement Element(XNamespace ns, string name, IEnumerable<XElement> children)
        => new(ns + name, children);

    /// <summary>
    /// Several child elements at once. A single <see cref="XElement"/> argument
    /// still binds to the <c>object</c> overload above — C# prefers the normal
    /// form over the expanded one — and <see cref="XElement"/>'s own constructor
    /// treats an element passed as content as a child, so both paths agree.
    /// </summary>
    public static XElement Element(XNamespace ns, string name, params XElement[] children)
        => new(ns + name, children);

    // ---- Reading --------------------------------------------------------

    public static XElement? Child(XElement? parent, string localName)
        => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    public static IEnumerable<XElement> Children(XElement? parent, string localName)
        => parent is null
            ? []
            : parent.Elements().Where(e => e.Name.LocalName == localName);

    /// <summary>
    /// The items of a repeated field.
    ///
    /// <para>Panopto wraps every array in an element named after the field —
    /// <c>PagedResults</c>, <c>Results</c>, <c>ConflictingSessions</c> — whose
    /// children are the items. The wrapper has to be stepped through: handing it
    /// to a reader that expects an item does not throw, it just finds none of the
    /// fields it wants and silently yields an empty result.</para>
    /// </summary>
    public static IEnumerable<XElement> Items(XElement? parent, string arrayName)
        => Children(parent, arrayName).SelectMany(array => array.Elements());

    /// <summary>
    /// A child's text, or null when absent, empty, or explicitly nil. Panopto
    /// represents "no value" as a present element with <c>xsi:nil="true"</c>, so
    /// reading the text without checking nil yields "" and hides the absence.
    /// </summary>
    public static string? Text(XElement? parent, string localName)
    {
        var element = Child(parent, localName);
        if (element is null || PanoptoSoapClient.IsNil(element)) return null;

        var value = element.Value.Trim();
        return value.Length == 0 ? null : value;
    }

    public static string? Text(XElement? element)
    {
        if (element is null || PanoptoSoapClient.IsNil(element)) return null;

        var value = element.Value.Trim();
        return value.Length == 0 ? null : value;
    }

    public static bool Bool(XElement? parent, string localName)
        => bool.TryParse(Text(parent, localName), out var value) && value;

    public static Guid? Guid(XElement? element)
    {
        var text = Text(element);
        return System.Guid.TryParse(text, out var value) ? value : null;
    }

    /// <summary>
    /// The guids of a repeated child. WCF arrays wrap each item in an element
    /// named after the item type, so the item name varies by field; every
    /// child element that parses as a guid is taken.
    ///
    /// <para>Stepping through the child elements rather than
    /// <see cref="XContainer.Descendants"/> from the wrapper: the wrapper's own
    /// concatenated text parses as a guid in the single-item case Panopto
    /// actually returns for one booking — the same guid, twice — so a count or
    /// cleanup loop over the result would act on the one session twice. The
    /// multi-item case hides the defect, because concatenating two guids does
    /// not parse, which is exactly why the earlier test could not see it.</para>
    /// </summary>
    public static IReadOnlyList<Guid> Guids(XElement? parent, string localName)
        => Children(parent, localName)
            .Elements()
            .Select(Guid)
            .Where(g => g is not null && g != System.Guid.Empty)
            .Select(g => g!.Value)
            .ToList();

    /// <summary>One line per clashing session, for the import report.</summary>
    /// <param name="roomZone">
    /// The zone the rooms keep time in, for a clash time the tenant sends as an
    /// instant. See <see cref="DateTime(XElement?, string, TimeZoneInfo?)"/>.
    /// </param>
    public static string DescribeConflict(XElement element, TimeZoneInfo? roomZone = null)
    {
        var name = Text(element, "SessionName");
        var start = DateTime(element, "StartTime", roomZone);

        if (name is null && start is null) return string.Empty;

        var when = start is null
            ? "already booked"
            : start.Value.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

        return name is null ? when : $"{name} ({when})";
    }

    /// <summary>
    /// Reads an <c>xs:dateTime</c> as the room's wall clock, which is how every
    /// other time in the app is held.
    ///
    /// <para>Delegated to <see cref="WcfDateTimeConverter.TryParse"/> so the SOAP
    /// path and the read path agree on what a value means. They had separate
    /// implementations of this, which is how the two came to disagree about the
    /// offset form. An unrecognised value returns null rather than throwing
    /// mid-batch.</para>
    ///
    /// <para><b>Except for an ISO value that names its own offset, when the room's
    /// zone is known.</b> The digits-only rule was measured on <c>Data.svc</c>
    /// reads, whose values carry no meaningful offset; nothing measured it for a
    /// SOAP reply. And SOAP's <c>xs:dateTime</c> is the one place the tenant has
    /// already been shown to mean what it says: a <c>Z</c> on the way in is UTC —
    /// that is why <see cref="Scheduling.RoomClock.ToWire"/> exists — so a
    /// <c>Z</c> on the way out is read the same way. Taking its digits as they
    /// stand showed a 09:00 Toronto clash as 13:00, a time at which nothing is
    /// booked, which sends the operator looking for the wrong recording. A value
    /// with no offset, and WCF's <c>/Date(…)/</c> form, keep the measured rule,
    /// as does every caller that passes no zone.</para>
    /// </summary>
    /// <param name="roomZone">
    /// The zone the rooms keep time in. Null keeps the digits-only reading for
    /// every shape, which is what a caller with no zone to hand has always had.
    /// </param>
    public static DateTime? DateTime(XElement? parent, string localName, TimeZoneInfo? roomZone = null)
    {
        if (Text(parent, localName) is not { } text) return null;

        if (roomZone is not null && TryReadInstant(text, out var instant))
            return Scheduling.RoomClock.FromWire(instant, roomZone);

        return WcfDateTimeConverter.TryParse(text, out var value) ? value : null;
    }

    /// <summary>
    /// An ISO <c>xs:dateTime</c> that ends in <c>Z</c> or an explicit offset,
    /// read as the instant it names. False for anything else, including a bare
    /// ISO value — parsing that as a <see cref="DateTimeOffset"/> would stamp it
    /// with this machine's offset, and this machine has nothing to say about the
    /// room.
    /// </summary>
    private static bool TryReadInstant(string text, out DateTimeOffset instant)
    {
        instant = default;

        return IsoWithOffset().IsMatch(text)
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind, out instant);
    }

    [GeneratedRegex(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex IsoWithOffset();

    // ---- Writing --------------------------------------------------------

    public static string Boolean(bool value) => value ? "true" : "false";

    /// <summary>
    /// An <c>xs:dateTime</c> for the wire, from an instant.
    ///
    /// <para><b>The trailing <c>Z</c> means UTC, and the value has to be converted
    /// before it gets here.</b> This method used to be documented as the opposite —
    /// that the digits are the room's wall clock and must not be converted, on the
    /// strength of measurements that were all of the read path. A read saying "the
    /// value stored is a wall clock" says nothing about what a write means, and the
    /// live tenant settled it: one booking written both ways into a free slot
    /// landed four hours early as wall-clock digits, and on the hour as an
    /// instant.</para>
    ///
    /// <para>The conversion belongs to the caller, because it needs the room's
    /// zone; <see cref="Scheduling.RoomClock.ToWire"/> is the one place that does
    /// it. The <see cref="DateTimeKind"/> check below is what keeps the two from
    /// being confused again.</para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value is not an instant. A wall clock must be converted first — see
    /// <see cref="Scheduling.RoomClock.ToWire"/>.
    /// </exception>
    public static string DateTime(DateTime value)
        => value.Kind is DateTimeKind.Utc
            ? value.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : throw new ArgumentException(
                $"A wall clock cannot be put on the wire as it stands: this is Kind={value.Kind}, " +
                "and Panopto reads the Z as UTC. Convert it with RoomClock.ToWire first — " +
                "unconverted, the booking lands at the wrong hour and nothing reports it.",
                nameof(value));
}
