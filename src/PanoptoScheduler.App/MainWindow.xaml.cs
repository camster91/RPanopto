using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using PanoptoScheduler.App.ViewModels;
using PanoptoScheduler.Core;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Import;
using PanoptoScheduler.Core.Layout;

namespace PanoptoScheduler.App;

public partial class MainWindow : Window
{
    private readonly CalendarViewModel _viewModel;
    private readonly PanoptoConnection _panopto;

    private BulkWindowViewModel? _bulk;
    private BulkWindow? _bulkWindow;

#if DEBUG
    private bool _fixture;
    private bool _fixtureOpensPanel;
    private bool _fixtureRoomsIncomplete;
#endif

    private SessionBlockViewModel? _dragging;

    /// <summary>
    /// Whether the press in progress is allowed to end in a move. Decided when
    /// the press starts, because the release has two possible meanings and only
    /// one of them is available for a recording that already happened.
    /// </summary>
    private bool _dragMayMove;

    private Point _dragOrigin;
    private double _originTop;
    private double _originLeft;

    public MainWindow(PanoptoConnection panopto)
    {
        InitializeComponent();

        _panopto = panopto;
        _viewModel = new CalendarViewModel(panopto);
        DataContext = _viewModel;

        // Subscribed before the first load, because that load is what decides
        // whether a prompt is needed. The view model serialises the prompts, so
        // no guard is needed here.
        _viewModel.SignInPromptRequested += OnSignInPromptRequested;

        // Same shape as the sign-in prompt: the view model decides that the
        // operator asked, and the window decides what that means on screen.
        _viewModel.FullScreenRequested += ToggleFullScreen;

        // And again for the clipboard, which the view model does not touch.
        _viewModel.CopyRequested += CopyToClipboard;

        // Sign-in is silent when a saved session exists, and that involves a
        // round trip to refresh the token — so it happens once the window is up
        // rather than blocking construction.
        Loaded += async (_, _) =>
        {
#if DEBUG
            if (_fixture)
            {
                if (_fixtureRoomsIncomplete) _viewModel.UseIncompleteRoomsFixture();

                _viewModel.LoadDebugFixture();

                if (_fixtureOpensPanel) OpenPanelInFixture();

                CheckMetrics();

                return;
            }
#endif
            await _viewModel.InitializeAsync();

            // No saved session, so nothing was loaded and no request was made to
            // be refused. Without this the first run ends on an empty calendar
            // with a status line the user has no reason to read.
            if (!_viewModel.IsSignedIn)
                await _viewModel.RequestSignInAsync("Sign in to load the recording schedule.");
        };
    }

#if DEBUG
    /// <summary>
    /// Draws <see cref="DebugFixtureWeek"/> instead of loading a real week. Only
    /// reachable from a development build started with <c>--self-test-week</c>.
    /// </summary>
    /// <summary>
    /// True when this window draws <see cref="DebugFixtureWeek"/> and touches no
    /// network.
    ///
    /// <para>Read by <c>App</c> so the title can say what the window is. A fixture
    /// is fully interactive and can never write — it makes no request, so it is
    /// never signed in, so every drag and edit is refused. That is correct, and it
    /// is also a trap: an operator who finds one open will work in it, watch
    /// everything fail, and conclude the app is broken. Which is what happened,
    /// and why the distinction belongs in the title bar rather than in a status
    /// line nobody has a reason to read.</para>
    /// </summary>
    public bool IsFixture => _fixture;

    public void UseDebugFixture() => _fixture = true;

    /// <summary>
    /// The same fixture with the details panel already open, for
    /// <c>--self-test-panel</c>.
    ///
    /// <para>It opens on purpose on a recording that <b>cannot be moved</b>, and
    /// asserts that it did. That is the recording whose click used to be
    /// swallowed, so a panel that opens on it is the fix being demonstrated
    /// rather than a neighbouring case that happened to work. Opening the panel
    /// on a scheduled recording would prove nothing about the bug.</para>
    /// </summary>
    public void UseDebugFixtureWithPanel()
    {
        _fixture = true;
        _fixtureOpensPanel = true;
    }

