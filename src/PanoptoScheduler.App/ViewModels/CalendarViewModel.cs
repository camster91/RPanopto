using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Media;
using PanoptoScheduler.Core;
using PanoptoScheduler.Core.Clients;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Import;
using PanoptoScheduler.Core.Layout;
using PanoptoScheduler.Core.Models;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.App.ViewModels;

/// <summary>
/// Drives the week calendar.
///
/// Reads go through the internal <c>Data.svc</c> because the public REST API
/// cannot enumerate scheduled recordings. That endpoint is undocumented, so it
/// is reached only through <see cref="DataSvcClient"/> — swapping the read
/// strategy later should not touch this class.
/// </summary>
public sealed class CalendarViewModel : ObservableObject
{
    /// <summary>
    /// The grid's geometry for the window it is currently drawn in.
    ///
    /// <para>These three were compile-time constants and the calendar was
    /// therefore a fixed-size drawing: seven columns at 176px is about 1290px and
    /// sixteen hours at 56px is 896px, in a window that opens at 840px tall. So
    /// maximizing the window mostly added whitespace, and the axis that actually
    /// overflowed could not use the room it had. Fitted instead — see
    /// <see cref="ApplyMetrics"/> — with floors and ceilings so it stays readable
    /// at both ends.</para>
    /// </summary>
    private CalendarMetrics _metrics = CalendarMetrics.Default;

    /// <summary>Height of one hour row. Fitted to the window; see <see cref="_metrics"/>.</summary>
    public double HourHeight => _metrics.HourHeight;

    /// <summary>Width of one day column, without its gap.</summary>
    public double ColumnWidth => _metrics.ColumnWidth;

    /// <summary>The gap to the right of a column, so two days are not flush.</summary>
    public const double ColumnGap = 4;

    /// <summary>
    /// Column plus its gap — the horizontal distance between two days.
    ///
    /// <para>Not a constant, and it matters: a drag divides by this, so a pitch
    /// that lagged the fitted column width would drop a block a gap short of
    /// where it was aimed.</para>
    /// </summary>
    public double ColumnPitch => _metrics.ColumnPitch;

    /// <summary>
    /// The row of day names above the grid. The gutter and the grid both start
    /// below this, so it is stated once here rather than as a margin in two
    /// places that have to keep agreeing.
    ///
    /// <para>Stays constant where the other two do not: the day-name row is a
    /// fixed piece of text at a fixed size, so stretching it would only move the
    /// grid down.</para>
    /// </summary>
    public const double HeaderHeight = 42;

    public const int FirstHour = 7;
    public const int LastHour = 23;

    /// <summary>How many hour rows the grid draws.</summary>
    public static int Hours => LastHour - FirstHour;

    /// <summary>
    /// Tall enough for a block's title and its time line: 11px of title plus
    /// 10px of time plus the margins around them. Anything shorter is drawn
    /// compact — see <see cref="SessionBlockViewModel.IsCompact"/>.
    ///
    /// <para>Constant on purpose, and it is the one number here that should not
    /// follow the window: it is a font threshold, not grid geometry. Scaling it
    /// with the hour would make a block's layout depend on the screen it is
    /// drawn on.</para>
    /// </summary>
    public const double FullBlockHeight = 32;

    /// <summary>Tall enough for the title alone.</summary>
    public const double CompactBlockHeight = 18;

    /// <summary>
    /// The height of the bookings canvas.
    ///
    /// <para>Body only, no header: the canvas sits inside the day column, which is
    /// already below the day-name row, so adding the header here would push every
    /// block 42px down the column and leave a matching gap at the foot.</para>
    /// </summary>
    public double GridHeight => _metrics.BodyHeight(Hours);

    /// <summary>
    /// The right-hand gap every cell carries, so a column's content is
    /// <see cref="ColumnWidth"/> wide and two columns sit <see cref="ColumnPitch"/>
    /// apart. Bound in XAML rather than written there as "0,0,4,0", because a
    /// literal in the markup is one more place the gap is asserted.
    /// </summary>
    public static Thickness CellGap { get; } = new(0, 0, ColumnGap, 0);

    /// <summary>
    /// How many day columns a week is drawn as, for
    /// <see cref="ApplyMetrics"/> before the first week has loaded.
    /// </summary>
    private const int DaysInWeek = 7;

    private double _scrollbarInset;

    /// <summary>
    /// How much width the bookings pane has lost to its vertical scrollbar right
    /// now, so the day-name row can give up the same amount.
    ///
    /// <para><b>Why this is measured rather than assumed.</b> The two panes are
    /// centred independently, and they only agree if the box they are centred in
    /// is the same one. It is not: the bookings sit in a ScrollViewer whose
    /// viewport is narrower than the header's by exactly the scrollbar, so at the
    /// ceiling — the one time there is leftover to distribute — centring both
    /// would set the day names about 8px off their own columns, which is the
    /// misalignment the whole translate-transform mechanism exists to avoid.</para>
    ///
    /// <para>It cannot be the constant in <see cref="CalendarMetrics"/> either:
    /// the ceiling is reached when sixteen hours at 110px still fit the height, and
    /// at that point there is no vertical scrollbar at all. Measured, both cases
    /// are right; assumed, one of them is wrong.</para>
    /// </summary>
    public double ScrollbarInset
    {
        get => _scrollbarInset;
        set
        {
            // Sub-pixel changes are the scrollbar animating in and out; a raise
            // for those would cascade into a layout pass for nothing.
            if (Math.Abs(_scrollbarInset - value) < 0.5) return;

            _scrollbarInset = value;

            Raise(nameof(ScrollbarInset));
            Raise(nameof(HeaderInset));
        }
    }

    /// <summary>
    /// <see cref="ScrollbarInset"/> as padding, because that is the shape the
    /// header needs it in.
    /// </summary>
    public Thickness HeaderInset => new(0, 0, _scrollbarInset, 0);

    /// <summary>
    /// One entry per hour the grid draws: the gutter labels, and the hour cells
    /// the rules and the labels are both repeated for.
    ///
    /// <para>A schedule reads in 12-hour terms, so "7 AM" rather than a 24-hour
    /// "07". The list ends at <see cref="LastHour"/> - 1: the last hour's
    /// boundary is the bottom edge of the grid, which the border draws, and a
    /// label under it would fall outside the week.</para>
    /// </summary>
    public IReadOnlyList<string> HourMarks { get; } =
        Enumerable.Range(FirstHour, LastHour - FirstHour)
            .Select(h => $"{h % 12 switch { 0 => 12, var n => n }} {(h < 12 ? "AM" : "PM")}")
            .ToList();

    /// <summary>
    /// The stripes, rebuilt by <see cref="AssignRecorderBrushes"/> on every
    /// rebuild. The colours themselves live in
    /// <see cref="RecorderPalette"/> — six of them here was the reason the
    /// seventh room inherited the first room's stripe, and the assignment is a
    /// property worth testing rather than one worth clicking.
    /// </summary>
    private readonly Dictionary<string, Brush> _recorderBrushes = new(StringComparer.OrdinalIgnoreCase);

    private readonly PanoptoConnection _panopto;

    /// <summary>
    /// The zone the rooms keep time in, taken once from the connection.
    ///
    /// <para><b>Nothing in this class reads this machine's zone.</b> The grid is
    /// the rooms' calendar: which day a block falls on, which column is today, and
    /// whether a move has gone into the past are all questions about the rooms, and
    /// answering them with the workstation's clock makes every one of them wrong on
    /// a laptop that has travelled — by four or five hours, silently.</para>
    /// </summary>
    private readonly TimeZoneInfo _roomZone;

    private DateOnly _weekStart;
    private IReadOnlyList<PanoptoSession> _sessions = [];

    /// <summary>
    /// Every room the tenant reports, independent of the week on screen.
    ///
    /// <para>This is the difference between the legend being a key for what is on
    /// screen and being the list of the tenant's rooms. It is what lets a room
    /// with nothing booked this week still be found, and it is the only reason a
    /// room can be clicked to see a week that is empty.</para>
    /// </summary>
    private IReadOnlyList<string> _tenantRooms = [];

    /// <summary>
    /// Whether the tenant's room listing was read to the end. Starts true so the
    /// fixture and the signed-out calendar — neither of which reads a listing —
    /// do not display a caveat about a read that never happened.
    /// </summary>
    private bool _roomsComplete = true;

    /// <summary>How many recorders the last read returned, for the note's number.</summary>
    private int _roomsRead;

#if DEBUG
    /// <summary>
    /// Makes the fixture week pretend its room read stopped short.
    ///
    /// <para>Set only by <c>--self-test-rooms</c>. Without it the banner that
    /// says the room list is incomplete is drawn by nothing on any machine that
    /// has no tenant, which is every machine this is developed on — so the one
    /// piece of UI that reports the original bug would be the one piece never
    /// looked at. That is the same mistake the legend's own assertion was added
    /// to stop.</para>
    /// </summary>
    private bool _fixtureRoomsIncomplete;
#endif

    /// <summary>
    /// Whether the tenant listing has been read for this sign-in. It answers
    /// "which rooms exist", which does not change as the operator pages through
    /// weeks, so re-reading it on every navigation would spend requests on a
    /// question already answered — against a limiter shared with the load itself.
    /// The Reload rooms button is the retry.
    /// </summary>
    private bool _roomsLoaded;

    /// <summary>The room the grid is filtered to, or empty for the whole week.</summary>
    private string _roomFilter = "";

    private string _roomSearch = "";
    private bool _showAllRooms;

    private RoomInventory _inventory =
        RoomInventory.Build(Array.Empty<string?>(), Array.Empty<string?>());

    private string _status = "Not signed in.";
    private string _detail = "Sign in to load the schedule.";
    private string _detailTooltip = "";
    private bool _isSignedIn;
    private bool _isBusy;

    public CalendarViewModel(PanoptoConnection panopto)
    {
        _panopto = panopto;
        _roomZone = panopto.RoomZone;
        _weekStart = StartOfWeek(RoomToday());

        _panopto.SignInUrlReady += url =>
        {
            // Kept so the prompt can offer the link when the browser does not
            // open — which is the normal case on a locked-down or headless
            // machine, and leaves the user with nothing to click otherwise.
            SignInUrl = url;

            Status = "Waiting for sign-in in your browser…";
            Detail = "Complete the sign-in in the browser tab that just opened.";

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // No browser, or it was blocked. The URL is on screen instead.
                Detail = "Could not open a browser. Open the link shown in this window.";
            }
        };

