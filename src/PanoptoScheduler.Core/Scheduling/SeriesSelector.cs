using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Scheduling;

public sealed record SeriesFolder(string FolderId, string FolderName, int Upcoming);

public sealed record SeriesFilter(
    string FolderId,
    string? RecorderName = null,
    IReadOnlySet<DayOfWeek>? Weekdays = null,
    DateOnly? From = null,
    DateOnly? To = null);

/// <summary>A course series is a folder's future scheduled sessions (spec §2). No calls: reads the cache.</summary>
public static class SeriesSelector
{
    public static IReadOnlyList<SeriesFolder> Folders(IEnumerable<PanoptoSession> sessions, DateTime nowRoom)
        => sessions.Where(s => s.FolderID is not null && s.EffectiveStart > nowRoom)
            .GroupBy(s => s.FolderID!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SeriesFolder(g.Key, g.First().FolderName ?? g.Key, g.Count()))
            .OrderBy(f => f.FolderName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    public static IReadOnlyList<PanoptoSession> Select(IEnumerable<PanoptoSession> sessions, SeriesFilter f, DateTime nowRoom)
        => sessions.Where(s =>
                string.Equals(s.FolderID, f.FolderId, StringComparison.OrdinalIgnoreCase)
                && s.EffectiveStart is { } start && start > nowRoom
                && (f.RecorderName is null || string.Equals(s.RemoteRecorderName?.Trim(), f.RecorderName.Trim(), StringComparison.OrdinalIgnoreCase))
                && (f.Weekdays is null || f.Weekdays.Count == 0 || f.Weekdays.Contains(start.DayOfWeek))
                && (f.From is null || DateOnly.FromDateTime(start) >= f.From)
                && (f.To is null || DateOnly.FromDateTime(start) <= f.To))
            .OrderBy(s => s.EffectiveStart)
            .ToList();

    public static IReadOnlyList<PanoptoSession> OnDates(IEnumerable<PanoptoSession> series, IReadOnlySet<DateOnly> dates)
        => series.Where(s => s.EffectiveStart is { } start && dates.Contains(DateOnly.FromDateTime(start))).ToList();
}
