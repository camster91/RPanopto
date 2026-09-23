using System.Collections.ObjectModel;
using System.Globalization;
using PanoptoScheduler.Core;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Import;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.App.ViewModels;

/// <summary>
/// One row of the booking grid, hand-editable.
///
/// <para><b>The date and the two times are text, not <see cref="DateTime"/>.</b>
/// A grid cell bound to a <c>DateTime</c> throws while it is being typed in —
/// "2026-09-" is not a date — so the operator gets a red cell and no explanation.
/// Held as text, a half-typed value is simply a value that does not parse yet, and
/// the complaint at booking time can name the row and say what it wanted. The
/// parsing is the importer's own, so a cell accepts
/// <c>2026-09-07</c> and <c>10:00 AM</c> and <c>10:00</c> alike.</para>
///
/// <para><b>Every change tells the grid.</b> That is what revokes the preview: the
/// operator agreed to what the preview described, and a row that has changed since
/// is not in that description.</para>
/// </summary>
public sealed class EditableBookingRow : ObservableObject
{
    private readonly Action _changed;

    private int _line;
    private string _title;
    private string _room;
    private string _date;
    private string _start;
    private string _end;
    private string _presenter;
    private string _folder;
    private bool _isBroadcast;
    private string _notes;

    public EditableBookingRow(
        Action changed,
        string title = "",
        string room = "",
        string date = "",
        string start = "10:00 AM",
        string end = "11:00 AM",
        string presenter = "",
        string folder = "",
        bool isBroadcast = false,
        string notes = "")
    {
        _changed = changed;
        _title = title;
        _room = room;
        _date = date;
        _start = start;
        _end = end;
        _presenter = presenter;
        _folder = folder;
        _isBroadcast = isBroadcast;
        _notes = notes;
    }

    /// <summary>Turns a generated or parsed row into an editable one.</summary>
    public static EditableBookingRow From(ScheduleImportRow row, Action changed)
        => new(changed,
            title: row.Title,
            room: row.RecorderName,
            date: row.Start.ToString(GridFormats.Day, CultureInfo.InvariantCulture),
            start: row.Start.ToString(GridFormats.Clock, CultureInfo.InvariantCulture),
            end: row.End.ToString(GridFormats.Clock, CultureInfo.InvariantCulture),
            presenter: row.Presenter ?? "",
            folder: row.FolderHint ?? "",
            isBroadcast: row.IsBroadcast,
            notes: string.Join(" ", row.Warnings))
        {
            // The row it came from, kept whole, so anything the grid does not show
            // — a folder hint with odd spacing, the exact seconds — is not lost by
            // a round trip through the cells.
            Source = row,
        };

    /// <summary>The row this was built from, when it was built from one.</summary>
    public ScheduleImportRow? Source { get; private init; }

    /// <summary>
    /// The grid ordinal, reassigned whenever the list changes. This is the
    /// importer's <c>Line</c>, which is what a booking outcome quotes back — so it
    /// has to stay unique or a failure would be attributed to the wrong row.
    /// </summary>
    public int Line
    {
        get => _line;
        private set => Set(ref _line, value, nameof(Line));
    }

    internal void Renumber(int line) => Line = line;

    public string Title
    {
        get => _title;
        set { if (Set(ref _title, value)) _changed(); }
    }

    public string Room
    {
        get => _room;
        set { if (Set(ref _room, value)) _changed(); }
    }

    /// <summary>As the room's clock, <c>yyyy-MM-dd</c>.</summary>
    public string Date
    {
        get => _date;
        set { if (Set(ref _date, value)) _changed(); }
    }

    public string Start
    {
        get => _start;
        set { if (Set(ref _start, value)) _changed(); }
    }

    public string End
    {
        get => _end;
        set { if (Set(ref _end, value)) _changed(); }
    }

    public string Presenter
    {
        get => _presenter;
        set { if (Set(ref _presenter, value)) _changed(); }
    }

    public string Folder
    {
        get => _folder;
        set { if (Set(ref _folder, value)) _changed(); }
    }

    public bool IsBroadcast
    {
        get => _isBroadcast;
        set { if (Set(ref _isBroadcast, value)) _changed(); }
    }

    /// <summary>
    /// Whatever the generator or the parser said about this row — read-only,
    /// because a warning is a fact about where the row came from, not a setting.
    /// </summary>
    public string Notes
    {
        get => _notes;
        private set => Set(ref _notes, value, nameof(Notes));
    }