        SignInCommand = new AsyncRelayCommand(async () => await SignInAsync(), () => !IsSignedIn && !IsBusy);
        SignOutCommand = new RelayCommand(SignOut, () => IsSignedIn && !IsBusy);
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(), () => IsSignedIn && !IsBusy);
        PreviousWeekCommand = new AsyncRelayCommand(() => ShiftWeekAsync(-7), () => !IsBusy);
        NextWeekCommand = new AsyncRelayCommand(() => ShiftWeekAsync(7), () => !IsBusy);
        ThisWeekCommand = new AsyncRelayCommand(GoToThisWeekAsync, () => !IsBusy);

        ClearRoomFilterCommand = new RelayCommand(() => FilterToRoom(""), () => IsRoomFiltered);
        ReloadRoomsCommand = new AsyncRelayCommand(ReloadRoomsAsync, () => IsSignedIn && !IsBusy);

        Rebuild();
    }

    public ObservableCollection<DayColumnViewModel> Days { get; } = [];

    /// <summary>
    /// The ticked sessions, as the bulk tools address them.
    ///
    /// <para>Read from the blocks rather than tracked separately, so a rebuild
    /// after a reschedule cannot leave the selection pointing at a block that is
    /// no longer on screen.</para>
    ///
    /// <para><b>Carries the times and the description, not just the identity.</b>
    /// A retime has to shift from the slot each recording is already in, and a
    /// description edit has to show the text it is about to overwrite — neither of
    /// which can be worked out from an id. The operations that need nothing but an
    /// identity map down to the pair at their own call site, so the editor's older
    /// contract is unchanged.</para>
    ///
    /// <para>The times are taken from the block's own session, through the same
    /// <see cref="PanoptoSession.EffectiveStart"/> and
    /// <see cref="PanoptoSession.EffectiveEnd"/> the block was drawn from, so a
    /// shift moves a recording by exactly the length the operator can see.</para>
    /// </summary>
    public IReadOnlyList<SessionTarget> SelectedSessions =>
        Days.SelectMany(d => d.Blocks)
            .Where(b => b.IsSelected && b.SessionId is not null)
            .Select(b => new SessionTarget(
                b.SessionId!.Value,
                b.Title,
                b.Session.EffectiveStart,
                b.Session.EffectiveEnd,
                b.Session.Description))
            .ToList();

    public int SelectedCount => Days.SelectMany(d => d.Blocks).Count(b => b.IsSelected);

    public string SelectionSummary =>
        SelectedCount == 0 ? "" : $"{SelectedCount} selected";

    /// <summary>
    /// The block the details panel is describing, found by id among the blocks
    /// currently on screen.
    ///
    /// <para>Found rather than held, for the same reason
    /// <see cref="SelectedSessions"/> is read out of the blocks: a rebuild
    /// replaces every block with a new one, so a held reference would go on
    /// describing a block that is no longer drawn — and the panel would keep
    /// showing the times from before a reschedule. Looked up, it either finds the
    /// refreshed block or finds nothing, and nothing is honest.</para>
    /// </summary>
    public SessionBlockViewModel? SelectedBlock =>
        _detailsSessionId is null
            ? null
            : Days.SelectMany(d => d.Blocks).FirstOrDefault(b => b.SessionId == _detailsSessionId);

    /// <summary>Whether the details panel has anything to show.</summary>
    public bool HasSelectedBlock => SelectedBlock is not null;

    /// <summary>
    /// Opens a block in the details panel. Null closes it.
    ///
    /// <para>Deliberately separate from the tick in <see cref="SelectedBlock"/>
    /// terms: a tick is for a bulk operation over many sessions, and this is the
    /// one the operator is reading. Ticking three recordings and then opening a
    /// fourth should not clear the three.</para>
    /// </summary>
    public void SelectForDetails(SessionBlockViewModel? block)
    {
        var id = block?.SessionId;

        if (_detailsSessionId == id) return;

        _detailsSessionId = id;
        NotifyDetailsChanged();
    }

    /// <summary>Closes the details panel without touching the ticks.</summary>
    public void ClearDetails()
    {
        if (_detailsSessionId is null) return;

        _detailsSessionId = null;
        NotifyDetailsChanged();
    }

    private void NotifyDetailsChanged()
    {
        Raise(nameof(SelectedBlock));
        Raise(nameof(HasSelectedBlock));

        // The copy button's availability follows the selection, and there is no
        // CommandManager requery anywhere in this app — so a command whose
        // CanExecute changed has to say so itself or it stays in whatever state it
        // was first asked in. The edit commands are raised by SeedEdits below.
        CopySessionIdCommand.RaiseCanExecuteChanged();

        // The panel's fields are filled here and nowhere else: this runs when the
        // selection changes, when it is cleared, and once after a successful write
        // — the three moments the values on screen stop being the ones in the
        // boxes. Deliberately not on every rebuild, which would throw away what is
        // being typed the moment anything resized.
        SeedEdits();
    }

    /// <summary>
    /// Raised when the operator asks for the session id to be put on the
    /// clipboard, carrying the text to place there.
    ///
    /// <para>An event rather than the view model reaching for
    /// <c>Clipboard.SetText</c> for the same reason <see cref="FullScreenRequested"/>
    /// is one: the clipboard is the window's, it can throw when another process is
    /// holding it, and a view model that touches it cannot be reasoned about
    /// without one. The window decides, and owns the failure.</para>
    /// </summary>
    public event Action<string, string>? CopyRequested;

    /// <summary>
    /// Copies the selected session's id, so it can be pasted into a Panopto URL,
    /// a support ticket, or a message to whoever administers the room.
    ///
    /// <para>The id is the one thing on this panel that a person has to move
    /// somewhere else, and reading sixteen hex groups off a screen by eye is how a
    /// support request arrives with a typo in it.</para>
    /// </summary>
    public RelayCommand CopySessionIdCommand =>
        _copySessionId ??= new RelayCommand(CopySessionId, () => SelectedBlock?.CanCopySessionId == true);

    private RelayCommand? _copySessionId;

    private void CopySessionId()
    {
        if (SelectedBlock is not { } block || block.SessionId is not { } id) return;

        CopyRequested?.Invoke(
            id.ToString("D"),
            $"Session id for \"{block.Title}\"");
    }

    private Guid? _detailsSessionId;

    /// <summary>
    /// Raised when only the user can restore the session: at startup with no
    /// saved session, and when Panopto rejects a token mid-session.
    ///
    /// <para>Carries the reason so the prompt can say why it is asking — being
    /// asked to sign in again with no explanation reads as a bug.</para>
    /// </summary>
    public event Func<string, Task>? SignInPromptRequested;

    /// <summary>
    /// Raised when the operator asks to fill the screen, from F11 or the toolbar
    /// button.
    ///
    /// <para>An event rather than the view model setting <c>WindowState</c>
    /// itself, for the same reason <see cref="SignInPromptRequested"/> is one:
    /// where a window is on screen is the window's business, and a view model
    /// that reaches for it cannot be reasoned about without one. Both entry points
    /// go through the one command, so the key and the button cannot drift.</para>
    /// </summary>
    public event Action? FullScreenRequested;

    /// <summary>Fills the screen, or gives it back. See <see cref="FullScreenRequested"/>.</summary>
    public RelayCommand ToggleFullScreenCommand =>
        _toggleFullScreen ??= new RelayCommand(() => FullScreenRequested?.Invoke());
    private RelayCommand? _toggleFullScreen;

    /// <summary>
    /// Asks the view to prompt. Returns once the user has dismissed it.
    ///
    /// <para>Only one prompt runs at a time. Signing in reloads the schedule,
    /// and a reload that is refused again lands back here — without this guard
    /// that is a stack of dialogs, each one hiding the last.</para>
    /// </summary>
    public async Task RequestSignInAsync(string reason)
    {
        if (_signInPromptOpen) return;

        _signInPromptOpen = true;
        try
        {
            await (SignInPromptRequested?.Invoke(reason) ?? Task.CompletedTask);
        }
        finally
        {
            _signInPromptOpen = false;
        }
    }

    private bool _signInPromptOpen;

    /// <summary>
    /// The URL the user must visit. Surfaced so the prompt can show it when the
    /// browser fails to open.
    /// </summary>
    public string? SignInUrl
    {
        get => _signInUrl;
        private set
        {
            if (!Set(ref _signInUrl, value)) return;
            Raise(nameof(HasSignInUrl));
        }
    }
    private string? _signInUrl;

    public bool HasSignInUrl => !string.IsNullOrEmpty(SignInUrl);

    /// <summary>
    /// Called by the view when a tick changes. The ticks live on the blocks, so
    /// the view model has no event of its own to notice them by.
    /// </summary>
    public void NotifySelectionChanged()
    {
        Raise(nameof(SelectedCount));
        Raise(nameof(SelectionSummary));
    }

    /// <summary>
    /// Clears every tick. Called after a bulk edit, because the names those
    /// blocks carry are now stale and a second operation on the same ticks would
    /// be acting on a description of the world that has changed.
    /// </summary>
    public void ClearSelection()
    {
        foreach (var block in Days.SelectMany(d => d.Blocks)) block.IsSelected = false;
    }

    public RelayCommand ClearSelectionCommand => _clearSelection ??= new RelayCommand(ClearSelection);
    private RelayCommand? _clearSelection;

    /// <summary>Closes the details panel. Does not touch the ticks.</summary>
    public RelayCommand ClearDetailsCommand => _clearDetails ??= new RelayCommand(ClearDetails);
    private RelayCommand? _clearDetails;

    // ---- The details panel's edits ---------------------------------------

    /// <summary>The date format the panel seeds itself with.</summary>
    /// <remarks>
    /// The import's own unambiguous format, deliberately. Seeding with
    /// "4/10/2012" would hand the operator a value the shared parser reads two
    /// ways, and the first thing they do is press Apply on it.
    /// </remarks>
    private const string EditDayFormat = "yyyy-MM-dd";

    /// <inheritdoc cref="EditDayFormat"/>
    private const string EditClockFormat = "h:mm tt";

    /// <summary>
    /// The five writable fields, each with its own Apply.
    ///
    /// <para><b>They live here and not on the block.</b> A rebuild replaces every
    /// block with a new instance — that is what makes the panel show refreshed
    /// values — so state kept on one would be thrown away by anything that
    /// triggered a rebuild, including a window resize. A half-typed name is not
    /// something a resize should be able to discard.</para>
    ///
    /// <para>Seeded when the <i>selection</i> changes and after a successful
    /// write, never on every rebuild, for the same reason: reseeding on rebuild
    /// would overwrite what is being typed.</para>
    /// </summary>
    public string EditName
    {
        get => _editName;
        set { if (Set(ref _editName, value)) RaiseEditCommands(); }
    }

    private string _editName = string.Empty;

    /// <summary>
    /// The description, which is where the legacy tool puts the presenter.
    ///
    /// <para>Shown before it is written, which is the point of reading
    /// <c>Abstract</c> back at all: an edit that silently replaced text the app
    /// never displayed is the accident the delete permit exists to prevent,
    /// arriving through a text box instead of a button.</para>
    /// </summary>
    public string EditDescription
    {
        get => _editDescription;
        set { if (Set(ref _editDescription, value)) RaiseEditCommands(); }
    }

    private string _editDescription = string.Empty;

    public bool EditBroadcast
    {
        get => _editBroadcast;
        set { if (Set(ref _editBroadcast, value)) RaiseEditCommands(); }
    }

    private bool _editBroadcast;

    public string EditDate
    {
        get => _editDate;
        set { if (Set(ref _editDate, value)) RaiseEditCommands(); }
    }

    private string _editDate = string.Empty;

    public string EditStart
    {
        get => _editStart;
        set { if (Set(ref _editStart, value)) RaiseEditCommands(); }
    }

    private string _editStart = string.Empty;

    public string EditEnd
    {
        get => _editEnd;
        set { if (Set(ref _editEnd, value)) RaiseEditCommands(); }
    }

    private string _editEnd = string.Empty;

    /// <summary>
    /// The folder to move into, by name or guid.
    ///
    /// <para><b>A hint rather than a selection from a list.</b> The move resolves
    /// it with the same code the bulk path uses — guid, then exact name, then
    /// containment — and that resolution is where the incomplete-listing refusal
    /// lives. A picker over every folder in the tenant would be a second, larger
    /// read for the same answer, and would be the one path that skipped the
    /// refusal.</para>
    /// </summary>
    public string EditFolder
    {
        get => _editFolder;
        set { if (Set(ref _editFolder, value)) RaiseEditCommands(); }
    }

    private string _editFolder = string.Empty;

    /// <summary>
    /// Folders matching <see cref="EditFolder"/>, so the name does not have to be
    /// known exactly. Narrowed by the search rather than listing the whole tree,
    /// which on a real tenant is a large read for a picker nobody scrolls.
    /// </summary>
    public ObservableCollection<string> FolderChoices { get; } = [];

    /// <summary>
    /// What the panel last did, or refused to do.
    ///
    /// <para>In the panel rather than only on the status line, which is the one
    /// place this deviates from the drag path: a drag moves the block, so the
    /// result is visible where the operator is looking. A form does not, and a
    /// validation failure reported only at the foot of the window leaves the bad
    /// value in the box for the next press of Apply.</para>
    /// </summary>
    public string EditNote
    {
        get => _editNote;
        private set { if (Set(ref _editNote, value)) Raise(nameof(HasEditNote)); }
    }

    private string _editNote = string.Empty;

    public bool HasEditNote => _editNote.Length > 0;

    /// <summary>
    /// Whether the panel's edits are armed: signed in, the block reporting
    /// itself writable, and nothing already running. Writability is Panopto's
    /// answer and not this app's, and everything below is gated on this, so
    /// the buttons are disabled with an explanation rather than failing on
    /// the press.
    ///
    /// <para>Signed-in is a condition of its own, not something writability
    /// implies. A block can report itself writable from its own JSON while the
    /// app holds no token at all — which is the state of a fixture window, and
    /// of a real one whose sign-in failed or lapsed. Arming an edit there offers
    /// a button whose press can only throw, and the throw is what an operator
    /// saw: "Could not move the recording." over "Call SignInAsync first.",
    /// which reads as the app being broken rather than as nobody being signed
    /// in.</para>
    /// </summary>
    public bool CanEditSelection => IsSignedIn && SelectedBlock?.ReportsWritable == true && !IsBusy;

    public AsyncRelayCommand ApplyNameCommand => _applyName ??= new AsyncRelayCommand(
        () => ApplyEditAsync("Renamed", () => _panopto.BulkEditing.RenameAsync(
            [Target()], _ => EditName.Trim(), dryRun: false)),
        () => CanEditSelection && EditName.Trim().Length > 0);

    private AsyncRelayCommand? _applyName;

    /// <summary>
    /// Gated on the text having actually changed, like the webcast one.
    ///
    /// <para>Core would refuse an unchanged description anyway — it says
    /// "The description is unchanged." rather than spending a request — but a
    /// button whose only possible outcome is that sentence is noise. The rule is
    /// the same either way; this just stops the press happening.</para>
    /// </summary>
    public AsyncRelayCommand ApplyDescriptionCommand => _applyDescription ??= new AsyncRelayCommand(
        () => ApplyEditAsync("Set the description", () => _panopto.BulkEditing.SetDescriptionAsync(
            [TargetWithDescription()], _ => EditDescription, dryRun: false)),
        () => CanEditSelection && SelectedBlock is not null
              && !string.Equals(EditDescription, SelectedBlock.Session.Description ?? string.Empty,
                                StringComparison.Ordinal));

    private AsyncRelayCommand? _applyDescription;

    public AsyncRelayCommand ApplyBroadcastCommand => _applyBroadcast ??= new AsyncRelayCommand(
        () => ApplyEditAsync(
            EditBroadcast ? "Turned webcasting on" : "Turned webcasting off",
            () => _panopto.BulkEditing.SetBroadcastAsync(
                [Target()], EditBroadcast, dryRun: false)),
        () => CanEditSelection && SelectedBlock is not null
              && SelectedBlock.Session.IsBroadcast != EditBroadcast);

    private AsyncRelayCommand? _applyBroadcast;

    public AsyncRelayCommand ApplyRetimeCommand => _applyRetime ??= new AsyncRelayCommand(
        ApplyRetimeAsync, () => CanEditSelection);

    private AsyncRelayCommand? _applyRetime;

    public AsyncRelayCommand ApplyMoveCommand => _applyMove ??= new AsyncRelayCommand(
        () => ApplyEditAsync("Moved", () => _panopto.BulkEditing.MoveAsync(
            [Target()], EditFolder.Trim(), dryRun: false)),
        () => CanEditSelection && EditFolder.Trim().Length > 0);

    private AsyncRelayCommand? _applyMove;

    /// <summary>
    /// Fills <see cref="FolderChoices"/> with folders matching what has been typed.
    /// A read, and the only one in the panel.
    /// </summary>
    public AsyncRelayCommand FindFoldersCommand => _findFolders ??= new AsyncRelayCommand(
        FindFoldersAsync, () => !IsBusy && EditFolder.Trim().Length > 0);

    private AsyncRelayCommand? _findFolders;

    /// <summary>
    /// The one-element target every write below is built from.
    ///
    /// <para>Written as a list because the Core methods take one — that is what
    /// lets the panel and the bulk path share them, and a single-session overload
    /// would be a second path that could drift from the tested one.</para>
    /// </summary>
    private (Guid Id, string CurrentName) Target()
    {
        var block = SelectedBlock!;

        return (block.SessionId!.Value, block.Title);
    }

    /// <summary>
    /// The description write's target, which carries the current description
    /// because the comparison and the refusal to write an unchanged value both
    /// happen in Core.
    /// </summary>
    private (Guid Id, string CurrentName, string CurrentDescription) TargetWithDescription()
    {
        var block = SelectedBlock!;

        return (block.SessionId!.Value, block.Title, block.Session.Description ?? string.Empty);
    }

    private void RaiseEditCommands()
    {
        // Manually, because there is no CommandManager requery anywhere in this
        // app: a command whose CanExecute changed has to say so itself or it keeps
        // whatever answer it first gave.
        foreach (var command in EditCommands())
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private IEnumerable<AsyncRelayCommand> EditCommands()
    {
        yield return ApplyNameCommand;
        yield return ApplyDescriptionCommand;
        yield return ApplyBroadcastCommand;
        yield return ApplyRetimeCommand;
        yield return ApplyMoveCommand;
        yield return FindFoldersCommand;
    }

    private async Task ApplyEditAsync(string did, Func<Task<BulkEditReport>> run)
    {
        if (SelectedBlock is null) return;

        EditNote = string.Empty;
        IsBusy = true;
        RaiseEditCommands();

        try
        {
            var report = await run().ConfigureAwait(true);
            var result = report.Results.FirstOrDefault();

            // The trail is the record of what this write changed. If it could not
            // be written the change still happened, so the sentence rides along
            // with whichever note is about to be shown rather than being dropped
            // — the panel is where the operator is looking.
            var trail = report.AuditWarning is { } warning ? $" {warning}" : string.Empty;

            // A single write cannot be "partly" applied, so one row is the whole
            // answer — and Core's own sentence is the one that knows why, which is
            // why it is passed through rather than replaced with a summary.
            if (result is null)
            {
                EditNote = "Panopto was not asked to change anything.";
                return;
            }

            if (result.Outcome == SessionEditOutcome.Failed)
            {
                EditNote = result.Message + trail;
                return;
            }

            EditNote = $"{did}.{trail}";

            // The read comes first and the report second, because the read sets
            // the status line itself ("219 scheduled session(s)…") and would
            // otherwise overwrite what just happened with a count of the week.
            await ReloadSelectedAsync().ConfigureAwait(true);

            // Also on the status line, where every other write reports, so the
            // window's own account of itself does not go stale behind the panel's.
            Status = EditNote;
            Detail = result.Message + trail;
        }
        catch (OperationCanceledException ex) when (ProblemText.IsTimeout(ex))
        {
            // Not rethrown, and it used to be. The comment below is right that
            // this must never escape — AsyncRelayCommand.Execute is async void,
            // so an exception here ends the process rather than the operation —
            // and rethrowing is what made that true in the worst way. A timeout
            // is not the operator asking to stop, so it is not silently obeyed
            // either: the write was sent, and Panopto does not say whether it
            // landed.
            //
            // Said plainly rather than as "try again", because for this
            // operation a second attempt is a second change to the tenant.
            EditNote = "Panopto did not answer in time. The change may or may not "
                     + "have gone through — reopen the recording to check before "
                     + "pressing that again.";
        }
        catch (Exception ex)
        {
            // Never let this escape: AsyncRelayCommand.Execute is async void, so
            // an exception here ends the process rather than the operation.
            EditNote = $"That did not go through: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseEditCommands();
        }
    }

    /// <summary>
    /// Reads the three boxes, refuses anything the shared parser cannot read, and
    /// hands Core a wall clock.
    ///
    /// <para>The parse is the import's own — <see cref="LegacyScheduleReader.TryReadDate"/>
    /// and <see cref="LegacyScheduleReader.TryReadTime"/> are public for this —
    /// so a date typed here and the same date in a spreadsheet cannot mean
    /// different things.</para>
    /// </summary>
    private async Task ApplyRetimeAsync()
    {
        if (SelectedBlock is null) return;

        if (!LegacyScheduleReader.TryReadDate(EditDate, dayFirst: false, out var day, out var ambiguous))
        {
            // The example date is the session's own, never this machine's today.
            // The room can be in another zone, and the whole point of keeping the
            // calendar off the workstation clock is that it answers the same thing
            // wherever it runs — so an error message that suggested today would be
            // a small, wrong answer of exactly the kind the rest of this file is
            // arranged to avoid.
            var example = SelectedBlock.StartsAt is { Year: > 1 } room
                ? room.ToString(EditDayFormat, CultureInfo.InvariantCulture)
                : EditDayFormat;

            EditNote = $"Could not read the date \"{EditDate}\". Write it as {example}.";
            return;
        }

        if (!LegacyScheduleReader.TryReadTime(EditStart, out var start))
        {
            EditNote = $"Could not read the start time \"{EditStart}\". Try 2:00 PM.";
            return;
        }

        if (!LegacyScheduleReader.TryReadTime(EditEnd, out var end))
        {
            EditNote = $"Could not read the end time \"{EditEnd}\". Try 3:00 PM.";
            return;
        }

        // Clock faces, not instants: the room's zone is applied by the recorder on
        // the way out, and anything carrying a Kind here would be a value that had
        // picked up this machine's offset somewhere.
        var startsAt = DateTime.SpecifyKind(day.Date + start, DateTimeKind.Unspecified);
        var endsAt = DateTime.SpecifyKind(day.Date + end, DateTimeKind.Unspecified);

        // Held rather than written to EditNote, because the apply below empties it
        // first. Said rather than silently resolved, exactly as the import says it:
        // the value written is the one the parser chose, so the write still goes
        // ahead and the note explains which reading it was.
        var readAs = ambiguous
            ? $"Read \"{EditDate}\" as {startsAt.ToString(EditDayFormat, CultureInfo.InvariantCulture)}. "
            : string.Empty;

        await ApplyEditAsync("Retimed", () =>
        {
            var block = SelectedBlock!;

            return _panopto.BulkEditing.RetimeAsync(
                [new SessionRetime(block.SessionId!.Value, block.Title, startsAt, endsAt)],
                dryRun: false);
        }).ConfigureAwait(true);

        if (readAs.Length > 0) EditNote = readAs + EditNote;
    }

    private async Task FindFoldersAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        RaiseEditCommands();

        try
        {
            var matches = await _panopto.Sessions.ListFoldersAsync(EditFolder.Trim())
                .ConfigureAwait(true);

            FolderChoices.Clear();

            foreach (var folder in matches.Take(50))
            {
                FolderChoices.Add(folder.Name);
            }

            EditNote = FolderChoices.Count == 0
                ? $"No folder matches \"{EditFolder.Trim()}\"."
                : $"{FolderChoices.Count} matching folder(s)."
                  + (matches.Complete ? string.Empty : " The folder list was incomplete.");
        }
        catch (Exception ex)
        {
            EditNote = $"Could not list folders: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RaiseEditCommands();
        }
    }

    /// <summary>
    /// Re-reads the week so the panel and the calendar show what the tenant now
    /// holds, and re-seeds the fields from it.
    ///
    /// <para><b>A read after a write, which is normally the thing not to do.</b>
    /// It is worth it here for two reasons. It is one request: the read asks for
    /// the scheduled set, which on this tenant is a single page. And the
    /// alternative — patching the session in place — works for a name, a
    /// description and a time, but <b>not</b> for a move: the folder hint is
    /// resolved inside Core, so the folder that comes back is not the string that
    /// went in, and a block redrawn from the hint would show the operator a folder
    /// their recording is not in. Reading back is the only version of that answer
    /// this app can be sure of.</para>
    ///
    /// <para><see cref="LoadAsync"/> is told not to jump, so a rename does not
    /// move the operator off the week they are working in.</para>
    /// </summary>
    private async Task ReloadSelectedAsync()
    {
        var keep = _detailsSessionId;

        await LoadAsync(jumpToFirst: false).ConfigureAwait(true);

        // Put back, because Rebuild may have found nothing to match it to: if the
        // session left the week — moved to another date, or deleted — the panel
        // closes rather than describing something no longer drawn. Restoring the
        // id before the notification is what lets SelectedBlock's own lookup
        // decide, instead of this deciding for it.
        _detailsSessionId = keep;
        NotifyDetailsChanged();
    }

    /// <summary>
    /// Fills the panel's fields from the selected block, or empties them.
    /// </summary>
    private void SeedEdits()
    {
        var block = SelectedBlock;

        if (block is null)
        {
            _editName = _editDescription = _editDate = _editStart = _editEnd = _editFolder = string.Empty;
            _editBroadcast = false;
            EditNote = string.Empty;
            FolderChoices.Clear();
        }
        else
        {
            _editName = block.Title;
            _editDescription = block.Session.Description ?? string.Empty;
            _editBroadcast = block.Session.IsBroadcast;

            if (block.Session.EffectiveStart is { } from)
            {
                var to = from + block.Duration;

                _editDate = from.ToString(EditDayFormat, CultureInfo.InvariantCulture);
                _editStart = from.ToString(EditClockFormat, CultureInfo.InvariantCulture);
                _editEnd = to.ToString(EditClockFormat, CultureInfo.InvariantCulture);
            }
            else
            {
                // No time to seed from. Left blank rather than guessed at: a
                // pre-filled time the app invented would be applied by whoever
                // pressed Apply without reading it.
                _editDate = _editStart = _editEnd = string.Empty;
            }

            EditNote = string.Empty;
            FolderChoices.Clear();
        }

        // Raised as properties rather than through the setters, so seeding cannot
        // recurse back into RaiseEditCommands twelve times.
        Raise(nameof(EditName));
        Raise(nameof(EditDescription));
        Raise(nameof(EditBroadcast));
        Raise(nameof(EditDate));
        Raise(nameof(EditStart));
        Raise(nameof(EditEnd));
        Raise(nameof(EditFolder));

        RaiseEditCommands();
    }

    public AsyncRelayCommand SignInCommand { get; }
    public RelayCommand SignOutCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviousWeekCommand { get; }
    public AsyncRelayCommand NextWeekCommand { get; }
    public AsyncRelayCommand ThisWeekCommand { get; }

    /// <summary>Clears the room filter, which is also what clicking the room again does.</summary>
    public RelayCommand ClearRoomFilterCommand { get; }

    /// <summary>Re-reads the tenant's room list. The retry for a listing that failed.</summary>
    public AsyncRelayCommand ReloadRoomsCommand { get; }

    /// <summary>
    /// Puts a sentence on the status line from outside this class.
    ///
    /// <para>Exists so the window can report the outcomes only it can observe —
    /// the clipboard being held by another process, most of all — without the
    /// setters on <see cref="Status"/> and <see cref="Detail"/> becoming public.
    /// Everything else that writes them is a step in a Panopto operation, which is
    /// this class's own business; a copy that failed is not.</para>
    /// </summary>
    /// <param name="status">The one-line summary, always shown.</param>
    /// <param name="detail">
    /// The explanation behind the info button, or empty. Assigned before the
    /// status so the clearing in <see cref="Detail"/> does not wipe it.
    /// </param>
    public void Report(string status, string detail = "")
    {
        if (detail.Length > 0) Detail = detail;
        Status = status;
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        private set
        {
            if (!Set(ref _detail, value)) return;

            // Whatever was being explained is no longer on screen. Without this
            // a successful refresh would leave the previous failure's stack
            // trace hanging off the new, cheerful detail line — which is worse
            // than showing nothing, because it reads as current.
            //
            // The one place that wants both set at once assigns Detail first and
            // the tooltip second, so this clears and is then overwritten.
            DetailTooltip = "";
        }
    }

    /// <summary>
    /// The whole exception chain behind <see cref="Detail"/>, for that line's
    /// tooltip.
    ///
    /// <para>Empty when there is nothing wrong, so the tooltip shows nothing on a
    /// good day rather than repeating the detail line back at the reader.</para>
    /// </summary>
    public string DetailTooltip
    {
        get => _detailTooltip;
        private set => Set(ref _detailTooltip, value);
    }

    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set
        {
            if (!Set(ref _isSignedIn, value)) return;
            SignInCommand.RaiseCanExecuteChanged();
            SignOutCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            ReloadRoomsCommand.RaiseCanExecuteChanged();
            Raise(nameof(ShowsRoomBar));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            SignInCommand.RaiseCanExecuteChanged();
            SignOutCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            PreviousWeekCommand.RaiseCanExecuteChanged();
            NextWeekCommand.RaiseCanExecuteChanged();
            ThisWeekCommand.RaiseCanExecuteChanged();
            ReloadRoomsCommand.RaiseCanExecuteChanged();
        }
    }

    public string WeekLabel =>
        $"{_weekStart:MMM d} – {_weekStart.AddDays(6):MMM d, yyyy}";

    /// <summary>
    /// Whether the week has nothing in it. Seven empty columns look like a
    /// failure to load rather than an empty week, and the two want different
    /// things from whoever is looking: one wants the refresh button, the other
    /// wants to go to another week.
    /// </summary>
    public bool IsWeekEmpty => Days.All(day => day.Blocks.Count == 0);

    /// <summary>
    /// The rooms the legend shows: this week's first, in the order their colours
    /// were handed out, then the tenant's other rooms.
    ///
    /// <para>Colour alone is not a key. Twelve stripes is more than anyone
    /// memorises, two of them are always going to be close on somebody's
    /// monitor, and a person who cannot distinguish this app's red from its
    /// green has no way in at all. The legend is what makes a stripe a
    /// convenience rather than the answer.</para>
    ///
    /// <para><b>It is the tenant's rooms, not the week's.</b> It used to be built
    /// from the sessions loaded for the displayed week, so a room with nothing
    /// booked that week could not appear in it at all — and it was read,
    /// reasonably, as the list of rooms. It was a key for what was on screen and
    /// it looked like an inventory, and no wording fixes that: the list has to
    /// actually be the inventory. See <see cref="RoomInventory"/>.</para>
    /// </summary>
    public IReadOnlyList<RecorderLegendViewModel> Legend { get; private set; } = [];

    /// <summary>Whether there is anything to put in the legend.</summary>
    public bool HasLegend => Legend.Count > 0;

    /// <summary>
    /// Whether the rooms bar is drawn at all.
    ///
    /// <para>Signed in, because the bar carries the Reload rooms button and a
    /// listing that failed has to leave the way to retry it on screen — a bar that
    /// hides itself when it has nothing to show hides exactly when the reload is
    /// needed.</para>
    ///
    /// <para>Also whenever there are week rooms, which is what puts the bar in
    /// front of the debug fixture. That fixture runs signed out by design, and the
    /// legend is one of the things it exists to be able to look at.</para>
    /// </summary>
    public bool ShowsRoomBar => IsSignedIn || HasLegend;

    /// <summary>
    /// "This week — 6 of 412 rooms", so the row can never be read as complete
    /// while the rest is behind the toggle.
    ///
    /// <para>Carries <see cref="RoomsIncompleteMark"/> when the tenant's list was
    /// too long to finish reading, so the one number on screen that claims to be a
    /// total cannot be a lie. The sentence underneath says what it means; this
    /// says that there is one, because a reader who stops at the count is exactly
    /// the reader who concludes a room is missing from the tenant.</para>
    /// </summary>
    public string RoomHeader => _inventory.HasRooms
        ? $"This week — {_inventory.Summary}{RoomsIncompleteMark}"
        : "";

    /// <summary>Marks the header as counting a list that stopped short.</summary>
    private string RoomsIncompleteMark => RoomsIncomplete ? " (incomplete)" : "";

    /// <summary>
    /// Whether the tenant's room list was longer than the paging ceiling, so rooms
    /// this app has never heard of may exist.
    ///
    /// <para><b>Measured, on this tenant, not hypothetical.</b> Rotman reports
    /// 5000 recorders and no <c>TotalNumber</c>, and hit the old 20-page ceiling on
    /// every single sign-in. The report of "i dont see all the recorders from event
    /// space" was that ceiling, and the only trace of it was a log line. Raising
    /// the ceiling to 60 pages moves the wall; it does not remove it. This is what
    /// makes the wall visible when it is reached again.</para>
    /// </summary>
    public bool RoomsIncomplete => !_roomsComplete;

    /// <summary>
    /// The sentence under the header when the room list stopped short.
    ///
    /// <para>Addressed to the operator's actual situation: a room they expect is
    /// not in the list, and the useful fact is not "an error occurred" but that
    /// absence from this list is not evidence about the tenant.</para>
    /// </summary>
    public string RoomsIncompleteNote => _roomsComplete
        ? ""
        : $"Panopto has more rooms than this app read ({_roomsRead:N0}), so the list above is "
          + "some of them. A room you expect and cannot find may still exist.";

    /// <summary>Rooms the tenant has that have no booking in the week shown.</summary>
    public int HiddenRoomCount => _inventory.TotalRoomCount - _inventory.WeekRoomCount;

    public bool HasHiddenRooms => HiddenRoomCount > 0;

    /// <summary>The label on the toggle, which says how much is behind it.</summary>
    public string ShowAllRoomsLabel => $"Show all {_inventory.TotalRoomCount} rooms";

    /// <summary>
    /// Whether the whole inventory is on show rather than just this week's rooms.
    /// </summary>
    public bool ShowAllRooms
    {
        get => _showAllRooms;
        set
        {
            if (!Set(ref _showAllRooms, value)) return;
            BuildLegend();
        }
    }

    /// <summary>
    /// The search box over the inventory.
    ///
    /// <para>It searches every room, not only the ones on show. A box that only
    /// filtered the rows already visible would be useless for the case it exists
    /// for — typing a room's name is a request to find it, and the room most
    /// worth finding is the one with nothing booked this week.</para>
    /// </summary>
    public string RoomSearch
    {
        get => _roomSearch;
        set
        {
            if (!Set(ref _roomSearch, value ?? "")) return;
            BuildLegend();
        }
    }

    /// <summary>The room the grid is showing, or empty for the whole week.</summary>
    public string RoomFilter => _roomFilter;

    public bool IsRoomFiltered => _roomFilter.Length > 0;

    /// <summary>
    /// The sentence that says the calendar is not showing the whole week.
    ///
    /// <para>An empty grid has two completely different readings — nothing is
    /// booked, or nothing is booked <i>here</i> — and the operator has to be able
    /// to tell which one they are looking at without hunting for the control that
    /// filtered it.</para>
    /// </summary>
    public string FilterNote => IsRoomFiltered
        ? $"Showing {_roomFilter} only — the rest of the week is hidden."
        : "";

    /// <summary>What an empty grid says, which differs when a filter is on.</summary>
    public string EmptyWeekMessage => IsRoomFiltered
        ? $"Nothing scheduled for {_roomFilter} this week."
        : "Nothing scheduled this week.";

    /// <summary>
    /// Resumes a saved session if there is one, so a returning user goes
    /// straight to the schedule. Otherwise waits for the sign-in button.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!_panopto.Restore()) return;

        IsSignedIn = true;
        Status = "Restoring your saved session…";
        await LoadAsync();
    }

    /// <summary>
    /// Runs the browser sign-in and loads the schedule.
    ///
    /// <para>Public so the sign-in prompt can drive it: the prompt is a dialog
    /// over the calendar, and having it call a separate code path would mean two
    /// places that decide whether a failure is a failed sign-in or a failed
    /// load.</para>
    /// </summary>
    /// <returns><c>true</c> when the tenant accepted the sign-in.</returns>
    public async Task<bool> SignInAsync()
    {
        IsBusy = true;
        var signedIn = false;
        try
        {
            // A fresh attempt gets a fresh URL. Keeping the last one on screen
            // would offer a link whose PKCE challenge belongs to an abandoned
            // attempt, which the tenant refuses — and the user would have no way
            // to tell that from a link they mistyped.
            SignInUrl = null;

            await _panopto.SignInAsync();

            IsSignedIn = true;
            signedIn = true;
            Status = "Signed in.";
            await LoadAsync();

            // Only surfaced after loading, so a storage problem never hides the
            // fact that the schedule itself came back fine.
            if (_panopto.PersistenceWarning is { } warning) Detail = warning;
        }
        catch (Exception ex)
        {
            Status = "Sign-in failed.";
            (Detail, DetailTooltip) = Problem.Describe("Sign-in failed.", ex);
        }
        finally
        {
            IsBusy = false;
        }

        // A sign-in the tenant rejects leaves IsSignedIn false, so the prompt
        // stays open and the user can retry rather than being told it worked.
        return signedIn && IsSignedIn;
    }

