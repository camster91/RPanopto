using System.Collections.ObjectModel;
using System.Globalization;
using PanoptoScheduler.Core;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Import;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.App.ViewModels;

/// <summary>
/// Bulk rename, move, delete and webcast changes, over the calendar's selection.
///
/// <para>Every operation here is destructive or hard to spot afterwards, so all
/// four go through the same preview-then-apply shape, and the preview and the
/// real run are one code path with a flag between them.</para>
///
/// <para><b>A preview unlocks only the operation that was previewed.</b> One
/// shared "has previewed" flag would let an operator preview a rename and then
/// press Apply-delete, which is exactly the accident this design exists to
/// prevent. Changing any input revokes the preview for the same reason.</para>
///
/// <para><b>Delete needs a second thing on top of that.</b> A preview is
/// information, not consent, so the delete additionally wants the word
/// <i>delete</i> typed out — and the permission is granted for the number of
/// sessions that were on screen at the preview, so ticking one more block on the
/// calendar takes it away again. The enforcement is not here; it is
/// <see cref="DestructiveAction"/> inside the editor, because this class cannot
/// be reached by a test and a rule that cannot be tested is a rule nobody has
/// checked.</para>
/// </summary>
public sealed class BulkEditViewModel : ObservableObject
{
    /// <summary>
    /// The word an operator types to confirm a delete. Spelled here once, so the
    /// prompt and the check cannot disagree about what was asked for.
    /// </summary>
    public const string DeleteWord = "delete";

    /// <summary>
    /// One of the six operations, as the preview and apply buttons both call it.
    ///
    /// <para>The token rides on the delegate rather than being held by the view
    /// model, so the run and the thing that stops it cannot end up looking at
    /// different tokens — which is the whole failure mode of a cancel button that
    /// appears to work and does not.</para>
    /// </summary>
    private delegate Task<BulkEditReport> Op(
        IReadOnlyList<SessionTarget> targets,
        bool dryRun,
        IProgress<int>? progress,
        CancellationToken ct);

    /// <summary>
    /// The id-and-name pair the four older operations take. Retime and description
    /// take the richer shape, because they need the times and the current text.
    /// </summary>
    private static IReadOnlyList<(Guid Id, string CurrentName)> Flat(
        IReadOnlyList<SessionTarget> targets)
        => [.. targets.Select(t => (t.Id, t.CurrentName))];

    private readonly PanoptoConnection _panopto;
    private readonly Func<IReadOnlyList<SessionTarget>> _selection;
    private readonly Action _selectionConsumed;

    /// <summary>
    /// Every command whose enabled state this view model keeps current, filled by
    /// <see cref="Register"/> as the commands are built. See that method for why it
    /// is registered rather than listed.
    /// </summary>
    private readonly List<Action> _gated = [];

    private bool _busy;

    /// <summary>
    /// The token source for the run in flight, or null when nothing is running.
    ///
    /// <para>Cleared before <see cref="IsBusy"/> goes false rather than after, so a
    /// press that arrives in the same frame cannot reach a source that has already
    /// been disposed.</para>
    /// </summary>
    private CancellationTokenSource? _cts;

    private string? _previewed;
    private int _progressDone;
    private int _progressTotal;

    private IReadOnlyList<Guid> _deleteTargets = [];
    private string _deleteConfirmation = "";
    private bool _deleteAcknowledged;

    private string _find = "";
    private string _replaceWith = "";
    private string _targetFolder = "";

    // Prefilled rather than empty: an hour later is the shift a slipped schedule
    // actually needs, and a card that is ready to preview with one press teaches
    // what it does. An empty box teaches only that the button is greyed out.
    private bool _retimeSetTime;
    private bool _retimeEarlier;
    private string _retimeShiftText = "60";
    private string _retimeAtTimeText = "9:00 AM";

    private string _newDescription = "";
    private bool _descriptionAppend;

    private string _status = "Tick sessions on the calendar, then choose an operation.";
    private string _detail = "";
    private string _detailTooltip = "";

