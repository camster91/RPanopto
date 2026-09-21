#if DEBUG
using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.App.ViewModels;

/// <summary>
/// A week of made-up sessions, so the calendar can be looked at on a machine
/// with no tenant — and so the layout can be checked without booking anything.
///
/// <para><b>None of this is real.</b> It exists only in a development build:
/// the whole file is inside <c>#if DEBUG</c>, so a packaged release has no code
/// path that can produce it and no way to be talked into it. A window opened
/// with <c>--self-test-week</c> says so in its status line, so it cannot be
/// mistaken for a schedule even by whoever ran it.</para>
///
/// <para>The rows are chosen to make every conditional piece of the grid draw at
/// once, because a fixture that only shows the happy path hides the bugs the
/// conditions exist for: two sessions overlapping in one room (side-by-side
/// widths), a twenty-minute session (a block too short for its time line), a
/// completed session (not draggable), a row with no reliable start time (the
/// warning glyph), and more recorders than the palette has colours.</para>
///
/// <para><b>The details panel is covered by the same rule.</b> It shows about a
/// dozen fields and half of them are conditional, so a fixture that filled only
/// the common ones would leave the panel's other branches drawn by nothing —
/// on the machine where the panel is being worked on, which is a machine with no
/// tenant. So the rows below deliberately include a session with no presenters,
/// one this account cannot write to, one with no Panopto link reported, one whose
/// two names differ, and one whose presenter lists are different lengths.</para>
/// </summary>
internal static class DebugFixtureWeek
{
    public static IReadOnlyList<PanoptoSession> Sessions(DateOnly today)
    {
        var day = today.ToDateTime(new TimeOnly(0, 0));
        var tomorrow = day.AddDays(1);

        return
        [
            // A full hour, and a second recording in the same room at the same
            // time — the case that has to draw two blocks side by side.
            //
            // The overflow capture is also the session this account cannot write
            // to, so the panel's "cannot write" wording and its hidden editor link
            // are both drawn.
            Make("FIN 101 — Lecture 01", "Rotman 1050", day.AddHours(9), 60,
                presenters: ["Maya", "Chen"], owner: "Maya Chen", views: 1284),

            Make("FIN 101 — Overflow capture", "Rotman 1050", day.AddHours(9), 60,
                writable: false, presenters: ["Maya", "Chen"], owner: "Maya Chen"),

            // Two first names against one surname: the mismatched-length case in
            // the panel's presenter zip, which is the one a hand-written join gets
            // wrong by producing a trailing space.
            Make("MGT 2010 — Case study", "Rotman 1060", day.AddHours(10.5), 90,
                presenters: ["Dana", "Whitfield", "Okafor"], surnames: ["Whitfield"],
                owner: "Dana Whitfield", views: 412),

            Make("RSM 1210 — Tutorial", "Rotman 1080", day.AddHours(14), 60, status: 3,
                presenters: ["Sam"], surnames: ["Iqbal"], owner: "Sam Iqbal", views: 97),

            // Twenty minutes: too short for a title and a time line both, so the
            // block has to go compact.
            Make("Quick test recording", "Rotman 1070", day.AddHours(13), 20),

            // No Panopto link reported, so the panel's "no link" line is drawn
            // rather than only its two Hyperlinks.
            Make("Studio B — rehearsal", "Studio B", day.AddHours(12), 45,
                links: false, presenters: ["Jo"], surnames: ["Beaulieu"]),

            // Two names that differ, so the panel's second title line is drawn.
            // Panopto keeps both and the panel shows the second only when it adds
            // something.
            Make("Rotman 1100 — standing booking", "Rotman 1100", day.AddHours(11), 60,
                delivery: "Rotman 1100 — standing booking (AV capture)",
                broadcast: true, presenters: ["Alex"], surnames: ["Nakamura"]),

            // No reliable time at all — only an availability window, which is the
            // fallback. Draws the warning glyph.
            new()
            {
                SessionID = Guid.NewGuid().ToString(),
                SessionName = "Booked from the room panel",
                RemoteRecorderName = "Rotman 1090",
                FolderName = "Rotman AV",
                AvailabilityWindowStart = day.AddHours(15),
                Duration = 45 * 60,
                Status = 1,
                OwnerFullName = "Room panel",
                IsBroadcast = false,
            },

            // Enough recorders to run past the palette, which wraps at six. The
            // seventh room sharing a colour with the first is what the fixture is
            // for; it is not a bug being papered over.
            //
            // A different room from the 1100 row above, deliberately: two sessions
            // in one room at one time is a case the grid has to draw and the panel
            // must not be ambiguous about, so it is covered once, on 1050, and not
            // repeated where it would only be confusing to look at.
            Make("Rotman 1110 — standing booking", "Rotman 1110", day.AddHours(16), 60),
            Make("Rotman 1120 — standing booking", "Rotman 1120", day.AddHours(17), 60),
            Make("Rotman 1130 — standing booking", "Rotman 1130", day.AddHours(18), 60),

            Make("Tomorrow's lecture", "Rotman 1050", tomorrow.AddHours(10), 60),
        ];
    }