#if DEBUG
    /// <summary>
    /// The fixture week, with the tenant's room read reported as having stopped
    /// short. Only reachable from <c>--self-test-rooms</c>.
    /// </summary>
    public void UseIncompleteRoomsFixture() => _fixtureRoomsIncomplete = true;

    /// <summary>
    /// Draws a week of made-up sessions and makes no request to Panopto. See
    /// <see cref="DebugFixtureWeek"/>; the status line says which week this is,
    /// so a window opened this way cannot be read as a real schedule.
    /// </summary>
    public void LoadDebugFixture()
    {
        var today = RoomToday();
        _weekStart = StartOfWeek(today);
        _sessions = DebugFixtureWeek.Sessions(today);

        // Including the rooms with nothing booked, so the legend's "exists but is
        // not on screen" state is drawable without a tenant. This does not make
        // the fixture a signed-in session: nothing here can reach Panopto.
        _tenantRooms = DebugFixtureWeek.Rooms;

        if (_fixtureRoomsIncomplete)
        {
            // Not a property of the fixture week, a property of the read. The
            // fixture has fourteen rooms; the point is that the read stopped
            // short, which is the state the banner exists for and the one that
            // cannot otherwise be drawn without a tenant that is too big.
            _roomsComplete = false;
            _roomsRead = 15000;
        }

        Raise(nameof(WeekLabel));
        Rebuild();

        IsSignedIn = false;
        IsBusy = false;
        Status = "Fixture week — none of these sessions are real.";
        Detail = "Started with --self-test-week. No request was made to Panopto "
               + "and nothing here can be written back.";

        // The rooms bar is asserted here rather than left to be looked at,
        // because looking at it is exactly what did not happen last time: the
        // bar's visibility was bound to IsSignedIn, a fixture sets that false,
        // and the whole legend drew collapsed through a self-test that reported
        // nothing wrong. A hidden control is not a binding error, so zero
        // warnings was not evidence — this line is.
        var listed = Legend.Count;

        if (ShowsRoomBar && listed > 0)
        {
            AppLog.Info(
                $"Self-test week: rooms bar shown — \"{RoomHeader}\", "
                + $"{listed} row(s) listed, {HiddenRoomCount} behind the toggle.");
        }
        else
        {
            AppLog.Warn(
                $"Self-test week: the rooms bar is "
                + $"{(ShowsRoomBar ? "shown but empty" : "hidden")} with {listed} row(s). "
                + "The legend cannot be drawn in this build.");
        }

        // The banner is asserted on the state that draws it, and only when it was
        // asked for. Asserting it on the ordinary fixture would be asserting that a
        // complete listing claims to be incomplete, which is the opposite of the
        // rule — so this is checked on the run that deliberately truncates, and
        // the ordinary run checks the other direction.
        var bannerOk = _fixtureRoomsIncomplete
            ? RoomsIncomplete && RoomsIncompleteNote.Length > 0 && RoomHeader.Contains("(incomplete)")
            : !RoomsIncomplete && RoomsIncompleteNote.Length == 0 && !RoomHeader.Contains("(incomplete)");

        if (bannerOk)
        {
            AppLog.Info(
                _fixtureRoomsIncomplete
                    ? $"Self-test rooms: incomplete banner shown — header \"{RoomHeader}\", note \"{RoomsIncompleteNote}\""
                    : "Self-test rooms: a complete listing shows no incomplete banner.");
        }
        else
        {
            AppLog.Warn(
                $"Self-test rooms: the incomplete-room banner is wrong for a "
                + $"{(_fixtureRoomsIncomplete ? "truncated" : "complete")} listing — "
                + $"header \"{RoomHeader}\", shows={RoomsIncomplete}, note \"{RoomsIncompleteNote}\".");
        }

        CheckDetailsFixture();
    }

    /// <summary>
    /// Asserts that the fixture week can actually draw every branch of the details
    /// panel.
    ///
    /// <para><b>Because a panel of blanks raises no warning.</b> The panel has about
    /// a dozen fields and half of them are behind a condition; a fixture that filled
    /// only the common ones would leave the other half drawn by nothing, and the
    /// machine this is worked on has no tenant to supply them. Zero binding warnings
    /// says the bindings resolved — it says nothing about whether the sections they
    /// resolve to are ever reached, which is precisely how the rooms bar came to
    /// draw collapsed through a self-test that reported nothing wrong.</para>
    ///
    /// <para><b>The presenter join is asserted rather than assumed</b>, on the row
    /// built with more first names than surnames. That is the one place a hand-rolled
    /// zip goes wrong — a trailing space, a doubled comma, an empty segment in the
    /// middle of a name — and it is invisible in a fixture where the two lists are
    /// always the same length.</para>
    /// </summary>
    private void CheckDetailsFixture()
    {
        var blocks = Days.SelectMany(d => d.Blocks).ToList();

        if (blocks.Count == 0)
        {
            AppLog.Warn("Self-test details: the fixture week has no blocks, so the panel has nothing to describe.");
            return;
        }

        // Every conditional branch the panel has, and whether the fixture reaches it.
        var withPresenters = blocks.Count(b => b.HasPresenters);
        var withoutPresenters = blocks.Count(b => !b.HasPresenters);
        var withLinks = blocks.Count(b => b.HasViewerUrl);
        var withoutLinks = blocks.Count(b => !b.HasViewerUrl);
        var readOnly = blocks.Count(b => !b.ReportsWritable);
        var secondName = blocks.Count(b => b.HasOtherName);
        var countedViews = blocks.Count(b => b.Views != "Not reported");

        // The read-only row is the one that proves the editor link's gate: Panopto
        // sends an editor URL for it and the panel must decline to offer it.
        var gateHeld = blocks.Where(b => !b.ReportsWritable).All(b => !b.HasEditorUrl);

        // The mismatched row: "Dana Whitfield, Whitfield, Okafor". Checked for the
        // shapes a broken zip leaves behind rather than for the exact string, so
        // this does not become a second copy of the join.
        var mismatched = blocks.FirstOrDefault(b => b.Presenters.Contains("Okafor", StringComparison.Ordinal));
        var joinClean = mismatched is not null
            && !mismatched.Presenters.Contains(", ,", StringComparison.Ordinal)
            && !mismatched.Presenters.Contains("  ", StringComparison.Ordinal)
            && !mismatched.Presenters.EndsWith(", ", StringComparison.Ordinal)
            && !mismatched.Presenters.StartsWith(", ", StringComparison.Ordinal);

        // Panopto's placeholder is not a second name. Every fixture row but one
        // carries DeliveryName "default" — the shape the live tenant sends — so if
        // the panel's suppression ever regresses, this count goes up rather than
        // the panel quietly gaining a line of noise under every recording.
        var placeholderRows = blocks.Count(b => b.Session.DeliveryName == "default");
        var placeholderSuppressed = blocks
            .Where(b => b.Session.DeliveryName == "default")
            .All(b => !b.HasOtherName);

        var ok = withPresenters > 0 && withoutPresenters > 0
              && withLinks > 0 && withoutLinks > 0
              && readOnly > 0 && secondName > 0 && countedViews > 0
              && gateHeld && joinClean
              && placeholderRows > 0 && placeholderSuppressed;

        if (ok)
        {
            AppLog.Info(
                "Self-test details: every panel branch is drawable — "
                + $"{withPresenters} with presenters and {withoutPresenters} without, "
                + $"{withLinks} with a Panopto link and {withoutLinks} without, "
                + $"{readOnly} this account cannot write to (editor link withheld), "
                + $"{secondName} with a second name, {countedViews} with view counts, "
                + $"{placeholderRows} carrying Panopto's \"default\" and suppressed. "
                + $"Presenter join: \"{mismatched!.Presenters}\".");
            return;
        }

        AppLog.Warn(
            "Self-test details: the fixture cannot draw the whole panel — "
            + $"{withPresenters} with presenters, {withoutPresenters} without, "
            + $"{withLinks} with a link, {withoutLinks} without, "
            + $"{readOnly} read-only, {secondName} with a second name, "
            + $"{countedViews} with views, editor-link gate held={gateHeld}, "
            + $"presenter join clean={joinClean} (\"{mismatched?.Presenters ?? "no such row"}\"), "
            + $"placeholder rows={placeholderRows} suppressed={placeholderSuppressed}.");
    }