    /// <summary>
    /// The row as the scheduler wants it, or why it cannot be one. The complaint
    /// is a clause, not a sentence: the caller prefixes the row number, because
    /// only the caller knows how the rows are numbered on screen.
    /// </summary>
    public bool TryBuild(out ScheduleImportRow row, out string complaint)
    {
        row = null!;

        if (Title.Trim().Length == 0)
        {
            complaint = "has no name";
            return false;
        }

        if (Room.Trim().Length == 0)
        {
            complaint = "names no room";
            return false;
        }

        if (!LegacyScheduleReader.TryReadDate(Date, dayFirst: false, out var day, out _))
        {
            complaint = $"cannot read the date \"{Date}\" — write it as {GridFormats.Day}";
            return false;
        }

        if (!LegacyScheduleReader.TryReadTime(Start, out var start))
        {
            complaint = $"cannot read the start time \"{Start}\" — write it as {GridFormats.Clock}";
            return false;
        }

        if (!LegacyScheduleReader.TryReadTime(End, out var end))
        {
            complaint = $"cannot read the end time \"{End}\" — write it as {GridFormats.Clock}";
            return false;
        }

        var startsAt = DateTime.SpecifyKind(day.Date + start, DateTimeKind.Unspecified);
        var endsAt = DateTime.SpecifyKind(day.Date + end, DateTimeKind.Unspecified);

        if (endsAt == startsAt)
        {
            complaint = "starts and ends at the same time, so it would be no length";
            return false;
        }

        // An end before the start is the next morning, the same reading the
        // generator takes and only when it was asked for there. Here the operator
        // typed both halves by hand and can see them side by side, so the reading
        // is shown rather than refused.
        if (endsAt < startsAt) endsAt = endsAt.AddDays(1);

        row = new ScheduleImportRow
        {
            Line = Line,
            Title = Title.Trim(),
            RecorderName = Room.Trim(),
            Start = startsAt,
            End = endsAt,
            Presenter = Blank(Presenter),
            FolderHint = Blank(Folder) ?? Source?.FolderHint,
            IsBroadcast = IsBroadcast,
        };

        complaint = "";
        return true;
    }

    private static string? Blank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class GridFormats
{
    public const string Day = "yyyy-MM-dd";
    public const string Clock = "h:mm tt";
}

/// <summary>
/// The booking grid, and the only preview-then-book path in the app.
///
/// <para><b>Why it is one type and not two.</b> A pattern and an imported file
/// produce the same <see cref="ScheduleImportRow"/>, and both end in the same
/// grid, so the preview, the dry run, the rate limiting and the per-row report
/// exist here once. The import tab no longer books anything itself: it reads a
/// file, shows what it read unedited, and hands the rows over. Two copies of this
/// discipline would eventually disagree, and the one that drifted would be the one
/// holding the blue button.</para>
///
/// <para><b>Nothing is sent if any row cannot be read.</b> A partial run that
/// booked the rows it understood would leave the operator with a grid they cannot
/// safely re-run, because re-running it would double-book what already went.</para>
/// </summary>
public sealed class BookingGridViewModel : ObservableObject
{
    private readonly PanoptoConnection _panopto;

    /// <summary>
    /// The "your enabled state may have changed" call for every command whose state
    /// depends on the grid, registered as the command is created.
    ///
    /// <para>Hand-maintaining a list of the commands is how a new one quietly stops
    /// refreshing — which is exactly the defect <see cref="BulkEditViewModel"/> has,
    /// where a hand-written array has to be updated in three places. Registering is
    /// what creates the entry here, so omission is not possible.</para>
    /// </summary>
    private readonly List<Action> _raisers = [];

    private bool _busy;

    /// <summary>
    /// The token source for the run in flight, or null when nothing is running.
    ///
    /// <para>Cleared before <see cref="IsBusy"/> goes false, so a press arriving in
    /// the same frame cannot reach a source that has already been disposed.</para>
    /// </summary>
    private CancellationTokenSource? _cts;
    private bool _previewed;
    private double _progress;
    private string _status = "Nothing to book yet.";
    private string _detail = "";
    private string _detailTooltip = "";
    private string _defaultFolderName = "";
    private bool _presenterAsDescription = true;
    private EditableBookingRow? _selected;

    public BookingGridViewModel(PanoptoConnection panopto)
    {
        _panopto = panopto;

        PreviewCommand = Gate(new AsyncRelayCommand(
            () => RunAsync(dryRun: true), () => CanRun));

        BookCommand = Gate(new AsyncRelayCommand(
            () => RunAsync(dryRun: false), () => CanRun && _previewed));

        RemoveSelectedCommand = Gate(new RelayCommand(RemoveSelected, () => !IsBusy && Selected is not null));

        ClearCommand = Gate(new RelayCommand(Clear, () => !IsBusy && Rows.Count > 0));

        // The stop button. Registered through Gate like the others so its enabled
        // state follows IsBusy, which is the only time there is a run to stop.
        CancelCommand = Gate(new AsyncRelayCommand(
            () => { _cts?.Cancel(); return Task.CompletedTask; },
            () => IsBusy));
    }