    /// <summary>
    /// The rooms the tenant has — a superset of the ones booked this week.
    ///
    /// <para>The unbooked ones are the point. The legend has to list a room that
    /// exists with nothing scheduled: no stripe, still clickable, filtering the
    /// grid to an empty week. That state does not occur in a fixture built only
    /// from sessions, which is exactly how the legend came to be read as a
    /// complete inventory when it was nothing of the sort.</para>
    ///
    /// <para>"Event Space 214" is in here because it is the room that was looked
    /// for and not found.</para>
    /// </summary>
    public static IReadOnlyList<string> Rooms { get; } =
    [
        "Event Space 214",

        // The nine that have bookings this week.
        "Rotman 1050", "Rotman 1060", "Rotman 1070", "Rotman 1080", "Rotman 1090",
        "Rotman 1100", "Rotman 1110", "Rotman 1120", "Rotman 1130",

        // And four that do not.
        "Rotman 1140", "Rotman 1150", "Studio A", "Studio B",
    ];

    /// <summary>
    /// One fixture session.
    ///
    /// <para><b>The defaults are the common case, and the panel's conditions are
    /// exercised by the callers that depart from them.</b> A row made with no
    /// presenters and no links is not an oversight — it is the state that draws
    /// the panel's "not named" and "no link was reported" wording, and one row
    /// below is made that way on purpose.</para>
    ///
    /// <para><b>Presenters are two lists, not one.</b> <c>Data.svc</c> sends first
    /// and last names separately and the panel zips them by index, so the fixture
    /// sends them separately too. One row below has more first names than surnames,
    /// because a join that assumes equal lengths is the bug the zip exists to
    /// avoid and a fixture where the lengths always match cannot show it.</para>
    /// </summary>
    /// <param name="presenters">First names, or none for a session with no presenter.</param>
    /// <param name="surnames">
    /// Last names. Shorter than <paramref name="presenters"/> on one row on purpose.
    /// </param>
    /// <param name="writable">
    /// Whether this account may change the session. False draws the panel's
    /// read-only wording and hides the editor link, which Panopto still returns
    /// for a session this account cannot edit — that is the whole reason the panel
    /// gates the link on this rather than on the URL being present.
    /// </param>
    /// <param name="links">
    /// Whether to report the two Panopto URLs. False draws the panel's "no link"
    /// line instead of its two links.
    /// </param>
    private static PanoptoSession Make(
        string name,
        string recorder,
        DateTime start,
        double minutes,
        int status = 1,
        string[]? presenters = null,
        string[]? surnames = null,
        string? owner = null,
        string? delivery = null,
        bool broadcast = false,
        bool writable = true,
        bool links = true,
        long views = 0)
    {
        // A local id, so the two Panopto URLs below name the session they belong
        // to rather than sharing one guid across the week — a panel that copied an
        // id from the wrong row would look right in a fixture where every id was
        // the same.
        var id = Guid.NewGuid();

        return new PanoptoSession
        {
            SessionID = id.ToString(),
            SessionName = name,

            // The literal the tenant actually sends. Measured on the Rotman
            // tenant: DeliveryName is the string "default" for every one of 213
            // scheduled sessions, so a fixture that left it null would be
            // modelling a shape the server never produces — and the panel's
            // second-title line printed "default" under every recording because
            // of exactly that. Written as a literal rather than shared with the
            // panel's own constant on purpose: this is pretending to be Panopto,
            // not agreeing with the app.
            DeliveryName = delivery ?? "default",
            RemoteRecorderName = recorder,
            FolderName = "Rotman AV",
            StartTime = start,
            Duration = minutes * 60,
            Status = status,
            PresenterFirstNames = presenters is null ? null : [.. presenters],
            PresenterLastNames = surnames is null ? null : [.. surnames],
            OwnerFullName = owner,
            IsBroadcast = broadcast,
            HasWriteAccess = writable,
            IsEditable = writable,
            AnalyticsAllTimeViewCount = views > 0 ? views : null,
            ViewerUrl = links ? $"https://rotman.ca.panopto.com/Panopto/Pages/Viewer.aspx?id={id:D}" : null,
            EditorUrl = links ? $"https://rotman.ca.panopto.com/Panopto/Pages/Sessions/List.aspx#folderID={id:D}" : null,
        };
    }
}
#endif
