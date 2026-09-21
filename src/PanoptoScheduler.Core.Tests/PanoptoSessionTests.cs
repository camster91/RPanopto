using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Tests;

/// <summary>
/// The derived facts on a session that are decisions rather than plumbing:
/// which of Panopto's several names is the one to show, and which of its several
/// time fields is the one to read.
///
/// <para><b>Why these are worth facts of their own.</b> Every one of them
/// replaced a rule that was first written in a WPF view model — where nothing can
/// reach it — and each was found to be wrong by looking at live traffic rather
/// than by a test. <see cref="PanoptoSession.SecondName"/> printed the word
/// "default" under every recording on the calendar for a revision. That is the
/// cost of putting a payload rule somewhere unprovable, and these are where it
/// goes instead.</para>
/// </summary>
public class PanoptoSessionTests
{
    // ---------------------------------------------------------------
    // SecondName — Panopto keeps two names and only one is usually worth
    // showing.
    // ---------------------------------------------------------------

    [Fact]
    public void Reports_a_delivery_name_that_genuinely_differs()
    {
        var session = new PanoptoSession
        {
            SessionName = "FIN 101 — Lecture 01",
            DeliveryName = "FIN 101 — Lecture 01 (AV capture)",
        };

        Assert.Equal("FIN 101 — Lecture 01 (AV capture)", session.SecondName);
    }

    [Fact]
    public void Reports_no_second_name_when_the_two_agree()
    {
        var session = new PanoptoSession
        {
            SessionName = "FIN 101 — Lecture 01",
            DeliveryName = "FIN 101 — Lecture 01",
        };

        Assert.Null(session.SecondName);
    }

    [Fact]
    public void Reports_no_second_name_when_the_delivery_name_is_absent()
    {
        var session = new PanoptoSession { SessionName = "FIN 101 — Lecture 01" };

        Assert.Null(session.SecondName);
    }

    /// <summary>
    /// The measured case, and the one that shipped wrong: every one of 213
    /// scheduled sessions on the Rotman tenant carries this literal. It differs
    /// from every session name, so a rule that only compared the two names treated
    /// Panopto's placeholder as a name and printed it as a second title on every
    /// recording in the week.
    /// </summary>
    [Fact]
    public void Treats_panoptos_placeholder_as_no_name_at_all()
    {
        var session = new PanoptoSession
        {
            SessionName = "147 RSM436H1F LEC0101 Tues 310PM on 12/22/2026 (Tue)",
            DeliveryName = "default",
        };

        Assert.Null(session.SecondName);
    }

    /// <summary>
    /// The placeholder is the server's string, so neither its casing nor stray
    /// whitespace is this app's to assume away.
    /// </summary>
    [Theory]
    [InlineData("default")]
    [InlineData("Default")]
    [InlineData("DEFAULT")]
    [InlineData("  default  ")]
    public void Suppresses_the_placeholder_however_the_server_spells_it(string deliveryName)
    {
        var session = new PanoptoSession { SessionName = "Some lecture", DeliveryName = deliveryName };

        Assert.Null(session.SecondName);
    }

    /// <summary>
    /// A real name that merely contains the word is a real name. The check is
    /// equality against the placeholder, not a search for it — otherwise the one
    /// delivery actually called "Default Room Capture" would lose its name.
    /// </summary>
    [Fact]
    public void Keeps_a_real_name_that_merely_contains_the_placeholder()
    {
        var session = new PanoptoSession
        {
            SessionName = "Some lecture",
            DeliveryName = "Default Room Capture",
        };

        Assert.Equal("Default Room Capture", session.SecondName);
    }

    // ---------------------------------------------------------------
    // EffectiveStart — the field the name gives no hint of.
    // ---------------------------------------------------------------

    /// <summary>
    /// <c>ScheduledStartTime</c> is null for every scheduled session on the live
    /// tenant — re-measured on 213 rows — so a calendar built on the field whose
    /// name means "when this is scheduled" renders empty while appearing to work.
    /// The time is in <c>StartTime</c>.
    /// </summary>
    [Fact]
    public void Reads_the_start_time_from_the_field_that_actually_holds_it()
    {
        var start = new DateTime(2026, 12, 22, 15, 5, 0);

        var session = new PanoptoSession
        {
            ScheduledStartTime = null,
            StartTime = start,
            AvailabilityWindowStart = start.AddHours(-1),
        };

        Assert.Equal(start, session.EffectiveStart);
        Assert.True(session.HasReliableTime);
    }

