using System.Collections.ObjectModel;
using System.Globalization;
using PanoptoScheduler.Core;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Import;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.App.ViewModels;

/// <summary>
/// One room in the pattern's tick list.
///
/// <para>A tick here is not a commitment to book; it is which rooms the pattern
/// fills in. The rows it generates are editable, and a room can also be changed
/// on a row afterwards.</para>
/// </summary>
public sealed class RoomChoice : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;

    public RoomChoice(string name, Action changed)
    {
        Name = name;
        _changed = changed;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) _changed(); }
    }
}

/// <summary>One day of the week in the pattern's tick list.</summary>
public sealed class DayChoice : ObservableObject
{
    private bool _isTicked;

    public DayChoice(DayOfWeek day, string label)
    {
        Day = day;
        Label = label;
    }

    public DayOfWeek Day { get; }
    public string Label { get; }

    public bool IsTicked
    {
        get => _isTicked;
        set => Set(ref _isTicked, value);
    }
}

/// <summary>
/// The pattern builder: a repeating booking described rather than typed out, which
/// then fills the booking grid with rows the operator can hand-tweak.
///
/// <para><b>It generates rows; it does not book.</b> Nothing here writes to
/// Panopto, so there is no preview to revoke and no permit to hold — the grid is
/// the only thing that books, and it is the same grid a file fills.</para>
///
/// <para><b>The room list is the complete one.</b> The count beside it is the
/// visible proof that the listing was read to the end rather than the first page,
/// which is the defect that made rooms past the first 250 quietly not book.</para>
/// </summary>
public sealed class BookingPatternViewModel : ObservableObject
{
    private readonly PanoptoConnection _panopto;
    private readonly BookingGridViewModel _grid;

    private readonly List<RoomChoice> _allRooms = [];

    /// <summary>
    /// Every command whose enabled state depends on the rooms, registered as it is
    /// created — the same shape <see cref="BookingGridViewModel"/> uses, and for the
    /// same reason. Hand-maintaining a list is how a new command quietly stops
    /// refreshing, and hand-raising at each call site is how one of four gets
    /// forgotten at one of three sites.
    /// </summary>
    private readonly List<Action> _raisers = [];

    private bool _roomsLoaded;
    private bool _loadingRooms;
    private string _roomFilter = "";
    private bool _roomsComplete = true;
    private string _roomSummary = "Rooms have not been read yet.";
    private IReadOnlyList<string> _roomNames = [];

    private string _fromText;
    private string _toText;
    private string _startText = "10:00 AM";
    private string _endText = "11:00 AM";
    private string _skipDatesText = "";
    private string _titleFormat = "{room} {date}";
    private string _presenter = "";
    private string _folder = "";
    private bool _isBroadcast;
    private bool _allowOvernight;
    private string _status = "Fill in a pattern, then add its rows to the grid.";
    private string _detail = "";

    private readonly ObservableCollection<BookingTemplate> _templates = [];
    private BookingTemplate? _selectedTemplate;
    private string _templateName = "";
    private string _templateNote = "No saved templates yet.";

    /// <summary>
    /// Rooms a template named before the room list had been read, held until it
    /// has been. Null when there is nothing waiting.
    /// </summary>
    private IReadOnlyList<string>? _pendingRoomNames;