    public BulkEditViewModel(
        PanoptoConnection panopto,
        Func<IReadOnlyList<SessionTarget>> selection,
        Action selectionConsumed)
    {
        _panopto = panopto;
        _selection = selection;
        _selectionConsumed = selectionConsumed;

        PreviewRenameCommand = Preview("rename", RenameAsync);
        ApplyRenameCommand = Apply("rename", RenameAsync);

        PreviewMoveCommand = Preview("move", MoveAsync);
        ApplyMoveCommand = Apply("move", MoveAsync);

        PreviewWebcastOnCommand = Preview("webcast-on", (t, d, p, ct) => SetBroadcastAsync(t, true, d, p, ct));
        ApplyWebcastOnCommand = Apply("webcast-on", (t, d, p, ct) => SetBroadcastAsync(t, true, d, p, ct));

        PreviewWebcastOffCommand = Preview("webcast-off", (t, d, p, ct) => SetBroadcastAsync(t, false, d, p, ct));
        ApplyWebcastOffCommand = Apply("webcast-off", (t, d, p, ct) => SetBroadcastAsync(t, false, d, p, ct));

        PreviewRetimeCommand = Preview("retime", RetimeAsync);
        ApplyRetimeCommand = Apply("retime", RetimeAsync);

        PreviewDescriptionCommand = Preview("description", DescriptionAsync);
        ApplyDescriptionCommand = Apply("description", DescriptionAsync);

        PreviewDeleteCommand = Preview("delete", DeleteAsync, report =>
        {
            // Recorded here rather than read later, because this is the set the
            // operator was shown. Anything else they do from now on is a change
            // to what the confirmation was given for.
            RecordDeleteTargets([.. report.Results.Select(r => r.SessionId)]);

            // A fresh preview asks a fresh question, so the previous answer does
            // not carry over to a set it was not given for.
            ClearDeleteConfirmation();
        });

        ApplyDeleteCommand = Apply("delete", DeleteAsync,
            () => DeletePermit.Permits(DestructiveAction.DeleteVerb, SelectedIds()));

        CancelDeleteCommand = new RelayCommand(CancelDelete);

        // Registered like the ten above it, so its enabled state follows IsBusy:
        // busy is the only condition under which there is a run to stop, and
        // leaving it out of the raisers would leave the button greyed out over a
        // run that is very much still going.
        CancelCommand = Register(new AsyncRelayCommand(
            () => { _cts?.Cancel(); return Task.CompletedTask; },
            () => IsBusy));
    }

    private AsyncRelayCommand Preview(string op, Op run, Action<BulkEditReport>? onPreviewed = null)
        => Register(new AsyncRelayCommand(
            () => RunAsync(op, run, dryRun: true, onPreviewed),
            () => !IsBusy && _selection().Count > 0));

    /// <summary>
    /// Unlocked only by a preview of <paramref name="op"/> itself. A preview of
    /// some other operation must not arm this one.
    /// </summary>
    /// <param name="extra">
    /// Any further condition that operation carries — the delete's
    /// acknowledgement. Purely an affordance: the same rule is enforced in the
    /// editor, where it can be tested.
    /// </param>
    private AsyncRelayCommand Apply(string op, Op run, Func<bool>? extra = null)
        => Register(new AsyncRelayCommand(
            () => RunAsync(op, run, dryRun: false),
            () => !IsBusy && _selection().Count > 0 && _previewed == op
                  && (extra?.Invoke() ?? true)));

    /// <summary>
    /// Registers a command as one whose enabled state this view model keeps
    /// current. Registering and creating are the same act, which is the whole
    /// point: a command cannot be added and then forgotten.
    ///
    /// <para>The list this replaced was hand-written and held exactly the ten
    /// commands above. It was correct, and it would have stayed correct only as
    /// long as nobody added an eleventh — and an eleventh that was left out would
    /// not fail to build or to run. It would simply stop greying out, which reads
    /// as the app offering an operation it will refuse.</para>
    ///
    /// <para>There is no <c>CommandManager.RequerySuggested</c> anywhere in this
    /// app, so nothing else would have caught it either.</para>
    /// </summary>
    private AsyncRelayCommand Register(AsyncRelayCommand command)
    {
        _gated.Add(command.RaiseCanExecuteChanged);
        return command;
    }

    public ObservableCollection<EditResultViewModel> Results { get; } = [];

