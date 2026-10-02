using System.Collections.ObjectModel;
using Microsoft.Win32;
using PanoptoScheduler.Core;
using PanoptoScheduler.Core.Diagnostics;
using PanoptoScheduler.Core.Import;
using PanoptoScheduler.Core.Scheduling;

namespace PanoptoScheduler.App.ViewModels;

/// <summary>
/// Reads a legacy schedule file, shows what it said, and hands the rows to the
/// booking grid.
///
/// <para><b>It does not book.</b> The preview discipline, the dry run, the rate
/// limit and the per-row report live once, in
/// <see cref="BookingGridViewModel"/>, because a file and a pattern produce the
/// same rows. A second booking path here would be a second copy of that
/// discipline, and the copy that drifted would be the one holding the blue
/// button.</para>
///
/// <para><b>The rows it shows are the diagnosis, not the plan.</b> They are what
/// the parser read, unedited — which is the thing to look at when arguing with it
/// about a date column. The editable copy is on the booking tab, and changing it
/// there does not change this.</para>
/// </summary>
public sealed class ImportViewModel : ObservableObject
{
    private readonly Action<IReadOnlyList<ScheduleImportRow>> _send;

    private ScheduleImportResult _parsed = ScheduleImportResult.Empty;

    private string _filePath = "";
    private bool _dayFirstDates;
    private string _fileSummary = "No file loaded.";
    private string _status = "Choose a schedule file to begin.";
    private string _detail = "";
    private string _detailTooltip = "";

    public ImportViewModel(Action<IReadOnlyList<ScheduleImportRow>> send)
    {
        _send = send;

        ChooseFileCommand = new RelayCommand(ChooseFile);
        SendRowsCommand = new RelayCommand(SendRows, () => HasRows);
    }

    public ObservableCollection<ImportRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];

    public RelayCommand ChooseFileCommand { get; }

    /// <summary>Copies the parsed rows into the booking grid and goes there.</summary>
    public RelayCommand SendRowsCommand { get; }

    public bool HasRows => Rows.Count > 0;

    /// <summary>Drives the "could not be read" panel, which stays hidden when empty.</summary>
    public bool HasErrors => Errors.Count > 0;

    public string FilePath
    {
        get => _filePath;
        private set => Set(ref _filePath, value);
    }

    /// <summary>
    /// Read <c>10/04/2012</c> as 10 April. The parser has always had the option,
    /// and its ambiguous-date warning tells the operator to re-import with it — but
    /// nothing here passed it, so the advice pointed at a switch that did not exist.
    ///
    /// <para>Changing it reads the file again, the same way choosing a file does:
    /// the rows on screen were parsed under the old setting, and leaving them up
    /// would send the very dates the operator just said were the wrong way round.
    /// Rows already sent to the booking grid are its copy and are not touched.</para>
    /// </summary>
    public bool DayFirstDates
    {
        get => _dayFirstDates;
        set
        {
            if (!Set(ref _dayFirstDates, value)) return;

            if (FilePath.Length > 0) Load();
        }
    }

    public string FileSummary
    {
        get => _fileSummary;
        private set => Set(ref _fileSummary, value);
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

    private void ChooseFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a legacy schedule file",
            // No .txt: the parser refuses it, so offering it produced a file
            // picker whose only outcome was an error about the file just picked.
            Filter = "Schedule files (*.csv;*.xml)|*.csv;*.xml|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true) return;

        FilePath = dialog.FileName;
        Load();
    }

    private void Load()
    {
        Rows.Clear();
        Errors.Clear();

        try
        {
            _parsed = LegacyScheduleReader.ReadFile(
                FilePath, new ScheduleImportOptions { DayFirstDates = DayFirstDates });
        }
        catch (Exception ex)
        {
            _parsed = ScheduleImportResult.Empty;
            FileSummary = "Could not read that file.";
            Status = "Could not read the file.";
            (Detail, DetailTooltip) = Problem.Describe("Could not read the schedule file.", ex);

            // The previous file's rows are gone, so the send button must go with
            // them — otherwise it would offer to send nothing.
            Raise(nameof(HasRows));
            Raise(nameof(HasErrors));
            SendRowsCommand.RaiseCanExecuteChanged();
            return;
        }

        foreach (var row in _parsed.Rows) Rows.Add(new ImportRowViewModel(row));

        foreach (var error in _parsed.Errors)
            Errors.Add($"Line {error.Line}: {error.Message}");

        // Both are computed from the collections, which do not notify for them.
        Raise(nameof(HasRows));
        Raise(nameof(HasErrors));
        SendRowsCommand.RaiseCanExecuteChanged();

        var warnings = _parsed.WarningCount;

        FileSummary = $"{_parsed.Rows.Count} row(s)"
                    + (_parsed.Errors.Count > 0 ? $", {_parsed.Errors.Count} unusable" : "")
                    + (warnings > 0 ? $", {warnings} warning(s)" : "");

        Status = _parsed.Rows.Count == 0
            ? "Nothing to schedule in that file."
            : $"Read {_parsed.Rows.Count} row(s) from the file.";

        // Errors are usually the interesting part, so they get the detail line
        // rather than being hidden behind a count.
        Detail = _parsed.Errors.Count > 0
            ? $"{_parsed.Errors.Count} row(s) could not be read. They are not sent to the grid."
            : "Send them to the booking grid to edit or book.";
    }

    /// <summary>
    /// Hands the parsed rows over. They are passed as the parser produced them,
    /// not as the diagnosis grid shows them — that grid is read-only, so the two
    /// are the same rows.
    /// </summary>
    private void SendRows() => _send(_parsed.Rows);
}

public sealed class ImportRowViewModel(ScheduleImportRow row)
{
    public int Line { get; } = row.Line;
    public string Title { get; } = row.Title;
    public string Recorder { get; } = row.RecorderName;
    public string Starts { get; } = row.Start.ToString("ddd d MMM yyyy, h:mm tt");
    public string Ends { get; } = row.End.ToString("h:mm tt");
    public string Folder { get; } = row.FolderHint ?? "";
    public string Presenter { get; } = row.Presenter ?? "";
    public string Warnings { get; } = string.Join(" ", row.Warnings);
}

public sealed class OutcomeViewModel(ScheduleOutcome outcome)
{
    public int Line { get; } = outcome.Line;
    public string Title { get; } = outcome.Title;

    public string Result { get; } = outcome.Kind switch
    {
        ScheduleOutcomeKind.Scheduled => "Booked",
        ScheduleOutcomeKind.Conflict => "Booked, with a clash",
        ScheduleOutcomeKind.WouldSchedule => "Would book",
        ScheduleOutcomeKind.Skipped => "Skipped",
        _ => "Failed",
    };

    public string Message { get; } = outcome.Message;
}