    /// <summary>
    /// The same fixture, pretending the tenant's room read stopped short, for
    /// <c>--self-test-rooms</c>.
    ///
    /// <para>The banner that reports a truncated room list cannot otherwise be
    /// drawn on a machine with no tenant — the fixture's fourteen rooms are a
    /// complete listing, and a complete listing correctly shows nothing. So the
    /// state that matters would be the one state never rendered. This makes it
    /// renderable, and the fixture asserts on it.</para>
    /// </summary>
    public void UseDebugFixtureWithIncompleteRooms()
    {
        _fixture = true;
        _fixtureRoomsIncomplete = true;
    }

    /// <summary>
    /// Reports the fitted geometry once the grid has been laid out, and says
    /// whether the calendar actually fills the room it was given.
    ///
    /// <para><b>Asserted rather than eyeballed,</b> for the same reason the rooms
    /// bar is: the bug being fixed was that the calendar drew at a fixed size in
    /// whatever window it got, and a grid that is too small raises no error and
    /// logs nothing. The arithmetic is unit-tested in Core; what can only be seen
    /// here is that the window hands the scroller the geometry those tests assume
    /// — a fitting routine wired to a zero-height scroller would pass every test
    /// and still draw a one-hour week.</para>
    ///
    /// <para>Queued at Background priority so it runs after the refit queued at
    /// Loaded, which is what puts the real numbers in the view model.</para>
    /// </summary>
    private void CheckMetrics()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var viewportWidth = DayScroller.ActualWidth;
            var viewportHeight = DayScroller.ActualHeight;

            var column = _viewModel.ColumnWidth;
            var hour = _viewModel.HourHeight;

            var drawnWidth = _viewModel.Days.Count * _viewModel.ColumnPitch;
            var drawnHeight = hour * CalendarViewModel.Hours;

            // Overflow is the designed answer at either clamp: below the floor a
            // scrollbar is better than seven slivers, above the ceiling margin is
            // better than a stretched hour. Only an overflow that no clamp
            // explains is a fault.
            var widthFits = drawnWidth <= viewportWidth + 1;
            var heightFits = drawnHeight <= viewportHeight + 1;

            var widthBlamed = column is CalendarMetrics.MinColumnWidth or CalendarMetrics.MaxColumnWidth;
            var heightBlamed = hour is CalendarMetrics.MinHourHeight or CalendarMetrics.MaxHourHeight;

            var summary =
                $"Self-test metrics: viewport {viewportWidth:F0}x{viewportHeight:F0}, "
                + $"drawn {drawnWidth:F0}x{drawnHeight:F0} in {_viewModel.Days.Count} column(s) "
                + $"at {column:F1}x{hour:F1}; width {(widthFits ? "fits" : "overflows")}, "
                + $"height {(heightFits ? "fits" : "overflows")}.";

            if ((widthFits || widthBlamed) && (heightFits || heightBlamed))
            {
                AppLog.Info(summary);
                return;
            }