    public AsyncRelayCommand PreviewRenameCommand { get; }
    public AsyncRelayCommand ApplyRenameCommand { get; }
    public AsyncRelayCommand PreviewMoveCommand { get; }
    public AsyncRelayCommand ApplyMoveCommand { get; }
    public AsyncRelayCommand PreviewWebcastOnCommand { get; }
    public AsyncRelayCommand ApplyWebcastOnCommand { get; }
    public AsyncRelayCommand PreviewWebcastOffCommand { get; }
    public AsyncRelayCommand ApplyWebcastOffCommand { get; }
    public AsyncRelayCommand PreviewDeleteCommand { get; }
    public AsyncRelayCommand ApplyDeleteCommand { get; }
    public AsyncRelayCommand PreviewRetimeCommand { get; }
    public AsyncRelayCommand ApplyRetimeCommand { get; }
    public AsyncRelayCommand PreviewDescriptionCommand { get; }
    public AsyncRelayCommand ApplyDescriptionCommand { get; }

    /// <summary>The way out of the delete confirmation without destroying anything.</summary>
    public RelayCommand CancelDeleteCommand { get; }

    /// <summary>
    /// Stops the run in flight.
    ///
    /// <para>Safe to offer because every operation on this tab applies an end
    /// state: whatever went through before the stop is already in the state a
    /// second run would put it in, so there is no partial result to unpick. The
    /// booking tab's stop means something different and says so.</para>
    /// </summary>
    public AsyncRelayCommand CancelCommand { get; }

    /// <summary>Text to find in the current name. Matched case-sensitively.</summary>
    public string Find
    {
        get => _find;
        set { if (Set(ref _find, value)) OnInputChanged(); }
    }

    public string ReplaceWith
    {
        get => _replaceWith;
        set { if (Set(ref _replaceWith, value)) OnInputChanged(); }
    }

    /// <summary>Folder name or guid to move into.</summary>
    public string TargetFolder
    {
        get => _targetFolder;
        set { if (Set(ref _targetFolder, value)) OnInputChanged(); }
    }

    // ---------------------------------------------------------------
    // Retime.
    //
    // Two shapes, one card: a relative shift, which is what a slipped schedule
    // needs ("the room was booked from 10, everything moves to 11"), and a set
    // time of day, which is what a timetable change needs ("the class now starts
    // at nine"). Both keep each recording's own length and each recording's own
    // date — see RetimePlan, which is where that is decided and tested.
    //
    // Each pair of radio buttons is two properties that are each other's negation,
    // with either setter raising both. Two independent booleans would let the pair
    // disagree, and the UI would then show a mode the run does not use.
    // ---------------------------------------------------------------

    /// <summary>Whether the retime shifts by an amount, rather than setting a time.</summary>
    public bool RetimeShiftMode
    {
        get => !_retimeSetTime;
        set { if (value) SetRetimeSetTime(false); }
    }

    /// <inheritdoc cref="RetimeShiftMode"/>
    public bool RetimeSetTimeMode
    {
        get => _retimeSetTime;
        set => SetRetimeSetTime(value);
    }

    /// <summary>
    /// Whether the shift moves forward. The direction is a pair of buttons rather
    /// than a sign on the number, because a dropped minus sign is a move the wrong
    /// way — and the preview would faithfully offer it.
    /// </summary>
    public bool RetimeLater
    {
        get => !_retimeEarlier;
        set { if (value) SetRetimeEarlier(false); }
    }

    /// <inheritdoc cref="RetimeLater"/>
    public bool RetimeEarlier
    {
        get => _retimeEarlier;
        set => SetRetimeEarlier(value);
    }

    /// <summary>How many minutes to shift by, as typed. Always positive.</summary>
    public string RetimeShiftText
    {
        get => _retimeShiftText;
        set { if (Set(ref _retimeShiftText, value)) OnInputChanged(); }
    }

    /// <summary>
    /// The time of day to move every selected recording's start to, on the day it
    /// is already on. Parsed by the import's own reader, so a time typed here and
    /// the same time in a spreadsheet cannot mean different things.
    /// </summary>
    public string RetimeAtTimeText
    {
        get => _retimeAtTimeText;
        set { if (Set(ref _retimeAtTimeText, value)) OnInputChanged(); }
    }

    // ---------------------------------------------------------------
    // Presenter / description.
    // ---------------------------------------------------------------

    /// <summary>
    /// The text to write. Empty clears the description, which is a real thing to
    /// want and which the editor allows.
    /// </summary>
    public string NewDescription
    {
        get => _newDescription;
        set { if (Set(ref _newDescription, value)) OnInputChanged(); }
    }

