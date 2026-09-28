using PanoptoScheduler.Core.Models;

namespace PanoptoScheduler.Core.Status;

/// <summary>
/// When to look at the rooms today — the only automatic reads the app makes
/// (spec §1: two per distinct start time, never a timer poll).
/// </summary>
public static class CheckpointPlanner
{
    public static readonly TimeSpan MergeWithin = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<DateTime> ForDay(IEnumerable<PanoptoSession> sessions, DateOnly day)
    {
        var raw = sessions
            .Select(s => s.EffectiveStart)
            .Where(s => s is { } start && DateOnly.FromDateTime(start) == day)
            .Select(s => s!.Value)
            .Distinct()
            .SelectMany(start => new[] { start - RoomStatusEvaluator.OfflineWarnBefore, start + RoomStatusEvaluator.LateAfter })
            .Order()
            .ToList();

        var merged = new List<DateTime>(raw.Count);
        foreach (var t in raw)
            if (merged.Count == 0 || t - merged[^1] > MergeWithin) merged.Add(t);
        return merged;
    }
}