            AppLog.Warn(
                summary
                + " One axis overflows with no clamp to explain it, so the window "
                + "is not handing this grid the room the fit was computed for.");
        }));
    }

    private void OpenPanelInFixture()
    {
        var unmovable = _viewModel.Days.SelectMany(d => d.Blocks)
            .FirstOrDefault(b => !b.CanReschedule);

        _viewModel.SelectForDetails(unmovable);

        if (unmovable is null)
        {
            AppLog.Warn("Self-test panel: the fixture has no unmovable recording to open.");
            return;
        }

        if (_viewModel.HasSelectedBlock && ReferenceEquals(_viewModel.SelectedBlock, unmovable))
        {
            AppLog.Info(
                $"Self-test panel: open on \"{unmovable.Title}\" ({unmovable.TimeRange}), "
                + "which is complete and so cannot be dragged.");
        }
        else
        {
            AppLog.Warn(
                "Self-test panel: opening a completed recording did not put it in the panel.");
        }

        CheckPanelEdits();

        // Put back open, on the recording it opened on.
        //
        // Not tidiness: CheckPanelEdits ends by closing the panel, and this whole
        // method runs synchronously inside the window's Loaded handler — so
        // anything left collapsed here is still collapsed when the layout pass
        // runs, and the panel would never be drawn at all. The binding checks
        // would then pass by having nothing to describe, which is the failure mode
        // the panel fixture exists to avoid.
        _viewModel.SelectForDetails(unmovable);
    }

    /// <summary>
    /// Exercises the panel's edit half against the fixture, which is the only
    /// place it can be exercised at all: this machine has no tenant, and every one
    /// of these commands would otherwise be reached for the first time by an
    /// operator, on a real recording, having never been run once.
    ///
    /// <para><b>The load-bearing check is the round trip.</b> The panel seeds a
    /// date and two times by formatting them, and the Apply parses them back with
    /// <c>LegacyScheduleReader</c> — the importer's own parser. If those two ever
    /// disagree, every press of "Move to this time" on an untouched form fails,
    /// and it fails the same way for every session, which is exactly the kind of
    /// fault a person discovers in front of a room booking. Asserting the seed
    /// parses back to what it was seeded from is what closes that.</para>
    ///
    /// <para>What it cannot check, and does not pretend to: whether Panopto
    /// accepts the write, and whether a <i>signed-in</i> window opens these
    /// commands on a writable recording. Both need a tenant, and the Probe's
    /// <c>--verify-write</c> is where they are settled — a fixture window is never
    /// signed in, and every one of these commands is gated on
    /// <see cref="CalendarViewModel.CanEditSelection"/>, which includes
    /// <c>IsSignedIn</c>. See <see cref="CommandGates"/> for what is checked
    /// instead, and why.</para>
    /// </summary>
    private void CheckPanelEdits()
    {
        var blocks = _viewModel.Days.SelectMany(d => d.Blocks).ToList();

        var writable = blocks.FirstOrDefault(b => b.ReportsWritable);
        var readOnly = blocks.FirstOrDefault(b => !b.ReportsWritable);

        var complaints = new List<string>();

        void Complain(string what) => complaints.Add(what);

        if (writable is null || readOnly is null)
        {
            // The fixture is supposed to draw both, and the details self-test
            // already asserts that it does. Said anyway rather than skipped
            // quietly, because a fixture that stopped drawing one of them would
            // otherwise turn half of this check off without anything saying so.
            Complain("the fixture does not draw both a writable and a read-only recording, "
                     + "so only half of the panel's edit surface could be checked.");
        }

        var seeded = 0;
        var gated = 0;

        if (writable is not null)
        {
            _viewModel.SelectForDetails(writable);

            seeded += SeedRoundTrip(writable, Complain);
            gated += CommandGates(writable, expectEnabled: true, Complain);
        }

        if (readOnly is not null)
        {
            _viewModel.SelectForDetails(readOnly);

            // The fields are still filled — the operator can read them — and every
            // command is shut, which is the panel keeping the same promise the
            // editor link keeps.
            seeded += SeedRoundTrip(readOnly, Complain);
            gated += CommandGates(readOnly, expectEnabled: false, Complain);
        }

        // Closing empties it. A panel that left the last session's description in
        // the box would put it on the next recording opened.
        _viewModel.ClearDetails();

        if (_viewModel.EditName.Length > 0 || _viewModel.EditDescription.Length > 0 ||
            _viewModel.EditDate.Length > 0 || _viewModel.EditFolder.Length > 0 ||
            _viewModel.HasEditNote)
        {
            Complain("closing the panel left values in the form, so the next recording "
                     + "opened would start from the last one's.");
        }

        if (complaints.Count > 0)
        {
            AppLog.Warn("Self-test panel edits: " + string.Join(" ", complaints));
            return;
        }

        // Signed-in-ness is said out loud rather than left implicit, because it is
        // what decides how much of the gate check was live: unsigned, the six
        // commands are shut for every block alike, so the count below proves
        // nothing opened — not that writability is wired the right way round.
        AppLog.Info(
            $"Self-test panel edits: {seeded} seeded field(s) parse back to what they were seeded "
            + $"from, {gated} command(s) gated correctly on writability "
            + $"({(_viewModel.IsSignedIn ? "signed in, so both halves were live" : "not signed in, so nothing opening is the half checked")}), "
            + "and closing the panel emptied the form.");
    }

    /// <summary>
    /// Checks the seeded date and times parse back to the block they came from.
    /// Returns how many fields round-tripped, so the log line carries a count
    /// rather than an adjective.
    /// </summary>
    private int SeedRoundTrip(SessionBlockViewModel block, Action<string> complain)
    {
        var round = 0;

        var startsAt = block.StartsAt;
        var endsAt = startsAt + block.Duration;

        if (string.Equals(_viewModel.EditName, block.Title, StringComparison.Ordinal))
        {
            round++;
        }
        else
        {
            complain($"the name seeded as \"{_viewModel.EditName}\" rather than \"{block.Title}\".");
        }

        // Through the parser the Apply uses, not DateTime.Parse. A seed the
        // importer cannot read is a form that cannot be submitted.
        if (LegacyScheduleReader.TryReadDate(_viewModel.EditDate, dayFirst: false, out var day, out _))
        {
            if (day.Date == startsAt.Date)
            {
                round++;
            }
            else
            {
                complain($"\"{_viewModel.EditDate}\" read back as {day:yyyy-MM-dd} "
                         + $"rather than {startsAt:yyyy-MM-dd}.");
            }
        }
        else
        {
            complain($"the panel seeded the date \"{_viewModel.EditDate}\", which the importer's "
                     + "own parser cannot read, so Apply would refuse it.");
        }

        round += TimeRoundTrip("start", _viewModel.EditStart, startsAt, complain);
        round += TimeRoundTrip("end", _viewModel.EditEnd, endsAt, complain);

        return round;
    }

    private static int TimeRoundTrip(
        string which, string text, DateTime expected, Action<string> complain)
    {
        if (!LegacyScheduleReader.TryReadTime(text, out var time))
        {
            complain($"the panel seeded the {which} time \"{text}\", which the importer's own "
                     + "parser cannot read, so Apply would refuse it.");
            return 0;
        }

        if (time != expected.TimeOfDay)
        {
            complain($"\"{text}\" read back as {time} rather than the {which} of {expected:HH:mm}.");
            return 0;
        }

        return 1;
    }

    /// <summary>
    /// Checks each command's enabled state, and returns how many agreed.
    ///
    /// <para>The form has just been seeded from the block and nothing has been
    /// typed or changed, which is the only state this can be checked in without
    /// driving the UI. Two of these are ready the moment something is selected;
    /// the rest are off on purpose:</para>
    ///
    /// <list type="bullet">
    /// <item><b>Move</b> needs a destination typed, because the box is deliberately
    /// left empty rather than filled with the folder the recording is already in — a
    /// prefilled destination is a Move button that does nothing.</item>
    /// <item><b>Webcast</b> and <b>Save description</b> need the value to differ
    /// from what is already there, because writing what is already there spends a
    /// request to be told nothing happened.</item>
    /// <item><b>Find</b> needs something to search for.</item>
    /// </list>
    /// </summary>
    private int CommandGates(
        SessionBlockViewModel block, bool expectEnabled, Action<string> complain)
    {
        // (name, ready once selected, command). "Ready once selected" is exactly
        // what it says: enabled on a writable block with the form untouched. It
        // does not depend on writability — that is `expectEnabled`, which is what
        // the expected value multiplies through.
        (string Name, bool ReadyOnceSelected, ICommand Command)[] checks =
        [
            ("Rename", true, _viewModel.ApplyNameCommand),
            ("Move to this time", true, _viewModel.ApplyRetimeCommand),

            ("Save description", false, _viewModel.ApplyDescriptionCommand),
            ("Move here", false, _viewModel.ApplyMoveCommand),
            ("Apply webcast", false, _viewModel.ApplyBroadcastCommand),
            ("Find", false, _viewModel.FindFoldersCommand),
        ];

        // A fixture window is never signed in — it makes no request, so there is
        // nothing to be signed in to — and every command above is gated on
        // CanEditSelection, which includes IsSignedIn. So on this window all six
        // are shut whatever the block says, and expecting a writable block to open
        // two of them complained on every single run.
        //
        // That is worth fixing rather than tolerating, because of what a permanent
        // alarm does: the log is the thing an operator is asked to send when
        // something looks wrong, and a warning that is always there is one nobody
        // reads — so the first real one goes with it.
        //
        // What is checked instead is the half that survives an unsigned window:
        // nothing opens that should not, which is the read-only block's promise and
        // the one that matters for safety. What this can no longer tell apart is
        // writable from read-only — that is the tenant's half, and it is settled by
        // the Probe.
        var canOpen = _viewModel.IsSignedIn;

        var agreed = 0;

        foreach (var (name, readyOnceSelected, command) in checks)
        {
            var expected = canOpen && expectEnabled && readyOnceSelected;
            var actual = command.CanExecute(null);

            if (actual == expected)
            {
                agreed++;
                continue;
            }

            complain($"\"{name}\" is {(actual ? "enabled" : "disabled")} on a "
                     + $"{(expectEnabled ? "writable" : "read-only")} recording"
                     + (readyOnceSelected ? "" : " whose form has not been touched")
                     + (canOpen ? "" : ", in a window that is not signed in") + ".");
        }

        return agreed;
    }
