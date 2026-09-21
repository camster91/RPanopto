namespace PanoptoScheduler.Core.Import;

/// <summary>
/// A schedule row parsed from a legacy file, normalized but not yet resolved
/// against Panopto.
///
/// <para><see cref="Start"/> and <see cref="End"/> are tenant-local wall-clock
/// times with <see cref="DateTimeKind.Unspecified"/> on purpose. A legacy file
/// says "10:30 AM on 10/04" and means 10:30 where the recorder is; converting
/// through UTC here would silently shift every session by the offset.</para>
/// </summary>
public sealed record ScheduleImportRow
{
    /// <summary>Physical line the row started on, so an error can point at it.</summary>
    public required int Line { get; init; }

    public required string Title { get; init; }
    public required string RecorderName { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }

    public string? Presenter { get; init; }

    /// <summary>Folder GUID or folder name. Resolution is deferred to scheduling.</summary>
    public string? FolderHint { get; init; }

    public bool IsBroadcast { get; init; }

    /// <summary>Non-fatal problems: ambiguous dates, suspect durations, and so on.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public TimeSpan Duration => End - Start;
}

/// <summary>A row that could not be parsed at all.</summary>
public sealed record ScheduleImportError(int Line, string Message);

public sealed record ScheduleImportResult(
    IReadOnlyList<ScheduleImportRow> Rows,
    IReadOnlyList<ScheduleImportError> Errors)
{
    public bool HasErrors => Errors.Count > 0;

    public int WarningCount => Rows.Sum(r => r.Warnings.Count);

    public static ScheduleImportResult Empty { get; } = new([], []);
}