    public ObservableCollection<EditableBookingRow> Rows { get; } = [];

    public ObservableCollection<OutcomeViewModel> Outcomes { get; } = [];

    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand BookCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ClearCommand { get; }

    /// <summary>
    /// Stops the run in flight.
    ///
    /// <para>Exists here for a different reason than on the edit tab. Every edit
    /// there sets an end state, so a stopped one is repeated safely; a stopped
    /// booking has already created recordings, and creating them again is exactly
    /// what a second run must not do. So this button stops the run <i>and</i> takes
    /// the rows that went out of the grid — see <see cref="RunAsync"/>.</para>
    /// </summary>
    public AsyncRelayCommand CancelCommand { get; }

    public int RowCount => Rows.Count;

    public bool HasRows => Rows.Count > 0;

    public EditableBookingRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            RaiseAll();
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            RaiseAll();
        }
    }

    /// <summary>True once a preview has been seen for the rows as they now stand.</summary>
    public bool HasPreviewed => _previewed;

    /// <summary>Rows completed so far in the current run, 0–100.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
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
            DetailTooltip = "";
        }
    }

    public string DetailTooltip
    {
        get => _detailTooltip;
        private set => Set(ref _detailTooltip, value);
    }

    /// <summary>Folder to fall back to when a row names one that does not exist.</summary>
    public string DefaultFolderName
    {
        get => _defaultFolderName;
        set { if (Set(ref _defaultFolderName, value)) Revoke(); }
    }

    /// <summary>
    /// Write the presenter into the session description. Panopto's scheduling call
    /// has no field for a presenter, so this costs a second request per row.
    /// </summary>
    public bool SetPresenterAsDescription
    {
        get => _presenterAsDescription;
        set { if (Set(ref _presenterAsDescription, value)) Revoke(); }
    }

    private bool CanRun => !IsBusy && Rows.Count > 0;

    /// <summary>
    /// Says something about the grid from outside it, for the one caller that
    /// knows something the grid cannot work out — the file reader, which has just
    /// put rows in and knows where they came from.
    /// </summary>
    public void Announce(string status, string detail)
    {
        Status = status;
        Detail = detail;
    }

    /// <summary>
    /// Appends rows, keeping what is already in the grid.
    ///
    /// <para>Appending is the default because composing one term out of two
    /// patterns — Monday/Wednesday/Friday in one shape, Tuesday/Thursday in
    /// another — is what a real schedule looks like, and a grid that replaced
    /// itself would have to be filled in one pass or not at all.</para>
    /// </summary>
    public int Add(IReadOnlyList<ScheduleImportRow> rows)
    {
        foreach (var row in rows) Rows.Add(EditableBookingRow.From(row, OnRowChanged));

        Renumber();
        Revoke();
        Raise(nameof(RowCount));
        Raise(nameof(HasRows));

        return rows.Count;
    }

    /// <summary>Empties the grid and replaces it with these rows.</summary>
    public int Replace(IReadOnlyList<ScheduleImportRow> rows)
    {
        Clear();
        return Add(rows);
    }

    private void Clear()
    {
        Rows.Clear();
        Outcomes.Clear();

        // The rows the preview described are gone, so the preview is spent.
        _previewed = false;

        Renumber();
        Raise(nameof(RowCount));
        Raise(nameof(HasRows));
        RaiseAll();
    }

    private void RemoveSelected()
    {
        if (Selected is { } row) Rows.Remove(row);

        Renumber();
        Revoke();
        Raise(nameof(RowCount));
        Raise(nameof(HasRows));
    }

    /// <summary>
    /// Renumbers every row, because the numbering is the grid ordinal and the
    /// booking report quotes it back. A stale number would attribute a failure to
    /// the wrong row.
    /// </summary>
    private void Renumber()
    {
        for (var i = 0; i < Rows.Count; i++) Rows[i].Renumber(i + 1);
    }

    private void OnRowChanged() => Revoke();

    /// <summary>
    /// Any change to a row or a booking option revokes the preview. Without it an
    /// operator could preview one set of rows, edit them, and press Book — and the
    /// preview that unlocked the button had never described what would be booked.
    /// </summary>
    private void Revoke()
    {
        if (!_previewed) return;

        _previewed = false;

        Raise(nameof(HasPreviewed));
        RaiseAll();

        Status = "The rows changed — preview again.";
        Detail = "The preview described the rows as they were, so it no longer unlocks booking.";
    }

    private async Task RunAsync(bool dryRun)
    {
        if (Rows.Count == 0)
        {
            Status = "There are no rows to book.";
            return;
        }

        var rows = new List<ScheduleImportRow>(Rows.Count);
        var owners = new Dictionary<int, EditableBookingRow>();
        var complaints = new List<string>();

        foreach (var editable in Rows)
        {
            if (!editable.TryBuild(out var row, out var complaint))
            {
                complaints.Add($"Row {editable.Line} {complaint}.");
                continue;
            }

            rows.Add(row);
            owners[row.Line] = editable;
        }

        if (complaints.Count > 0)
        {
            // Nothing is sent at all. Booking the rows that parsed would leave a
            // grid nobody can safely re-run, because re-running it would double-book
            // whatever already went through.
            Status = $"{complaints.Count} row(s) cannot be booked yet. Nothing was sent.";

            Detail = string.Join(" ", complaints.Take(4))
                   + (complaints.Count > 4 ? $" …and {complaints.Count - 4} more." : "");

            DetailTooltip = string.Join("\n", complaints);
            return;
        }

        IsBusy = true;
        Outcomes.Clear();
        Progress = 0;

        var options = new BulkScheduleOptions
        {
            DefaultFolderName = string.IsNullOrWhiteSpace(DefaultFolderName) ? null : DefaultFolderName.Trim(),
            SetPresenterAsDescription = SetPresenterAsDescription,
            DryRun = dryRun,
        };

        // Every outcome the run reports, kept beside the on-screen copy. The
        // completed path reads these from the returned report; a stopped run has
        // no report, so this list is the only record of what had already gone —
        // and for booking, that record is what keeps a second run from
        // duplicating the recordings the first one created.
        var completed = new List<ScheduleOutcome>(rows.Count);

        // Progress arrives on the UI thread, so appending here is safe.
        var progress = new Progress<ScheduleProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : p.Completed * 100.0 / p.Total;
            Outcomes.Add(new OutcomeViewModel(p.Outcome));
            completed.Add(p.Outcome);
        });

        Status = dryRun ? "Working out what would happen…" : "Booking…";
        Detail = "";

        // One source per run: handed to the scheduler so it checks between rows,
        // and held here so the button reaches the same one.
        using var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var report = await _panopto.BulkScheduling
                .RunAsync(rows, options, progress, cts.Token)
                .ConfigureAwait(true);

            if (dryRun)
            {
                _previewed = true;

                Status = $"{report.WouldSchedule} of {report.Outcomes.Count} would be booked.";
                Detail = $"{report.Skipped} skipped, {report.Failed} failed."
                       + " Nothing was written. Booking is now enabled.";
            }
            else
            {
                _previewed = false;

                var spent = report.Outcomes
                    .Where(o => o.Kind is ScheduleOutcomeKind.Scheduled or ScheduleOutcomeKind.Conflict)
                    .Select(o => owners.GetValueOrDefault(o.Line))
                    .Where(r => r is not null)
                    .ToList();

                // The rows that went are removed and the ones that did not are
                // left, which makes booking anything twice structurally impossible
                // rather than something to remember.
                foreach (var row in spent) Rows.Remove(row!);

                Renumber();

                Status = $"{report.Scheduled} booked, {report.Conflicts} with a clash."
                       + (Rows.Count == 0
                            ? " The grid is empty."
                            : $" {Rows.Count} row(s) left, which nobody booked.");

                Detail = Rows.Count == 0
                    ? $"{report.Skipped} skipped, {report.Failed} failed."
                    : $"{report.Skipped} skipped, {report.Failed} failed. The rows still here are"
                      + " the ones that did not go — fix them and book again.";
            }

            // The trail is the record of what this run did to the tenant. A run
            // that could not write it still happened, so the sentence goes beside
            // the counts rather than replacing them — said plainly, because the
            // alternative is an operator believing a log exists that does not.
            if (report.AuditWarning is { } trail) Detail = $"{Detail} {trail}";
        }
        catch (OperationCanceledException ex) when (ProblemText.IsTimeout(ex))
        {
            // Not the operator's stop, though it arrives as the same exception
            // type: Panopto stopped answering mid-run. Reported as a fault, and
            // the remaining rows are not cleared the way a deliberate stop
            // clears them, because for booking the two events differ in one
            // load-bearing way. A stop is checked between rows, so nothing was
            // in flight and "none of the rest went" is true; a timeout leaves
            // the row that was in flight with its fate unknown — it may exist
            // in the tenant without ever being reported to `completed`, and
            // booking it again would create a second recording.
            _previewed = false;

            AppLog.Error(
                $"Booking {(dryRun ? "preview" : "run")} timed out after "
                + $"{completed.Count} of {rows.Count} row(s).", ex);

            if (dryRun)
            {
                Status = $"Preview timed out after {completed.Count} of {rows.Count} row(s).";
                Detail = "Nothing was written. Booking stays locked until a preview has"
                       + " seen these rows through.";
            }
            else
            {
                // The rows that reported going are removed exactly as a
                // completed run removes them, so pressing Book again cannot
                // re-book anything known to exist.
                var spent = completed
                    .Where(o => o.Kind is ScheduleOutcomeKind.Scheduled or ScheduleOutcomeKind.Conflict)
                    .Select(o => owners.GetValueOrDefault(o.Line))
                    .Where(r => r is not null)
                    .ToList();

                foreach (var row in spent) Rows.Remove(row!);

                Renumber();

                Status = $"Panopto stopped answering. {spent.Count} recording(s) had"
                       + " already been created.";

                Detail = Rows.Count == 0
                    ? "Every row in the grid had gone, so there is nothing left to book."
                    : $"{Rows.Count} row(s) are left, and one of them — the row that was in"
                      + " flight when Panopto stopped answering — may have been created"
                      + " without being reported. Check the tenant before booking it again:"
                      + " a second attempt at that row is a second recording, not the same"
                      + " one again.";
            }
        }
        catch (OperationCanceledException)
        {
            // The preview is spent: the rows are not the rows it described, because
            // some of them have just gone.
            _previewed = false;

            if (dryRun)
            {
                // A preview writes nothing, so a stopped one writes nothing. The
                // grid is left exactly as it was and only the arm is spent.
                Status = $"Preview stopped after {completed.Count} of {rows.Count} row(s).";
                Detail = "Nothing was written. Booking stays locked until a preview has"
                       + " seen these rows through.";
            }
            else
            {
                // Booking is the one operation in the app that must not be repeated:
                // a second ScheduleRecording is a second recording, not the same one
                // again. So the rows that already went are taken out of the grid
                // exactly as a completed run takes them out — which is what makes
                // pressing Book again send only what is left, instead of booking
                // every recording the stopped run had already created.
                var spent = completed
                    .Where(o => o.Kind is ScheduleOutcomeKind.Scheduled or ScheduleOutcomeKind.Conflict)
                    .Select(o => owners.GetValueOrDefault(o.Line))
                    .Where(r => r is not null)
                    .ToList();

                foreach (var row in spent) Rows.Remove(row!);

                Renumber();

                // Counted from the rows removed rather than from how far the run
                // got: the two answer different questions, and this is the one that
                // says how many recordings now exist in the tenant.
                Status = $"Stopped. {spent.Count} recording(s) had already been created.";

                Detail = Rows.Count == 0
                    ? "Every row in the grid had gone, so there is nothing left to book."
                    : $"{Rows.Count} row(s) left, and none of them went. Book again to send"
                      + " just those — anything already created has been taken out of the"
                      + " grid, so it cannot be booked a second time.";
            }
        }
        catch (Exception ex)
        {
            // The preview is spent either way: a run that threw has left the grid
            // in a state nobody has seen described.
            _previewed = false;

            Status = dryRun ? "The preview failed." : "The run failed.";
            (Detail, DetailTooltip) = Problem.Describe(
                dryRun ? "The booking preview failed." : "The booking run failed.", ex);
        }
        finally
        {
            // Cleared before IsBusy goes false, so a press arriving in the same
            // frame cannot reach a source that is about to be disposed.
            _cts = null;
            IsBusy = false;
            Progress = 0;
            Raise(nameof(RowCount));
            Raise(nameof(HasRows));
            Raise(nameof(HasPreviewed));
            RaiseAll();
        }
    }

    /// <summary>
    /// Creates a command and registers its raiser, so nobody has to remember to add
    /// it to a list later.
    /// </summary>
    private AsyncRelayCommand Gate(AsyncRelayCommand command)
    {
        _raisers.Add(command.RaiseCanExecuteChanged);
        return command;
    }

    private RelayCommand Gate(RelayCommand command)
    {
        _raisers.Add(command.RaiseCanExecuteChanged);
        return command;
    }

    private void RaiseAll()
    {
        foreach (var raise in _raisers) raise();
    }
}