#endif

    private Task OnSignInPromptRequested(string reason)
    {
        // Modal, so the calendar cannot be driven while a half-finished sign-in
        // is outstanding. ShowDialog pumps a nested message loop, so the async
        // command continuation inside the dialog still runs on this dispatcher.
        new SignInDialog(_viewModel, reason) { Owner = this }.ShowDialog();
        return Task.CompletedTask;
    }

    private void Block_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionBlockViewModel block }) return;

        // A block with no id cannot be opened, addressed or moved, so there is
        // no gesture it could be part of. Left alone rather than captured, so the
        // press falls through to whatever is behind it.
        if (block.SessionId is null) return;

        // The tick box sits inside the block, so its click arrives here too.
        // Capturing the mouse would swallow it and the box would never toggle.
        if (IsWithinCheckBox(e.OriginalSource as DependencyObject)) return;

        _dragging = block;

        // Whether this press may end in a move is decided now and not on release,
        // because a completed recording is still openable — it just cannot be
        // dragged. The previous version refused the press outright, which is why
        // a past recording swallowed the click and could never be looked at.
        _dragMayMove = block.CanReschedule;

        _dragOrigin = e.GetPosition(this);
        _originTop = block.Top;
        _originLeft = block.Left;

        // Captured unconditionally, so the release always arrives here to be
        // judged. A press that is never released over the block still has to end
        // in one of the two outcomes rather than in nothing.
        ((UIElement)sender).CaptureMouse();
    }

    private static bool IsWithinCheckBox(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is CheckBox) return true;

            // Reached the block itself without finding a box; stop rather than
            // walking the whole window.
            if (node is FrameworkElement { DataContext: SessionBlockViewModel }) return false;
        }

        return false;
    }

    private void Block_SelectionChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.NotifySelectionChanged();
        _bulk?.Refresh();
    }

    private void OpenBulk_Click(object sender, RoutedEventArgs e)
    {
        // Built once and reused, so switching tabs does not discard a preview or
        // a parsed file.
        _bulk ??= new BulkWindowViewModel(_panopto, () => _viewModel.SelectedSessions,
            _viewModel.ClearSelection);

        if (_bulkWindow is { IsLoaded: true })
        {
            _bulkWindow.Activate();
            return;
        }

        _bulkWindow = new BulkWindow(_bulk) { Owner = this };
        _bulkWindow.Closed += (_, _) => _bulkWindow = null;
        _bulkWindow.Show();
    }

    private void Block_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging is not { } block || e.LeftButton != MouseButtonState.Pressed) return;

        // A press that cannot move the block still tracks the pointer, so that
        // the same movement that would have dragged a scheduled recording does
        // not open a completed one either.
        if (!_dragMayMove) return;

        var (dx, dy) = Offset(e);

        // Inside the threshold this is still a click, so nothing moves —
        // otherwise a click with a pixel of tremor would nudge the recording.
        // The same test decides the release, so the preview and the outcome
        // cannot disagree about which gesture this was.
        if (CalendarLayout.IsClick(dx, dy)) return;

        block.Top = _originTop + SnapToSlot(dy);
        block.Left = _originLeft + SnapToDay(dx);
    }

    private async void Block_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging is not { } block) return;

        var (dx, dy) = Offset(e);
        var mayMove = _dragMayMove;

        _dragging = null;
        _dragMayMove = false;
        ((UIElement)sender).ReleaseMouseCapture();

        // A press that did not travel is a request to read the recording rather
        // than to move it. Checked first, so a tremor during a click cannot also
        // reschedule the thing it was opening.
        if (CalendarLayout.IsClick(dx, dy))
        {
            _viewModel.SelectForDetails(block);
            return;
        }

        // Travelled, and the block is not one that can move. Nothing happens: a
        // completed recording is readable, not editable, and opening it because
        // the pointer wandered would be the app answering a question nobody
        // asked.
        if (!mayMove)
        {
            block.Top = _originTop;
            block.Left = _originLeft;
            return;
        }

        // Read off the view model, not as statics: both are fitted to the window
        // now, and a drag must be measured against the grid as it is drawn.
        var shift = CalendarLayout.DragToShift(dy, _viewModel.HourHeight);
        var dayShift = CalendarLayout.DragToDayShift(dx, _viewModel.ColumnPitch);

        // Snap the block back before the call: if the move is refused, the grid
        // is already showing the truth rather than a position that was never
        // accepted.
        block.Top = _originTop;
        block.Left = _originLeft;

        if (shift == TimeSpan.Zero && dayShift == 0) return;

        await _viewModel.RescheduleAsync(block, shift, dayShift);
    }

    private (double Dx, double Dy) Offset(MouseEventArgs e)
    {
        var now = e.GetPosition(this);
        return (now.X - _dragOrigin.X, now.Y - _dragOrigin.Y);
    }

    /// <summary>
    /// Rounds a vertical drag to the nearest slot, so the preview lands where the
    /// drop will actually put the recording rather than trailing the pointer.
    ///
    /// <para>Instance rather than static because the hour is now fitted to the
    /// window: a static read of it would snap to whatever the last window size
    /// happened to be.</para>
    /// </summary>
    private double SnapToSlot(double dy)
    {
        var slot = _viewModel.HourHeight * CalendarLayout.DragSnap.TotalMinutes / 60.0;
        return slot <= 0 ? dy : Math.Round(dy / slot) * slot;
    }

    private double SnapToDay(double dx)
        => Math.Round(dx / _viewModel.ColumnPitch) * _viewModel.ColumnPitch;

    /// <summary>
    /// Refits the grid whenever the room the calendar is drawn in changes.
    ///
    /// <para><b>The scroller's size, not the window's.</b> Its
    /// <c>ActualWidth</c> is already net of the toolbar, the legend, the details
    /// panel, the hour gutter and the scrollbar, so it is the width the columns
    /// actually have. Fitting to the window would mean subtracting four things
    /// here that the layout is already subtracting.</para>
    /// </summary>
    private void DayScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged && !e.HeightChanged) return;

        QueueRefit();
    }

    /// <summary>
    /// Keeps the day-name row's inset level with the bookings pane's scrollbar.
    ///
    /// <para>Subscribed separately from the size change because the scrollbar can
    /// appear and disappear without the scroller being resized — the fit reaching
    /// the hour ceiling removes it — and the inset is the whole reason the two
    /// centred panes stay over each other.</para>
    /// </summary>
    private void DayScroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => QueueRefit();

    private bool _refitQueued;

    /// <summary>
    /// Asks for a refit, at most once per frame.
    ///
    /// <para><b>Deferred rather than done in place,</b> because both events can be
    /// raised from inside a layout pass and a refit replaces every block on the
    /// grid — changing the visual tree while WPF is arranging it is the kind of
    /// thing that throws on one machine and not another. Posted at Loaded
    /// priority, which is below Layout, so it runs after the pass that asked for
    /// it rather than in the middle of it.</para>
    ///
    /// <para><b>Coalesced,</b> because a scroll raises the event for every pixel
    /// of travel and each queued refit would fit the same geometry again.</para>
    /// </summary>
    private void QueueRefit()
    {
        if (_refitQueued) return;

        _refitQueued = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _refitQueued = false;
            Refit();
        }));
    }

    /// <summary>
    /// Reports the scroller's real geometry to the view model: the box the
    /// columns are drawn in, and how much of it the scrollbar has taken.
    /// </summary>
    private void Refit()
    {
        _viewModel.ScrollbarInset = DayScroller.ComputedVerticalScrollBarVisibility == Visibility.Visible
            ? SystemParameters.VerticalScrollBarWidth
            : 0;

        _viewModel.ApplyMetrics(DayScroller.ActualWidth, DayScroller.ActualHeight);
    }

    /// <summary>
    /// Opens a Panopto link in the operator's own browser.
    ///
    /// <para><b>Handled rather than left to <c>NavigateUri</c>.</b> A bare
    /// <c>NavigateUri</c> on a <see cref="Hyperlink"/> only navigates a frame;
    /// in a window with no frame it does nothing at all, so the link would look
    /// live, highlight on hover, and be inert.</para>
    ///
    /// <para><b>The scheme is checked, and that is not decoration.</b> These URLs
    /// come from Panopto's JSON rather than from this app, and
    /// <c>Process.Start</c> on a URL hands it to the shell — which resolves any
    /// registered scheme, not only web ones. A <c>file:</c> URL would open a local
    /// path and a custom scheme would launch whatever registered it. Only http and
    /// https are passed on, and a URL that is neither is reported rather than
    /// followed, because the one thing that must not happen is this app turning a
    /// string from an API response into a program launch.</para>
    /// </summary>
    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;

        var url = e.Uri?.ToString() ?? "";

        if (e.Uri is not { Scheme: "http" or "https" } parsed)
        {
            AppLog.Warn($"Refused to open a link Panopto reported with a non-web scheme: {url}");
            _viewModel.Report(
                "That link is not a web address.",
                $"Panopto reported \"{url}\", which this app will not open. "
                + "Only http and https links are followed.");
            return;
        }

        try
        {
            // UseShellExecute so the default browser is what opens, and the URL is
            // not treated as a file to execute.
            Process.Start(new ProcessStartInfo(parsed.ToString()) { UseShellExecute = true });
            _viewModel.Report("Opened in your browser.");
        }
        catch (Exception ex)
        {
            _viewModel.Report(
                "Could not open the link.",
                $"{parsed} — {ex.Message}");
        }
    }

    /// <summary>
    /// Puts text on the clipboard and says so, or says why it could not.
    ///
    /// <para><b>The try is the point.</b> The clipboard is a shared OS resource
    /// and an ordinary <c>SetText</c> fails whenever another process holds it —
    /// which is a transient, not a fault in this app. Without the catch the
    /// exception reaches <c>DispatcherUnhandledException</c>, and that handler
    /// closes the app: copying an id would occasionally quit the program, with a
    /// crash report naming a method that looks like it cannot fail.</para>
    ///
    /// <para>The status line is how the operator learns whether it worked. A
    /// silent failure here is indistinguishable from a copy that happened, and
    /// they would paste the previous clipboard contents into a support ticket.</para>
    /// </summary>
    private void CopyToClipboard(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            _viewModel.Report($"Copied {what}.");
        }
        catch (Exception ex)
        {
            _viewModel.Report(
                "Could not reach the clipboard.",
                $"Something else is holding the clipboard. Try again. {ex.Message}");
        }
    }

    /// <summary>
    /// Fills the screen, or gives it back.
    ///
    /// <para>Plain <see cref="WindowState.Maximized"/>, not a borderless window:
    /// at 840px tall the grid could not use the height it was given, which is a
    /// fitting problem and is fixed elsewhere. This only hands over the room.</para>
    /// </summary>
    private void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