    /// <summary>
    /// The fallback still answers, and says so — an inferred time is shown and
    /// flagged rather than dropped, because an invisible session is worse than a
    /// doubted one.
    /// </summary>
    [Fact]
    public void Falls_back_to_the_availability_window_and_admits_it_is_inferred()
    {
        var window = new DateTime(2026, 12, 22, 15, 0, 0);

        var session = new PanoptoSession { AvailabilityWindowStart = window };

        Assert.Equal(window, session.EffectiveStart);
        Assert.False(session.HasReliableTime);
    }

    // ---------------------------------------------------------------
    // EffectiveDuration — seconds, not minutes.
    // ---------------------------------------------------------------

    /// <summary>
    /// <c>Duration</c> is in <b>seconds</b> and fractional. Read as minutes it
    /// drew every block in the calendar sixty times too tall — which is a bug you
    /// can see, and was, rather than one you have to reason about.
    /// </summary>
    [Fact]
    public void Reads_the_duration_as_seconds()
    {
        var session = new PanoptoSession { StartTime = new DateTime(2026, 12, 22, 15, 0, 0), Duration = 7200 };

        Assert.Equal(TimeSpan.FromHours(2), session.EffectiveDuration);
    }

    /// <summary>A fractional second count is a real value on the tenant, not a typo.</summary>
    [Fact]
    public void Accepts_a_fractional_second_count()
    {
        var session = new PanoptoSession { StartTime = new DateTime(2026, 12, 22, 15, 0, 0), Duration = 3599.535 };

        Assert.NotNull(session.EffectiveDuration);
        Assert.Equal(3599.535, session.EffectiveDuration!.Value.TotalSeconds, precision: 3);
    }

    /// <summary>
    /// An explicit start and end beat the duration field, because they are two
    /// statements rather than one and they agree.
    /// </summary>
    [Fact]
    public void Prefers_an_explicit_end_over_the_duration_field()
    {
        var start = new DateTime(2026, 12, 22, 15, 0, 0);

        var session = new PanoptoSession
        {
            ScheduledStartTime = start,
            ScheduledEndTime = start.AddMinutes(90),
            Duration = 7200,
        };

        Assert.Equal(TimeSpan.FromMinutes(90), session.EffectiveDuration);
    }

    // ---------------------------------------------------------------
    // EffectiveEnd — derived from the pair the block is drawn from.
    // ---------------------------------------------------------------

    /// <summary>
    /// The end is the start plus the length, through the same two properties the
    /// calendar draws a block from.
    ///
    /// <para>A bulk retime asks this for the slot each recording is in now, so that
    /// each one keeps its own length. Reading <c>ScheduledEndTime</c> directly would
    /// have answered null on the live tenant — it is null wherever
    /// <c>ScheduledStartTime</c> is — and the retime would then have had no length
    /// to keep for any of the 213 sessions, which is a failure that looks like "no
    /// data" rather than like a bug.</para>
    /// </summary>
    [Fact]
    public void Ends_where_the_start_plus_the_duration_says()
    {
        var session = new PanoptoSession
        {
            ScheduledStartTime = null,
            StartTime = new DateTime(2026, 12, 22, 15, 0, 0),
            Duration = 5400,
            ScheduledEndTime = null,
        };

        Assert.Equal(new DateTime(2026, 12, 22, 16, 30, 0), session.EffectiveEnd);
    }

    /// <summary>
    /// Null when there is no length, rather than a start with an invented end. A
    /// retime is refused for such a row by name; guessing a length would move the
    /// recording to a slot nobody chose.
    /// </summary>
    [Fact]
    public void Has_no_end_when_there_is_no_length()
    {
        var session = new PanoptoSession { StartTime = new DateTime(2026, 12, 22, 15, 0, 0) };

        Assert.Null(session.EffectiveEnd);
    }
}