#endif

    private void SignOut()
    {
        _panopto.SignOut();

        _sessions = [];
        IsSignedIn = false;
        Rebuild();

        Status = "Signed out.";
        Detail = _panopto.PersistenceWarning ?? "Sign in to load the schedule.";
    }

    /// <summary>
    /// Moves a session that has been dragged to a new slot.
    ///
    /// <para>Uses <c>UpdateRecordingTime</c> rather than delete-and-recreate:
    /// it keeps the session id, and with it the folder, the description and the
    /// viewer link that students may already have.</para>
    ///
    /// <para>The times are wall clocks here and are converted to the instant the
    /// room names on the way out, by <see cref="RemoteRecorderClient"/>, which
    /// holds the room's zone. This comment used to say the opposite — that they go
    /// out unconverted — on the strength of measurements that were all of the read
    /// path. A write is not the read in reverse: sending the digits unchanged put a
    /// live booking four hours early, which is a room with nobody recording in it
    /// and nothing in the app to say so.</para>
    /// </summary>
    public async Task<bool> RescheduleAsync(
        SessionBlockViewModel block,
        TimeSpan shift,
        int dayShift = 0,
        CancellationToken ct = default)
    {
        // A drag looks the same signed in or not, and the server is the wrong
        // place to find out which: the refusal arrives as an authentication
        // exception, and the operator is told a move failed without being told
        // why. Checked first, before the id and the past, because it is the
        // reason that applies to every block on screen at once.
        if (!IsSignedIn)
        {
            Status = "Not signed in.";
            Detail = "Sign in before moving a recording — nothing was sent to Panopto.";
            return false;
        }

        if (block.SessionId is not { } id)
        {
            Status = "That session has no id, so it cannot be moved.";
            return false;
        }

        if (shift == TimeSpan.Zero && dayShift == 0) return false;

        var start = block.StartsAt.Add(shift).AddDays(dayShift);
        var end = start + block.Duration;

        // A drag is easy to fumble, and the cost of an accidental drop is a room
        // with no recording. Anything in the past is refused outright rather than
        // sent and rejected — and "the past" is the room's past, not this
        // machine's.
        if (RoomClock.WouldLandInThePast(end, _roomZone))
        {
            Status = "Not moved.";
            Detail = "That would put the recording in the past.";
            return false;
        }

        IsBusy = true;
        Status = "Moving recording…";
        Detail = $"{block.Title}: {block.StartsAt:h:mm tt} → {start:h:mm tt}";

        try
        {
            var result = await _panopto.Recorders
                .UpdateRecordingTimeAsync(id, start, end, ct).ConfigureAwait(true);

            // The server can accept the move and still report a clash, so this is
            // reported rather than treated as a clean success.
            if (result.ConflictsExist)
            {
                Status = "Moved, with a clash.";
                Detail = $"Something else is booked on {block.Recorder} at that time: "
                       + string.Join("; ", result.Conflicts);
            }
            else
            {
                Status = "Recording moved.";
                Detail = $"{block.Title} is now at {start:ddd d MMM, h:mm tt}.";
            }

            // Update locally instead of re-reading: the write already happened,
            // and a refresh spends a request to learn what we just told Panopto.
            // `start` is handed over as the wall clock it is. It used to be
            // wrapped in a DateTimeOffset stamped with this machine's offset so
            // that reading it back would undo the stamp — the block then moved
            // with the workstation rather than with the room.
            block.Session.ApplyReschedule(start, block.Duration);

            Rebuild();
            return true;
        }
        catch (OperationCanceledException ex) when (ProblemText.IsTimeout(ex))
        {
            // Not rethrown, and it used to be — which is how a drag that timed
            // out closed the app. The block was snapped back before the call and
            // the local model was never updated, so the grid cannot be showing
            // the answer: only a read can say whether the move landed.
            var reloaded = await LoadAsync(jumpToFirst: false).ConfigureAwait(true);

            Status = "Panopto did not answer in time.";

            // The reassurance is earned or withheld, not assumed: LoadAsync
            // swallows its own failure, so a re-read that could not reach
            // Panopto leaves the grid showing the pre-drag snapshot — claiming
            // then that "what is on screen is what Panopto holds" would give
            // false certainty about a write whose fate is unknown.
            Detail = reloaded
                ? $"{block.Title} may or may not have moved. The week has just been "
                  + "re-read, so what is on screen is what Panopto holds."
                : $"{block.Title} may or may not have moved, and the week could not be "
                  + "re-read — the screen still shows the position from before the drag. "
                  + "Refresh when Panopto answers to see where it really landed.";

            // False, and it is the honest value rather than the tidy one: every
            // other path returning false is saying "the grid is showing the
            // pre-drag position", and this one is not — it has just re-read the
            // week, so the grid is showing whatever Panopto holds, which may be
            // the moved session. What false does mean here, and the only thing
            // this method can certify, is that it did not itself confirm the move.
            return false;
        }
        catch (PanoptoSoapFaultException ex)
        {
            // Panopto answered and said no. On the live tenant this is what a
            // session this account can see but not retime looks like, and it is
            // the one refusal worth remembering: the operator can drag that block
            // all day and it will never work.
            _moveRefused.Add(id);

            Status = "Panopto refused to move it.";
            Detail = $"You can see this recording, but Panopto will not let this account "
                   + $"change its schedule. Its own words: {ex.Message}";

            // A measurement, not a guess: whether the drag can be gated up front
            // on the listing's own permission fields is exactly the question this
            // line answers, and it cannot be answered from here.
            AppLog.Warn(
                $"Refused a move of session {id:D}: HasWriteAccess={block.Session.HasWriteAccess}, "
                + $"IsEditable={block.Session.IsEditable}, status={block.Session.Status}, "
                + $"recorder={block.Session.RemoteRecorderName ?? "(none)"}. "
                + "The block will no longer offer the drag in this session.");

            // The grid still shows the old position, so it is consistent with
            // the server even though the operator's drag was refused.
            Rebuild();
            return false;
        }
        catch (Exception ex)
        {
            Status = "Could not move the recording.";
            (Detail, DetailTooltip) = Problem.Describe("Could not move the recording.", ex);

            // The grid still shows the old position, so it is consistent with
            // the server even though the operator's drag was refused.
            Rebuild();
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ShiftWeekAsync(int days)
    {
        _weekStart = _weekStart.AddDays(days);
        Raise(nameof(WeekLabel));
        await LoadAsync();
    }

    private async Task GoToThisWeekAsync()
    {
        _weekStart = StartOfWeek(RoomToday());
        Raise(nameof(WeekLabel));
        await LoadAsync();
    }

    /// <summary>
    /// Reads the week and redraws it.
    /// </summary>
    /// <param name="jumpToFirst">
    /// Whether to move the view to the first week with anything in it. True for
    /// every navigation and for sign-in, <b>false after an edit</b> — the
    /// scheduled set runs months ahead, so a rename would otherwise yank the
    /// operator off the week they were working in and onto the far end of the
    /// calendar, which reads as the app having lost their place.
    /// </param>
    /// <returns>
    /// Whether the week was actually re-read. False means the status line
    /// already says why ("Could not load the schedule.") and the grid is still
    /// showing whatever it held before — which is what the caller needs to know
    /// before claiming the screen matches Panopto.
    /// </returns>
    private async Task<bool> LoadAsync(bool jumpToFirst = true)
    {
        string? prompt = null;
        var loaded = false;

        IsBusy = true;
        try
        {
            Status = "Loading schedule…";
            // Every page. A single request returns a clamped slice, which draws
            // as a complete calendar with sessions quietly absent from it.
            _sessions = await _panopto.Reads.GetAllSessionsAsync([1]);

            // Before the repaint, so the first legend the operator sees is the
            // whole inventory rather than this week's rooms completing a beat
            // later. Once per sign-in — see _roomsLoaded.
            if (!_roomsLoaded) await LoadRoomsAsync();

            // A session's real time lives in StartTime; ScheduledStartTime is
            // null on every scheduled row. PanoptoScheduler.Core handles that.
            var unplaced = _sessions.Count(s => s.EffectiveStart is null);

            // The scheduled set runs months ahead, so land on the first week
            // that actually has something rather than showing an empty grid.
            if (jumpToFirst && _sessions.Any(s => s.EffectiveStart is not null))
                JumpToFirstSessionWeek();

            Rebuild();

            var shown = Days.Sum(d => d.Blocks.Count);

            // Through the palette's own filter, which drops the blanks. Counting
            // Distinct() over the nullable names made every unassigned session
            // one more room, so the line over-reported the building by one.
            var rooms = RecorderPalette.Names(_sessions.Select(s => s.RemoteRecorderName)).Count;

            Status = $"{_sessions.Count} scheduled session(s) at {rooms} recorder(s)."
                   + (IsRoomFiltered ? $" Filtered to {_roomFilter}." : "");

            Detail = $"{shown} in view this week"
                   + (unplaced > 0 ? $" · {unplaced} with no usable time" : "");

            loaded = true;
        }
        catch (PanoptoRequestException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // Verified against the live tenant: Data.svc accepts the same OAuth
            // bearer token as the public REST API, so this is not a missing
            // session cookie — the token itself was refused.
            IsSignedIn = false;
            Status = "Panopto rejected the sign-in.";
            Detail = "The saved session is no longer valid. Sign in again.";
            AppLog.Error("The sign-in was rejected.", ex);

            // Held until the finally clears IsBusy. Opening a modal prompt while
            // the view model still believes it is loading leaves every button
            // behind the dialog greyed out, including the one that retries.
            prompt = Detail;
        }
        catch (Exception ex)
        {
            Status = "Could not load the schedule.";
            (Detail, DetailTooltip) = Problem.Describe("Could not load the schedule.", ex);
        }
        finally
        {
            IsBusy = false;
        }

        if (prompt is not null) await RequestSignInAsync(prompt);

        return loaded;
    }

    private void JumpToFirstSessionWeek()
    {
        var first = _sessions
            .Select(s => s.EffectiveStart)
            .Where(s => s is not null)
            // The room's own day, straight off the wall clock. Reading it through
            // LocalDateTime asked this machine which day the room's 09:00 fell on.
            .Select(s => DateOnly.FromDateTime(s!.Value))
            .OrderBy(d => d)
            .FirstOrDefault();

        if (first != default) _weekStart = StartOfWeek(first);
        Raise(nameof(WeekLabel));
    }

    /// <summary>
    /// Refits the grid to the space the calendar actually has, and redraws it if
    /// the fit changed.
    ///
    /// <para><b>Called with the scroller's own size, not the window's.</b> The
    /// scroller's <c>ActualWidth</c> is already net of the toolbar, the legend,
    /// the details panel, the hour gutter and the vertical scrollbar, so it is
    /// the width the columns truly have and it is correct by construction. Fitting
    /// to the window instead would mean subtracting four things here that the
    /// layout is already subtracting, and being wrong about one of them silently
    /// — which is what the scrollbar reserve is for and not the same job.</para>
    ///
    /// <para><b>Rebuilds only on a real change.</b> The clamps mean most resizes
    /// change nothing: dragging a window edge from 1000px to 1400px at the ceiling
    /// leaves every number where it was, and a rebuild for that would throw away
    /// and recreate every block to draw the identical picture. Since Stage 3 made
    /// a rebuild keep the operator's ticks, this is now only a cost rather than a
    /// data loss — but it is still a cost paid on every pixel of a drag.</para>
    /// </summary>
    /// <param name="viewportWidth">Width available to the columns.</param>
    /// <param name="viewportHeight">
    /// Height available to the hour rows — the body only. The day-name row is a
    /// sibling of the scroller rather than something inside it, so it is already
    /// off this number and taking it off again would leave the grid exactly
    /// <see cref="HeaderHeight"/> short of filling the window, which is the bug
    /// this method exists to fix.
    /// </param>
    public void ApplyMetrics(double viewportWidth, double viewportHeight)
    {
        var fitted = CalendarMetrics.Fit(
            viewportWidth,
            viewportHeight,
            Days.Count > 0 ? Days.Count : DaysInWeek,
            Hours,
            _metrics);

        if (fitted == _metrics) return;

        _metrics = fitted;

        // Raised rather than left to the rebuild: these are read by the gutter's
        // row heights and the columns' widths, which are outside the items that
        // Rebuild replaces, so a redraw alone would leave them at the old size.
        Raise(nameof(HourHeight));
        Raise(nameof(ColumnWidth));
        Raise(nameof(ColumnPitch));
        Raise(nameof(GridHeight));

        Rebuild();
    }

    private void Rebuild()
    {
        // Captured before anything is cleared, and restored as each block is
        // built. Without this every rebuild drops the operator's ticks — and a
        // rebuild is no longer only a refresh: the calendar now refits itself
        // whenever the window is resized, so dragging a window edge would have
        // silently emptied a selection the operator had just spent a minute
        // assembling. Distinct from ClearSelection, which stays the deliberate
        // clear after a bulk edit or a filter.
        var ticked = Days.SelectMany(d => d.Blocks)
            .Where(b => b.IsSelected && b.SessionId is not null)
            .Select(b => b.SessionId!.Value)
            .ToHashSet();

        Days.Clear();

        // The colours for the whole week are decided here, before any block is
        // built, because the assignment is a function of the set of rooms rather
        // than of the order they are reached in. See RecorderPalette.
        AssignRecorderBrushes();

        // Filtered or not, the colours above were assigned over the whole week on
        // purpose: filtering to a room must not recolour it, or the stripe just
        // clicked stops meaning what it meant a moment ago.
        var columns = CalendarLayout.ArrangeByDay(VisibleSessions(), _weekStart, DaysInWeek);
        var today = RoomToday();

        foreach (var (day, placed) in columns)
        {
            var column = new DayColumnViewModel(day)
            {
                IsToday = day == today,
            };

            foreach (var item in placed) column.Blocks.Add(ToBlock(item, ticked));
            Days.Add(column);
        }

        // After AssignRecorderBrushes: the stripes it fills in are what the
        // legend rows for this week's rooms borrow.
        BuildLegend();

        // Computed from the columns, which do not notify for it. The selection
        // counts are re-raised for the same reason: they are read back out of the
        // blocks, so a session that has gone from the week changes the count
        // without any tick having changed.
        Raise(nameof(IsWeekEmpty));
        Raise(nameof(SelectedBlock));
        Raise(nameof(HasSelectedBlock));
        NotifySelectionChanged();
    }

    /// <summary>
    /// The sessions the grid draws: the whole week, or one room's share of it.
    /// </summary>
    private IEnumerable<PanoptoSession> VisibleSessions() =>
        IsRoomFiltered ? _sessions.Where(s => IsRoom(s, _roomFilter)) : _sessions;

    private static bool IsRoom(PanoptoSession session, string room) =>
        string.Equals(session.RemoteRecorderName ?? "", room, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shows one room's week and hides the rest.
    ///
    /// <para>Filtering <b>clears the selection</b>, deliberately. A tick is a
    /// statement about a block the operator is looking at, and holding ticks on
    /// blocks that have just been hidden leaves the bulk tools acting on things
    /// nobody can see — which is how a bulk delete reaches a session that is no
    /// longer on screen to be argued with.</para>
    /// </summary>
    public void FilterToRoom(string room)
    {
        var wanted = string.IsNullOrWhiteSpace(room) ? "" : room.Trim();

        // Clicking the room already being shown clears it, which is the gesture
        // people try first.
        _roomFilter = IsActiveRoom(wanted) ? "" : wanted;

        ClearSelection();
        NotifySelectionChanged();

        Rebuild();
    }

    private bool IsActiveRoom(string room) =>
        _roomFilter.Length > 0 && string.Equals(_roomFilter, room, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the tenant's room list. Answers "which rooms exist", which does not
    /// change as the operator pages through weeks.
    /// </summary>
    /// <returns><c>true</c> when the listing came back.</returns>
    private async Task<bool> LoadRoomsAsync()
    {
        var loaded = false;

        try
        {
            var recorders = await _panopto.Recorders.ListRecordersAsync();
            _tenantRooms = RecorderPalette.Names(recorders.Select(r => r.Name));
            _roomsComplete = recorders.Complete;
            _roomsRead = recorders.Count;
            loaded = true;

            // The fixture has asserted the legend's shape since it was written;
            // the real path asserted nothing, so "did the rooms actually load, and
            // how many are there" was unanswerable from a log a user could send.
            // It is the exact question behind "i dont see all the recorders from
            // event space", and a count here settles it. SoapPaging logs
            // separately when it hit its ceiling, so a count that stops at a round
            // 15000 is visibly a truncated read and not a small tenant.
            AppLog.Info(
                $"Rooms: {_tenantRooms.Count} distinct name(s) from {recorders.Count} recorder(s) "
                + (recorders.Complete
                    ? "and the listing was read to the end. "
                    : $"and the listing STOPPED SHORT of the tenant's full set"
                      + (recorders.ReportedTotal > 0 ? $" ({recorders.ReportedTotal} reported). " : ". ")
                      + "The legend says so on screen. ")
                + "The legend lists this week's first and the rest behind the toggle.");

            if (!recorders.Complete)
            {
                // Warned as well as shown, because the two audiences are different:
                // the operator needs the sentence on screen, and whoever reads a
                // log from a machine they cannot log into needs it here.
                AppLog.Warn(
                    $"The room list is incomplete: {recorders.Count} recorder(s) read, "
                    + (recorders.ReportedTotal > 0 ? $"{recorders.ReportedTotal} reported, " : "no total reported, ")
                    + "and the paging ceiling was reached. Rooms beyond it cannot be "
                    + "filtered to, and a booking row naming one of them will be reported as Failed rather than "
                    + "silently skipped.");
            }
        }
        catch (Exception ex)
        {
            // The inventory is an addition to the calendar, not a prerequisite
            // for it. A tenant that will not list its rooms leaves the legend
            // showing this week's, which is what it showed before this existed.
            AppLog.Warn($"Could not list the tenant's rooms, so the legend shows this week's only. {AppLog.Full(ex)}");
        }
        finally
        {
            // Set either way. Asking again on every week navigation would spend
            // requests on the same refusal; the Reload rooms button is the retry.
            _roomsLoaded = true;
        }

        return loaded;
    }

    /// <summary>The Reload rooms button: re-reads the inventory and repaints.</summary>
    private async Task ReloadRoomsAsync()
    {
        IsBusy = true;
        try
        {
            Status = "Reloading the room list…";
            var loaded = await LoadRoomsAsync();
            Rebuild();

            if (loaded)
            {
                Status = $"Room list reloaded — {_inventory.Summary}.";
            }
            else
            {
                Status = "Could not reload the room list.";
                Detail = "The calendar still works; the legend shows this week's rooms only.";
            }
        }
        catch (Exception ex)
        {
            Status = "Could not reload the room list.";
            (Detail, DetailTooltip) = Problem.Describe("Could not reload the room list.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Sessions Panopto has refused to move in this session, so the grid stops
    /// offering the drag. See <c>SessionBlockViewModel.CanReschedule</c>.
    /// </summary>
    private readonly HashSet<Guid> _moveRefused = [];

    private SessionBlockViewModel ToBlock(PositionedSession placed, IReadOnlySet<Guid> ticked)
    {
        var session = placed.Session;
        var local = placed.Start;

        var id = Guid.TryParse(session.SessionID, out var parsed) ? parsed : (Guid?)null;

        var top = (local.Hour + local.Minute / 60.0 - FirstHour) * HourHeight;

        // A block holds a title and a time line. Below the full height there is
        // room for one of them, so the block says so and drops the time rather
        // than drawing it sliced in half. The old floor was 22, which clipped
        // both.
        var wanted = placed.Duration.TotalHours * HourHeight;
        var compact = wanted < FullBlockHeight;
        var height = Math.Max(wanted, compact ? CompactBlockHeight : FullBlockHeight);

        var recorder = session.RemoteRecorderName ?? "(unassigned)";

        return new SessionBlockViewModel
        {
            Session = session,
            SessionId = id,

            // The tick is restored from the id, so a rebuild for a resize or a
            // refresh does not quietly untick everything. Filtering clears the
            // selection before it gets here, which is how the two rules stay
            // distinct: a rebuild is not a decision, and a filter is.
            IsSelected = id is not null && ticked.Contains(id.Value),

            // Restored from the set, like the tick. A refusal is a fact about the
            // account that does not change when the grid is redrawn, so a refresh
            // must not quietly hand the drag back.
            MoveRefused = id is not null && _moveRefused.Contains(id.Value),
            Top = top,
            Height = height,
            IsCompact = compact,
            Left = placed.LeftFraction * ColumnWidth,
            Width = Math.Max(60, placed.WidthFraction * ColumnWidth - 3),
            Accent = BrushFor(recorder),
            Title = session.SessionName ?? "(untitled)",
            Recorder = recorder,
            Folder = session.FolderName ?? "",
            Starts = local.ToString("h:mm tt"),
            Ends = (local + placed.Duration).ToString("h:mm tt"),
            Unreliable = !session.HasReliableTime,
        };
    }

    /// <summary>
    /// Rebuilds the week's recorder-to-colour map from scratch.
    ///
    /// <para>From scratch rather than incrementally, because the colour a room
    /// gets depends on where its name falls in the sorted list of every room on
    /// screen — so a week that gains or loses a room can legitimately recolour
    /// others, and a cache that only ever grew would keep colours that no longer
    /// match what the palette would assign. Clearing it each rebuild is what
    /// makes a refresh with the same rooms draw the same colours.</para>
    /// </summary>
    private void AssignRecorderBrushes()
    {
        _recorderBrushes.Clear();

        var assigned = RecorderPalette.Assign(
            _sessions.Select(session => session.RemoteRecorderName ?? ""));

        // Names(), not the dictionary's own enumeration: this is the order the
        // colours were handed out, and the legend reads in the same order so a
        // row's stripe is the stripe on its blocks.
        foreach (var recorder in RecorderPalette.Names(
                     _sessions.Select(session => session.RemoteRecorderName ?? "")))
        {
            var brush = new SolidColorBrush(
                Color.FromRgb(assigned[recorder].R, assigned[recorder].G, assigned[recorder].B));
            brush.Freeze();
            _recorderBrushes[recorder] = brush;
        }
    }

    /// <summary>
    /// Builds the legend rows from the inventory, applying the toggle and the
    /// search box.
    ///
    /// <para>This week's rooms are always listed. The stripe is on a block the
    /// operator can see, and hiding its name would leave a colour on screen with
    /// nothing explaining it. The rest wait behind the toggle — and the search box
    /// reaches past the toggle, because typing a room's name is a request to find
    /// it and the room most worth finding is the one with nothing booked.</para>
    /// </summary>
    private void BuildLegend()
    {
        _inventory = RoomInventory.Build(_tenantRooms, _sessions.Select(s => s.RemoteRecorderName));

        var searching = _roomSearch.Trim().Length > 0;
        var rows = new List<RecorderLegendViewModel>(Legend.Count);

        foreach (var row in _inventory.Rows)
        {
            if (!row.IsOnScreen && !_showAllRooms && !searching) continue;

            if (searching && !row.Name.Contains(_roomSearch.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Null for a room with no booking this week: it has no blocks, so it
            // has no colour, and inventing one would make it look like a room you
            // can see.
            Brush? stripe = _recorderBrushes.TryGetValue(row.Name, out var brush) ? brush : null;

            var name = row.Name;
            rows.Add(new RecorderLegendViewModel(
                name,
                stripe,
                row.IsOnScreen,
                IsActiveRoom(name),
                new RelayCommand(() => FilterToRoom(name))));
        }

        Legend = rows;

        Raise(nameof(Legend));
        Raise(nameof(HasLegend));
        Raise(nameof(ShowsRoomBar));
        Raise(nameof(RoomHeader));
        Raise(nameof(RoomsIncomplete));
        Raise(nameof(RoomsIncompleteNote));
        Raise(nameof(HiddenRoomCount));
        Raise(nameof(HasHiddenRooms));
        Raise(nameof(ShowAllRoomsLabel));

        // Also raised by Rebuild, so every path that can change the filter has
        // covered it — this one runs before Rebuild does on the load path.
        Raise(nameof(RoomFilter));
        Raise(nameof(IsRoomFiltered));
        Raise(nameof(FilterNote));
        Raise(nameof(EmptyWeekMessage));

        ClearRoomFilterCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The stripe for a recorder.
    ///
    /// <para>A room with no colour — one Panopto returned with no recorder name,
    /// or one that appeared after the week was assigned — falls back to the
    /// neutral grey rather than to the first palette entry, so that an unnamed
    /// room never wears a colour that means a different room.</para>
    /// </summary>
    private Brush BrushFor(string recorder) =>
        _recorderBrushes.TryGetValue(recorder, out var brush) ? brush : UnnamedRecorderBrush;

    private static readonly Brush UnnamedRecorderBrush = CreateUnnamedBrush();

    private static Brush CreateUnnamedBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xA8, 0xAE, 0xB8));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Today, in the rooms. Not this machine's today — a workstation in another
    /// zone would otherwise highlight the wrong column and anchor the week on the
    /// wrong Monday.
    /// </summary>
    private DateOnly RoomToday() => DateOnly.FromDateTime(RoomClock.Now(_roomZone));

    private static DateOnly StartOfWeek(DateOnly day)
    {
        var delta = ((int)day.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return day.AddDays(-delta);
    }
}

/// <summary>
/// One room in the calendar's legend: its stripe, its name, and whether it is on
/// screen this week.
/// </summary>
/// <param name="Name">The recorder, as Panopto named it.</param>
/// <param name="Stripe">
/// The same brush the blocks for this room carry, or <c>null</c> for a room with
/// no booking this week. It has no blocks, so it has no colour, and inventing one
/// would make it look like a room you can see.
/// </param>
/// <param name="IsOnScreen">Whether this room has a booking in the week shown.</param>
/// <param name="IsActive">Whether the grid is currently filtered to this room.</param>
/// <param name="Select">
/// Filters the grid to this room — or clears the filter when it is already the
/// one on. Carried on the row so the markup needs no command parameter.
/// </param>
public sealed record RecorderLegendViewModel(
    string Name,
    Brush? Stripe,
    bool IsOnScreen,
    bool IsActive,
    RelayCommand Select);

public sealed class DayColumnViewModel(DateOnly day) : ObservableObject
{
    public DateOnly Date { get; } = day;

    public string Header { get; } = day.ToString("ddd d");

    public string SubHeader { get; } = day.ToString("MMM");

    public bool IsToday { get; init; }

    public ObservableCollection<SessionBlockViewModel> Blocks { get; } = [];
}

public sealed class SessionBlockViewModel : ObservableObject
{
    private double _top;
    private double _left;

    /// <summary>The row this block was drawn from, so a move can update it.</summary>
    public required PanoptoSession Session { get; init; }

    /// <summary>
    /// Settable so a drag can move the block as it is dragged. The value here is
    /// only ever a preview — committing the move redraws the grid from the
    /// session, which is what makes a rejected drop snap back.
    /// </summary>
    public required double Top
    {
        get => _top;
        set => Set(ref _top, value);
    }

    public required double Left
    {
        get => _left;
        set => Set(ref _left, value);
    }

    public required double Height { get; init; }
    public required double Width { get; init; }
    public required Brush Accent { get; init; }
    public required string Title { get; init; }
    public required string Recorder { get; init; }
    public required string Folder { get; init; }
    public required string Starts { get; init; }
    public required string Ends { get; init; }

    /// <summary>
    /// Both times as one line, for the details panel.
    ///
    /// <para>Two properties joined here rather than a multi-binding in the XAML,
    /// because the separator is a decision about how the pair reads and belongs
    /// with the two strings it joins — a multi-binding would put it in a template
    /// where neither time is defined.</para>
    /// </summary>
    public string TimeRange => $"{Starts} – {Ends}";

    /// <summary>True when the time was inferred rather than reported.</summary>
    public bool Unreliable { get; init; }

    /// <summary>
    /// True when the block is too short to show a time line as well as a title.
    /// </summary>
    public bool IsCompact { get; init; }

    // ---------------------------------------------------------------
    // The details panel.
    //
    // Everything below reads through to Session rather than being copied into a
    // field at construction. That is not fussiness: a rebuild replaces every
    // block, so an init-set copy would be correct, but a computed one cannot
    // disagree with the session even for the moment between a refresh and the
    // redraw. It is also what keeps this a view of one session rather than a
    // second place a session's values are written down.
    //
    // These are the fields the listing has always carried and no screen ever
    // showed. They were in the model and in no view, which is why they are
    // gathered here rather than left to {Binding Session.X} in the XAML — the
    // formatting decisions below (how two name lists become one string, what an
    // absent owner says) are decisions about how a session reads, and they belong
    // beside each other where they can be compared.
    // ---------------------------------------------------------------

    /// <summary>
    /// The recording's own name, when there is one worth showing, as the empty
    /// string so the panel's visibility binding needs no converter.
    ///
    /// <para><b>The rule itself lives on the model.</b> It used to be here, and it
    /// printed the word "default" as a second title under every recording on the
    /// calendar, because the live tenant sends <c>"default"</c> as the delivery
    /// name for all 213 scheduled sessions and the first version only compared it
    /// to the session name. Moving it to
    /// <see cref="PanoptoSession.SecondName"/> is what makes it testable: a WPF
    /// view model is reachable by nothing, so a rule kept here can only be checked
    /// by looking at live traffic.</para>
    /// </summary>
    public string OtherName => Session.SecondName ?? "";

    public bool HasOtherName => OtherName.Length > 0;

    /// <summary>The calendar day, so a panel opened from a filtered grid still says which day.</summary>
    public string DateText => Session.EffectiveStart is { } start
        ? start.ToString("dddd d MMMM yyyy")
        : "No date reported";

    /// <summary>
    /// How long the recording is, from the session's own duration.
    ///
    /// <para>Falls back to the block's drawn length when Panopto reports none,
    /// because the drawn length is the operator's own evidence for what they are
    /// looking at — and it is the same number the drag will use.</para>
    /// </summary>
    public string DurationText
    {
        get
        {
            var duration = Session.EffectiveDuration;

            if (duration is null or { TotalSeconds: <= 0 })
                return "Not reported";

            // Over an hour is worth reading in hours; under it, minutes is the
            // unit a person says out loud.
            return duration.Value.TotalHours >= 1
                ? $"{(int)duration.Value.TotalHours}h {duration.Value.Minutes:D2}m"
                : $"{(int)Math.Round(duration.Value.TotalMinutes)} min";
        }
    }

    /// <summary>
    /// Scheduled, Recording, Complete — the same word the grid's own tooltip uses,
    /// through the same extension, so the two cannot describe one session
    /// differently.
    /// </summary>
    public string StatusLabel => Session.Status.Label();

    /// <summary>
    /// The presenters as one line.
    ///
    /// <para>Two parallel lists, and either may be shorter than the other or
    /// absent entirely — the listing returns <c>[]</c> for most sessions. Zipping
    /// them by index and dropping blanks is what stops a missing surname becoming
    /// a trailing space or an "undefined" in the middle of a name.</para>
    /// </summary>
    public string Presenters
    {
        get
        {
            var first = Session.PresenterFirstNames ?? [];
            var last = Session.PresenterLastNames ?? [];

            var names = new List<string>(Math.Max(first.Count, last.Count));

            for (var i = 0; i < Math.Max(first.Count, last.Count); i++)
            {
                var name = string.Join(' ',
                    new[] { i < first.Count ? first[i] : null, i < last.Count ? last[i] : null }
                        .Where(part => !string.IsNullOrWhiteSpace(part)));

                if (name.Length > 0) names.Add(name);
            }

            return names.Count > 0 ? string.Join(", ", names) : "";
        }
    }

    public bool HasPresenters => Presenters.Length > 0;

    /// <summary>Who owns the session, or a word that says nobody was named.</summary>
    public string Owner => string.IsNullOrWhiteSpace(Session.OwnerFullName)
        ? "Not named"
        : Session.OwnerFullName!;

    public string Webcast => Session.IsBroadcast ? "Yes — it is webcast live." : "No";

    /// <summary>
    /// Editable and movable, said separately, because they are separate questions
    /// and a session can be one without the other.
    ///
    /// <para>These are the tenant's own fields and they are reported as the tenant
    /// sent them — never as a promise. The measured case is a session this account
    /// can see, is told it may write, and cannot retime; see
    /// <see cref="MoveRefused"/>. So the wording says what Panopto says rather than
    /// what the app will let you do.</para>
    /// </summary>
    public string Permissions => (Session.HasWriteAccess, Session.IsEditable) switch
    {
        (true, true) => "Panopto reports this account can write to it.",
        (true, false) => "Panopto reports write access but not that it is editable.",
        (false, true) => "Panopto reports it is editable but not that this account may write.",
        _ => "Panopto reports this account cannot write to it.",
    };

    /// <summary>
    /// Whether a write the panel offers should be offered at all. One gate, so the
    /// buttons and the sentence above them cannot disagree.
    /// </summary>
    public bool ReportsWritable => Session.HasWriteAccess || Session.IsEditable;

    /// <summary>The session's id, as text, or a dash when the row carried none.</summary>
    public string SessionIdText => SessionId?.ToString("D") ?? "—";

    /// <summary>
    /// Whether there is an id worth copying. The copy button is disabled rather
    /// than hidden when there is not, so the panel's shape does not change under
    /// the pointer.
    /// </summary>
    public bool CanCopySessionId => SessionId is not null;

    /// <summary>All-time views, or a word for "the listing did not say".</summary>
    public string Views => Session.AnalyticsAllTimeViewCount is { } count
        ? count == 1 ? "1 view" : $"{count:N0} views"
        : "Not reported";

    public string ViewerUrl => Session.ViewerUrl ?? "";

    public bool HasViewerUrl => ViewerUrl.Length > 0;

    public string EditorUrl => Session.EditorUrl ?? "";

    /// <summary>
    /// Whether to offer the editor link at all.
    ///
    /// <para>Separate from <see cref="HasEditorUrl"/> on purpose: Panopto returns
    /// an editor URL for sessions this account cannot edit, and offering a link
    /// that opens a page where nothing can be changed is worse than not offering
    /// it. Both facts come from the same payload, so the link is offered only when
    /// the session is one the panel would let you change.</para>
    /// </summary>
    public bool HasEditorUrl => Session.EditorUrl is { Length: > 0 } && ReportsWritable;

    /// <summary>
    /// Whether the time line has room. The time is still in
    /// <see cref="Tooltip"/>, so a compact block loses the glance, not the fact.
    /// </summary>
    public bool ShowTime => !IsCompact;

    /// <summary>
    /// The session's id, parsed once when the block is built.
    ///
    /// <para>Set on the block rather than parsed on each read, because it stopped
    /// being a field read by one caller: the tick, the selection the bulk tools
    /// address, the permit that authorises a delete and the details panel all ask
    /// for it, several times per rebuild. A block whose id does not parse is
    /// null, which is what keeps it out of all four.</para>
    /// </summary>
    public required Guid? SessionId { get; init; }

    /// <summary>
    /// Where the block starts — the room's wall clock, which is what Panopto
    /// stores and what the grid is drawn in.
    ///
    /// <para>Wall-clock arithmetic throughout, because the day column, the hour
    /// offset and the slot a drag lands in are all relative to a clock face. A
    /// grid built on UTC instants would render every recording at the wrong hour
    /// and still look internally consistent while doing it.</para>
    ///
    /// <para>This is the value as it came off the wire, with no machine zone
    /// applied and none to apply; see
    /// <see cref="PanoptoScheduler.Core.Json.WcfDateTimeConverter"/>.</para>
    /// </summary>
    public DateTime StartsAt => Session.EffectiveStart ?? DateTime.MinValue;

    public TimeSpan Duration =>
        Session.EffectiveDuration ?? CalendarLayout.DefaultDuration;

    /// <summary>
    /// Whether dragging this block can do anything.
    ///
    /// <para>Gated on the session being merely <i>scheduled</i>, not on
    /// <c>HasWriteAccess</c>. A recording that already happened has no time left
    /// to change, but write access is the server's call — gating on a field the
    /// tenant may not populate would leave the whole calendar undraggable with
    /// nothing on screen explaining why.</para>
    ///
    /// <para><b><see cref="MoveRefused"/> is the other half of that.</b> The
    /// reasoning above is right about not trusting a field, and it left the app
    /// offering a drag the server refuses: measured on the live tenant, a session
    /// this account can see but not retime answered <c>Invalid Session Id … at
    /// accessLevel: Videographer</c>, and the operator got "Could not move the
    /// recording" with no reason and no way to stop trying. So instead of guessing
    /// from a field, the app learns from the server's own answer and stops
    /// offering the gesture.</para>
    /// </summary>
    public bool CanReschedule =>
        SessionId is not null
        && !MoveRefused
        && Session.Status.ToSessionStatus() == SessionStatus.Scheduled;

    /// <summary>
    /// Set once Panopto has refused to move this session, so the block stops
    /// offering a drag that cannot succeed.
    ///
    /// <para>Applied by <c>ToBlock</c> from a set held on the view model, for the
    /// same reason the tick is: a rebuild replaces every block, so a flag stored
    /// only on the block would be forgotten by the next refresh — which is exactly
    /// when the operator would try again.</para>
    /// </summary>
    public bool MoveRefused { get; init; }

    /// <summary>Ticked, for a bulk operation to act on.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
    private bool _isSelected;

    /// <summary>
    /// A session with no id cannot be addressed by any write call, so it is not
    /// offered for selection — better than a tick that silently does nothing.
    /// </summary>
    public bool CanSelect => SessionId is not null;

    public string Tooltip =>
        $"{Title}\n{Starts} – {Ends}\nRecorder: {Recorder}\nFolder: {Folder}"
        + (Unreliable ? "\n\nWarning: this session reported no start time." : "")
        + MoveNote;

    /// <summary>
    /// What a drag on this block would do, or why it would do nothing.
    ///
    /// <para>Three answers rather than two, because "not movable" was one sentence
    /// covering two unrelated situations: a recording that already happened, and
    /// one Panopto will not let this account retime. The second is a permission
    /// problem the operator can take to an administrator, and saying only that it
    /// is "not scheduled" sent them looking for a fault in the app.</para>
    ///
    /// <para>Every branch now says the block can be clicked, because since the
    /// details panel arrived it can be — and a block that refuses the drag is
    /// exactly the one worth opening to find out why.</para>
    /// </summary>
    private string MoveNote
    {
        get
        {
            if (MoveRefused)
                return "\n\nPanopto will not let this account reschedule it. Click to read it.";

            return CanReschedule
                ? "\n\nDrag to move. Click to read it."
                : "\n\nNot movable — this one is not scheduled. Click to read it.";
        }
    }
}