    /// <summary>
    /// Whether to keep what is already there and add this text after it, rather
    /// than replacing it.
    ///
    /// <para>Unticked by default, because replacing is what the panel's
    /// single-session edit does and the two should read the same way. Ticking it is
    /// for the case the field actually exists for: adding a presenter to a
    /// description that already carries one.</para>
    /// </summary>
    public bool DescriptionAppend
    {
        get => _descriptionAppend;
        set { if (Set(ref _descriptionAppend, value)) OnInputChanged(); }
    }

    private void SetRetimeSetTime(bool value)
    {
        _retimeSetTime = value;

        Raise(nameof(RetimeShiftMode));
        Raise(nameof(RetimeSetTimeMode));

        // Switching the mode changes which inputs a run would read, so a preview
        // taken under the other one must not stay armed.
        OnInputChanged();
    }

    private void SetRetimeEarlier(bool value)
    {
        _retimeEarlier = value;

        Raise(nameof(RetimeLater));
        Raise(nameof(RetimeEarlier));
        OnInputChanged();
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

            // Whatever was being explained is no longer on screen. See the same
            // property on CalendarViewModel.
            DetailTooltip = "";
        }
    }

    /// <summary>
    /// The whole exception chain behind <see cref="Detail"/>, for that line's
    /// tooltip.
    /// </summary>
    public string DetailTooltip
    {
        get => _detailTooltip;
        private set => Set(ref _detailTooltip, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            Refresh();
        }
    }

    /// <summary>
    /// How many sessions the delete confirmation was given for. The window shows
    /// this number, so the operator is reading the count they agreed to rather
    /// than a count that has moved since.
    /// </summary>
    public int DeleteTargetCount => _deleteTargets.Count;

    /// <summary>
    /// The permit the delete will actually be spent against: which sessions were
    /// previewed, and whether the operator has typed the word.
    ///
    /// <para>Built in one place and asked three times — by the apply button, by
    /// the hint under it, and by the editor — so the sentence explaining the lock
    /// cannot describe a different rule from the one holding it.</para>
    /// </summary>
    private DestructiveAction DeletePermit =>
        new(DestructiveAction.DeleteVerb, _deleteTargets, DeleteAcknowledged);

    /// <summary>
    /// Which sessions the current selection names, in the shape the permit takes.
    /// </summary>
    private IReadOnlyList<Guid> SelectedIds() => [.. _selection().Select(s => s.Id)];

    /// <summary>
    /// Remembers the set the operator was shown, rather than how many there were.
    ///
    /// <para>The count was the original version of this, and a count cannot tell a
    /// selection that shrank from one that was swapped: untick A, tick B, and the
    /// number is unchanged while the set is not. The confirmation is given for a
    /// set of recordings, so the set is what gets recorded.</para>
    /// </summary>
    private void RecordDeleteTargets(IReadOnlyList<Guid> ids)
    {
        _deleteTargets = ids;

        Raise(nameof(DeleteTargetCount));
        Raise(nameof(DeleteGateHint));
    }

    /// <summary>
    /// What the operator typed into the confirmation box.
    ///
    /// <para>The acknowledgement is derived from this rather than held beside it,
    /// so there is no assignment that can arm the delete without the word having
    /// been typed. A checkbox states an intent; typing the verb is a small act
    /// that a person cannot do by reflex, which is the whole point of asking for
    /// one on the only operation here that cannot be undone.</para>
    /// </summary>
    public string DeleteConfirmation
    {
        get => _deleteConfirmation;
        set
        {
            if (!Set(ref _deleteConfirmation, value)) return;

            _deleteAcknowledged = string.Equals(
                value?.Trim(), DeleteWord, StringComparison.OrdinalIgnoreCase);

            Raise(nameof(DeleteAcknowledged));
            Raise(nameof(DeleteGateHint));
            Refresh();
        }
    }

    /// <summary>
    /// Whether the operator has typed the word. Read-only: consent is given by
    /// typing it, not by a property being set from somewhere else.
    /// </summary>
    public bool DeleteAcknowledged => _deleteAcknowledged;

    /// <summary>What the confirmation asks for, phrased from the word itself.</summary>
    public string DeletePrompt => $"Type {DeleteWord} to confirm";

    /// <summary>
    /// Whether a delete has been previewed, which is what brings the confirmation
    /// onto the screen. Distinct from <see cref="DeleteAcknowledged"/>: this is
    /// the operator having been shown what would go, that is the operator
    /// agreeing to it.
    /// </summary>
    public bool IsDeletePreviewed => _previewed == "delete";

    /// <summary>
    /// What still stands between the operator and the delete, said in words.
    /// A disabled button with no explanation is its own small accident: it gets
    /// pressed harder, or worked around.
    ///
    /// <para>The refusal is the permit's own, not a second copy of the rule: when
    /// the selection has changed it comes back saying which sessions were added
    /// and which removed, which is more use than a pair of counts that match by
    /// coincidence.</para>
    /// </summary>
    public string DeleteGateHint
    {
        get
        {
            if (!IsDeletePreviewed) return "Preview the delete first.";

            var ids = SelectedIds();

            if (!DeletePermit.Permits(DestructiveAction.DeleteVerb, ids))
                return DeletePermit.Refusal(DestructiveAction.DeleteVerb, ids);

            return $"This permanently deletes {DeleteTargetCount} session(s). "
                 + "Panopto keeps no undo, so nothing here can bring them back.";
        }
    }

    /// <summary>
    /// How much of the tightest allowance this app has spent in the last minute,
    /// naming the operation.
    ///
    /// <para>Read from the limiter the requests actually go through, rather than
    /// modelled again here: a second copy of the limits could disagree with the one
    /// doing the waiting, and the one doing the waiting is right. It is finally a
    /// use for <c>EndpointRateLimiter.Snapshot</c>, which was written for this and
    /// never called.</para>
    ///
    /// <para><b>A reading, not a meter.</b> It updates when this view model
    /// refreshes — before and after each run — so it says what the last run cost
    /// rather than ticking while one is in flight. That is enough to turn an
    /// opaque multi-minute pause into an understood one: "at the limit" explains a
    /// wait that "working…" does not.</para>
    /// </summary>
    public string RateLimit
    {
        get
        {
            // The per-minute window, not the per-second or per-hour one: the
            // per-minute cap is the one a run of any size actually reaches.
            var minute = TimeSpan.FromMinutes(1);

            var busiest = _panopto.Limiter.All
                .SelectMany(entry => entry.Value.Snapshot()
                    .Where(limit => limit.Window == minute)
                    .Select(limit => (entry.Key, limit.Limit, limit.Used)))
                .OrderByDescending(x => x.Limit == 0 ? 0d : (double)x.Used / x.Limit)
                .FirstOrDefault();

            if (busiest.Limit == 0) return "";

            // The trailing segment only. These keys are full service paths, and
            // "/Panopto/PublicAPI/4.6/SessionManagement.svc/DeleteSessions" is not
            // a thing to show a person.
            var name = busiest.Key.Split('/')[^1];

            return $"{busiest.Used} / {busiest.Limit} requests this minute ({name})";
        }
    }

    /// <summary>
    /// Re-evaluates every command. The window calls this when the calendar
    /// selection changes, since the selection lives in the other view model.
    /// </summary>
    public void Refresh()
    {
        RaiseAll();
        Raise(nameof(IsDeletePreviewed));
        Raise(nameof(DeleteGateHint));
        Raise(nameof(RateLimit));
    }

    /// <summary>
    /// Revokes the preview and everything granted with it. Every path that
    /// invalidates what was previewed comes through here, so a new one cannot
    /// revoke half of it.
    /// </summary>
    private void RevokePreview()
    {
        _previewed = null;
        ClearDeleteConfirmation();
    }

    /// <summary>
    /// Takes the typed word back, which is what actually withdraws the
    /// permission. Clearing the box as well as the flag means the screen cannot
    /// show a box that still reads "delete" beside a button that has stopped
    /// working.
    /// </summary>
    private void ClearDeleteConfirmation()
    {
        _deleteAcknowledged = false;

        Set(ref _deleteConfirmation, "");
        Raise(nameof(DeleteAcknowledged));
    }

    /// <summary>
    /// The operator's way out. Clears the preview as well as the word, so
    /// cancelling leaves nothing armed behind it.
    /// </summary>
    private void CancelDelete()
    {
        RevokePreview();
        Refresh();
    }

    /// <summary>
    /// Any change to the inputs revokes the preview: applying an operation whose
    /// inputs the operator has not looked at is the accident being prevented.
    /// </summary>
    private void OnInputChanged()
    {
        if (_previewed is null && !_deleteAcknowledged) return;

        RevokePreview();
        Refresh();
    }

    private async Task RunAsync(string op, Op run, bool dryRun, Action<BulkEditReport>? onPreviewed = null)
    {
        var targets = _selection();

        if (targets.Count == 0)
        {
            Status = "Nothing selected.";
            Detail = "Tick sessions on the calendar first.";
            return;
        }

        IsBusy = true;
        Results.Clear();
        Status = dryRun ? "Working out what would happen…" : "Applying…";
        Detail = "";

        var (now, done) = Wording(op);

        _progressDone = 0;
        _progressTotal = targets.Count;
        Raise(nameof(Progress));
        Raise(nameof(ProgressText));

        // Delivered on the UI thread, so the report is marshalled rather than
        // touched from whatever thread the editor happened to be on. Progress is
        // posted to the captured context, and this runs on it.
        var progress = new Progress<int>(completed =>
        {
            _progressDone = completed;
            Raise(nameof(Progress));
            Raise(nameof(ProgressText));
        });

        // One source per run: handed to the operation so the editor checks it
        // between rows, and held here so the button can reach the same one.
        using var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            var report = await run(targets, dryRun, progress, cts.Token);

            foreach (var result in report.Results) Results.Add(new EditResultViewModel(result, now));

            if (dryRun)
            {
                // Armed only when the preview found something it could do. A
                // preview in which every row was refused is information, not
                // permission: the rename whose text matches, the retime of rows
                // with no reported time, the delete of a set that has since been
                // unticked. Arming apply there offers a button whose run would
                // repeat the same refusals and write nothing — which reads as the
                // app having failed rather than as there being nothing to do.
                _previewed = report.Results.Any(r => r.Outcome != SessionEditOutcome.Failed)
                    ? op
                    : null;

                onPreviewed?.Invoke(report);

                Status = $"{report.WouldApply} of {report.Results.Count} would be {done}.";
                Detail = _previewed is null
                    ? "Nothing here can be applied — see the results."
                    : "Nothing was written. Applying this operation is now enabled.";
            }
            else
            {
                // The names in hand are now stale, so the selection is dropped
                // rather than left ticked for a second run against old text.
                RevokePreview();
                _selectionConsumed();

                Status = $"{report.Applied} {done}.";
                Detail = report.Failed > 0
                    ? $"{report.Failed} failed — see the results. Refresh to see the current state."
                    : "Refresh to see the current state.";
            }

            // Beside the counts, not instead of them: the writes went through
            // either way, and what is missing is the record of them.
            if (report.AuditWarning is { } trail) Detail = $"{Detail} {trail}";
        }
        catch (OperationCanceledException ex) when (ProblemText.IsTimeout(ex))
        {
            // Not the operator's stop, though it arrives as the same exception
            // type: Panopto stopped answering mid-run. The clean-up is the same
            // as a real stop — the run is over either way and nothing stays
            // armed — but it must not be *reported* as a stop, because the two
            // are different events: every row already sent may or may not have
            // landed, and an operator told "stopped by you" would press the
            // button again believing nothing had happened. Logged as a fault,
            // not as information, for the same reason.
            RevokePreview();
            if (!dryRun) _selectionConsumed();

            AppLog.Error(
                $"Bulk {op} {(dryRun ? "preview" : "run")} timed out at "
                + $"{_progressDone} of {_progressTotal}.", ex);

            Status = dryRun
                ? $"Preview timed out at {_progressDone} of {_progressTotal}."
                : $"Panopto stopped answering at {_progressDone} of {_progressTotal}.";

            Detail = dryRun
                ? "Nothing was written. Preview again when Panopto answers."
                : $"{_progressDone} session(s) went through before Panopto stopped"
                  + " answering, and the one in flight may or may not have landed —"
                  + " refresh to see where things stand. Running it again is safe:"
                  + " every operation here sets an end state, and setting the same"
                  + " one twice leaves the same result.";
        }
        catch (OperationCanceledException)
        {
            // A stopped run is a partial one, so whatever armed it is spent. For a
            // delete that includes the permit, because the set the operator
            // confirmed is no longer the set on the calendar — the same reason a
            // completed run drops both.
            RevokePreview();

            // Dropped for a real run only: the names in hand are stale for whatever
            // went through before the stop, and a second run against stale text is
            // what the completed path already refuses. A stopped preview has
            // written nothing, so its ticks stay where they are.
            if (!dryRun) _selectionConsumed();

            // Logged as information, not as a fault. The trail in Core is written
            // row by row as the run goes, so a stopped run still leaves a record of
            // what it did — what it cannot leave is the report that would carry an
            // audit warning, because the report is the thing that never came back.
            AppLog.Info(
                $"Bulk {op} {(dryRun ? "preview" : "run")} stopped by the operator at "
                + $"{_progressDone} of {_progressTotal}.");

            Status = dryRun
                ? $"Preview stopped at {_progressDone} of {_progressTotal}."
                : $"Stopped at {_progressDone} of {_progressTotal}.";

            // Two things the operator needs and cannot work out for themselves:
            // what already happened, and whether pressing the button again is safe.
            // It is, and for a reason specific to this tab — every operation here
            // applies an end state, so the same state applied twice is the same as
            // once. Booking cannot say that, and does not.
            Detail = dryRun
                ? "Nothing was written."
                : $"{_progressDone} session(s) went through before the stop. Running it again"
                  + " is safe — every operation here sets an end state, and setting the same"
                  + " one twice leaves the same result. Refresh to see where things stand.";
        }
        catch (Exception ex)
        {
            // An operation that just threw has not been looked at, so it must not
            // stay armed for a second press. This is the delete's worst case: the
            // refusal comes from the editor rather than the button, and leaving
            // the button live would invite the operator to press it again.
            RevokePreview();

            Status = dryRun ? "The preview failed." : "The operation failed.";
            (Detail, DetailTooltip) = Problem.Describe(
                dryRun ? "The bulk preview failed." : "The bulk operation failed.", ex);
        }
        finally
        {
            // Cleared before IsBusy goes false, so a press arriving in the same
            // frame cannot reach a source that is about to be disposed.
            _cts = null;
            IsBusy = false;

            // Left on screen after the run rather than reset to zero: "200 of 200"
            // is how the operator knows the run finished rather than stopped.
            // Refresh has just raised RateLimit, which is the other half of that.
        }
    }

    /// <summary>
    /// How far the current run has got, 0–100, for the bar.
    ///
    /// <para>Zero only before anything has run, so the bar starts empty rather than
    /// half-drawn. After a run it stays where the run ended: the count left on
    /// screen is how the operator tells a run that finished from one that stopped,
    /// and <see cref="ProgressText"/> says which of the two in words. It is reset
    /// at the start of the next run, not at the end of this one.</para>
    /// </summary>
    public double Progress => _progressTotal == 0
        ? 0
        : Math.Min(100, _progressDone * 100.0 / _progressTotal);

    /// <summary>"37 of 200" — the same pair the bar draws, in the words an
    /// operator reads a long run by. Empty before anything has run, so it does not
    /// claim "0 of 0".</summary>
    public string ProgressText => _progressTotal == 0
        ? ""
        : $"{_progressDone} of {_progressTotal}";

    /// <summary>
    /// How an operation names itself to the operator, in both tenses. The word
    /// "change" was the only one a delete ever got, which is what let a permanent
    /// deletion read like an edit.
    /// </summary>
    private static (string Now, string Done) Wording(string op) => op switch
    {
        "rename" => ("rename", "renamed"),
        "move" => ("move", "moved"),
        "retime" => ("retime", "retimed"),
        "description" => ("update", "updated"),
        "delete" => ("delete", "deleted"),
        _ => ("change", "changed"),
    };

    private Task<BulkEditReport> RenameAsync(
        IReadOnlyList<SessionTarget> targets, bool dryRun, IProgress<int>? progress, CancellationToken ct)
    {
        if (Find.Length == 0) throw new InvalidOperationException("Enter the text to find.");

        // Substituting an empty replacement is allowed — stripping a prefix is a
        // real thing to want — but emptying the name entirely is not, and the
        // editor refuses that below this call.
        return _panopto.BulkEditing.RenameAsync(
            Flat(targets), name => name.Replace(Find, ReplaceWith), dryRun, progress, ct);
    }

    private Task<BulkEditReport> MoveAsync(
        IReadOnlyList<SessionTarget> targets, bool dryRun, IProgress<int>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(TargetFolder))
            throw new InvalidOperationException("Enter the folder to move into.");

        return _panopto.BulkEditing.MoveAsync(Flat(targets), TargetFolder.Trim(), dryRun, progress, ct);
    }

    /// <summary>
    /// The permit is built from what the operator actually agreed to, and the
    /// editor refuses anything else. A preview carries none — asking what would
    /// happen is the step that comes before the confirmation, not after it.
    /// </summary>
    private Task<BulkEditReport> DeleteAsync(
        IReadOnlyList<SessionTarget> targets, bool dryRun, IProgress<int>? progress, CancellationToken ct)
        => _panopto.BulkEditing.DeleteAsync(
            Flat(targets),
            dryRun,
            dryRun ? null : DeletePermit,
            progress,
            ct);

    private Task<BulkEditReport> SetBroadcastAsync(
        IReadOnlyList<SessionTarget> targets,
        bool isBroadcast,
        bool dryRun,
        IProgress<int>? progress,
        CancellationToken ct)
        => _panopto.BulkEditing.SetBroadcastAsync(Flat(targets), isBroadcast, dryRun, progress, ct);

    /// <summary>
    /// The bulk retime, in whichever of its two shapes the card is set to.
    ///
    /// <para>Parsed here and nowhere else: a time of day goes through the import's
    /// own reader, so "9:00 AM" typed into this box and the same text in the
    /// panel's retime box mean the same time. The arithmetic — the shift, the
    /// length each recording keeps, and the row that has no time reported at all —
    /// is <see cref="RetimePlan"/> in Core, where it is reachable by a test.</para>
    /// </summary>
    private Task<BulkEditReport> RetimeAsync(
        IReadOnlyList<SessionTarget> targets, bool dryRun, IProgress<int>? progress, CancellationToken ct)
    {
        if (RetimeSetTimeMode)
        {
            // The reader answers a time of day, which is exactly what the planner
            // wants — no date is involved, because each recording keeps its own.
            if (!LegacyScheduleReader.TryReadTime(RetimeAtTimeText, out var at))
                throw new InvalidOperationException(
                    $"Could not read the time \"{RetimeAtTimeText}\". Try 9:00 AM.");

            return _panopto.BulkEditing.RetimeStartAtAsync(targets, at, dryRun, progress, ct: ct);
        }

        if (!int.TryParse(
                RetimeShiftText.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var minutes))
        {
            throw new InvalidOperationException(
                $"Could not read \"{RetimeShiftText}\" as a number of minutes.");
        }

        var by = TimeSpan.FromMinutes(RetimeEarlier ? -minutes : minutes);

        return _panopto.BulkEditing.RetimeByAsync(targets, by, dryRun, progress, ct: ct);
    }

    /// <summary>
    /// The bulk presenter / description write.
    ///
    /// <para><b>Written into the session description, because that is the only
    /// field Panopto has for a presenter.</b> The scheduling call has none, which is
    /// why the legacy tool put the presenter here and why the panel names its box
    /// the same way. An edit to this field therefore replaces something the app
    /// showed the operator first — which is the whole reason the current text is
    /// carried in the selection and printed in the preview.</para>
    /// </summary>
    private Task<BulkEditReport> DescriptionAsync(
        IReadOnlyList<SessionTarget> targets, bool dryRun, IProgress<int>? progress, CancellationToken ct)
    {
        var text = NewDescription.Trim();

        return _panopto.BulkEditing.SetDescriptionAsync(
            targets,
            current => text.Length == 0
                // An empty box means clear, whatever the append box says: there is
                // no text to append, so the choice does not apply.
                ? string.Empty
                : DescriptionAppend && current.Trim().Length > 0
                    ? $"{current.Trim()} {text}"
                    : text,
            dryRun,
            progress,
            ct);
    }

    private void RaiseAll()
    {
        foreach (var raise in _gated) raise();
    }
}

/// <param name="verb">
/// What the operation is, in the imperative — "delete", "rename", "move". The
/// grid has to say what would happen, and every operation saying "would change"
/// gave a deletion the same gentle word as a rename.
/// </param>
public sealed class EditResultViewModel(SessionEditResult result, string verb)
{
    public string Session { get; } = result.SessionName ?? result.SessionId.ToString();

    public string Outcome { get; } = result.Outcome switch
    {
        SessionEditOutcome.Applied => "Done",
        SessionEditOutcome.WouldApply => $"Would {verb}",
        _ => "Failed",
    };

    public string Message { get; } = result.Message;
}