    public BookingPatternViewModel(PanoptoConnection panopto, BookingGridViewModel grid)
    {
        _panopto = panopto;
        _grid = grid;

        // The room's today, not this machine's. A workstation set to another zone
        // would otherwise offer a range starting on the wrong date — and these are
        // the room's calendar days.
        var today = DateOnly.FromDateTime(RoomClock.Now(panopto.RoomZone));
        _fromText = today.ToString(GridFormats.Day, CultureInfo.InvariantCulture);
        _toText = today.AddMonths(4).ToString(GridFormats.Day, CultureInfo.InvariantCulture);

        // Monday first, the way the calendar reads.
        foreach (var (day, label) in new[]
        {
            (DayOfWeek.Monday, "Mon"), (DayOfWeek.Tuesday, "Tue"), (DayOfWeek.Wednesday, "Wed"),
            (DayOfWeek.Thursday, "Thu"), (DayOfWeek.Friday, "Fri"), (DayOfWeek.Saturday, "Sat"),
            (DayOfWeek.Sunday, "Sun"),
        })
        {
            Weekdays.Add(new DayChoice(day, label));
        }

        // Not gated on the rooms: generating is gated on the pattern, and the
        // pattern's own problems are reported when the button is pressed rather
        // than by greying it out with nothing to say.
        //
        // Gated on the grid's run, though, the same way the grid's own Clear and
        // Remove are. Replace empties the grid before it fills it, so pressed
        // mid-booking it wiped the on-screen outcome of every row already booked
        // — the only record the operator had of which ones went through — while
        // the run carried on writing behind it. Add is gated with it because a
        // row arriving mid-run changes the grid the run, its progress bar and
        // its report are counting against, and was never previewed.
        AddRowsCommand = new AsyncRelayCommand(() => GenerateAsync(replace: false), () => !_grid.IsBusy);
        ReplaceRowsCommand = new AsyncRelayCommand(() => GenerateAsync(replace: true), () => !_grid.IsBusy);

        // The grid's raisers are its own, so a run starting or ending re-checks
        // Clear and Remove but not these two, which live on this view model.
        // Nothing in this app requeries commands on its own (there is no
        // CommandManager hook), so without this the two buttons would keep
        // whatever state they had when the run began.
        _grid.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BookingGridViewModel.IsBusy)) return;

            AddRowsCommand.RaiseCanExecuteChanged();
            ReplaceRowsCommand.RaiseCanExecuteChanged();
        };

        ReloadRoomsCommand = Gate(new AsyncRelayCommand(LoadRoomsAsync, () => !IsLoadingRooms));
        TickShownCommand = Gate(new RelayCommand(TickShown, () => Shown.Count > 0));
        UntickShownCommand = Gate(new RelayCommand(UntickShown, () => Shown.Count > 0));
        ClearRoomsCommand = Gate(new RelayCommand(ClearRooms, () => _allRooms.Any(r => r.IsSelected)));

        // Gated on the picker because there is nothing to load or delete without
        // one. Save is deliberately not gated: like Add rows, a form that will not
        // parse is reported when the button is pressed rather than hidden behind a
        // grey button with nothing to say.
        SaveTemplateCommand = new AsyncRelayCommand(SaveTemplateAsync);
        LoadIntoFormCommand = Gate(new RelayCommand(LoadIntoForm, () => SelectedTemplate is not null));
        DeleteTemplateCommand = Gate(new RelayCommand(DeleteTemplate, () => SelectedTemplate is not null));

        LoadTemplates();
    }

    public ObservableCollection<DayChoice> Weekdays { get; } = [];

    /// <summary>The rooms the filter lets through, which is what the list draws.</summary>
    public ObservableCollection<RoomChoice> Shown { get; } = [];

    /// <summary>What the pattern could not make sense of, in the order to fix it.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    public AsyncRelayCommand AddRowsCommand { get; }
    public AsyncRelayCommand ReplaceRowsCommand { get; }
    public AsyncRelayCommand ReloadRoomsCommand { get; }
    public RelayCommand TickShownCommand { get; }
    public RelayCommand UntickShownCommand { get; }
    public RelayCommand ClearRoomsCommand { get; }

    /// <summary>
    /// Saves the form as a template, under the name in <see cref="TemplateName"/>.
    /// </summary>
    public AsyncRelayCommand SaveTemplateCommand { get; }

    /// <summary>
    /// Fills the form from the picked template. Not "book", and not even "add
    /// rows": it does to the form exactly what typing it out again would, and the
    /// grid is not touched.
    /// </summary>
    public RelayCommand LoadIntoFormCommand { get; }

    public RelayCommand DeleteTemplateCommand { get; }

    /// <summary>
    /// Every room in the tenant, for the grid's Room column picker. A plain list
    /// with a change notification: it is replaced whole when the rooms load rather
    /// than edited a room at a time.
    /// </summary>
    public IReadOnlyList<string> RoomNames
    {
        get => _roomNames;
        private set { if (Set(ref _roomNames, value)) Raise(); }
    }

    public bool IsLoadingRooms
    {
        get => _loadingRooms;
        private set
        {
            if (!Set(ref _loadingRooms, value)) return;
            RaiseAll();
        }
    }

    public string RoomFilter
    {
        get => _roomFilter;
        set { if (Set(ref _roomFilter, value)) Refilter(); }
    }

    /// <summary>
    /// How many rooms were read, and whether that was all of them. The second half
    /// matters: an incomplete listing is the reason a room seems to be missing.
    /// </summary>
    public string RoomSummary
    {
        get => _roomSummary;
        private set => Set(ref _roomSummary, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public string FromText
    {
        get => _fromText;
        set => Set(ref _fromText, value);
    }

    public string ToText
    {
        get => _toText;
        set => Set(ref _toText, value);
    }

    public string StartText
    {
        get => _startText;
        set => Set(ref _startText, value);
    }

    public string EndText
    {
        get => _endText;
        set => Set(ref _endText, value);
    }

    /// <summary>Days to leave out — reading week, a holiday. Separated by commas.</summary>
    public string SkipDatesText
    {
        get => _skipDatesText;
        set => Set(ref _skipDatesText, value);
    }

    /// <summary>
    /// The name every generated row gets, with tokens. The line under the box lists
    /// them, because a token language nobody can see is one nobody uses.
    /// </summary>
    public string TitleFormat
    {
        get => _titleFormat;
        set => Set(ref _titleFormat, value);
    }

    /// <summary>Written into the session description, which is where a presenter goes.</summary>
    public string Presenter
    {
        get => _presenter;
        set => Set(ref _presenter, value);
    }

    public string Folder
    {
        get => _folder;
        set => Set(ref _folder, value);
    }

    public bool IsBroadcast
    {
        get => _isBroadcast;
        set => Set(ref _isBroadcast, value);
    }

    public bool AllowOvernight
    {
        get => _allowOvernight;
        set => Set(ref _allowOvernight, value);
    }

    /// <summary>The saved templates, in the order they are in the file.</summary>
    public ObservableCollection<BookingTemplate> Templates => _templates;

    public BookingTemplate? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (!Set(ref _selectedTemplate, value)) return;

            // The name box follows the picker, so pressing Save after loading
            // updates the template picked rather than quietly adding a second one
            // beside it — which is the whole reason names are compared without
            // case in the store.
            if (value is not null) TemplateName = value.Name;

            RaiseAll();
        }
    }

    /// <summary>
    /// What the form is saved as. Editable on purpose: saving under a name that is
    /// already there is how a template is updated, and typing a new one is how it
    /// is added.
    /// </summary>
    public string TemplateName
    {
        get => _templateName;
        set => Set(ref _templateName, value);
    }

    /// <summary>
    /// How many templates there are and where they live, so a person who wants to
    /// move one to another machine, or delete one by hand, is told where to look.
    /// </summary>
    public string TemplateNote
    {
        get => _templateNote;
        private set => Set(ref _templateNote, value);
    }

    /// <summary>
    /// Reads the rooms once, the first time the tab is opened. Failure is reported
    /// rather than thrown: this runs off a tab change, where there is nobody to
    /// catch it.
    /// </summary>
    public void EnsureRoomsLoaded()
    {
        if (_roomsLoaded || IsLoadingRooms) return;

        // Deliberately not awaited, and safe because LoadRoomsAsync catches
        // everything itself.
        _ = LoadRoomsAsync();
    }

    private int TickedCount => _allRooms.Count(r => r.IsSelected);

    private async Task LoadRoomsAsync()
    {
        IsLoadingRooms = true;

        try
        {
            var found = await _panopto.Recorders.ListRecordersAsync().ConfigureAwait(true);

            // Whatever was ticked stays ticked, by name — reloading must not throw
            // away a selection made against a listing that stopped early.
            var ticked = _allRooms.Where(r => r.IsSelected)
                .Select(r => r.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _allRooms.Clear();

            foreach (var recorder in found
                .Select(r => r.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                _allRooms.Add(new RoomChoice(recorder, OnRoomsTickedChanged)
                {
                    IsSelected = ticked.Contains(recorder),
                });
            }

            // A template loaded before the rooms arrived named rooms there was
            // nothing to look up. Applied now that the list exists, rather than
            // left unticked with nothing on screen to say why.
            if (_pendingRoomNames is { Count: > 0 } pending)
            {
                _pendingRoomNames = null;
                TickRooms(pending);
            }

            _roomsComplete = found.Complete;
            _roomsLoaded = true;

            RoomNames = [.. _allRooms.Select(r => r.Name)];

            Refilter();
            OnRoomsTickedChanged();

            RoomSummary = found.Complete
                ? $"{_allRooms.Count} room(s) in this tenant."
                : $"{_allRooms.Count} room(s) read, and the listing stopped before the end — "
                  + "there may be more. Reload, or narrow the search.";

            if (!found.Complete)
            {
                Status = "The room list is incomplete.";
                Detail = "Rooms past the point the listing stopped are not offered, so a name "
                       + "typed by hand is the only way to book one of them.";
            }
        }
        catch (Exception ex)
        {
            RoomSummary = "The rooms could not be read.";
            (Status, Detail) = Problem.Describe("Listing the rooms failed.", ex);
            OnRoomsTickedChanged();
        }
        finally
        {
            IsLoadingRooms = false;
        }
    }

    /// <summary>
    /// Rebuilds the visible page of rooms. The list is virtualized, but 412 rows
    /// rebuilt on every keystroke is still work worth not doing twice.
    /// </summary>
    private void Refilter()
    {
        var needle = _roomFilter.Trim();

        Shown.Clear();

        foreach (var room in _allRooms)
        {
            if (needle.Length == 0
                || room.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                Shown.Add(room);
            }
        }

        RaiseAll();
    }

    private void TickShown()
    {
        foreach (var room in Shown) room.IsSelected = true;
    }

    private void UntickShown()
    {
        foreach (var room in Shown) room.IsSelected = false;
    }

    private void ClearRooms()
    {
        foreach (var room in _allRooms) room.IsSelected = false;
    }

    // ---- Saved templates ---------------------------------------------------

    /// <summary>
    /// Reads the saved templates. Called from the constructor, so a failure has to
    /// be reported rather than thrown: there is nobody above to catch it, and a
    /// mistyped templates file must not be the reason the app will not open.
    /// </summary>
    private void LoadTemplates()
    {
        try
        {
            _templates.Clear();

            foreach (var template in BookingTemplateStore.Load()) _templates.Add(template);

            SelectedTemplate = null;
            NoteTemplates();
        }
        catch (Exception ex)
        {
            // Reported on the note line and in the status, not as a crash. The file
            // is hand-editable and sits beside credentials.json, so this is most
            // likely a typo in it — and every other part of the tab still works
            // without a single template.
            TemplateNote = "The saved templates could not be read.";
            (Status, Detail) = Problem.Describe("Reading the saved templates failed.", ex);
        }
    }

    private void NoteTemplates() => TemplateNote = _templates.Count == 0
        ? "No saved templates yet. Fill the form in and save it to keep it for next term."
        : $"{_templates.Count} saved template(s) in {BookingTemplateStore.DefaultPath}. "
          + "Dates are not kept with a template, so loading one leaves the dates in the form alone.";

    private async Task SaveTemplateAsync()
    {
        try
        {
            SaveTemplate();
        }
        catch (Exception ex)
        {
            (Status, Detail) = Problem.Describe("Saving that template failed.", ex);
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>
    /// The synchronous core, so the self-test can drive it without a command that
    /// cannot be awaited.
    /// </summary>
    internal void SaveTemplate()
    {
        Problems.Clear();

        var name = TemplateName.Trim();

        if (name.Length == 0)
        {
            Problems.Add("A saved template needs a name. Type one beside Save, or pick one to update.");

            Status = "The template has one problem to fix.";
            Detail = "Nothing was saved. The name is what the picker shows, so it is the one "
                   + "thing a template cannot go without.";
            return;
        }

        // The form's own reader, so a pattern that will not parse is refused here
        // with the same complaints Add rows would give — one reader, so the two
        // cannot come to disagree about what the form says.
        var (pattern, formProblems, _) = Build();

        if (pattern is null)
        {
            foreach (var problem in formProblems) Problems.Add(problem);

            Status = formProblems.Count == 1
                ? "The pattern has one problem to fix."
                : $"The pattern has {formProblems.Count} problems to fix.";

            Detail = "Nothing was saved — the form has to make sense before it is worth keeping.";
            return;
        }

        // The generator's whole rulebook, rather than a second partial copy of it
        // written for this button. Two of its checks are about a particular date
        // range — the row ceiling and the hour the clock jumps over — and a
        // template has no range of its own, so they are asked about the range in
        // the form: the one the operator was looking at when they pressed Save.
        var problems = BookingPatternGenerator.Problems(pattern, _panopto.RoomZone);

        if (problems.Count > 0)
        {
            foreach (var problem in problems) Problems.Add(problem);

            Status = problems.Count == 1
                ? "The pattern has one problem to fix."
                : $"The pattern has {problems.Count} problems to fix.";

            Detail = "Nothing was saved — this is what would stop it generating rows, and a "
                   + "template that cannot generate anything is not worth keeping.";
            return;
        }

        var template = BookingTemplate.From(pattern, name);

        _templates.Clear();
        foreach (var saved in BookingTemplateStore.Upsert(template)) _templates.Add(saved);

        SelectedTemplate = _templates.FirstOrDefault(
            t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        NoteTemplates();

        Status = $"Saved \"{template.Name}\".";
        Detail = $"Kept in {BookingTemplateStore.DefaultPath}. It carries {template.SpanDays} "
               + "day(s) of range, not the dates themselves, so loading it next term keeps the "
               + "dates in the form and brings back the rooms, the hour, the weekdays and the naming.";
    }

    /// <summary>
    /// The command's half: a template that will not convert is reported rather than
    /// thrown, for the same reason <see cref="SaveTemplateAsync"/> catches — this
    /// runs from a button press, and a throw there reaches the dispatcher.
    /// </summary>
    private void LoadIntoForm()
    {
        try
        {
            LoadTemplate();
        }
        catch (Exception ex)
        {
            (Status, Detail) = Problem.Describe("Loading that template failed.", ex);
        }
    }

    /// <summary>
    /// Fills the form from the picked template. Does not touch the grid and writes
    /// nothing to Panopto — it is the same form the operator would have typed into.
    ///
    /// <para>The synchronous core, so the self-test can drive it without a command
    /// that cannot be awaited.</para>
    /// </summary>
    internal void LoadTemplate()
    {
        if (SelectedTemplate is not { } template) return;

        Problems.Clear();

        // The dates already in the form are kept. A template carries no dates,
        // deliberately, because last term's range is always the part that is
        // wrong — so the anchor has to come from somewhere, and the term the
        // operator has already named is the only one they can have meant. Only
        // when that box does not parse is it replaced with the room's today, which
        // is where the form starts out.
        var anchor = LegacyScheduleReader.TryReadDate(
            FromText, dayFirst: false, out var read, out _)
                ? DateOnly.FromDateTime(read)
                : DateOnly.FromDateTime(RoomClock.Now(_panopto.RoomZone));

        var pattern = template.ToPattern(anchor);

        FromText = pattern.From.ToString(GridFormats.Day, CultureInfo.InvariantCulture);
        ToText = pattern.To.ToString(GridFormats.Day, CultureInfo.InvariantCulture);
        StartText = ClockText(pattern.From, pattern.Start);
        EndText = ClockText(pattern.From, pattern.End);
        SkipDatesText = string.Join(", ", pattern.SkipDates.Select(
            day => day.ToString(GridFormats.Day, CultureInfo.InvariantCulture)));
        TitleFormat = pattern.TitleFormat;
        Presenter = pattern.Presenter ?? "";
        Folder = pattern.FolderHint ?? "";
        IsBroadcast = pattern.IsBroadcast;
        AllowOvernight = pattern.AllowOvernight;

        foreach (var day in Weekdays) day.IsTicked = pattern.Weekdays.Contains(day.Day);

        TickRooms(pattern.Recorders);

        Status = Problems.Count > 0
            ? $"Loaded \"{template.Name}\", with {Problems.Count} thing(s) to check."
            : $"Loaded \"{template.Name}\".";

        Detail = $"{pattern.Recorders.Count} room(s), {pattern.From:yyyy-MM-dd} to "
               + $"{pattern.To:yyyy-MM-dd}, {pattern.Weekdays.Count} day(s) a week. "
               + "Add its rows to the grid when it looks right.";
    }

    /// <summary>
    /// Deletes the picked template from the file.
    ///
    /// <para>No confirmation step, and that is a decision rather than an oversight:
    /// this is one entry in a local file in the operator's own folder, it is a form
    /// they can fill in and save again in under a minute, and the alternative is a
    /// modal dialog in the middle of a tab that has none anywhere. What it does
    /// instead is name what went, so a mispress is visible the moment it happens
    /// rather than at the point where they next reach for it.</para>
    /// </summary>
    internal void DeleteTemplate()
    {
        if (SelectedTemplate is not { } template) return;

        try
        {
            _templates.Clear();
            foreach (var left in BookingTemplateStore.Remove(template.Name)) _templates.Add(left);

            SelectedTemplate = null;
            TemplateName = "";

            NoteTemplates();

            Status = $"Deleted \"{template.Name}\".";
            Detail = _templates.Count == 0
                ? "That was the last one. The form is untouched, so Save puts it back."
                : $"{_templates.Count} left. The form is untouched, so Save puts it back.";
        }
        catch (Exception ex)
        {
            (Status, Detail) = Problem.Describe("Deleting that template failed.", ex);
        }
    }

    /// <summary>
    /// Ticks the rooms a template names, by name.
    ///
    /// <para>A name that is not a room in this tenant is reported rather than
    /// dropped, which is the rule <see cref="BookingPattern"/> already follows: a
    /// template that quietly booked two of its three rooms would be a term of
    /// bookings with a room missing and nothing on screen to say so.</para>
    ///
    /// <para>Before the room list has been read there is nothing to match against,
    /// so the names are held and applied when it arrives — see
    /// <see cref="LoadRoomsAsync"/>.</para>
    /// </summary>
    private void TickRooms(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return;

        if (_allRooms.Count == 0)
        {
            _pendingRoomNames = names;
            return;
        }

        foreach (var name in names)
        {
            var match = _allRooms.FirstOrDefault(
                room => string.Equals(room.Name, name, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                Problems.Add($"\"{name}\" is not a room in this tenant, so no row will be made for it.");
            else
                match.IsSelected = true;
        }
    }

    /// <summary>
    /// A time of day as the form's own boxes write it: "2:30 PM", the format
    /// <see cref="GridFormats.Clock"/> and the reader both work in.
    ///
    /// <para>Rendered on <paramref name="day"/> — the first day of the range being
    /// filled in — rather than by adding the time to today. The display path in
    /// this app must never read this machine's clock (see <c>MachineClockGuardTests</c>),
    /// and it does not need to: the day is already known, and formatting through the
    /// same format string the reader parses keeps the two agreeing by construction
    /// rather than by a hand-rolled "12:00 AM" that would drift from it.</para>
    /// </summary>
    private static string ClockText(DateOnly day, TimeSpan time) =>
        day.ToDateTime(TimeOnly.FromTimeSpan(time))
           .ToString(GridFormats.Clock, CultureInfo.InvariantCulture);

    private void OnRoomsTickedChanged()
    {
        Raise(nameof(TickedRoomsSummary));
        RaiseAll();
    }

    /// <summary>
    /// Registers a command as room-dependent, by creating it. Registering and
    /// creating are the same act, so a command cannot be added and forgotten.
    /// </summary>
    private AsyncRelayCommand Gate(AsyncRelayCommand command)
    {
        _raisers.Add(command.RaiseCanExecuteChanged);
        return command;
    }

    /// <inheritdoc cref="Gate(AsyncRelayCommand)"/>
    private RelayCommand Gate(RelayCommand command)
    {
        _raisers.Add(command.RaiseCanExecuteChanged);
        return command;
    }

    private void RaiseAll()
    {
        foreach (var raise in _raisers) raise();
    }

    /// <summary>
    /// "12 of 412 rooms ticked" — the same pair of numbers as the legend, for the
    /// same reason: a count on its own cannot be told apart from a truncated list.
    /// </summary>
    public string TickedRoomsSummary => _allRooms.Count == 0
        ? "No rooms read yet."
        : $"{TickedCount} of {_allRooms.Count} room(s) ticked"
          + (_roomsComplete ? "" : " (the list is incomplete)");

    private async Task GenerateAsync(bool replace)
    {
        try
        {
            Generate(replace);
        }
        catch (Exception ex)
        {
            (Status, Detail) = Problem.Describe("Building those rows failed.", ex);
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    /// <summary>
    /// The synchronous core, so the self-test can fill the grid without going
    /// through a command that cannot be awaited. Returns how many rows arrived.
    /// </summary>
    internal int Generate(bool replace)
    {
        Problems.Clear();

        var (pattern, formProblems, notes) = Build();

        if (pattern is null)
        {
            Report(formProblems, replace);
            return 0;
        }

        var problems = BookingPatternGenerator.Problems(pattern, _panopto.RoomZone);

        if (problems.Count > 0)
        {
            Report(problems, replace);
            return 0;
        }

        // The zone is passed in, so a start inside the hour the clock jumps
        // over is reported here as one problem rather than as one failed row
        // per date later.
        var rows = BookingPatternGenerator.Generate(pattern, _panopto.RoomZone);

        var added = replace ? _grid.Replace(rows) : _grid.Add(rows);

        Status = replace
            ? $"{added} row(s) generated, replacing what was in the grid."
            : $"{added} row(s) added — {_grid.RowCount} in the grid now.";

        Detail = notes.Count == 0
            ? "Preview before booking."
            : string.Join(" ", notes) + " Preview before booking.";

        return added;
    }

#if DEBUG
    /// <summary>
    /// Installs a room list without reading the tenant, and marks it loaded so
    /// nothing reaches for the network. For <c>--self-test-bulk</c>, which runs
    /// with nobody signed in.
    /// </summary>
    internal void UseFixtureRooms(IReadOnlyList<string> names)
    {
        _allRooms.Clear();

        foreach (var name in names)
            _allRooms.Add(new RoomChoice(name, OnRoomsTickedChanged));

        _roomsComplete = true;
        _roomsLoaded = true;

        RoomNames = [.. names];
        Refilter();
        OnRoomsTickedChanged();

        RoomSummary = $"{names.Count} room(s) in this tenant.";
    }
#endif

    private void Report(IReadOnlyList<string> problems, bool replace)
    {
        foreach (var problem in problems) Problems.Add(problem);

        Status = problems.Count == 1
            ? "The pattern has one problem to fix."
            : $"The pattern has {problems.Count} problems to fix.";

        Detail = replace
            ? "The grid was not touched."
            : "Nothing was added.";
    }

    /// <summary>
    /// The form as a pattern, or null when it cannot even be read — in which case
    /// the complaints are about the boxes rather than the pattern. A mistyped date
    /// is a different thing to fix from an impossible range, and they are checked
    /// in that order because the second cannot be asked until the first parses.
    ///
    /// <para>Notes are neither: things that parsed but are worth saying, such as a
    /// date written the other way round that both readings accept. They do not stop
    /// the rows being made.</para>
    /// </summary>
    private (BookingPattern? Pattern, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes) Build()
    {
        var complaints = new List<string>();
        var notes = new List<string>();

        var from = default(DateTime);
        var to = default(DateTime);
        var start = TimeSpan.Zero;
        var end = TimeSpan.Zero;

        if (!LegacyScheduleReader.TryReadDate(FromText, dayFirst: false, out from, out var fromAmbiguous))
            complaints.Add($"Could not read the first day \"{FromText}\". Write it as {GridFormats.Day}.");
        else if (fromAmbiguous)
            notes.Add($"\"{FromText}\" could be read two ways, and was taken as {from:yyyy-MM-dd}.");

        if (!LegacyScheduleReader.TryReadDate(ToText, dayFirst: false, out to, out var toAmbiguous))
            complaints.Add($"Could not read the last day \"{ToText}\". Write it as {GridFormats.Day}.");
        else if (toAmbiguous)
            notes.Add($"\"{ToText}\" could be read two ways, and was taken as {to:yyyy-MM-dd}.");

        if (!LegacyScheduleReader.TryReadTime(StartText, out start))
            complaints.Add($"Could not read the start time \"{StartText}\". Write it as {GridFormats.Clock}.");

        if (!LegacyScheduleReader.TryReadTime(EndText, out end))
            complaints.Add($"Could not read the end time \"{EndText}\". Write it as {GridFormats.Clock}.");

        var skip = new List<DateOnly>();

        foreach (var piece in SkipDatesText.Split(
            [',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!LegacyScheduleReader.TryReadDate(piece, dayFirst: false, out var day, out var ambiguous))
            {
                complaints.Add($"Could not read the skip date \"{piece}\". Write it as {GridFormats.Day}.");
                continue;
            }

            if (ambiguous)
                notes.Add($"Skip date \"{piece}\" could be read two ways, and was taken as {day:yyyy-MM-dd}.");

            skip.Add(DateOnly.FromDateTime(day));
        }

        if (complaints.Count > 0) return (null, complaints, notes);

        return (new BookingPattern
        {
            Recorders = [.. _allRooms.Where(r => r.IsSelected).Select(r => r.Name)],
            From = DateOnly.FromDateTime(from),
            To = DateOnly.FromDateTime(to),
            Weekdays = [.. Weekdays.Where(d => d.IsTicked).Select(d => d.Day)],
            Start = start,
            End = end,
            SkipDates = skip,
            TitleFormat = TitleFormat,
            Presenter = Presenter,
            FolderHint = Folder,
            IsBroadcast = IsBroadcast,
            AllowOvernight = AllowOvernight,
        }, [], notes);
    }
}

/// <summary>
/// The booking tab: the pattern builder and the grid it fills.
///
/// <para>One container rather than two tab data sources, because the two halves
/// are a single workflow — the pattern exists to fill that grid, and the grid's
/// Room picker offers the rooms the pattern just read.</para>
/// </summary>
public sealed class BookingViewModel : ObservableObject
{
    public BookingViewModel(PanoptoConnection panopto)
    {
        Grid = new BookingGridViewModel(panopto);
        Pattern = new BookingPatternViewModel(panopto, Grid);
    }

    public BookingPatternViewModel Pattern { get; }
    public BookingGridViewModel Grid { get; }

    /// <summary>Reads the rooms the first time the tab is opened.</summary>
    public void Load() => Pattern.EnsureRoomsLoaded();
}
